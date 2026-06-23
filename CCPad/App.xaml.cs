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
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CCPad", "logs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "startup-error.log");
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n{ex}\n\n");
            }
            catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();

            // Lightweight notify helper spawned by Codex (notify=[...] config):
            //   CCPad.exe --notify <paneId> <port> [<json-payload-codex-appends>]
            // Forward the "your turn" nudge to the already-running CCPad listener
            // and exit before any window is created. The trailing JSON arg Codex
            // appends is ignored.
            if (cmdArgs.Length > 3 && cmdArgs[1] == "--notify")
            {
                try
                {
                    if (int.TryParse(cmdArgs[3], out int notifyPort))
                        Web.CliNotify.SendLocal(notifyPort, cmdArgs[2], "waiting");
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
                if (arg.EndsWith(Settings.WorkspaceConfig.FileExtension, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(arg))
                {
                    // Open a .ccpad-workspace file
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
        }
    }
}
