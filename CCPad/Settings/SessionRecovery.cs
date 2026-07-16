using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace CCPad.Settings
{
    /// <summary>
    /// Chrome-style crash recovery, per-instance. Files live under
    /// %LOCALAPPDATA%\CCPad\sessions\:
    /// - running-&lt;pid&gt;.lock — one per live instance. On startup we scan for
    ///   locks whose PID is no longer alive — those are crashed instances.
    /// - snapshot-&lt;pid&gt;.json — that instance's autosaved live snapshot. Each
    ///   instance only ever writes its own file, so parallel windows can't
    ///   overwrite each other's recovery data.
    /// - closed\closed-&lt;stamp&gt;-&lt;pid&gt;.json — history of ended sessions, both
    ///   clean closes and swept crashes. The newest entry with a pending flag
    ///   (RestoreOnLaunch or Crashed) is auto-restored by the next launch; the
    ///   rest stay restorable from the menu until pruned.
    /// Snapshot writes go through a .tmp + File.Replace pair so a crash
    /// mid-write can't leave a truncated JSON file. Cross-process races between
    /// simultaneously launching/closing instances are serialized by a named mutex.
    /// </summary>
    public static class SessionRecovery
    {
        private static readonly string Dir = AppPaths.Sub("sessions");
        private static readonly string ClosedDir = Path.Combine(Dir, "closed");

        /// <summary>Pre-1.8 single shared snapshot; migrated into history on sweep.</summary>
        private static readonly string LegacySnapshotFile = Path.Combine(Dir, "last-session.json");
        private static readonly string CrashRestoreMarker = Path.Combine(Dir, "crash-restore.attempt");
        private static string LockFile => Path.Combine(Dir, $"running-{Environment.ProcessId}.lock");
        private static string OwnSnapshotFile => Path.Combine(Dir, $"snapshot-{Environment.ProcessId}.json");

        private const int MaxClosedEntries = 12;

        public sealed class ClosedEntry
        {
            public string Path = "";
            public DateTime ClosedAt;
            public WorkspaceEntry Entry = new();
        }

        public sealed class PendingRestore
        {
            public WorkspaceEntry Entry = new();
            public bool WasCrashed;
        }

        // ── Cross-process guard ───────────────────────────────────────
        // Two instances launching (or closing) at the same moment must not both
        // consume the same pending entry / prune the same files.

        private static T WithLock<T>(Func<T> body, T fallback)
        {
            Mutex? mutex = null;
            bool owned = false;
            try
            {
                mutex = new Mutex(false, @"Local\CCPad.SessionRecovery");
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { owned = true; }
                return body();
            }
            catch { return fallback; }
            finally
            {
                try { if (owned) mutex?.ReleaseMutex(); } catch { }
                mutex?.Dispose();
            }
        }

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
            DeleteOwnSnapshot();
            // A clean close proves the last restored session was healthy, so the
            // next crash may again auto-restore silently.
            ClearCrashRestoreAttempt();
        }

        // ── Crash-loop guard ──────────────────────────────────────────
        // Set before a silent crash restore, cleared only on clean close. If a
        // crash is detected while the marker is still present, the previous
        // silent restore itself never reached a clean close — fall back to
        // asking the user instead of silently restoring a possibly-bad snapshot
        // in a loop.

        public static bool HasCrashRestoreAttempt()
        {
            try { return File.Exists(CrashRestoreMarker); }
            catch { return false; }
        }

        public static void MarkCrashRestoreAttempt()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(CrashRestoreMarker, DateTime.Now.ToString("o"));
            }
            catch { }
        }

        public static void ClearCrashRestoreAttempt()
        {
            try { if (File.Exists(CrashRestoreMarker)) File.Delete(CrashRestoreMarker); }
            catch { }
        }

        // ── Live snapshot (this instance only) ────────────────────────

        public static void SaveSnapshot(WorkspaceEntry entry)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(entry, WorkspaceJsonContext.Default.WorkspaceEntry);

                var target = OwnSnapshotFile;
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, json);

                if (File.Exists(target))
                    File.Replace(tmp, target, null);
                else
                    File.Move(tmp, target);
            }
            catch { }
        }

        public static void DeleteOwnSnapshot()
        {
            try { if (File.Exists(OwnSnapshotFile)) File.Delete(OwnSnapshotFile); }
            catch { }
        }

        // ── Crash sweep ───────────────────────────────────────────────

        /// <summary>
        /// Move every dead instance's leftovers into the closed-session history
        /// (marked Crashed so the next launch may auto-restore it) and clean up
        /// its lock file. Also migrates the pre-1.8 shared last-session.json.
        /// </summary>
        public static void SweepCrashedSessions() => WithLock<object?>(() =>
        {
            if (!Directory.Exists(Dir)) return null;

            var orphanLockPids = new HashSet<int>();
            foreach (var lockPath in Directory.GetFiles(Dir, "running-*.lock"))
            {
                if (IsPidFileOrphan(lockPath, "running-", out var pid))
                {
                    orphanLockPids.Add(pid);
                    try { File.Delete(lockPath); } catch { }
                }
            }

            var sweptPids = new HashSet<int>();
            foreach (var snapPath in Directory.GetFiles(Dir, "snapshot-*.json"))
            {
                if (!IsPidFileOrphan(snapPath, "snapshot-", out var pid)) continue;
                sweptPids.Add(pid);
                var entry = LoadEntry(snapPath);
                var stamp = File.GetLastWriteTime(snapPath);
                if (entry?.Layout != null)
                {
                    entry.Crashed = true;
                    entry.RestoreOnLaunch = false;
                    WriteClosedEntry(entry, stamp);
                }
                try { File.Delete(snapPath); } catch { }
            }

            // Legacy shared snapshot: keep it restorable, then retire the file.
            // RestoreOnLaunch carries over. An orphan lock with no per-PID
            // snapshot means a killed pre-1.8 instance — its state lives in the
            // legacy file, so that counts as a crash; otherwise the legacy file
            // is just a stale autosave and goes into history menu-only.
            var legacy = LoadEntry(LegacySnapshotFile);
            if (legacy?.Layout != null)
            {
                foreach (var pid in orphanLockPids)
                    if (!sweptPids.Contains(pid)) { legacy.Crashed = true; break; }
                WriteClosedEntry(legacy, File.GetLastWriteTime(LegacySnapshotFile));
            }
            try { if (File.Exists(LegacySnapshotFile)) File.Delete(LegacySnapshotFile); } catch { }

            PruneClosed();
            return null;
        }, null);

        private static bool IsPidFileOrphan(string path, string prefix, out int pid)
        {
            pid = 0;
            try
            {
                var name = Path.GetFileNameWithoutExtension(path); // e.g. "running-1234"
                var pidStr = name.Substring(prefix.Length);
                if (!int.TryParse(pidStr, out pid)) return true;
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

        // ── Closed-session history ────────────────────────────────────

        /// <summary>Archive a finished session (clean close or menu-replace).</summary>
        public static void ArchiveClosed(WorkspaceEntry entry) => WithLock<object?>(() =>
        {
            WriteClosedEntry(entry, DateTime.Now);
            PruneClosed();
            return null;
        }, null);

        private static void WriteClosedEntry(WorkspaceEntry entry, DateTime stamp)
        {
            try
            {
                Directory.CreateDirectory(ClosedDir);
                var json = JsonSerializer.Serialize(entry, WorkspaceJsonContext.Default.WorkspaceEntry);

                // Stamp in the name so ordering survives later flag rewrites.
                var baseName = $"closed-{stamp:yyyyMMddHHmmssfff}-{Environment.ProcessId}";
                var path = Path.Combine(ClosedDir, baseName + ".json");
                for (int i = 1; File.Exists(path); i++)
                    path = Path.Combine(ClosedDir, $"{baseName}-{i}.json");

                File.WriteAllText(path, json);
                try { File.SetLastWriteTime(path, stamp); } catch { }
            }
            catch { }
        }

        /// <summary>All history entries, newest first.</summary>
        public static List<ClosedEntry> ListClosed()
        {
            var result = new List<ClosedEntry>();
            try
            {
                if (!Directory.Exists(ClosedDir)) return result;
                foreach (var path in Directory.GetFiles(ClosedDir, "closed-*.json")
                                              .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal))
                {
                    var entry = LoadEntry(path);
                    if (entry?.Layout == null) continue;
                    result.Add(new ClosedEntry
                    {
                        Path = path,
                        ClosedAt = File.GetLastWriteTime(path),
                        Entry = entry
                    });
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Pick the newest history entry flagged for auto-restore — Crashed
        /// (when crash recovery is enabled) or RestoreOnLaunch from a clean
        /// close — and clear the pending flags on *all* entries so no other
        /// launch restores a duplicate. Entries stay in history for the menu.
        /// </summary>
        public static PendingRestore? TryConsumePendingRestore(bool includeCrashed) =>
            WithLock<PendingRestore?>(() =>
            {
                PendingRestore? picked = null;
                foreach (var item in ListClosed())
                {
                    var e = item.Entry;
                    bool pending = e.RestoreOnLaunch || (includeCrashed && e.Crashed);
                    if (picked == null && pending)
                        picked = new PendingRestore { Entry = e, WasCrashed = e.Crashed };

                    if (e.RestoreOnLaunch || e.Crashed)
                    {
                        e.RestoreOnLaunch = false;
                        e.Crashed = false;
                        try
                        {
                            var stamp = File.GetLastWriteTime(item.Path);
                            File.WriteAllText(item.Path,
                                JsonSerializer.Serialize(e, WorkspaceJsonContext.Default.WorkspaceEntry));
                            File.SetLastWriteTime(item.Path, stamp);
                        }
                        catch { }
                    }
                }
                return picked;
            }, null);

        private static void PruneClosed()
        {
            try
            {
                if (!Directory.Exists(ClosedDir)) return;
                var files = Directory.GetFiles(ClosedDir, "closed-*.json")
                                     .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                                     .ToList();
                for (int i = MaxClosedEntries; i < files.Count; i++)
                    try { File.Delete(files[i]); } catch { }
            }
            catch { }
        }

        /// <summary>Menu "clear recovery data": own live snapshot + full history.</summary>
        public static void ClearAll() => WithLock<object?>(() =>
        {
            DeleteOwnSnapshot();
            try { if (File.Exists(LegacySnapshotFile)) File.Delete(LegacySnapshotFile); } catch { }
            try
            {
                if (Directory.Exists(ClosedDir))
                    foreach (var f in Directory.GetFiles(ClosedDir, "closed-*.json"))
                        try { File.Delete(f); } catch { }
            }
            catch { }
            return null;
        }, null);

        private static WorkspaceEntry? LoadEntry(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.WorkspaceEntry);
            }
            catch { return null; }
        }
    }
}
