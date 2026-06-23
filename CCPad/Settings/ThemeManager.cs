using System;
using Microsoft.UI.Xaml;

namespace CCPad.Settings
{
    /// <summary>
    /// App theme preference ("dark" / "light" / "system") plus the resolved
    /// effective theme. The chrome (window background + tab strip) switches purely
    /// through XAML ThemeDictionaries keyed off the root element's RequestedTheme;
    /// the terminal panes — whose extra styling (CJK font, larger size, dimmed text)
    /// is dark-only — can't see XAML resources, so they subscribe to
    /// <see cref="EffectiveChanged"/> and re-style their xterm front-end live.
    /// </summary>
    public static class ThemeManager
    {
        public const string Dark = "dark";
        public const string Light = "light";
        public const string System = "system";

        private static string _pref = Dark;

        /// <summary>Saved preference string ("dark" / "light" / "system").</summary>
        public static string Pref => _pref;

        /// <summary>
        /// The theme actually in effect (true = dark). Kept in sync by MainWindow
        /// from the root element's ActualTheme, so under "system" it tracks the OS.
        /// Defaults to dark to match the default preference.
        /// </summary>
        public static bool IsDark { get; private set; } = true;

        /// <summary>Raised when the preference string changes (re-apply + menu rebuild).</summary>
        public static event Action? PrefChanged;

        /// <summary>Raised when the resolved dark/light value changes (terminals re-style).</summary>
        public static event Action<bool>? EffectiveChanged;

        public static void Init() => _pref = Normalize(AppConfig.Load().Theme);

        public static string Normalize(string? v) => v switch
        {
            Light => Light,
            System => System,
            _ => Dark,
        };

        public static ElementTheme ToElementTheme(string pref) => Normalize(pref) switch
        {
            Light => ElementTheme.Light,
            System => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };

        public static void SetPref(string pref, bool persist = true)
        {
            pref = Normalize(pref);
            if (pref == _pref) return;
            _pref = pref;
            if (persist)
            {
                var p = AppConfig.Load();
                p.Theme = pref;
                AppConfig.Save(p);
            }
            try { PrefChanged?.Invoke(); } catch { }
        }

        /// <summary>
        /// Update the resolved theme. Called right after RequestedTheme is applied
        /// and whenever the root element's ActualTheme changes (e.g. the OS theme
        /// flips while the preference is "system").
        /// </summary>
        public static void SetEffective(bool dark)
        {
            if (dark == IsDark) return;
            IsDark = dark;
            try { EffectiveChanged?.Invoke(dark); } catch { }
        }
    }
}
