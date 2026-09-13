using System;
using System.IO;
using System.Runtime.InteropServices;
using CCPad.Localization;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.Graphics;

namespace CCPad
{
    public partial class App : Application
    {
        private Window? _window;

        public Window? MainWnd => _window;
        public string? StartupWorkingDir { get; private set; }
        public string? StartupWorkspaceFile { get; private set; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WmNcLButtonDown = 0x00A1;
        private static readonly IntPtr HtCaption = new(2);

        /// <summary>True when CCPad's window is the foreground OS window.</summary>
        public static bool IsMainWindowForeground()
        {
            try
            {
                if ((Current as App)?._window is not { } w) return false;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                return GetForegroundWindow() == hwnd;
            }
            catch { return false; }
        }

        /// <summary>Bring CCPad's window to the foreground (toast-click target).</summary>
        public static void ActivateMainWindow()
        {
            try
            {
                if ((Current as App)?._window is not { } w) return;
                w.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        w.Activate();
                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                        SetForegroundWindow(hwnd);
                    }
                    catch { }
                });
            }
            catch { }
        }

        public App()
        {
            // Every WebView2 in this process paints WHITE (its default
            // background) whenever the page inside isn't composing — during
            // init, after a renderer death, after a re-parent. Terminals are
            // dark; make the failure color dark too, before any core is created.
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF0C0C0C");
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // Persist a record before WinUI fail-fasts. The running lock is left
            // in place on purpose so crash recovery can offer to restore on the
            // next launch — the recovery path is now hardened against re-crashing.
            LogStartupError("UnhandledException", e.Exception);
        }

        /// <summary>Best-effort append of a startup/runtime error to a log file.</summary>
        public static void LogStartupError(string context, Exception? ex)
        {
            try
            {
                var dir = CCPad.Settings.AppPaths.Sub("logs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "startup-error.log");
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n{ex}\n\n");
            }
            catch { }
        }

        /// <summary>Lightweight diagnostics line (perf timings etc.) — logs\diag.log.</summary>
        public static void LogDiag(string message)
        {
            try
            {
                var dir = CCPad.Settings.AppPaths.Sub("logs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "diag.log");
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }

        /// <summary>
        /// Claude Code hooks pipe a JSON payload to the hook command's stdin. Besides
        /// the real conversation ID, Stop payloads expose background_tasks and
        /// session_crons: a non-empty list means Claude is paused for work that will
        /// wake it again, not genuinely idle. Hard timeout because Codex direct-execs
        /// this helper without piping stdin.
        /// </summary>
        private readonly record struct HookPayload(
            string? SessionId, string? EventName, bool HasPendingWork);

        private static HookPayload ReadHookPayload()
        {
            try
            {
                if (!Console.IsInputRedirected) return default;
                var read = System.Threading.Tasks.Task.Run(() => Console.In.ReadToEnd());
                if (!read.Wait(1500)) return default;
                var json = read.Result;
                if (string.IsNullOrWhiteSpace(json)) return default;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? sessionId = null;
                string? eventName = null;
                if (doc.RootElement.TryGetProperty("session_id", out var sid) &&
                    Guid.TryParse(sid.GetString(), out _))
                    sessionId = sid.GetString();
                if (root.TryGetProperty("hook_event_name", out var evt))
                    eventName = evt.GetString();

                static bool NonEmptyArray(System.Text.Json.JsonElement root, string name) =>
                    root.TryGetProperty(name, out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    value.GetArrayLength() > 0;

                bool pending = NonEmptyArray(root, "background_tasks") ||
                               NonEmptyArray(root, "session_crons");
                return new HookPayload(sessionId, eventName, pending);
            }
            catch { }
            return default;
        }

        /// <summary>Turn a left press on non-interactive tab-strip whitespace into
        /// a normal Windows caption drag. The stock title bar remains enabled, so
        /// this only adds the expected draggable gap between tabs and the add button.</summary>
        public static void BeginMainWindowDrag()
        {
            try
            {
                if ((Current as App)?._window is not { } w) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                ReleaseCapture();
                SendMessage(hwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Turn the coarse event word embedded in old and new hook files
        /// into the pane state that the payload actually describes.</summary>
        private static string? ResolveHookEvent(string requested, HookPayload payload)
        {
            // Notification is not a lifecycle transition. It can fire for permission
            // prompts and other mid-turn notices; treating it as idle releases staged
            // messages into a still-running turn.
            if (string.Equals(payload.EventName, "Notification", StringComparison.Ordinal))
                return null;

            if (string.Equals(payload.EventName, "Stop", StringComparison.Ordinal))
                return payload.HasPendingWork ? "working" : "waiting";
            if (string.Equals(payload.EventName, "UserPromptSubmit", StringComparison.Ordinal))
                return "working";
            if (string.Equals(payload.EventName, "SessionStart", StringComparison.Ordinal))
                return "waiting";

            // Codex notify and older Claude versions may not provide hook_event_name.
            return requested;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();

            // Lightweight notify helper spawned by a Claude hook or by Codex
            // (notify=[...] config):
            //   CCPad.exe --notify <paneId> <evt> [<json-payload-codex-appends>]
            // Forward the status nudge to the running CCPad that owns the pane and
            // exit before any window is created. The trailing JSON arg Codex
            // appends is ignored.
            //   • <evt> is a word (waiting/working) → broadcast to whichever live
            //     process holds this pane's handler (port-agnostic; the robust path).
            //   • <evt> is a number → a port baked by an older hook/Codex config
            //     that is still running; deliver to exactly that port (legacy).
            if (cmdArgs.Length > 3 && cmdArgs[1] == "--notify")
            {
                try
                {
                    string paneId = cmdArgs[2];
                    string evtOrPort = cmdArgs[3];
                    var payload = ReadHookPayload();
                    string requested = int.TryParse(evtOrPort, out int notifyPort)
                        ? "waiting"
                        : evtOrPort;
                    string? effectiveEvent = ResolveHookEvent(requested, payload);
                    if (effectiveEvent != null)
                    {
                        if (notifyPort > 0)
                            Web.CliNotify.SendLocal(notifyPort, paneId, effectiveEvent, payload.SessionId);
                        else
                            Web.CliNotify.Broadcast(paneId, effectiveEvent, payload.SessionId);
                    }
                }
                catch { }
                Environment.Exit(0);
                return;
            }

            if (cmdArgs.Length > 1 && cmdArgs[1] == "--unregister")
            {
                UnregisterContextMenu();
                Environment.Exit(0);
                return;
            }

            if (cmdArgs.Length > 1)
            {
                var arg = cmdArgs[1];
                if (Settings.WorkspaceConfig.IsWorkspaceOrTemplateFile(arg) && File.Exists(arg))
                {
                    // Open a .ccpad-workspace or frozen .ccpad-template file.
                    StartupWorkspaceFile = arg;
                }
                else if (Directory.Exists(arg))
                {
                    StartupWorkingDir = arg;
                }
            }

            // Auto-detect workspace file in the working directory
            if (StartupWorkspaceFile == null)
            {
                var searchDir = StartupWorkingDir ?? Directory.GetCurrentDirectory();
                try
                {
                    var wsFiles = Directory.GetFiles(searchDir, "*" + Settings.WorkspaceConfig.FileExtension);
                    if (wsFiles.Length > 0)
                    {
                        Array.Sort(wsFiles, StringComparer.OrdinalIgnoreCase);
                        StartupWorkspaceFile = wsFiles[0];
                        StartupWorkingDir = null; // workspace takes precedence
                    }
                }
                catch { }
            }

            // Resolve UI language (saved pref or OS) before any UI is built.
            Loc.Init();

            // Resolve the saved theme preference before the first window is built.
            Settings.ThemeManager.Init();

            // 每次启动自动注册右键菜单（幂等操作）
            try { RegisterContextMenu(); } catch { }

            // Register the toast notifier early so background "waiting" nudges
            // and their click-to-reveal activation work. Best-effort (unpackaged).
            Notify.ToastService.EnsureRegistered();

            _window = new MainWindow();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(wid);
            appWindow.Resize(new SizeInt32(1200, 800));
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "claude.ico"));

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = appWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = false;
                titleBar.BackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            }

            _window.Activate();
        }

        /// <summary>Re-write the shell context-menu labels (e.g. after a language switch).</summary>
        public static void ReRegisterContextMenu()
        {
            try { RegisterContextMenu(); } catch { }
        }

        private static void RegisterContextMenu()
        {
            string exePath = Path.Combine(AppContext.BaseDirectory, "CCPad.exe");
            // 优先使用 exe 内嵌图标（通过 ApplicationIcon 编译进去的），比 .ico 文件路径更可靠
            string iconValue = $"\"{exePath}\",0";

            // 文件夹背景右键（在文件夹内空白处右键）
            using var bgKey = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\Directory\Background\shell\CCPad");
            bgKey.SetValue("", Loc.T("shell_open"));
            bgKey.SetValue("Icon", iconValue);
            using var bgCmd = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\Directory\Background\shell\CCPad\command");
            bgCmd.SetValue("", $"\"{exePath}\" \"%V\"");

            // 文件夹右键（右键点击文件夹）
            using var dirKey = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\Directory\shell\CCPad");
            dirKey.SetValue("", Loc.T("shell_open"));
            dirKey.SetValue("Icon", iconValue);
            using var dirCmd = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\Directory\shell\CCPad\command");
            dirCmd.SetValue("", $"\"{exePath}\" \"%V\"");

            // .ccpad-workspace 文件关联 — 双击打开
            using var extKey = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\.ccpad-workspace");
            extKey.SetValue("", "CCPad.Workspace");
            using var typeKey = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\CCPad.Workspace");
            typeKey.SetValue("", Loc.T("shell_workspace_type"));
            typeKey.SetValue("FriendlyTypeName", Loc.T("shell_workspace_type"));
            using var typeIcon = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\CCPad.Workspace\DefaultIcon");
            typeIcon.SetValue("", iconValue);
            using var typeCmd = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\CCPad.Workspace\shell\open\command");
            typeCmd.SetValue("", $"\"{exePath}\" \"%1\"");

            // Frozen-template files always launch a separate CCPad process. The
            // file itself contains only frozen tabs, so no CLI starts until clicked.
            using var templateExtKey = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\.ccpad-template");
            templateExtKey.SetValue("", "CCPad.FrozenTemplate");
            using var templateTypeKey = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\CCPad.FrozenTemplate");
            templateTypeKey.SetValue("", Loc.T("shell_template_type"));
            templateTypeKey.SetValue("FriendlyTypeName", Loc.T("shell_template_type"));
            using var templateIcon = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\CCPad.FrozenTemplate\DefaultIcon");
            templateIcon.SetValue("", iconValue);
            using var templateCmd = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\CCPad.FrozenTemplate\shell\open\command");
            templateCmd.SetValue("", $"\"{exePath}\" \"%1\"");
        }

        private static void UnregisterContextMenu()
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\Directory\Background\shell\CCPad", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\Directory\shell\CCPad", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\.ccpad-workspace", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\CCPad.Workspace", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\.ccpad-template", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\CCPad.FrozenTemplate", false);
        }
    }
}
