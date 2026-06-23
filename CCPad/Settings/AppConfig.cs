using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCPad.Settings
{
    public class AppPrefs
    {
        public double TabHeight { get; set; } = TabHeightManager.DefaultHeight;

        /// <summary>Default CLI for new tabs: "claude" or "codex".</summary>
        public string DefaultCli { get; set; } = "claude";

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
    }

    [JsonSerializable(typeof(AppPrefs))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class AppPrefsJsonContext : JsonSerializerContext { }

    public static class AppConfig
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCPad");
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
