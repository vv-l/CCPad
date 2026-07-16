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
        /// <summary>True once the xterm page has reported in — the pane can be
        /// shown without flashing an uninitialized (white) WebView2.</summary>
        public bool IsReady => _ready;
        private bool _coreWired;   // one-time CoreWebView2 event/settings wiring done
        private bool _sessionPending;
        // CLI output that arrived before xterm reported ready (the session is now
        // started in parallel with the page load); replayed on "ready".
        private readonly object _pendingLock = new();
        private System.IO.MemoryStream? _pendingOutput;
        private const int PendingOutputCap = 4 * 1024 * 1024;
        // Actual spawn time of the current CLI process (StartSession), as opposed
        // to LaunchedAtUtc which is when the pane was told to launch.
        private DateTime _cliStartedUtc;
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

        /// <summary>User-defined tag shown as a badge next to the tab title.
        /// Empty = no tag. Persisted with the workspace snapshot.</summary>
        public string TabTag { get; set; } = "";

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

        // Symmetric self-correction for a stale GREEN light: green→amber normally
        // rides on the CLI's Stop hook, but that callback can be lost (hookless
        // resume, notify request dropped, hook file rejected) or undone by the
        // amber→green persistence heuristic above firing on a long turn-end
        // burst. Either way the pane reports Working forever and the staging
        // queue never flushes. The tell for "actually idle" is the mirror of the
        // persistence signal: a working CLI repaints its spinner/elapsed timer at
        // least once a second, so sustained SILENCE while we show Working means
        // the turn is over. Checked by a 1s timer (silence produces no output
        // events, so an event-driven check could never see it). Guards:
        //  • >6s since the last user keystroke, so someone idling mid-composition
        //    at a stale-green prompt isn't interrupted by a queue flush;
        //  • normally requires ≥1 output chunk since the green flip and >6s of
        //    silence after it (≈six missed spinner frames);
        //  • if NOTHING has painted at all since the flip (optimistic Enter on an
        //    empty prompt, dead-quiet CLI), fall back to a longer 15s window.
        // Real hook events keep overriding this — it only runs while Working.
        private DateTime _lastAnyOutputUtc = DateTime.MinValue; // last output in ANY status
        private DateTime _workingSinceUtc = DateTime.MinValue;  // when the light last turned green
        private bool _workingOutputSeen;                        // any output since the green flip
        private Timer? _workingWatchTimer;

        // The live CLI printed a fatal API error (e.g. "API Error: 529 Overloaded",
        // 402 billing, 403 auth). The process is still alive at its prompt, so no
        // exit fires and the hooks would show amber ("your turn") — but it's really
        // an error state, so we force the red light and keep it red across the
        // turn-ending Stop hook until the user retries (submits input) or the
        // session restarts.
        private bool _apiErrored;
        private string _recentErrScan = "";

        // While dropped to the fallback shell, watch for the user relaunching the
        // CLI by hand ("claude --resume ..." typed at the cmd prompt WITHOUT our
        // --settings hook file — only the ↑-injected command carries it). A
        // hookless CLI never posts status callbacks, so nothing would ever clear
        // the exit-red light. Two independent signals hand the light back to the
        // heuristics: the typed command line itself (TrackShellInput) and the
        // CLI's startup banner in the output stream (CheckShellCliBanner).
        private bool _shellCliActive;
        private string _shellLineBuf = "";
        private string _recentBannerScan = "";

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
            else if (status == PaneStatus.Working)
            {
                // Arm the stale-green watch: silence from here on means idle.
                _workingSinceUtc = DateTime.UtcNow;
                _workingOutputSeen = false;
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
        private void OnCliNotify(string evt, string? cliSessionId)
        {
            // Claude hooks report the CLI's real conversation UUID with every event.
            // Track it live so snapshots survive /clear, an in-CLI /resume, or a
            // claude relaunched from the fallback shell — the --session-id assigned
            // at launch goes stale in all of those, and a stale ID makes the next
            // restore silently start a fresh conversation.
            if (!string.IsNullOrEmpty(cliSessionId) &&
                !string.Equals(cliSessionId, SessionId, StringComparison.OrdinalIgnoreCase))
                SessionId = cliSessionId;

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

        /// <summary>Conversation ID for this pane, when known. Claude: always set (we pass
        /// --session-id / --resume). Codex: set only when restored via resume; fresh Codex
        /// sessions are harvested from disk at snapshot time instead.</summary>
        public string? SessionId { get; set; }

        /// <summary>When the CLI process was started — used to scope the Codex
        /// session-file scan to files this pane could have produced.</summary>
        public DateTime LaunchedAtUtc { get; private set; }

        /// <summary>When a CLI was brought back to life inside the fallback shell
        /// (possibly hand-typed, without our hooks), or null if that never happened.
        /// Such a CLI can own a conversation the pane isn't tracking, so it re-opens
        /// the disk-scan fallback in ResolveSessionId — scoped to files written
        /// since this moment, to keep unrelated same-cwd CLIs (bots, plain
        /// terminals) from being mistaken for ours.</summary>
        public DateTime? ShellRelaunchUtc { get; private set; }

        /// <summary>Most recent sign of life on this pane (launch, user keystroke,
        /// or any pty output — a working CLI repaints its spinner every second, so
        /// an old reading means the CLI really is idle at its prompt). Used by the
        /// auto-freeze idle scan.</summary>
        public DateTime LastActivityUtc
        {
            get
            {
                var t = LaunchedAtUtc;
                if (_lastUserInputUtc > t) t = _lastUserInputUtc;
                if (_lastAnyOutputUtc > t) t = _lastAnyOutputUtc;
                return t;
            }
        }

        /// <summary>
        /// Screenshot of the live terminal page, for the frozen-tab placeholder.
        /// Must be called BEFORE the pane is disposed. Null on any failure (e.g.
        /// a dead renderer) — the placeholder then falls back to a plain card.
        /// </summary>
        public async Task<Microsoft.UI.Xaml.Media.ImageSource?> CaptureSnapshotAsync()
        {
            if (_disposed || WebView.CoreWebView2 == null) return null;
            try
            {
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                // Bounded: CapturePreviewAsync on a sick renderer can hang forever
                // (it doesn't throw), which used to strand the whole freeze with
                // the Busy flag stuck. No screenshot beats no freeze.
                var capture = WebView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, stream).AsTask();
                if (await Task.WhenAny(capture, Task.Delay(1500)) != capture)
                    return null;
                await capture;
                stream.Seek(0);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(stream);
                return bmp;
            }
            catch { return null; }
        }

        public TerminalPane()
        {
            InitializeComponent();
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 12, 12, 12);
            CCPad.Settings.ThemeManager.EffectiveChanged += OnThemeEffectiveChanged;
            CCPad.Settings.LastCmdBarManager.Changed += OnLastCmdBarChanged;
            // Stale-green watchdog. Always ticking (created once here so no
            // create/dispose race with SetStatus); the callback no-ops unless
            // the light is green. See the _workingWatchTimer field comment.
            _workingWatchTimer = new Timer(CheckStaleWorking, null, 1000, 1000);
        }

        // Push the live theme into the xterm front-end (dark-only font/dim styling).
        private void OnThemeEffectiveChanged(bool dark) => SendTheme(dark);

        // Global last-command-bar toggle flipped (toolbar button or Alt+L in any pane).
        private void OnLastCmdBarChanged(bool on)
        {
            if (_disposed) return;
            string json = $"{{\"type\":\"setLastCmdBar\",\"on\":{(on ? "true" : "false")}}}";
            DispatcherQueue.TryEnqueue(() => WebView.CoreWebView2?.PostWebMessageAsString(json));
        }

        private void SendTheme(bool dark)
        {
            if (_disposed) return;
            string json = $"{{\"type\":\"theme\",\"dark\":{(dark ? "true" : "false")}}}";
            DispatcherQueue.TryEnqueue(() => WebView.CoreWebView2?.PostWebMessageAsString(json));
        }

        /// <summary>
        /// Phase 1: Initialize WebView2 and load xterm.js. No process is started.
        /// Can be called while the pane sits in a hidden container. Safe to call
        /// again after a failure (retry button) — core wiring happens only once.
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

            // Bounded: EnsureCoreWebView2Async occasionally never completes when
            // the runtime is contended/sick — an unbounded await here is a silent
            // permanent white pane that not even the ready timeout can catch.
            var ensure = WebView.EnsureCoreWebView2Async().AsTask();
            if (await Task.WhenAny(ensure, Task.Delay(15000)) != ensure)
                throw new InvalidOperationException("WebView2 初始化超时（15 秒）。");
            await ensure;
            if (_disposed) throw new ObjectDisposedException(nameof(TerminalPane));

            if (WebView.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 failed to initialize.");

            if (!_coreWired)
            {
                _coreWired = true;
                WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                // Disable WebView2's built-in page zoom (Ctrl+wheel / Ctrl +-0). Its
                // ZoomFactor is a persistent, accumulating property capped at 5x, so a
                // long-lived pane drifts to the ceiling and "can't zoom in any more".
                // We handle Ctrl+wheel ourselves as terminal font-size zoom instead.
                WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
                // Renderer death (Chromium reclaims hidden/background renderers under
                // memory pressure) is only ever reported through this event — without
                // it the pane just turns permanently white.
                WebView.CoreWebView2.ProcessFailed += OnProcessFailed;
                WebView.GotFocus += (_, _) => PaneFocused?.Invoke();

                string xtermFolder = GetXtermFolder();
                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "xterm.local", xtermFolder,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            NavigatePage();
            await WaitForReadyAsync();
        }

        /// <summary>(Re)load the terminal page. Resets the ready gate first, so a
        /// pending StartSession fires again once the fresh page reports in.</summary>
        private void NavigatePage()
        {
            _ready = false;
            _readyTcs = new TaskCompletionSource();
            WebView.CoreWebView2!.NavigateToString(
                TerminalHtml
                    .Replace("Auto Confirm (自动回车)", Localization.Loc.T("autoconfirm_title"))
                    .Replace("__INITIAL_DARK__", CCPad.Settings.ThemeManager.IsDark ? "true" : "false")
                    .Replace("__INITIAL_LASTCMD__", CCPad.Settings.LastCmdBarManager.IsOn ? "true" : "false"));
        }

        /// <summary>Await xterm's "ready" callback, bounded — an unbounded wait is
        /// how a failed page load used to become a silent, permanent white pane.</summary>
        private async Task WaitForReadyAsync()
        {
            var readyTask = _readyTcs!.Task;
            var winner = await Task.WhenAny(readyTask, Task.Delay(ReadyTimeoutMs));
            if (winner != readyTask)
                throw new TimeoutException($"终端页面在 {ReadyTimeoutMs / 1000} 秒内未就绪。");
            await readyTask;
        }

        private const int ReadyTimeoutMs = 10000;

        /// <summary>
        /// Liveness probe: does the page's renderer still answer script execution?
        /// A prewarmed pane can sit hidden for hours and have its renderer reclaimed
        /// by Chromium; handing it out unprobed mounts a dead (white) terminal.
        /// </summary>
        public async Task<bool> PingAsync(int timeoutMs = 1000)
        {
            if (_disposed || !_ready || WebView.CoreWebView2 == null) return false;
            try
            {
                var probe = WebView.CoreWebView2.ExecuteScriptAsync("1").AsTask();
                var winner = await Task.WhenAny(probe, Task.Delay(timeoutMs));
                return winner == probe && probe.Status == TaskStatus.RanToCompletion;
            }
            catch { return false; }
        }

        private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (_disposed) return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_disposed) return;
                switch (args.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                    case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                        // The renderer died but the ConPty session (and the CLI in
                        // it) is untouched — reload the page and hand it back.
                        try
                        {
                            NavigatePage();
                            await WaitForReadyAsync();
                            OnPageRecovered();
                        }
                        catch
                        {
                            if (!_disposed)
                                ShowErrorOverlay($"渲染进程崩溃（{args.ProcessFailedKind}），自动恢复失败。会话进程仍在运行，点击重试重新加载页面。");
                        }
                        break;

                    default:
                        // Browser-process-level failure — reload alone can't fix it;
                        // let the retry button drive a full re-init attempt.
                        ShowErrorOverlay($"WebView2 进程异常（{args.ProcessFailedKind}）。会话进程仍在运行，点击重试重新初始化。");
                        break;
                }
            });
        }

        /// <summary>Re-sync pane state onto the freshly reloaded page after a renderer
        /// crash: scrollback is gone, but the pty and the CLI in it are still alive.</summary>
        private void OnPageRecovered()
        {
            if (_disposed || _sessionPending) return;
            if (_session != null)
            {
                SendOutput(Encoding.UTF8.GetBytes(
                    "\r\n\x1b[33m[终端页面已自动恢复；之前的滚动内容丢失，会话仍在运行]\x1b[0m\r\n"));
                // Wiggle the pty size so full-screen CLIs repaint into the blank page.
                _session.Resize(Math.Max(2, _cols - 1), _rows);
                _session.Resize(_cols, _rows);
            }
            SendPaneStatus(_status);
            SendShellMode(_inShell && _resumeCommand != null);
            if (StagingOn) SetStaging(true);
        }

        private void ShowErrorOverlay(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                ErrorDetail.Text = message;
                ErrorOverlay.Visibility = Visibility.Visible;
            });
        }

        private async void OnRetryClick(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;
            ErrorOverlay.Visibility = Visibility.Collapsed;
            try
            {
                await PrewarmAsync();
                OnPageRecovered();
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    ShowErrorOverlay("重试失败：" + ex.Message);
            }
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
            LaunchedAtUtc = DateTime.UtcNow;
            _sessionPending = true;
            _focusOnFirstOutput = focusOnReady;
            if (_ready)
                StartSession();
        }

        /// <summary>
        /// Combined init for non-prewarmed path (first tab). Failures surface as an
        /// in-pane error overlay with a retry button instead of propagating up into
        /// async-void event handlers (or, before this, a silent white pane).
        /// </summary>
        public async Task InitializeAsync(string command, string? workingDir = null, bool focusOnReady = false, string? cliMode = null)
        {
            _command = command;
            _workingDir = workingDir;
            CliMode = cliMode;
            LaunchedAtUtc = DateTime.UtcNow;
            _sessionPending = true;
            _focusOnFirstOutput = focusOnReady;
            // Spawn the CLI in parallel with the WebView2/xterm bring-up instead of
            // after it — early output is buffered (SendOutput) and replayed when the
            // page reports ready. A cold start/thaw then costs max(page, CLI)
            // instead of page + CLI.
            StartSession();
            try
            {
                await PrewarmAsync();
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                ShowErrorOverlay("终端初始化失败：" + ex.Message);
            }
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
            _shellCliActive = false;
            _shellLineBuf = "";
            _recentBannerScan = "";
            _resumeCommand = null;
            ShellRelaunchUtc = null;
            SendShellMode(false);
            try
            {
                _cliStartedUtc = DateTime.UtcNow;
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
            _lastAnyOutputUtc = DateTime.UtcNow;
            if (_status == PaneStatus.Working)
                _workingOutputSeen = true;
            SendOutput(data);
            if (_focusOnFirstOutput)
            {
                _focusOnFirstOutput = false;
                DispatcherQueue.TryEnqueue(FocusTerminal);
            }
            CheckShellCliBanner(data);
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
            if ((_inShell && !_shellCliActive) || _apiErrored) return;
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

        // While at the fallback shell with no CLI detected yet, watch the output
        // stream for Claude's startup banner ("Claude Code v2.1.198"). This
        // catches relaunches the input watcher can't see (doskey history recall,
        // a .bat wrapper). Same rolling-buffer trick as CheckApiError. Codex has
        // no stable banner marker; its relaunch is caught by TrackShellInput only.
        private void CheckShellCliBanner(byte[] data)
        {
            if (!_inShell || _shellCliActive) return;
            _recentBannerScan += Encoding.UTF8.GetString(data);
            if (_recentBannerScan.Length > 1024)
                _recentBannerScan = _recentBannerScan[^1024..];
            if (_recentBannerScan.ToLowerInvariant().Contains("claude code v"))
            {
                _recentBannerScan = "";
                OnShellCliRelaunched();
            }
        }

        // Keystroke-level watcher for the fallback shell: reconstruct the command
        // line being typed so the Enter that runs "claude ..." / "codex ..." is
        // recognized even though the relaunched CLI is a grandchild of the pty
        // (invisible to our process-exit tracking). Best-effort: backspace is
        // honored; escape sequences (history recall, cursor moves) abandon the
        // line and leave detection to the banner watcher.
        private void TrackShellInput(string data)
        {
            foreach (char c in data)
            {
                if (c == '\r' || c == '\n')
                {
                    string line = _shellLineBuf.Trim();
                    _shellLineBuf = "";
                    if (line.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
                        OnShellCliRelaunched();
                }
                else if (c == '\b' || c == '\x7f')
                {
                    if (_shellLineBuf.Length > 0)
                        _shellLineBuf = _shellLineBuf[..^1];
                }
                else if (c == '\x1b')
                {
                    _shellLineBuf = "";
                    return;
                }
                else if (!char.IsControl(c) && _shellLineBuf.Length < 512)
                {
                    _shellLineBuf += c;
                }
            }
        }

        // The user brought a CLI back to life inside the fallback shell. It may
        // run WITHOUT our --settings hook file (hand-typed resume), so hooks may
        // never fire for this pane again: clear the exit-red lock and hand the
        // light back to the heuristics (Enter → green, sustained output → green)
        // with the amber resting state as the starting point.
        private void OnShellCliRelaunched()
        {
            _shellCliActive = true;
            _apiErrored = false;
            _recentErrScan = "";
            // May be running untracked (hand-typed, no hooks) — remember when, so
            // the snapshot fallback can scan for a conversation it started.
            ShellRelaunchUtc = DateTime.UtcNow;
            // The ↑ resume offer is stale now — a later ↑ must reach the CLI
            // (prompt history), not paste a resume command into its input box.
            _resumeCommand = null;
            SendShellMode(false);
            SetStatus(PaneStatus.Waiting);
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

        // The mirror correction: showing Working (green) but the CLI has gone
        // SILENT for a sustained window → the turn actually ended and the Stop
        // signal was lost, so recover the amber light (which also lets the
        // staging queue flush). See the _workingWatchTimer field comment for the
        // full rationale and guards. Runs on a 1s timer, off the UI thread.
        private const double WorkingSilenceFlipMs = 6000;
        private const double WorkingNoPaintFlipMs = 15000;
        private void CheckStaleWorking(object? _)
        {
            if (_disposed || _status != PaneStatus.Working) return;
            var now = DateTime.UtcNow;
            if (_lastUserInputUtc != DateTime.MinValue &&
                (now - _lastUserInputUtc).TotalMilliseconds < WorkingSilenceFlipMs) return;
            double silentMs = _workingOutputSeen
                ? (now - _lastAnyOutputUtc).TotalMilliseconds
                : (now - _workingSinceUtc).TotalMilliseconds;
            if (silentMs >= (_workingOutputSeen ? WorkingSilenceFlipMs : WorkingNoPaintFlipMs))
                SetStatus(PaneStatus.Waiting);
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
                _shellCliActive = false;
                _shellLineBuf = "";
                _recentBannerScan = "";
                SetStatus(PaneStatus.Disconnected);
                // A CLI that dies within seconds of its spawn almost certainly
                // failed to start (resume conflict, bad session, missing binary
                // deeper down) — say so in red instead of letting the drop to cmd
                // read as "still restoring".
                var aliveSecs = (int)(DateTime.UtcNow - _cliStartedUtc).TotalSeconds;
                string quickExitWarn = aliveSecs < 20
                    ? "\r\n\x1b[31m[" + Localization.Loc.T("cli_exit_fast", aliveSecs) + "]\x1b[0m\r\n"
                    : "";
                // BuildExitBanner() also sets _resumeCommand as a side effect.
                SendOutput(Encoding.UTF8.GetBytes(quickExitWarn + BuildExitBanner()));
                SendShellMode(_resumeCommand != null);
                _session?.SpawnProcess(ShellCommand, _workingDir);
            }
            else
            {
                // The fallback shell exited too — offer to relaunch the CLI.
                _inShell = false;
                _shellCliActive = false;
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
            // Page not up yet (parallel CLI start, or a reload after a renderer
            // crash) — buffer; the "ready" handler replays the backlog in order.
            if (!_ready)
            {
                lock (_pendingLock)
                {
                    if (!_ready)
                    {
                        _pendingOutput ??= new System.IO.MemoryStream();
                        if (_pendingOutput.Length < PendingOutputCap)
                            _pendingOutput.Write(data, 0, data.Length);
                        return;
                    }
                }
            }
            string b64 = Convert.ToBase64String(data);
            string json = $"{{\"type\":\"output\",\"data\":\"{b64}\"}}";
            DispatcherQueue.TryEnqueue(() => WebView.CoreWebView2?.PostWebMessageAsString(json));
        }

        /// <summary>Print a yellow host-side notice line into the terminal (e.g.
        /// "saved conversation not found"). Buffered along with CLI output when
        /// the page isn't ready yet, so it survives a cold thaw.</summary>
        public void ShowNotice(string text)
            => SendOutput(Encoding.UTF8.GetBytes("\r\n\x1b[33m[" + text + "]\x1b[0m\r\n"));

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
                        byte[]? backlog = null;
                        lock (_pendingLock)
                        {
                            _ready = true;
                            if (_pendingOutput is { Length: > 0 })
                                backlog = _pendingOutput.ToArray();
                            _pendingOutput = null;
                        }
                        _readyTcs?.TrySetResult();
                        if (_sessionPending)
                        {
                            StartSession();
                        }
                        else if (_session != null)
                        {
                            // The CLI outlived (or preceded) this page load: replay
                            // what it printed while the page was coming up, re-sync
                            // the status light, and wiggle the pty size so a
                            // full-screen CLI repaints cleanly at the real
                            // dimensions (a parallel start spawns at the default).
                            if (backlog != null)
                                SendOutput(backlog);
                            SendPaneStatus(_status);
                            _session.Resize(Math.Max(2, _cols - 1), _rows);
                            _session.Resize(_cols, _rows);
                        }
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
                            // Ordered after the green-flip check: a pasted
                            // "claude --resume ...\r" must land on amber (fresh
                            // CLI resting state), not flash green.
                            if (_inShell && !_shellCliActive)
                                TrackShellInput(data);
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

                    case "toggleLastCmd":
                        // Alt+L from the front-end: flip the GLOBAL last-command bar.
                        // The manager fans the change out to every pane (and the
                        // MainWindow button) via its Changed event.
                        DispatcherQueue.TryEnqueue(CCPad.Settings.LastCmdBarManager.Toggle);
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
                            // Seed the shell input watcher with the injected text
                            // (it bypasses the "input" path) so the user's Enter
                            // flips the light instantly; the SessionStart hook
                            // then re-asserts the truth.
                            _shellLineBuf = _resumeCommand;
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
            CCPad.Settings.LastCmdBarManager.Changed -= OnLastCmdBarChanged;
            _loadedTcs?.TrySetCanceled();
            _readyTcs?.TrySetCanceled();
            _autoConfirmTimer?.Dispose();
            _workingWatchTimer?.Dispose();
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
                {
                    WebView.CoreWebView2.WebMessageReceived -= OnWebMessage;
                    WebView.CoreWebView2.ProcessFailed -= OnProcessFailed;
                }
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
                /* ── Last-command info bar (上一条命令) — global toggle, Alt+L ── */
                #lastcmd {
                  display: none;
                  flex: 0 0 auto;
                  align-items: center;
                  gap: 8px;
                  padding: 3px 10px;
                  background: #141414;
                  border-bottom: 1px solid #333;
                  font-family: 'Cascadia Code', 'Microsoft YaHei', 'Cascadia Mono', Consolas, monospace;
                  font-size: 13px;
                  color: #9aa;
                  cursor: pointer;
                  user-select: none;
                }
                #lastcmd.open { display: flex; }
                #lastcmd .lbl { flex: 0 0 auto; color: #667; }
                #lastcmd .txt {
                  flex: 1 1 auto;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                  color: #cfcfcf;
                }
                #lastcmd .time { flex: 0 0 auto; color: #667; }
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
                #stage-list li .edit {
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
                #stage-list li .edit:hover {
                  color: #fff;
                  background: #3a4a8a;
                  border-color: #5a6ac0;
                }
                #stage-list li .edit-box {
                  flex: 1 1 auto;
                  resize: none;
                  min-height: 26px;
                  max-height: 84px;
                  background: #0c0c0c;
                  color: #eee;
                  border: 1px solid #6a78d8;
                  border-radius: 4px;
                  padding: 3px 6px;
                  font-family: inherit;
                  font-size: 14px;
                  line-height: 1.3;
                  outline: none;
                  white-space: pre-wrap;
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
              <div id="lastcmd" title="点击复制全文 · Alt+L 关闭">
                <span class="lbl">▸ 上一条</span>
                <span class="txt" id="lastcmd-text">—</span>
                <span class="time" id="lastcmd-time"></span>
              </div>
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
                <div id="stage-hint">寄存模式:输入排进队列,会话空闲时自动逐条发出。点✎或双击可编辑(编辑中暂停发送),Alt+V 贴图,Alt+` 退出寄存。</div>
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

                /* ── Last-command info bar (上一条命令) ──────────────────────
                   A shadow buffer mirrors what the user types into the CLI —
                   works for Claude, Codex, cmd, anything on the PTY: printable
                   input (incl. IME-composed CJK and pastes, both arrive via
                   term.onData) appends, backspace pops, Enter commits to the
                   bar. Staged commands commit exactly at flush time in
                   sendCmd(). Known limits: ↑/↓ history recall desyncs the
                   buffer, so ↑/↓ clear it rather than guess wrong. */
                let lastCmdOn = false;
                let lastCmdFull = '';
                let shadowBuf = '';
                const lastCmdBar = document.getElementById('lastcmd');
                const lastCmdText = document.getElementById('lastcmd-text');
                const lastCmdTime = document.getElementById('lastcmd-time');
                let lastCmdCopyTimer = null;
                function applyLastCmdBar(on) {
                  lastCmdOn = !!on;
                  lastCmdBar.classList.toggle('open', lastCmdOn);
                  fit.fit();
                }
                function setLastCmd(text) {
                  const t = String(text).replace(/\s+$/, '');
                  if (!t) return;
                  lastCmdFull = t;
                  lastCmdText.textContent = t.replace(/\r?\n|\r/g, ' ⏎ ');
                  lastCmdText.title = t;
                  const now = new Date();
                  lastCmdTime.textContent =
                    String(now.getHours()).padStart(2, '0') + ':' +
                    String(now.getMinutes()).padStart(2, '0');
                }
                function shadowCommit() {
                  if (shadowBuf.replace(/\s/g, '').length) setLastCmd(shadowBuf);
                  shadowBuf = '';
                }
                function shadowFeed(data) {
                  // Bracketed paste (CLIs turn it on): the payload is literal
                  // text — an inner \r is a newline in the prompt box there,
                  // NOT a submit.
                  if (data.indexOf('\x1b[200~') !== -1) {
                    shadowBuf += data.split('\x1b[200~').join('')
                                     .split('\x1b[201~').join('')
                                     .replace(/\r\n?/g, '\n');
                    return;
                  }
                  for (let i = 0; i < data.length; i++) {
                    const c = data[i];
                    if (c === '\x1b') {
                      const n = data[i + 1];
                      if (n === '\r') { shadowBuf += '\n'; i++; continue; }  // Alt/Shift+Enter → newline
                      if (n === '[') {
                        let j = i + 2;                                       // skip CSI …final
                        while (j < data.length && !(data[j] >= '@' && data[j] <= '~')) j++;
                        if (data[j] === 'A' || data[j] === 'B') shadowBuf = ''; // ↑/↓ history: desynced
                        i = j;
                      } else if (n === 'O') {                                // SS3 (app-cursor arrows)
                        if (data[i + 2] === 'A' || data[i + 2] === 'B') shadowBuf = '';
                        i += 2;
                      }
                      continue;                                              // lone ESC: ignore
                    }
                    if (c === '\r' || c === '\n') { shadowCommit(); continue; }
                    if (c === '\x7f' || c === '\b') {                        // backspace: pop a code point
                      shadowBuf = Array.from(shadowBuf).slice(0, -1).join('');
                      continue;
                    }
                    if (c === '\x15' || c === '\x03') { shadowBuf = ''; continue; } // Ctrl+U / Ctrl+C
                    if (c === '\x17') {                                      // Ctrl+W: kill last word
                      shadowBuf = shadowBuf.replace(/\S+\s*$/, '');
                      continue;
                    }
                    if (c < ' ') continue;                                   // other control chars
                    shadowBuf += c;
                  }
                }
                lastCmdBar.addEventListener('click', () => {
                  if (!lastCmdFull) return;
                  window.chrome.webview.postMessage(JSON.stringify({ type: 'copy', data: lastCmdFull }));
                  const prev = lastCmdTime.textContent;
                  lastCmdTime.textContent = '已复制 ✓';
                  if (lastCmdCopyTimer) clearTimeout(lastCmdCopyTimer);
                  lastCmdCopyTimer = setTimeout(() => { lastCmdTime.textContent = prev; }, 1200);
                });
                applyLastCmdBar(__INITIAL_LASTCMD__);

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
                // Index of the queue item being edited inline, or null. While an
                // edit is open the flush machinery is PAUSED (see maybeFlush) so a
                // CLI going idle mid-edit can't send the very line under the
                // user's cursor, and indices can't shift under the editor.
                let editingIdx = null;
                function renderQueue() {
                  editingIdx = null;   // any rebuild tears the edit box down
                  stageList.innerHTML = '';
                  queue.forEach((cmd, i) => {
                    const li = document.createElement('li');
                    const idx = document.createElement('span');
                    idx.className = 'idx'; idx.textContent = (i + 1) + '.';
                    const txt = document.createElement('span');
                    txt.className = 'txt'; txt.textContent = cmd.replace(/\n/g, ' ⏎ ');
                    txt.addEventListener('dblclick', () => beginEdit(i));
                    const edit = document.createElement('span');
                    edit.className = 'edit'; edit.textContent = '✎'; edit.title = '编辑这条(双击文字也可)';
                    edit.addEventListener('click', () => beginEdit(i));
                    const del = document.createElement('span');
                    del.className = 'del'; del.textContent = '✕'; del.title = '删除这条';
                    del.addEventListener('click', () => { queue.splice(i, 1); renderQueue(); });
                    li.appendChild(idx); li.appendChild(txt); li.appendChild(edit); li.appendChild(del);
                    stageList.appendChild(li);
                  });
                  stageEmpty.style.display = queue.length ? 'none' : 'block';
                  stageCount.textContent = queue.length + ' 条待发';
                }
                // Swap item i's text span for a textarea in place. Enter saves,
                // Esc cancels, clicking away saves (blur). Saving an emptied box
                // deletes the item. Position in the queue is preserved.
                function beginEdit(i) {
                  if (editingIdx !== null) return;   // one edit at a time
                  editingIdx = i;
                  const li = stageList.children[i];
                  const txt = li.querySelector('.txt');
                  const box = document.createElement('textarea');
                  box.className = 'edit-box';
                  box.rows = 1;
                  box.value = queue[i];
                  const size = () => {
                    box.style.height = 'auto';
                    box.style.height = Math.min(84, box.scrollHeight) + 'px';
                  };
                  li.replaceChild(box, txt);
                  size();
                  box.focus();
                  box.selectionStart = box.selectionEnd = box.value.length;
                  const done = save => {
                    if (editingIdx === null) return; // renderQueue already tore us down
                    editingIdx = null;
                    if (save) {
                      const v = box.value.replace(/\s+$/, '');
                      if (v.length === 0) queue.splice(i, 1);
                      else queue[i] = v;
                    }
                    renderQueue();
                    maybeFlush();   // the queue was paused for the edit; resume
                  };
                  box.addEventListener('input', size);
                  box.addEventListener('keydown', e => {
                    if (e.isComposing) return;       // IME confirm ≠ save
                    if (e.key === 'Enter' && !e.shiftKey) {
                      e.preventDefault(); done(true);
                    } else if (e.key === 'Escape') {
                      e.preventDefault(); e.stopPropagation(); done(false);
                    }
                  });
                  box.addEventListener('blur', () => done(true));
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
                  // Staged flush bypasses term.onData, so commit the exact text
                  // to the last-command bar here (100% accurate path).
                  setLastCmd(cmd);
                  shadowBuf = '';
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
                  if (editingIdx !== null) return;   // paused while an item is being edited
                  if (lastStatus !== 'waiting') return;
                  if (flushTimer) return;
                  // Small settle delay so the CLI prompt is ready to receive input.
                  flushTimer = setTimeout(() => {
                    flushTimer = null;
                    if (!stagingOn || queue.length === 0 || lastStatus !== 'waiting') return;
                    if (editingIdx !== null) return; // edit opened during the settle delay
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
                  } else if (e.altKey && !e.ctrlKey && !e.shiftKey && !e.metaKey &&
                      (e.code === 'KeyL' || e.key === 'l' || e.key === 'L')) {
                    // Alt+L: flip the global last-command bar (host fans it out).
                    e.preventDefault();
                    e.stopPropagation();
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'toggleLastCmd' }));
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
                  // (The Ctrl+U it sends bypasses onData — reset the shadow buffer here.)
                  if (e.altKey && !e.shiftKey && !e.ctrlKey && (e.key === 'a' || e.key === 'A')) {
                    shadowBuf = '';
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
                  shadowFeed(data);   // mirror typed input for the last-command bar
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
                  } else if (msg.type === 'setLastCmdBar') {
                    applyLastCmdBar(!!msg.on);
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
