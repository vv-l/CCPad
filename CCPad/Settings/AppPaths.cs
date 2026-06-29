using System;
using System.IO;

namespace CCPad.Settings
{
    /// <summary>
    /// Single source of truth for CCPad's per-user data root. All config,
    /// session snapshots, hooks and logs live under this folder.
    ///
    /// Defaults to <c>%LOCALAPPDATA%\CCPad</c>. Override with the
    /// <c>CCPAD_DATA_DIR</c> environment variable so a second instance
    /// (e.g. a dev/demo build launched alongside the real install) gets a
    /// fully isolated profile — separate prefs, projects, crash-recovery
    /// snapshot, lock files and hooks — and never cross-contaminates the
    /// production instance's session state.
    /// </summary>
    public static class AppPaths
    {
        private static readonly string _root = ResolveRoot();

        private static string ResolveRoot()
        {
            try
            {
                var custom = Environment.GetEnvironmentVariable("CCPAD_DATA_DIR");
                if (!string.IsNullOrWhiteSpace(custom))
                    return custom.Trim();
            }
            catch { }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CCPad");
        }

        /// <summary>The data root, e.g. <c>%LOCALAPPDATA%\CCPad</c> or the CCPAD_DATA_DIR override.</summary>
        public static string Root => _root;

        /// <summary>A sub-folder under the data root (created on demand by callers).</summary>
        public static string Sub(string name) => Path.Combine(_root, name);
    }
}
