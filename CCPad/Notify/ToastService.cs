using System;
using System.Collections.Concurrent;
using CCPad.Settings;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CCPad.Notify
{
    /// <summary>
    /// Best-effort Windows toast for "a background session is waiting for you".
    /// The app is unpackaged (WindowsPackageType=None), so toast delivery can be
    /// finicky — every call is wrapped and silently no-ops on failure. The tab
    /// status light is the reliable signal; this is the cross-window nudge.
    /// </summary>
    internal static class ToastService
    {
        // PaneId -> pane, so a toast click can reveal the right tab.
        private static readonly ConcurrentDictionary<string, TerminalPane> _panes = new();
        private static bool _registered;

        public static void RegisterPane(string paneId, TerminalPane pane)
            => _panes[paneId] = pane;

        public static void UnregisterPane(string paneId)
            => _panes.TryRemove(paneId, out _);

        /// <summary>Register the notification manager + activation callback once.</summary>
        public static void EnsureRegistered()
        {
            if (_registered) return;
            try
            {
                AppNotificationManager.Default.NotificationInvoked += OnInvoked;
                AppNotificationManager.Default.Register();
                _registered = true;
            }
            catch { }
        }

        public static void Unregister()
        {
            if (!_registered) return;
            try { AppNotificationManager.Default.Unregister(); } catch { }
            _registered = false;
        }

        private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            try
            {
                if (args.Arguments.TryGetValue("paneId", out var id) &&
                    _panes.TryGetValue(id, out var pane))
                {
                    pane.RequestReveal();
                }
            }
            catch { }
        }

        /// <summary>Pop a "waiting for you" toast. No-op if disabled or on any error.</summary>
        public static void ShowWaiting(string paneId, string label)
        {
            if (!AppConfig.Load().NotifyToastEnabled) return;
            try
            {
                var body = string.IsNullOrWhiteSpace(label)
                    ? Localization.Loc.T("toast_one")
                    : Localization.Loc.T("toast_named", label);

                var toast = new AppNotificationBuilder()
                    .AddText(Localization.Loc.T("toast_title"))
                    .AddText(body)
                    .AddArgument("paneId", paneId)
                    .BuildNotification();

                AppNotificationManager.Default.Show(toast);
            }
            catch { }
        }
    }
}
