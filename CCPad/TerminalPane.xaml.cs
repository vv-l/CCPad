using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using CCPad.Terminal;
using CCPad.Web;

namespace CCPad
{
    /// <summary>Tab status-light state. Working=green, Waiting=amber, Disconnected=red.</summary>
    public enum PaneStatus { Working, Waiting, Disconnected }

    public sealed partial class TerminalPane : UserControl, IDisposable
    {
        private string _command = "claude";
        private string? _workingDir;
        private ConPtySession? _session;
        private bool _disposed;
        private bool _awaitingRestart;
        private bool _inShell;
        // Plain "claude --resume <id>" command for the current dropped-to-cmd
        // state, or null when there's nothing to resume (not in shell / Codex /
        // no session file). Used by the numpad-up recovery hotkey.
        private string? _resumeCommand;
        private bool _ready;
        private bool _sessionPending;
        private bool _focusOnFirstOutput;
        private int _cols = 120;
        private int _rows = 30;
        private TaskCompletionSource? _readyTcs;
        private TaskCompletionSource? _loadedTcs;
        private bool _autoConfirm;
        private string _recentOutput = "";
        private Timer? _autoConfirmTimer;
        private static readonly string[] ConfirmHints = [
            "Do you want to proceed?", "Are you sure?", "Continue?", "Proceed?", "是否继续"
        ];

        /// <summary>Shell to drop into when the launched CLI exits.</summary>
        private const string ShellCommand = "cmd.exe";

        /// <summary>Unique ID for this pane, used by Web remote access.</summary>
        public string PaneId { get; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>Display label set by the tab host.</summary>
        public string Label { get; set; } = "";

        public event Action? NewTabRequested;
        public event Action? CloseTabRequested;
        public event Action? SplitHorizontalRequested;
        public event Action? SplitVerticalRequested;
        public event Action<string>? NavigateRequested;
        public event Action? ClosePaneRequested;
        /// <summary>Raised after the pane flips its own staging state (Alt+` hotkey),
        /// so the host toolbar "寄存" button can re-sync.</summary>
        public event Action? StagingChanged;
        public event Action? PaneFocused;
        public event Action<double, double>? ContextMenuRequested;

        /// <summary>Raised (on the UI thread) when the tab status light should change.</summary>
        public event Action<TerminalPane>? StatusChanged;
        /// <summary>Raised (on the UI thread) when a toast asks to reveal this pane's tab.</summary>
        public event Action? RevealRequested;

        // Resting state is Waiting (amber): a freshly-launched CLI is sitting at
        // its prompt waiting for you. Green means the AI is actively working.
        private PaneStatus _status = PaneStatus.Waiting;
        public PaneStatus Status => _status;

        // Self-correction for a stale amber light: a hook (e.g. Claude's
        // Notification) can flip us to Waiting mid-turn, but nothing flips back to
        // Working until the next UserPromptSubmit — which never comes while the
        // SAME turn keeps running. The signal that tells "still working" apart from
        // "turn just ended" is PERSISTENCE: a working CLI redraws its spinner every
        // second indefinitely, whereas a finished turn emits one short burst (final
        // message + recap + prompt redraw) then goes silent. So we only flip back to
        // green when output has streamed CONTINUOUSLY for a sustained window — a
        // lone turn-end burst can't reach it, so the amber light stands.
        private DateTime _lastUserInputUtc = DateTime.MinValue;
        private DateTime _outputRunStartUtc = DateTime.MinValue; // start of the current continuous output run while Waiting
        private DateTime _lastOutputUtc = DateTime.MinValue;     // last output seen while Waiting

        // The live CLI printed a fatal API error (e.g. "API Error: 529 Overloaded",
        // 402 billing, 403 auth). The process is still alive at its prompt, so no
        // exit fires and the hooks would show amber ("your turn") — but it's really
        // an error state, so we force the red light and keep it red across the
        // turn-ending Stop hook until the user retries (submits input) or the
        // session restarts.
        private bool _apiErrored;
        private string _recentErrScan = "";

        private void SetStatus(PaneStatus status)
        {
            if (_status == status) return;
            _status = status;
            if (status == PaneStatus.Waiting)
            {
                // Begin watching for a sustained output run that would mean the CLI
                // is actually still working despite this amber flip.
                _outputRunStartUtc = DateTime.MinValue;
                _lastOutputUtc = DateTime.MinValue;
            }
            DispatcherQueue.TryEnqueue(() => StatusChanged?.Invoke(this));
            // Mirror the status into the WebView so the in-terminal command-staging
            // UI knows when the CLI is idle (Waiting) and can flush the next queued
            // command. Demo feature — see the stage panel in TerminalHtml.
            SendPaneStatus(status);
        }

        /// <summary>Command-staging mode state (mirrors the WebView UI). Owned by the host (toolbar button).</summary>
        public bool StagingOn { get; private set; }

        /// <summary>Turn command-staging mode on/off from the host (the WinUI toolbar button).</summary>
        public void SetStaging(bool on)
        {
            StagingOn = on;
            if (_disposed) return;
            string json = $"{{\"type\":\"setStaging\",\"on\":{(on ? "true" : "false")}}}";
            DispatcherQueue.TryEnqueue(() =>
            {
                WebView.CoreWebView2?.PostWebMessageAsString(json);
                // Give keyboard focus to the WebView control so the page owns input;
                // applyStaging() in the page then puts the caret in the right inner
                // element (staging box when on, terminal when off). Don't call
                // term.focus() here — it would steal the caret back from the staging box.
                WebView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            });
        }

        /// <summary>Flip staging from the in-pane Alt+` hotkey, then nudge the host so
        /// the toolbar button mirrors it.</summary>
        public void ToggleStaging()
        {
            SetStaging(!StagingOn);
            StagingChanged?.Invoke();
        }

        /// <summary>
        /// Read an image off the system clipboard, save it as a PNG under the temp
        /// folder, and hand the path back to the staging input so it can be queued as
        /// plain text (Claude Code attaches image files referenced by path). Triggered
        /// by Alt+V while the staging input has focus — there the CLI's own clipboard
        /// read can't see the keystroke, so the host does the capture instead.
        /// </summary>
        private async Task StageImagePasteAsync()
        {
            string? path = null;
            string? error = null;
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
                {
                    error = "剪贴板里没有图片";
                }
                else
                {
                    var bitmapRef = await content.GetBitmapAsync();
                    using var inStream = await bitmapRef.OpenReadAsync();
                    var decoder = await BitmapDecoder.CreateAsync(inStream);
                    var pixels = await decoder.GetPixelDataAsync();

                    string dir = Path.Combine(Path.GetTempPath(), "CCPad", "staged-images");
                    Directory.CreateDirectory(dir);
                    var folder = await StorageFolder.GetFolderFromPathAsync(dir);
                    var file = await folder.CreateFileAsync(
                        $"img-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png",
                        CreationCollisionOption.GenerateUniqueName);
                    using (var outStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
                        encoder.SetPixelData(
                            decoder.BitmapPixelFormat,
                            decoder.BitmapAlphaMode,
                            decoder.PixelWidth, decoder.PixelHeight,
                            decoder.DpiX, decoder.DpiY,
                            pixels.DetachPixelData());
                        await encoder.FlushAsync();
                    }
                    path = file.Path;
                }
            }
            catch (Exception ex)
            {
                error = "读取剪贴板图片失败";
                System.Diagnostics.Debug.WriteLine("StageImagePaste failed: " + ex);
            }

            if (_disposed) return;
            string payload = path != null
                ? $"{{\"type\":\"stageImagePasted\",\"path\":{JsonSerializer.Serialize(path)}}}"
                : $"{{\"type\":\"stageImagePasted\",\"error\":{JsonSerializer.Serialize(error ?? "")}}}";
            WebView.CoreWebView2?.PostWebMessageAsString(payload);
        }

        /// <summary>Auto-confirm (自动回车) state. The detection logic lives host-side
        /// in CheckAutoConfirm; this just gates it. Toggled by the WinUI toolbar button.</summary>
        public bool AutoConfirmOn => _autoConfirm;

        /// <summary>Turn auto-confirm on/off from the host (the WinUI toolbar button).</summary>
        public void SetAutoConfirm(bool on)
        {
            _autoConfirm = on;
            _recentOutput = "";
        }

        /// <summary>Push the current pane status to the xterm front-end (for command staging).</summary>
        private void SendPaneStatus(PaneStatus status)
        {
            if (_disposed) return;
            string s = status switch
            {
                PaneStatus.Working => "working",
                PaneStatus.Waiting => "waiting",
                _ => "disconnected"
            };
            string json = $"{{\"type\":\"paneStatus\",\"status\":\"{s}\"}}";
            DispatcherQueue.TryEnqueue(() => WebView.CoreWebView2?.PostWebMessageAsString(json));
        }

        /// <summary>Map a hook callback (waiting/working) to a status change.</summary>
        private void OnCliNotify(string evt)
        {
            switch (evt)
            {
                case "waiting":
                    // A fatal API error keeps the red light; the turn-ending Stop
                    // hook (also "waiting") must not downgrade it to amber.
                    if (_apiErrored) return;
                    SetStatus(PaneStatus.Waiting);
                    break;
                case "working":
                    _apiErrored = false;
                    SetStatus(PaneStatus.Working);
                    break;
            }
        }

        /// <summary>Bring this pane's tab to the foreground (toast-click target).</summary>
        public void RequestReveal()
            => DispatcherQueue.TryEnqueue(() => RevealRequested?.Invoke());

        public bool IsPrewarmed => _ready;
        public string Command => _command;
        public string? WorkingDir => _workingDir;

        /// <summary>CLI mode this pane was launched with ("claude" / "codex"). Null until LaunchSession/InitializeAsync.</summary>
        public string? CliMode { get; private set; }

        public TerminalPane()
        {
            InitializeComponent();
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 12, 12, 12);
            CCPad.Settings.ThemeManager.EffectiveChanged += OnThemeEffectiveChanged;
        }

        // Push the live theme into the xterm front-end (dark-only font/dim styling).
        private void OnThemeEffectiveChanged(bool dark) => SendTheme(dark);

        private void SendTheme(bool dark)
        {
            if (_disposed) return;
            string json = $"{{\"type\":\"theme\",\"dark\":{(dark ? "true" : "false")}}}";
            DispatcherQueue.TryEnqueue(() => WebView.CoreWebView2?.PostWebMessageAsString(json));
        }

        /// <summary>
        /// Phase 1: Initialize WebView2 and load xterm.js. No process is started.
        /// Can be called while the pane sits in a hidden container.
        /// </summary>
        public async Task PrewarmAsync()
        {
            if (!WebView.IsLoaded)
            {
                _loadedTcs = new TaskCompletionSource();
                RoutedEventHandler? loaded = null;
                loaded = (_, _) =>
                {
                    if (loaded != null)
                        WebView.Loaded -= loaded;
                    _loadedTcs?.TrySetResult();
                };
                WebView.Loaded += loaded;
                await _loadedTcs.Task;
            }
            if (_disposed) throw new ObjectDisposedException(nameof(TerminalPane));

            await WebView.EnsureCoreWebView2Async();
            if (_disposed) throw new ObjectDisposedException(nameof(TerminalPane));

            if (WebView.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 failed to initialize.");

            WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            // Disable WebView2's built-in page zoom (Ctrl+wheel / Ctrl +-0). Its
            // ZoomFactor is a persistent, accumulating property capped at 5x, so a
            // long-lived pane drifts to the ceiling and "can't zoom in any more".
            // We handle Ctrl+wheel ourselves as terminal font-size zoom instead.
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            WebView.GotFocus += (_, _) => PaneFocused?.Invoke();

            string xtermFolder = GetXtermFolder();
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "xterm.local", xtermFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            _readyTcs = new TaskCompletionSource();
            WebView.CoreWebView2.NavigateToString(
                TerminalHtml
                    .Replace("Auto Confirm (自动回车)", Localization.Loc.T("autoconfirm_title"))
                    .Replace("__INITIAL_DARK__", CCPad.Settings.ThemeManager.IsDark ? "true" : "false"));
            await _readyTcs.Task;
        }

        /// <summary>
        /// Phase 2: Start the ConPty session. If prewarm isn't done yet, it will
        /// start automatically once xterm reports ready.
        /// </summary>
        public void LaunchSession(string command, string? workingDir = null, bool focusOnReady = false, string? cliMode = null)
        {
            _command = command;
            _workingDir = workingDir;
            CliMode = cliMode;
            _sessionPending = true;
            _focusOnFirstOutput = focusOnReady;
            if (_ready)
                StartSession();
        }

        /// <summary>
        /// Combined init for non-prewarmed path (first tab).
        /// </summary>
        public async Task InitializeAsync(string command, string? workingDir = null, bool focusOnReady = false, string? cliMode = null)
        {
            _command = command;
            _workingDir = workingDir;
            CliMode = cliMode;
            _sessionPending = true;
            _focusOnFirstOutput = focusOnReady;
            await PrewarmAsync();
        }

        private static string GetXtermFolder()
        {
            try
            {
                return System.IO.Path.Combine(
                    Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                    "Assets", "xterm");
            }
            catch
            {
                return System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "xterm");
            }
        }

        private void StartSession()
        {
            _sessionPending = false;
            _session?.Dispose();
            _session = null;
            _awaitingRestart = false;
            _inShell = false;
            _apiErrored = false;
            _recentErrScan = "";
            _resumeCommand = null;
            SendShellMode(false);
            try
            {
                _session = ConPtySession.Start(_command, _cols, _rows, _workingDir);
                _session.OutputReceived += OnOutput;
                _session.ProcessExited += OnProcessExited;

                TerminalSessionRegistry.Register(PaneId, new SessionEntry
                {
                    Id = PaneId,
                    Label = Label,
                    Command = _command,
                    WorkingDir = _workingDir,
                    Session = _session
                });
                TerminalSessionRegistry.NotifyChanged();

                // Notification wiring: Claude posts hook callbacks to CliNotify
                // (per-pane hook file is injected at launch via --settings).
                CliNotify.Register(PaneId, OnCliNotify);
                Notify.ToastService.RegisterPane(PaneId, this);
                SetStatus(PaneStatus.Waiting);
            }
            catch (Exception ex)
            {
                _awaitingRestart = true;
                SendOutput(Encoding.UTF8.GetBytes(
                    $"\r\n\x1b[31mFailed to start '{_command}': {ex.Message}\x1b[0m\r\n" +
                    "\x1b[33m[Press Enter to retry]\x1b[0m\r\n"));
            }
        }

        private void OnOutput(byte[] data)
        {
            SendOutput(data);
            if (_focusOnFirstOutput)
            {
                _focusOnFirstOutput = false;
                DispatcherQueue.TryEnqueue(FocusTerminal);
            }
            CheckApiError(data);
            MaybeCorrectStaleWaiting(data);
            if (_autoConfirm)
                CheckAutoConfirm(data);
        }

        // Detect the CLI's own fatal API-error banner (it stays alive at the prompt
        // afterwards, so nothing else flips the light). Matches the exact banner
        // forms Claude/Codex print, e.g. "API Error: 529 Overloaded", "API Error:
        // 402 ...", "API Error (request id ...): ...", covering 402/403/429/5xx.
        // A rolling buffer handles the marker being split across read chunks; it's
        // cleared on match so the same banner can't re-trigger after recovery.
        private void CheckApiError(byte[] data)
        {
            if (_inShell || _apiErrored) return;
            _recentErrScan += Encoding.UTF8.GetString(data);
            if (_recentErrScan.Length > 1024)
                _recentErrScan = _recentErrScan[^1024..];
            string lower = _recentErrScan.ToLowerInvariant();
            if (lower.Contains("api error:") || lower.Contains("api error ("))
            {
                _recentErrScan = "";
                _apiErrored = true;
                SetStatus(PaneStatus.Disconnected);
            }
        }

        // If we're showing Waiting (amber) but the CLI streams output CONTINUOUSLY
        // for a sustained window — output that can't be explained by user
        // keystrokes — it's actually still working, so recover the green light.
        // Continuity is the key: a finished turn emits one short burst then goes
        // quiet (stays amber); a working CLI's spinner keeps redrawing every second
        // (turns green). Guards:
        //  • user must have interacted at least once — a fresh CLI streaming its
        //    startup banner is legitimately amber;
        //  • >1500ms since the last user keystroke, so a user composing a prompt at
        //    the idle prompt (their echo IS output) stays amber;
        //  • a non-trivial chunk, so a stray byte doesn't count;
        //  • a >1500ms gap resets the run, so two separate short bursts (e.g. a
        //    delayed turn-end redraw) can't accumulate into a false positive;
        //  • only flip once the run has lasted >=2500ms of continuous output.
        private const double OutputRunGapMs = 1500;
        private const double OutputRunFlipMs = 2500;
        private void MaybeCorrectStaleWaiting(byte[] data)
        {
            if (_status != PaneStatus.Waiting) return;
            if (_lastUserInputUtc == DateTime.MinValue) return;
            if (data.Length < 24) return;
            var now = DateTime.UtcNow;
            if ((now - _lastUserInputUtc).TotalMilliseconds < OutputRunGapMs) return;

            // A pause longer than the gap means the previous burst ended — start a
            // fresh run rather than counting across the silence.
            if (_lastOutputUtc != DateTime.MinValue &&
                (now - _lastOutputUtc).TotalMilliseconds > OutputRunGapMs)
                _outputRunStartUtc = DateTime.MinValue;
            _lastOutputUtc = now;
            if (_outputRunStartUtc == DateTime.MinValue)
                _outputRunStartUtc = now;

            if ((now - _outputRunStartUtc).TotalMilliseconds >= OutputRunFlipMs)
                SetStatus(PaneStatus.Working);
        }

        private void CheckAutoConfirm(byte[] data)
        {
            // Strip ANSI escape sequences, keep plain text
            string text = Encoding.UTF8.GetString(data);
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\x1b' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next == '[')
                    {
                        i += 2;
                        while (i < text.Length && text[i] != '@' && !(text[i] >= 'A' && text[i] <= 'Z') && !(text[i] >= 'a' && text[i] <= 'z')) i++;
                        continue;
                    }
                    if (next == ']')
                    {
                        i += 2;
                        while (i < text.Length && text[i] != '\x07' && text[i] != '\x1b') i++;
                        continue;
                    }
                }
                else
                {
                    sb.Append(text[i]);
                }
            }
            _recentOutput += sb.ToString();
            if (_recentOutput.Length > 512)
                _recentOutput = _recentOutput[^512..];

            string lower = _recentOutput.ToLowerInvariant();
            bool matched = false;
            foreach (var hint in ConfirmHints)
            {
                if (lower.Contains(hint.ToLowerInvariant()))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return;

            _recentOutput = "";
            _autoConfirmTimer?.Dispose();
            _autoConfirmTimer = new Timer(_ =>
            {
                if (_autoConfirm && _session != null)
                    _session.WriteInput("\r");
                _autoConfirmTimer = null;
            }, null, 300, Timeout.Infinite);
        }

        private void OnProcessExited()
        {
            if (_disposed) return;
            // The process is gone; the red light is now owned by the
            // exit/shell flow, not the API-error flag.
            _apiErrored = false;

            if (!_inShell)
            {
                // The launched CLI (claude/codex) exited. Instead of leaving a
                // dead terminal, drop into a shell in the project directory. The
                // pseudoconsole stays alive, so prior scrollback is preserved.
                //
                // Don't rely on Claude having printed its own "claude --resume
                // <id>" banner: on an abnormal exit (API error / crash / idle
                // disconnect) it never gets the chance. Instead, look up the
                // latest session file on disk ourselves and print the exact
                // resume command, so recovery works even when Claude died hard.
                _inShell = true;
                SetStatus(PaneStatus.Disconnected);
                // BuildExitBanner() also sets _resumeCommand as a side effect.
                SendOutput(Encoding.UTF8.GetBytes(BuildExitBanner()));
                SendShellMode(_resumeCommand != null);
                _session?.SpawnProcess(ShellCommand, _workingDir);
            }
            else
            {
                // The fallback shell exited too — offer to relaunch the CLI.
                _inShell = false;
                _resumeCommand = null;
                SendShellMode(false);
                _awaitingRestart = true;
                TerminalSessionRegistry.Unregister(PaneId);
                TerminalSessionRegistry.NotifyChanged();
                SendOutput(Encoding.UTF8.GetBytes(
                    "\r\n\x1b[33m[Process exited — press Enter to restart]\x1b[0m\r\n"));
            }
        }

        /// <summary>
        /// Build the gray banner shown when the CLI exits and we drop to cmd.
        /// For Claude, append the exact "claude --resume &lt;id&gt;" command by
        /// reading the latest session file from disk, since an abnormal exit
        /// won't have printed Claude's own banner.
        /// </summary>
        private string BuildExitBanner()
        {
            const string head = "\r\n\x1b[90m[CLI exited — dropped to cmd. Type 'exit' to relaunch.]\x1b[0m\r\n";

            // Codex has its own resume flow ("codex resume"); don't fake a claude
            // command. Leave _resumeCommand null so the numpad-up hotkey is inert.
            _resumeCommand = null;
            if (string.Equals(CliMode, Settings.CliMode.Codex, StringComparison.OrdinalIgnoreCase))
                return head;

            // Re-attach the per-pane notification hooks (incl. SessionStart) so the
            // resumed Claude — a child of the fallback cmd, invisible to our process
            // tracking — drives the status light again. The banner display stays
            // clean; only the typed/↑-recalled command carries the --settings arg.
            string settings = CliNotify.ClaudeSettingsArg(PaneId);
            string claude = settings.Length > 0 ? "claude " + settings : "claude";

            var id = FindLatestClaudeSessionId();
            if (id == null)
            {
                // No session file found — still point the user at --continue.
                _resumeCommand = claude + " --continue";
                return head +
                    "\x1b[36mResume the last conversation:\x1b[0m \x1b[33mclaude --continue\x1b[0m" +
                    "  \x1b[90m(or press ↑)\x1b[0m\r\n";
            }

            _resumeCommand = claude + " --resume " + id;
            return head +
                "\x1b[36mResume this conversation:\x1b[0m \x1b[33mclaude --resume " + id + "\x1b[0m" +
                "  \x1b[90m(or: claude --continue, or press ↑)\x1b[0m\r\n";
        }

        /// <summary>
        /// Find the most recently written Claude session id for the pane's
        /// working directory. Claude stores transcripts under
        /// %USERPROFILE%\.claude\projects\&lt;encoded-cwd&gt;\&lt;session-id&gt;.jsonl,
        /// where the cwd is encoded by replacing every non-[A-Za-z0-9] char
        /// with '-' (non-collapsing). Returns null on any failure.
        /// </summary>
        private string? FindLatestClaudeSessionId()
        {
            try
            {
                if (string.IsNullOrEmpty(_workingDir)) return null;

                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "projects", EncodeProjectDir(_workingDir));
                if (!System.IO.Directory.Exists(dir)) return null;

                string? latest = null;
                DateTime latestTime = DateTime.MinValue;
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.jsonl"))
                {
                    var t = System.IO.File.GetLastWriteTimeUtc(f);
                    if (t > latestTime) { latestTime = t; latest = f; }
                }
                return latest == null ? null : System.IO.Path.GetFileNameWithoutExtension(latest);
            }
            catch { return null; }
        }

        /// <summary>
        /// Encode a working directory the way Claude Code names its project
        /// folders: every character that is not an ASCII letter or digit becomes
        /// '-'. Dashes are NOT collapsed (e.g. "C:\Users" -> "C--Users").
        /// </summary>
        private static string EncodeProjectDir(string path)
        {
            var sb = new StringBuilder(path.Length);
            foreach (char c in path)
            {
                bool ascii = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                sb.Append(ascii ? c : '-');
            }
            return sb.ToString();
        }

        private void SendOutput(byte[] data)
        {
            if (_disposed) return;
            string b64 = Convert.ToBase64String(data);
            string json = $"{{\"type\":\"output\",\"data\":\"{b64}\"}}";
            DispatcherQueue.TryEnqueue(() => WebView.CoreWebView2?.PostWebMessageAsString(json));
        }

        // Tell the xterm front-end whether we're in the dropped-to-cmd recovery
        // state, so it knows to map numpad-↑ to a resume request (and to leave
        // the key alone otherwise).
        private void SendShellMode(bool resumeAvailable)
        {
            if (_disposed) return;
            string json = $"{{\"type\":\"shellMode\",\"resume\":{(resumeAvailable ? "true" : "false")}}}";
            DispatcherQueue.TryEnqueue(() => WebView.CoreWebView2?.PostWebMessageAsString(json));
        }

        private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (_disposed) return;
            string raw = args.TryGetWebMessageAsString();
            try
            {
                var doc = JsonDocument.Parse(raw);
                string type = doc.RootElement.GetProperty("type").GetString() ?? "";

                switch (type)
                {
                    case "ready":
                        _cols = doc.RootElement.GetProperty("cols").GetInt32();
                        _rows = doc.RootElement.GetProperty("rows").GetInt32();
                        _ready = true;
                        _readyTcs?.TrySetResult();
                        if (_sessionPending)
                            StartSession();
                        break;

                    case "input":
                        string data = doc.RootElement.GetProperty("data").GetString() ?? "";
                        _lastUserInputUtc = DateTime.UtcNow;
                        if (_awaitingRestart && data == "\r")
                            StartSession();
                        else
                        {
                            // Only a real submit (Enter) means you handed the AI
                            // work → clear the amber light optimistically; the next
                            // hook re-asserts truth. Plain keystrokes, arrow keys,
                            // mouse events, and focus-report sequences (ESC[I, sent
                            // by xterm on tab switch) must NOT turn it green.
                            if (data.Contains('\r') &&
                                (_status == PaneStatus.Waiting || _apiErrored))
                            {
                                // Retrying after an API error counts as handing work
                                // back to the AI → clear the error and go green.
                                _apiErrored = false;
                                SetStatus(PaneStatus.Working);
                            }
                            _session?.WriteInput(data);
                        }
                        break;

                    case "requestStatus":
                        // Staging watchdog asks for the current status; re-send it
                        // unconditionally (SetStatus only emits on change, so this is
                        // the only way to recover a missed waiting transition).
                        SendPaneStatus(_status);
                        break;

                    case "resize":
                        _cols = doc.RootElement.GetProperty("cols").GetInt32();
                        _rows = doc.RootElement.GetProperty("rows").GetInt32();
                        _session?.Resize(_cols, _rows);
                        break;

                    case "newTab":
                        DispatcherQueue.TryEnqueue(() => NewTabRequested?.Invoke());
                        break;

                    case "closeTab":
                        DispatcherQueue.TryEnqueue(() => CloseTabRequested?.Invoke());
                        break;

                    case "splitHorizontal":
                        DispatcherQueue.TryEnqueue(() => SplitHorizontalRequested?.Invoke());
                        break;

                    case "splitVertical":
                        DispatcherQueue.TryEnqueue(() => SplitVerticalRequested?.Invoke());
                        break;

                    case "navigate":
                        string dir = doc.RootElement.GetProperty("direction").GetString() ?? "";
                        DispatcherQueue.TryEnqueue(() => NavigateRequested?.Invoke(dir));
                        break;

                    case "closePane":
                        DispatcherQueue.TryEnqueue(() => ClosePaneRequested?.Invoke());
                        break;

                    case "contextMenu":
                        double cx = doc.RootElement.GetProperty("x").GetDouble();
                        double cy = doc.RootElement.GetProperty("y").GetDouble();
                        DispatcherQueue.TryEnqueue(() => ContextMenuRequested?.Invoke(cx, cy));
                        break;

                    case "copy":
                        string copyText = doc.RootElement.GetProperty("data").GetString() ?? "";
                        if (copyText.Length > 0)
                        {
                            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                            dp.SetText(copyText);
                            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                        }
                        break;

                    case "toggleStaging":
                        // Alt+` from the front-end: flip staging and re-sync the button.
                        DispatcherQueue.TryEnqueue(ToggleStaging);
                        break;

                    case "stageImagePaste":
                        // Alt+V inside the staging input: capture the clipboard image
                        // host-side and feed its file path back for queueing.
                        DispatcherQueue.TryEnqueue(async () => await StageImagePasteAsync());
                        break;

                    case "resumeHotkey":
                        // ↑ pressed while dropped to cmd: type the resume command at
                        // the prompt WITHOUT Enter, so you can eyeball it and run it
                        // yourself. One-shot — drop the offer right after so a second
                        // ↑ can't append a duplicate, and the relaunched CLI gets ↑
                        // back to it normally.
                        if (_inShell && _resumeCommand != null)
                        {
                            _session?.WriteInput(_resumeCommand);
                            _resumeCommand = null;
                            SendShellMode(false);
                        }
                        break;

                    case "autoConfirm":
                        _autoConfirm = doc.RootElement.GetProperty("enabled").GetBoolean();
                        _recentOutput = "";
                        break;

                }
            }
            catch { }
        }

        public async void FocusTerminal()
        {
            WebView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            if (WebView.CoreWebView2 != null)
                await WebView.CoreWebView2.ExecuteScriptAsync("term.focus()");
        }

        /// <summary>
        /// Force xterm.js to recalculate dimensions. Call after the WebView's
        /// container size changes due to split-tree rebuilds.
        /// </summary>
        public async void Refit()
        {
            if (_disposed || !_ready || WebView.CoreWebView2 == null) return;
            try { await WebView.CoreWebView2.ExecuteScriptAsync("fit.fit()"); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CCPad.Settings.ThemeManager.EffectiveChanged -= OnThemeEffectiveChanged;
            _loadedTcs?.TrySetCanceled();
            _readyTcs?.TrySetCanceled();
            _autoConfirmTimer?.Dispose();
            CliNotify.Unregister(PaneId);
            CliNotify.CleanupHooks(PaneId);
            Notify.ToastService.UnregisterPane(PaneId);
            TerminalSessionRegistry.Unregister(PaneId);
            TerminalSessionRegistry.NotifyChanged();
            if (_session != null)
            {
                _session.OutputReceived -= OnOutput;
                _session.ProcessExited -= OnProcessExited;
                _session.Dispose();
                _session = null;
            }
            try
            {
                if (WebView.CoreWebView2 != null)
                    WebView.CoreWebView2.WebMessageReceived -= OnWebMessage;
                WebView.Close();
            }
            catch { }
        }

        private const string TerminalHtml = """
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8"/>
              <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                html, body { width: 100%; height: 100%; background: #0c0c0c; overflow: clip; }
                body { display: flex; flex-direction: column; }
                #terminal { width: 100%; flex: 1 1 auto; min-height: 0; overflow: hidden; }
                /* Keep the IME helper textarea inside the viewport so Chromium
                   doesn't shift the page trying to scroll it into view. */
                .xterm .xterm-helper-textarea {
                  position: fixed !important;
                  left: 0 !important;
                  top: 0 !important;
                }
                ::-webkit-scrollbar { width: 10px; }
                ::-webkit-scrollbar-track { background: #1e1e1e; }
                ::-webkit-scrollbar-thumb { background: #424242; border-radius: 5px; }
                ::-webkit-scrollbar-thumb:hover { background: #555; }
                /* ── Command staging (命令寄存) — toggled by the WinUI toolbar button ── */
                #stage {
                  display: none;
                  flex: 0 0 auto;
                  flex-direction: column;
                  background: #141414;
                  border-top: 1px solid #333;
                  font-family: 'Cascadia Code', 'Cascadia Mono', Consolas, monospace;
                  color: #ddd;
                }
                #stage.open { display: flex; }
                #stage-head {
                  display: flex;
                  align-items: center;
                  gap: 8px;
                  padding: 5px 10px;
                  font-size: 14px;
                  color: #9aa;
                  border-bottom: 1px solid #262626;
                }
                #stage-head .dot {
                  width: 7px; height: 7px; border-radius: 50%;
                  background: #f0ad4e;                 /* amber = waiting */
                }
                #stage-head .dot.working { background: #4feba0; }   /* green */
                #stage-head .dot.disconnected { background: #e05555; } /* red */
                #stage-head .spacer { flex: 1; }
                #stage-list {
                  list-style: none;
                  margin: 0; padding: 2px 0;
                  max-height: 84px;                    /* ~3 rows, then scroll */
                  overflow-y: auto;
                }
                #stage-list li {
                  display: flex;
                  align-items: center;
                  gap: 8px;
                  padding: 3px 10px;
                  font-size: 14px;            /* match the terminal page font */
                  white-space: pre;
                  overflow: hidden;
                }
                #stage-list li .idx { color: #667; flex: 0 0 auto; }
                #stage-list li .txt {
                  flex: 1 1 auto;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                  color: #e6e6e6;
                }
                #stage-list li .del {
                  flex: 0 0 auto;
                  cursor: pointer;
                  color: #c4c4c4;
                  font-size: 15px;
                  line-height: 1;
                  width: 24px;
                  height: 24px;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  border: 1px solid #444;
                  border-radius: 4px;
                  transition: all 0.12s;
                }
                #stage-list li .del:hover {
                  color: #fff;
                  background: #c0392b;
                  border-color: #c0392b;
                }
                #stage-empty { padding: 6px 10px; font-size: 14px; color: #666; }
                #stage-input-row { display: flex; align-items: flex-end; padding: 6px 10px; gap: 8px; }
                #stage-input {
                  flex: 1 1 auto;
                  resize: none;
                  min-height: 30px;
                  max-height: 96px;
                  background: #0c0c0c;
                  color: #eee;
                  border: 1px solid #3a3a3a;
                  border-radius: 4px;
                  padding: 6px 8px;
                  font-family: inherit;
                  font-size: 14px;
                  line-height: 1.35;
                  outline: none;
                }
                #stage-input:focus { border-color: #6a78d8; }
                #stage-hint { font-size: 12px; color: #555; padding: 0 10px 6px; }
                #stage-bulk-toggle {
                  cursor: pointer; color: #8aa0ff; font-size: 14px; user-select: none;
                }
                #stage-bulk-toggle:hover { color: #aab8ff; text-decoration: underline; }
                #stage-bulk-row {
                  display: none; flex-direction: column; gap: 6px; padding: 0 10px 8px;
                }
                #stage-bulk-row.open { display: flex; }
                #stage-bulk-input {
                  resize: vertical; min-height: 84px; max-height: 220px;
                  background: #0c0c0c; color: #eee; border: 1px solid #3a3a3a;
                  border-radius: 4px; padding: 6px 8px;
                  font-family: inherit; font-size: 13px; line-height: 1.35; outline: none;
                }
                #stage-bulk-input:focus { border-color: #6a78d8; }
                #stage-bulk-actions { display: flex; gap: 8px; }
                #stage-bulk-actions button {
                  font-family: inherit; font-size: 12px; padding: 4px 14px;
                  border-radius: 4px; cursor: pointer;
                  border: 1px solid #444; background: #1e1e1e; color: #ddd;
                }
                #stage-bulk-actions button:hover { filter: brightness(1.25); }
                #stage-bulk-import {
                  background: #3a4a8a; border-color: #5a6ac0; color: #fff;
                }
              </style>
              <link rel="stylesheet" href="https://xterm.local/xterm.css"/>
            </head>
            <body>
              <div id="terminal"></div>
              <div id="stage">
                <div id="stage-head">
                  <span class="dot" id="stage-dot"></span>
                  <span id="stage-status">等待中 · 进入等待自动发送</span>
                  <span class="spacer"></span>
                  <span id="stage-bulk-toggle">＋ 批量导入</span>
                  <span id="stage-count">0 条待发</span>
                </div>
                <ul id="stage-list"></ul>
                <div id="stage-empty">队列为空 —— 在下面输入,回车逐条寄存</div>
                <div id="stage-input-row">
                  <textarea id="stage-input" rows="1" placeholder="输入命令,回车寄存(Shift+Enter 换行)"></textarea>
                </div>
                <div id="stage-bulk-row">
                  <textarea id="stage-bulk-input" placeholder="批量导入:每行一条命令,空行忽略。粘贴一整段后点导入。"></textarea>
                  <div id="stage-bulk-actions">
                    <button id="stage-bulk-import">导入队列</button>
                    <button id="stage-bulk-cancel">取消</button>
                  </div>
                </div>
                <div id="stage-hint">寄存模式:输入排进队列,会话空闲时自动逐条发出。Alt+V 贴图(存为文件、路径入队),Alt+` 退出寄存。</div>
              </div>
              <script src="https://xterm.local/xterm.js"></script>
              <script src="https://xterm.local/xterm-addon-fit.js"></script>
              <script>
                const term = new Terminal({
                  fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
                  fontSize: 14,
                  lineHeight: 1.2,
                  theme: { background: '#0c0c0c' },
                  cursorBlink: true,
                  allowProposedApi: true
                });

                const fit = new FitAddon.FitAddon();
                term.loadAddon(fit);
                term.open(document.getElementById('terminal'));
                fit.fit();

                /* ── Theme-aware styling (dark-only: CJK font, bigger size, dimmed text).
                      Light mode falls back to the original Cascadia/14/no-dim look. ── */
                const FONT_DARK = "'Cascadia Code', 'Microsoft YaHei', 'Cascadia Mono', Consolas, monospace";
                const FONT_LIGHT = "'Cascadia Code', 'Cascadia Mono', Consolas, monospace";
                let FONT_DEFAULT = 14; // theme base size; updated by applyCcTheme
                function applyCcTheme(dark) {
                  term.options.fontFamily = dark ? FONT_DARK : FONT_LIGHT;
                  term.options.lineHeight = dark ? 1.25 : 1.2;
                  FONT_DEFAULT = dark ? 16 : 14;
                  term.options.fontSize = FONT_DEFAULT;
                  const el = document.getElementById('terminal');
                  if (el) el.style.filter = dark ? 'brightness(0.8)' : '';
                  fit.fit();
                }
                applyCcTheme(__INITIAL_DARK__);

                /* ── Ctrl+wheel font-size zoom (replaces WebView2 native page zoom) ── */
                const FONT_MIN = 8, FONT_MAX = 40;
                function setFontSize(s) {
                  s = Math.min(FONT_MAX, Math.max(FONT_MIN, Math.round(s)));
                  if (s === term.options.fontSize) return;
                  term.options.fontSize = s;
                  fit.fit();
                }
                document.addEventListener('wheel', e => {
                  if (!e.ctrlKey) return;
                  e.preventDefault();
                  setFontSize(term.options.fontSize + (e.deltaY < 0 ? 1 : -1));
                }, { passive: false });

                /* ── Command staging (命令寄存) ── */
                let stagingOn = false;
                let queue = [];
                let lastStatus = 'waiting';   // resting state of a fresh CLI
                let flushTimer = null;
                let stageWatchdog = null;     // re-polls real status so a missed
                                              // working→waiting transition can't
                                              // permanently stall the queue.
                const stagePanel = document.getElementById('stage');
                const stageList = document.getElementById('stage-list');
                const stageInput = document.getElementById('stage-input');
                const stageEmpty = document.getElementById('stage-empty');
                const stageCount = document.getElementById('stage-count');
                const stageDot = document.getElementById('stage-dot');
                const stageStatus = document.getElementById('stage-status');

                function statusLabel(s) {
                  if (s === 'working') return '工作中 · 等它空闲';
                  if (s === 'disconnected') return '已断开 · 暂停发送';
                  return '等待中 · 进入等待自动发送';
                }
                function renderStatus() {
                  stageDot.className = 'dot' + (lastStatus === 'working' ? ' working'
                    : lastStatus === 'disconnected' ? ' disconnected' : '');
                  stageStatus.textContent = statusLabel(lastStatus);
                }
                function renderQueue() {
                  stageList.innerHTML = '';
                  queue.forEach((cmd, i) => {
                    const li = document.createElement('li');
                    const idx = document.createElement('span');
                    idx.className = 'idx'; idx.textContent = (i + 1) + '.';
                    const txt = document.createElement('span');
                    txt.className = 'txt'; txt.textContent = cmd.replace(/\n/g, ' ⏎ ');
                    const del = document.createElement('span');
                    del.className = 'del'; del.textContent = '✕'; del.title = '删除这条';
                    del.addEventListener('click', () => { queue.splice(i, 1); renderQueue(); });
                    li.appendChild(idx); li.appendChild(txt); li.appendChild(del);
                    stageList.appendChild(li);
                  });
                  stageEmpty.style.display = queue.length ? 'none' : 'block';
                  stageCount.textContent = queue.length + ' 条待发';
                }
                function autoSize() {
                  stageInput.style.height = 'auto';
                  stageInput.style.height = Math.min(96, stageInput.scrollHeight) + 'px';
                }
                // Drop text at the caret in the staging box (used by Alt+V image paste).
                function insertAtCursor(el, text) {
                  const s = el.selectionStart ?? el.value.length;
                  const e = el.selectionEnd ?? el.value.length;
                  const ins = text + ' ';   // trailing space so the next word isn't glued on
                  el.value = el.value.slice(0, s) + ins + el.value.slice(e);
                  const pos = s + ins.length;
                  el.selectionStart = el.selectionEnd = pos;
                  el.focus();
                  autoSize();
                }
                // Briefly replace the hint line with a transient message (e.g. paste errors).
                const stageHint = document.getElementById('stage-hint');
                const stageHintDefault = stageHint.textContent;
                let hintTimer = null;
                function flashHint(text) {
                  stageHint.textContent = text;
                  stageHint.style.color = '#e07a5f';
                  if (hintTimer) clearTimeout(hintTimer);
                  hintTimer = setTimeout(() => {
                    stageHint.textContent = stageHintDefault;
                    stageHint.style.color = '';
                  }, 2200);
                }
                // Inject one staged command. The text and the submitting Enter are
                // sent as TWO separate writes with a gap: writing "text\r" in one
                // shot makes the CLI's TUI treat a long line as a paste and swallow
                // the trailing \r as a literal newline — the command then just sits
                // unsent in the prompt box. A standalone \r after the text lands is
                // seen as a real Enter keypress and submits reliably.
                function sendCmd(cmd) {
                  const post = d => window.chrome.webview.postMessage(
                    JSON.stringify({ type: 'input', data: d }));
                  post(cmd);
                  setTimeout(() => post('\r'), 180);
                }
                // While items are queued, keep asking the host for its REAL status.
                // The host only pushes paneStatus on a *change*, so a working→waiting
                // transition that races the optimistic flip (or never re-fires) would
                // otherwise leave the queue stuck. The poll re-asserts truth and
                // self-stops once the queue drains.
                function ensureWatchdog() {
                  if (stageWatchdog || !stagingOn) return;
                  stageWatchdog = setInterval(() => {
                    if (!stagingOn || queue.length === 0) {
                      clearInterval(stageWatchdog); stageWatchdog = null; return;
                    }
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'requestStatus' }));
                  }, 1500);
                }
                function maybeFlush() {
                  if (!stagingOn || queue.length === 0) return;
                  ensureWatchdog();
                  if (lastStatus !== 'waiting') return;
                  if (flushTimer) return;
                  // Small settle delay so the CLI prompt is ready to receive input.
                  flushTimer = setTimeout(() => {
                    flushTimer = null;
                    if (!stagingOn || queue.length === 0 || lastStatus !== 'waiting') return;
                    const cmd = queue.shift();
                    renderQueue();
                    // Optimistically mark working so we don't double-send before the
                    // host echoes the real status back.
                    lastStatus = 'working';
                    renderStatus();
                    sendCmd(cmd);
                  }, 450);
                }

                function applyStaging(on) {
                  stagingOn = on;
                  stagePanel.classList.toggle('open', stagingOn);
                  fit.fit();
                  // Move the caret to the right box (staging input when on, terminal
                  // when off). Reassert on the next frame so a same-frame host focus
                  // or the panel's display flip can't leave focus on the wrong element.
                  const focusTarget = () => { if (stagingOn) stageInput.focus(); else term.focus(); };
                  focusTarget();
                  requestAnimationFrame(focusTarget);
                  if (stagingOn) maybeFlush();
                }
                // Alt+` toggles staging from anywhere (terminal, staging box, bulk box).
                // Capture phase so it beats both xterm and the textareas and never
                // reaches the CLI. e.code is layout-independent for the backtick key.
                document.addEventListener('keydown', e => {
                  if (e.altKey && !e.ctrlKey && !e.shiftKey && !e.metaKey &&
                      (e.code === 'Backquote' || e.key === '`')) {
                    e.preventDefault();
                    e.stopPropagation();
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'toggleStaging' }));
                  }
                }, true);

                stageInput.addEventListener('input', autoSize);
                stageInput.addEventListener('keydown', e => {
                  // Alt+V: ask the host to drop a clipboard image into temp and queue
                  // its path (the CLI's native Alt+V can't see this textarea's focus).
                  if (e.altKey && !e.ctrlKey && !e.shiftKey && (e.key === 'v' || e.key === 'V')) {
                    e.preventDefault();
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'stageImagePaste' }));
                    return;
                  }
                  // Alt+A: toggle select-all of the current command. First press selects
                  // everything (for fast delete/copy); pressing it again cancels the
                  // selection and drops the caret at the end.
                  if (e.altKey && !e.ctrlKey && !e.shiftKey && (e.key === 'a' || e.key === 'A')) {
                    e.preventDefault();
                    const len = stageInput.value.length;
                    const allSelected = len > 0 &&
                      stageInput.selectionStart === 0 && stageInput.selectionEnd === len;
                    if (allSelected) {
                      stageInput.selectionStart = stageInput.selectionEnd = len; // deselect
                    } else {
                      stageInput.select();
                    }
                    return;
                  }
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    const v = stageInput.value.replace(/\s+$/, '');
                    if (v.length === 0) return;
                    queue.push(v);
                    stageInput.value = '';
                    autoSize();
                    renderQueue();
                    maybeFlush();   // flush now if the CLI is already idle
                  }
                });
                /* ── Batch import: one line = one entry, blank lines ignored ── */
                const bulkToggle = document.getElementById('stage-bulk-toggle');
                const bulkRow = document.getElementById('stage-bulk-row');
                const bulkInput = document.getElementById('stage-bulk-input');
                const bulkImport = document.getElementById('stage-bulk-import');
                const bulkCancel = document.getElementById('stage-bulk-cancel');
                function closeBulk() { bulkRow.classList.remove('open'); bulkInput.value = ''; fit.fit(); }
                bulkToggle.addEventListener('click', () => {
                  const open = bulkRow.classList.toggle('open');
                  fit.fit();
                  if (open) bulkInput.focus();
                });
                bulkCancel.addEventListener('click', () => { closeBulk(); stageInput.focus(); });
                bulkImport.addEventListener('click', () => {
                  const lines = bulkInput.value
                    .split(/\r?\n/)
                    .map(s => s.replace(/\s+$/, ''))
                    .filter(s => s.length > 0);
                  lines.forEach(l => queue.push(l));
                  if (lines.length) renderQueue();
                  closeBulk();
                  maybeFlush();
                });

                renderQueue();
                renderStatus();

                // True only while the CLI has dropped to cmd and a resume command
                // is available; set by the 'shellMode' message from the host.
                let shellResumeAvailable = false;

                term.attachCustomKeyEventHandler(e => {
                  if (e.type !== 'keydown') return true;
                  // ↑ (regular arrow or numpad-8 NumLock-off, both report
                  // key === 'ArrowUp') in the dropped-to-cmd state → resume the
                  // conversation. One-shot: the host disables this right after, so
                  // once the CLI relaunches ↑ goes back to it normally.
                  if (shellResumeAvailable && e.key === 'ArrowUp') {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'resumeHotkey' }));
                    return false;
                  }
                  if (e.ctrlKey && !e.shiftKey && e.key === 'c') {
                    const sel = term.getSelection();
                    if (sel) {
                      window.chrome.webview.postMessage(JSON.stringify({ type: 'copy', data: sel }));
                      term.clearSelection();
                      return false;
                    }
                    return true;
                  }
                  if (e.ctrlKey && !e.shiftKey && e.key === 'v') {
                    return false;
                  }
                  if (e.ctrlKey && !e.shiftKey && e.key === 't') {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'newTab' }));
                    return false;
                  }
                  if (e.ctrlKey && !e.shiftKey && e.key === 'w') {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'closeTab' }));
                    return false;
                  }
                  if (e.altKey && e.shiftKey && e.key === '-') {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'splitHorizontal' }));
                    return false;
                  }
                  if (e.altKey && e.shiftKey && (e.key === '=' || e.key === '+')) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'splitVertical' }));
                    return false;
                  }
                  // Alt+A: clear the current CLI input line (send Ctrl+U). xterm can't
                  // truly "select the current command" — that text belongs to the CLI,
                  // not the terminal grid — so this serves the quick-delete intent.
                  if (e.altKey && !e.shiftKey && !e.ctrlKey && (e.key === 'a' || e.key === 'A')) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'input', data: '\u0015' }));
                    return false;
                  }
                  // Alt+Shift+A: select the whole terminal screen so Ctrl+C can copy it —
                  // the closest "copy" analog the terminal can offer.
                  if (e.altKey && e.shiftKey && !e.ctrlKey && (e.key === 'a' || e.key === 'A')) {
                    term.selectAll();
                    return false;
                  }
                  if (e.altKey && !e.shiftKey && e.key.startsWith('Arrow')) {
                    const dir = e.key.replace('Arrow', '').toLowerCase();
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'navigate', direction: dir }));
                    return false;
                  }
                  if (e.ctrlKey && e.shiftKey && e.key === 'W') {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'closePane' }));
                    return false;
                  }
                  if (e.ctrlKey && (e.key === '=' || e.key === '+')) {
                    setFontSize(term.options.fontSize + 1);
                    return false;
                  }
                  if (e.ctrlKey && e.key === '-') {
                    setFontSize(term.options.fontSize - 1);
                    return false;
                  }
                  if (e.ctrlKey && e.key === '0') {
                    setFontSize(FONT_DEFAULT);
                    return false;
                  }
                  return true;
                });

                document.addEventListener('contextmenu', e => {
                  e.preventDefault();
                  window.chrome.webview.postMessage(JSON.stringify({
                    type: 'contextMenu', x: e.screenX, y: e.screenY
                  }));
                });

                term.focus();
                window.chrome.webview.postMessage(JSON.stringify({
                  type: 'ready', cols: term.cols, rows: term.rows
                }));

                term.onData(data => {
                  window.chrome.webview.postMessage(JSON.stringify({ type: 'input', data }));
                });

                term.onResize(({ cols, rows }) => {
                  window.chrome.webview.postMessage(JSON.stringify({ type: 'resize', cols, rows }));
                });

                window.chrome.webview.addEventListener('message', e => {
                  const msg = JSON.parse(e.data);
                  if (msg.type === 'output') {
                    const bin = atob(msg.data);
                    const bytes = new Uint8Array(bin.length);
                    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
                    term.write(bytes);
                  } else if (msg.type === 'shellMode') {
                    shellResumeAvailable = !!msg.resume;
                  } else if (msg.type === 'paneStatus') {
                    lastStatus = msg.status;
                    renderStatus();
                    if (lastStatus === 'waiting') maybeFlush();
                  } else if (msg.type === 'setStaging') {
                    applyStaging(!!msg.on);
                  } else if (msg.type === 'stageImagePasted') {
                    if (msg.path) insertAtCursor(stageInput, msg.path);
                    else flashHint(msg.error || '没有图片');
                  } else if (msg.type === 'theme') {
                    applyCcTheme(!!msg.dark);
                  }
                });

                new ResizeObserver(() => fit.fit()).observe(document.getElementById('terminal'));
              </script>
            </body>
            </html>
            """;
    }
}
