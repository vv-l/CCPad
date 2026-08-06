using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCPad.Settings
{
    public class AppPrefs
    {
        public double TabHeight { get; set; } = TabHeightManager.DefaultHeight;

        /// <summary>Default CLI for new tabs: "claude" / "codex" / "codex-remote".</summary>
        public string DefaultCli { get; set; } = "claude";

        /// <summary>Connection parameters for the "Codex@167" mode (ssh + tmux
        /// attach). No settings UI in v1 — edit prefs.json by hand; missing
        /// fields fall back to these defaults.</summary>
        public RemoteCodexConfig RemoteCodex { get; set; } = new();

        /// <summary>Chrome-style crash recovery: prompt to restore on next launch.</summary>
        public bool SessionRecoveryEnabled { get; set; } = true;

        /// <summary>Pop a Windows toast when a background tab's CLI is waiting for input.</summary>
        public bool NotifyToastEnabled { get; set; } = true;

        /// <summary>UI language: "zh-Hans" / "en" / "zh-Hant". Empty = follow OS on first run.</summary>
        public string Language { get; set; } = "";

        /// <summary>App theme: "dark" (default — the all-black skin) / "light" (original look) / "system".</summary>
        public string Theme { get; set; } = "dark";

        /// <summary>Launch Claude with --permission-mode bypassPermissions (auto-approve, skip
        /// permission prompts). Default on; users can turn it off in the About menu.</summary>
        public bool BypassPermissions { get; set; } = true;

        /// <summary>Show the last-command info bar (上一条命令) at the top of every pane.</summary>
        public bool LastCmdBarEnabled { get; set; } = true;

        /// <summary>Ask for confirmation when the main window is closed.</summary>
        public bool ConfirmOnClose { get; set; } = true;

        /// <summary>Last state of the close dialog's "restore next launch" checkbox.
        /// Also used directly when ConfirmOnClose is off.</summary>
        public bool RestoreOnClose { get; set; } = true;

        /// <summary>Automatically freeze tabs whose CLI has been idle for
        /// <see cref="AutoFreezeMinutes"/>. Default off — freezing is manual-first.</summary>
        public bool AutoFreezeEnabled { get; set; }

        /// <summary>Idle threshold (minutes) for auto-freeze.</summary>
        public int AutoFreezeMinutes { get; set; } = 60;
    }

    /// <summary>
    /// Everything CCPad needs to open a Codex@167 tab: ssh to
    /// <see cref="User"/>@<see cref="Host"/> with <see cref="KeyPath"/> and run
    /// <see cref="RemoteCommand"/> (with {dir}/{session} substituted from
    /// <see cref="RemoteDir"/>/<see cref="TmuxSession"/>). All tabs attach the
    /// SAME tmux session by design — mirrored views, no per-tab session names,
    /// so the remote box never accumulates zombie tmux sessions.
    /// </summary>
    public class RemoteCodexConfig
    {
        public string Host { get; set; } = "192.168.32.167";
        public string User { get; set; } = "root";
        /// <summary>Private key file; %VAR% is expanded at launch time.</summary>
        public string KeyPath { get; set; } = "%USERPROFILE%\\.ssh\\id_ed25519_167";
        /// <summary>Remote working directory, substituted for {dir}.</summary>
        public string RemoteDir { get; set; } = "/zettos/pool/1/agents/deploy/workspace";
        /// <summary>tmux session name, substituted for {session}.</summary>
        public string TmuxSession { get; set; } = "deploy";
        /// <summary>Command run on the remote host (inside "..." on the ssh
        /// line, so it must not itself contain double quotes). ssh runs this in
        /// a non-login shell with no LANG (Windows ssh sends no locale), so a
        /// UTF-8 locale is exported and tmux gets -u — otherwise the tmux
        /// client assumes a non-UTF-8 terminal and paints every CJK cell as
        /// an underscore.</summary>
        public string RemoteCommand { get; set; } =
            "cd {dir} && source /etc/profile.d/agents.sh && source /zettos/pool/1/agents/opt/proxy_env.sh && export LANG=C.UTF-8 LC_ALL=C.UTF-8 && tmux -u new -A -s {session} codex";
    }

    [JsonSerializable(typeof(AppPrefs))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class AppPrefsJsonContext : JsonSerializerContext { }

    public static class AppConfig
    {
        private static readonly string ConfigDir = AppPaths.Root;
        private static readonly string ConfigFile = Path.Combine(ConfigDir, "prefs.json");

        private static AppPrefs? _cached;

        public static AppPrefs Load()
        {
            if (_cached != null) return _cached;
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile);
                    _cached = JsonSerializer.Deserialize(json, AppPrefsJsonContext.Default.AppPrefs) ?? new AppPrefs();
                    return _cached;
                }
            }
            catch { }
            _cached = new AppPrefs();
            return _cached;
        }

        public static void Save(AppPrefs prefs)
        {
            _cached = prefs;
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var json = JsonSerializer.Serialize(prefs, AppPrefsJsonContext.Default.AppPrefs);
                File.WriteAllText(ConfigFile, json);
            }
            catch { }
        }
    }

    /// <summary>
    /// Global on/off state of the last-command info bar (上一条命令信息栏).
    /// One switch for every pane in the process: TerminalPanes subscribe to
    /// <see cref="Changed"/> and show/hide their in-page bar live; the
    /// MainWindow toolbar button mirrors the state. Persisted in AppPrefs.
    /// </summary>
    public static class LastCmdBarManager
    {
        private static bool? _on;

        public static bool IsOn
        {
            get
            {
                _on ??= AppConfig.Load().LastCmdBarEnabled;
                return _on.Value;
            }
        }

        public static event Action<bool>? Changed;

        public static void Set(bool on)
        {
            if (_on == on) return;
            _on = on;
            var prefs = AppConfig.Load();
            prefs.LastCmdBarEnabled = on;
            AppConfig.Save(prefs);
            try { Changed?.Invoke(on); } catch { }
        }

        public static void Toggle() => Set(!IsOn);
    }

    /// <summary>
    /// Shared tab-strip height across all TabPanel instances. Dragging the resize
    /// handle in any TabPanel updates all of them in real time; Persist() writes
    /// the value to disk on drag-release.
    /// </summary>
    public static class TabHeightManager
    {
        public const double DefaultHeight = 32;
        public const double MinHeight = 28;
        public const double MaxHeight = 120;

        private static double _height = -1;

        public static double Height
        {
            get
            {
                if (_height < 0)
                    _height = Clamp(AppConfig.Load().TabHeight);
                return _height;
            }
            set
            {
                var clamped = Clamp(value);
                if (Math.Abs(clamped - _height) < 0.5) return;
                _height = clamped;
                Changed?.Invoke(_height);
            }
        }

        public static event Action<double>? Changed;

        public static void Persist()
        {
            var prefs = AppConfig.Load();
            prefs.TabHeight = _height;
            AppConfig.Save(prefs);
        }

        private static double Clamp(double v) => Math.Max(MinHeight, Math.Min(MaxHeight, v));
    }
}
