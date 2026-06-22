using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
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

        private void SetStatus(PaneStatus status)
        {
            if (_status == status) return;
            _status = status;
            DispatcherQueue.TryEnqueue(() => StatusChanged?.Invoke(this));
        }

        /// <summary>Map a hook callback (waiting/working) to a status change.</summary>
        private void OnCliNotify(string evt)
        {
            switch (evt)
            {
                case "waiting": SetStatus(PaneStatus.Waiting); break;
                case "working": SetStatus(PaneStatus.Working); break;
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
                TerminalHtml.Replace("Auto Confirm (自动回车)", Localization.Loc.T("autoconfirm_title")));
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
            if (_autoConfirm)
                CheckAutoConfirm(data);
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
                        if (_awaitingRestart && data == "\r")
                            StartSession();
                        else
                        {
                            // Only a real submit (Enter) means you handed the AI
                            // work → clear the amber light optimistically; the next
                            // hook re-asserts truth. Plain keystrokes, arrow keys,
                            // mouse events, and focus-report sequences (ESC[I, sent
                            // by xterm on tab switch) must NOT turn it green.
                            if (_status == PaneStatus.Waiting && data.Contains('\r'))
                                SetStatus(PaneStatus.Working);
                            _session?.WriteInput(data);
                        }
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
                #terminal { width: 100%; height: 100%; overflow: hidden; filter: brightness(0.8); }
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
                #auto-confirm-btn {
                  position: fixed;
                  left: 6px;
                  bottom: 6px;
                  z-index: 9999;
                  background: rgba(50, 50, 50, 0.7);
                  color: #888;
                  border: 1px solid #555;
                  border-radius: 4px;
                  padding: 2px 8px;
                  font-size: 11px;
                  font-family: 'Cascadia Code', 'Cascadia Mono', Consolas, monospace;
                  cursor: pointer;
                  user-select: none;
                  transition: all 0.15s;
                  opacity: 0.5;
                }
                #auto-confirm-btn:hover { opacity: 1; }
                #auto-confirm-btn.active {
                  background: rgba(0, 120, 80, 0.8);
                  color: #4feba0;
                  border-color: #2a9;
                  opacity: 0.85;
                }
                #auto-confirm-btn.active:hover { opacity: 1; }
              </style>
              <link rel="stylesheet" href="https://xterm.local/xterm.css"/>
            </head>
            <body>
              <div id="terminal"></div>
              <button id="auto-confirm-btn" title="Auto Confirm (自动回车)">Auto ⏎</button>
              <script src="https://xterm.local/xterm.js"></script>
              <script src="https://xterm.local/xterm-addon-fit.js"></script>
              <script>
                const term = new Terminal({
                  fontFamily: "'Cascadia Code', 'Microsoft YaHei', 'Cascadia Mono', Consolas, monospace",
                  fontSize: 16,
                  lineHeight: 1.25,
                  theme: { background: '#0c0c0c' },
                  cursorBlink: true,
                  allowProposedApi: true
                });

                const fit = new FitAddon.FitAddon();
                term.loadAddon(fit);
                term.open(document.getElementById('terminal'));
                fit.fit();

                /* ── Ctrl+wheel font-size zoom (replaces WebView2 native page zoom) ── */
                const FONT_MIN = 8, FONT_MAX = 40, FONT_DEFAULT = 14;
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

                /* ── Auto-confirm toggle ── */
                let autoConfirm = false;
                const ACBtn = document.getElementById('auto-confirm-btn');
                ACBtn.addEventListener('click', () => {
                  autoConfirm = !autoConfirm;
                  ACBtn.classList.toggle('active', autoConfirm);
                  window.chrome.webview.postMessage(JSON.stringify({ type: 'autoConfirm', enabled: autoConfirm }));
                  term.focus();
                });

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
                  }
                });

                new ResizeObserver(() => fit.fit()).observe(document.getElementById('terminal'));
              </script>
            </body>
            </html>
            """;
    }
}
