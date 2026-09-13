using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>Auto-reply (自动应答): watch every pane's output for custom
        /// trigger phrases and send a preset message back. Off by default.</summary>
        public bool AutoReplyEnabled { get; set; }

        /// <summary>Auto-reply rule list. Seeded with the Codex capacity banner
        /// so the feature works out of the box; users edit via the 应答 button.</summary>
        public List<AutoReplyRule> AutoReplyRules { get; set; } = new()
        {
            new AutoReplyRule { Trigger = "Selected model is at capacity", Reply = "重试" },
        };
    }

    /// <summary>One auto-reply rule: when <see cref="Trigger"/> appears in a
    /// pane's output (case-insensitive, ANSI-stripped), send <see cref="Reply"/>
    /// followed by Enter. An empty Reply sends Enter alone.</summary>
    public class AutoReplyRule
    {
        public string Trigger { get; set; } = "";
        public string Reply { get; set; } = "";
        public bool Enabled { get; set; } = true;
        /// <summary>Max consecutive auto-fires of this rule; a real user submit
        /// resets the count. 0 = unlimited. Guards against a persistent error
        /// banner turning the reply loop into an endless message stream.</summary>
        public int MaxRetries { get; set; } = 5;
    }

    /// <summary>
    /// Everything CCPad needs to open a Codex@167 tab: ssh to
    /// <see cref="User"/>@<see cref="Host"/> with <see cref="KeyPath"/> and run
    /// <see cref="RemoteCommand"/> (with {dir}/{session} substituted). Each tab
    /// runs its OWN tmux session named "{SessionPrefix}-…" (the name doubles as
    /// the tab's persisted SessionId, so freeze/snapshot/restore reattach it).
    /// Closing a tab kills its session over a one-shot ssh, and an hourly cron
    /// sweeper (installed by <see cref="RemoteSessions"/>) reaps detached
    /// prefix-named sessions older than <see cref="SweepIdleHours"/> — the
    /// backstop for kills lost to crashes or network drops.
    /// </summary>
    public class RemoteCodexConfig
    {
        public string Host { get; set; } = "192.168.32.167";
        public string User { get; set; } = "root";
        /// <summary>Private key file; %VAR% is expanded at launch time.</summary>
        public string KeyPath { get; set; } = "%USERPROFILE%\\.ssh\\id_ed25519_167";
        /// <summary>Remote working directory, substituted for {dir}.</summary>
        public string RemoteDir { get; set; } = RemoteProjectConfig.DefaultWorkingDir;
        /// <summary>Legacy shared session name from the single-session design.
        /// New tabs no longer use it (each generates a private name); kept so
        /// old prefs.json files load cleanly and the session manager can label
        /// a still-running "deploy" session.</summary>
        public string TmuxSession { get; set; } = "deploy";
        /// <summary>Prefix for generated per-tab session names. Only sessions
        /// carrying this prefix are ever killed automatically (tab close /
        /// cron sweeper); anything else on the box is left alone.</summary>
        public string SessionPrefix { get; set; } = "ccpad";
        /// <summary>Hours a DETACHED per-tab session may idle on the box before
        /// the hourly cron sweeper kills it. Attached sessions are never swept.</summary>
        public int SweepIdleHours { get; set; } = 48;
        /// <summary>Command run on the remote host (inside "..." on the ssh
        /// line, so it must not itself contain double quotes). ssh runs this in
        /// a non-login shell with no LANG (Windows ssh sends no locale), so a
        /// UTF-8 locale is exported and tmux gets -u — otherwise the tmux
        /// client assumes a non-UTF-8 terminal and paints every CJK cell as
        /// an underscore.</summary>
        public string RemoteCommand { get; set; } =
            "mountpoint -q /zettos/pool/1 && cd {dir} && source /etc/profile.d/agents.sh && source /opt/agents/opt/proxy_env.sh && export LANG=C.UTF-8 LC_ALL=C.UTF-8 && tmux -u new -A -s {session} codex";
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

        /// <summary>Drop the in-memory cache so the next Load() re-reads disk.
        /// Every window is its own process and Save() writes the whole file from
        /// this cache — without invalidation, any save from a window with a stale
        /// cache silently reverts what another window persisted in the meantime.</summary>
        internal static void Reload() => _cached = null;

        /// <summary>Full path of prefs.json, for cross-process change watching.</summary>
        internal static string PrefsFile => ConfigFile;
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
    /// Global on/off state + rule set of auto-reply (自动应答). One switch for
    /// every pane in the process: TerminalPanes poll <see cref="IsOn"/> and
    /// <see cref="ActiveRules"/> on their output threads, so the enabled-rule
    /// snapshot is an immutable array swapped atomically on every save.
    /// Persisted in AppPrefs; the MainWindow toolbar button mirrors the state.
    /// </summary>
    public static class AutoReplyManager
    {
        private static readonly object _gate = new();
        private static bool _loaded;
        private static bool _on;
        private static volatile AutoReplyRule[] _active = Array.Empty<AutoReplyRule>();
        private static FileSystemWatcher? _watcher;
        private static System.Threading.Timer? _reloadDebounce;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                var prefs = AppConfig.Load();
                _on = prefs.AutoReplyEnabled;
                RebuildActive(prefs.AutoReplyRules);
                _loaded = true;
                StartWatcher();
            }
        }

        // Every window is its own process with its own once-loaded state, so a
        // toggle in one window would never reach the others (and their next save
        // would revert it on disk). Watch prefs.json and fold external writes in.
        private static void StartWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(AppConfig.PrefsFile) ?? "";
                if (dir.Length == 0 || !Directory.Exists(dir)) return;
                _watcher = new FileSystemWatcher(dir, Path.GetFileName(AppConfig.PrefsFile))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                };
                FileSystemEventHandler h = (_, _) => QueueReload();
                _watcher.Changed += h;
                _watcher.Created += h;
                _watcher.Renamed += (_, _) => QueueReload();
                _watcher.EnableRaisingEvents = true;
            }
            catch { _watcher = null; }
        }

        // Debounced: editors save in bursts, and our own Save() also lands here
        // (harmless — the reload reads back what we just wrote).
        private static void QueueReload()
        {
            _reloadDebounce?.Dispose();
            _reloadDebounce = new System.Threading.Timer(_ =>
            {
                bool on, flipped;
                lock (_gate)
                {
                    AppConfig.Reload();
                    var prefs = AppConfig.Load();
                    flipped = _on != prefs.AutoReplyEnabled;
                    _on = prefs.AutoReplyEnabled;
                    RebuildActive(prefs.AutoReplyRules);
                    on = _on;
                }
                if (flipped) { try { Changed?.Invoke(on); } catch { } }
            }, null, 300, System.Threading.Timeout.Infinite);
        }

        private static void RebuildActive(List<AutoReplyRule> rules)
        {
            _active = rules
                .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Trigger))
                .Select(r => new AutoReplyRule
                {
                    Trigger = r.Trigger.Trim(),
                    Reply = r.Reply,
                    Enabled = true,
                    MaxRetries = Math.Max(0, r.MaxRetries),
                })
                .ToArray();
        }

        public static bool IsOn
        {
            get { EnsureLoaded(); return _on; }
        }

        /// <summary>Enabled rules only, trigger trimmed — safe to iterate from any thread.</summary>
        public static AutoReplyRule[] ActiveRules
        {
            get { EnsureLoaded(); return _active; }
        }

        public static int RuleCount
        {
            get { EnsureLoaded(); return AppConfig.Load().AutoReplyRules.Count; }
        }

        public static event Action<bool>? Changed;

        public static void SetEnabled(bool on)
        {
            EnsureLoaded();
            if (_on == on) return;
            _on = on;
            var prefs = AppConfig.Load();
            prefs.AutoReplyEnabled = on;
            AppConfig.Save(prefs);
            try { Changed?.Invoke(on); } catch { }
        }

        /// <summary>Deep copy for the editor dialog, so cancel discards edits.</summary>
        public static List<AutoReplyRule> GetRulesCopy()
        {
            EnsureLoaded();
            return AppConfig.Load().AutoReplyRules
                .Select(r => new AutoReplyRule
                {
                    Trigger = r.Trigger,
                    Reply = r.Reply,
                    Enabled = r.Enabled,
                    MaxRetries = r.MaxRetries,
                })
                .ToList();
        }

        public static void SaveRules(List<AutoReplyRule> rules)
        {
            EnsureLoaded();
            var prefs = AppConfig.Load();
            prefs.AutoReplyRules = rules;
            AppConfig.Save(prefs);
            RebuildActive(rules);
        }
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
