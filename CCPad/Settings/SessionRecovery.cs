using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CCPad.Settings
{
    /// <summary>
    /// Chrome-style crash recovery. Maintains a per-instance lock file and a
    /// shared last-session snapshot under %LOCALAPPDATA%\CCPad\sessions\.
    /// - Lock files are named by PID. On startup we scan for locks whose PID
    ///   is no longer alive — those are the orphan sessions to recover.
    /// - Snapshot writes go through a .tmp + File.Replace pair so a crash
    ///   mid-write can't leave a truncated JSON file.
    /// </summary>
    public static class SessionRecovery
    {
        private static readonly string Dir = AppPaths.Sub("sessions");

        private static readonly string SnapshotFile = Path.Combine(Dir, "last-session.json");
        private static string LockFile => Path.Combine(Dir, $"running-{Environment.ProcessId}.lock");

        public static void MarkRunning()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(LockFile, Environment.ProcessId.ToString());
            }
            catch { }
        }

        public static void MarkClosedCleanly()
        {
            try { if (File.Exists(LockFile)) File.Delete(LockFile); }
            catch { }
        }

        /// <summary>
        /// Returns the previous snapshot if it appears to be from a crashed
        /// session (any orphan lock file exists). Returns null when no crash
        /// was detected. Cleans up orphan locks regardless.
        /// </summary>
        public static WorkspaceEntry? DetectCrashedSession()
        {
            bool foundOrphan = false;
            try
            {
                if (!Directory.Exists(Dir)) return null;

                foreach (var lockPath in Directory.GetFiles(Dir, "running-*.lock"))
                {
                    if (IsLockOrphan(lockPath))
                    {
                        foundOrphan = true;
                        try { File.Delete(lockPath); } catch { }
                    }
                }
            }
            catch { }

            if (!foundOrphan) return null;
            return LoadSnapshot();
        }

        private static bool IsLockOrphan(string path)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path); // "running-1234"
                var pidStr = name.Substring("running-".Length);
                if (!int.TryParse(pidStr, out var pid)) return true;
                if (pid == Environment.ProcessId) return false;

                try
                {
                    using var p = Process.GetProcessById(pid);
                    // Sanity check: process name should match our exe to avoid
                    // recycled PIDs registering as live.
                    return !string.Equals(p.ProcessName, "CCPad", StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException)
                {
                    return true; // process gone
                }
            }
            catch { return true; }
        }

        public static WorkspaceEntry? LoadSnapshot()
        {
            try
            {
                if (!File.Exists(SnapshotFile)) return null;
                var json = File.ReadAllText(SnapshotFile);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.WorkspaceEntry);
            }
            catch { return null; }
        }

        public static void SaveSnapshot(WorkspaceEntry entry)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(entry, WorkspaceJsonContext.Default.WorkspaceEntry);

                var tmp = SnapshotFile + ".tmp";
                File.WriteAllText(tmp, json);

                if (File.Exists(SnapshotFile))
                    File.Replace(tmp, SnapshotFile, null);
                else
                    File.Move(tmp, SnapshotFile);
            }
            catch { }
        }

        public static void ClearSnapshot()
        {
            try { if (File.Exists(SnapshotFile)) File.Delete(SnapshotFile); }
            catch { }
        }
    }
}
