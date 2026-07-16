using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CCPad.Settings
{
    /// <summary>
    /// Locates saved CLI conversations on disk so restored tabs can resume them.
    /// - Claude stores sessions as ~/.claude/projects/&lt;cwd-slug&gt;/&lt;uuid&gt;.jsonl.
    ///   We assign the UUID ourselves (--session-id), so we only need an existence check.
    /// - Codex assigns its own IDs, stored as
    ///   ~/.codex/sessions/YYYY/MM/DD/rollout-&lt;stamp&gt;-&lt;uuid&gt;.jsonl with the working
    ///   directory in the first-line session_meta JSON. We harvest the newest ID for a
    ///   given cwd at snapshot time.
    /// All lookups are best-effort: any IO/parse failure just means "no session".
    /// </summary>
    public static class CliSessions
    {
        private static string ClaudeProjectsDir
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return Path.Combine(root, "projects");
            }
        }

        private static string CodexSessionsDir
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                return Path.Combine(root, "sessions");
            }
        }

        public static bool ClaudeSessionExists(string sessionId)
        {
            if (!IsUuid(sessionId)) return false;
            try
            {
                var dir = ClaudeProjectsDir;
                if (!Directory.Exists(dir)) return false;
                return Directory.EnumerateFiles(dir, sessionId + ".jsonl", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }

        /// <summary>
        /// Newest Claude conversation on disk for <paramref name="workingDir"/> that was
        /// written after <paramref name="notBeforeUtc"/> (the pane's launch) and is not in
        /// <paramref name="excludeIds"/>. Fallback for panes whose tracked session ID has
        /// no conversation file — the user ran /clear or /resume inside the CLI, or
        /// relaunched claude from the fallback shell, so the real conversation lives
        /// under a different UUID. Returns null when nothing fits.
        /// </summary>
        public static string? FindLatestClaudeSessionId(string? workingDir, DateTime? notBeforeUtc, ISet<string>? excludeIds = null)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            try
            {
                var dir = Path.Combine(ClaudeProjectsDir, ClaudeProjectSlug(workingDir));
                if (!Directory.Exists(dir)) return null;
                return Directory.EnumerateFiles(dir, "*.jsonl")
                    .Select(p => new FileInfo(p))
                    .Where(f => notBeforeUtc == null || f.LastWriteTimeUtc >= notBeforeUtc.Value.AddMinutes(-1))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                    .FirstOrDefault(id => IsUuid(id) &&
                        (excludeIds == null || !excludeIds.Contains(id)));
            }
            catch { return null; }
        }

        /// <summary>Claude encodes a session's cwd as a projects/ subfolder name by
        /// replacing every non-alphanumeric character with '-' (D:\CC Pad → D--CC-Pad).</summary>
        private static string ClaudeProjectSlug(string workingDir)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDir));
            var sb = new StringBuilder(full.Length);
            foreach (var ch in full)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
            return sb.ToString();
        }

        public static bool CodexSessionExists(string sessionId)
        {
            if (!IsUuid(sessionId)) return false;
            try
            {
                var dir = CodexSessionsDir;
                if (!Directory.Exists(dir)) return false;
                return Directory.EnumerateFiles(dir, "rollout-*" + sessionId + ".jsonl", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }

        /// <summary>
        /// Newest interactive Codex session recorded for <paramref name="workingDir"/>,
        /// optionally ignoring files older than <paramref name="notBeforeUtc"/> (the pane's
        /// launch time), so a snapshot doesn't pick up an unrelated older conversation.
        /// IDs in <paramref name="excludeIds"/> (already owned by another tab) are skipped —
        /// two same-cwd panes must never persist the same conversation. Returns null when
        /// nothing matches.
        /// </summary>
        public static string? FindLatestCodexSessionId(string? workingDir, DateTime? notBeforeUtc, ISet<string>? excludeIds = null)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            try
            {
                var dir = CodexSessionsDir;
                if (!Directory.Exists(dir)) return null;

                var files = Directory.EnumerateFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .Where(f => notBeforeUtc == null || f.LastWriteTimeUtc >= notBeforeUtc.Value.AddMinutes(-1))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(200); // bound the scan; anything past this is weeks old

                foreach (var file in files)
                {
                    var id = MatchCodexMeta(file.FullName, workingDir);
                    if (id != null && (excludeIds == null || !excludeIds.Contains(id)))
                        return id;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Parse the first-line session_meta and return the session ID when its
        /// cwd matches; null otherwise. Non-interactive ("exec") sessions are skipped —
        /// `codex resume` hides those by default and they never came from a CCPad pane.</summary>
        private static string? MatchCodexMeta(string path, string workingDir)
        {
            try
            {
                using var reader = new StreamReader(path);
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return null;

                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("payload", out var payload)) return null;

                if (payload.TryGetProperty("source", out var source) &&
                    string.Equals(source.GetString(), "exec", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (!payload.TryGetProperty("cwd", out var cwd) ||
                    !PathsEqual(cwd.GetString(), workingDir))
                    return null;

                if (payload.TryGetProperty("id", out var id) && IsUuid(id.GetString()))
                    return id.GetString();
                if (payload.TryGetProperty("session_id", out var sid) && IsUuid(sid.GetString()))
                    return sid.GetString();
            }
            catch { }
            return null;
        }

        private static bool PathsEqual(string? a, string? b)
        {
            if (a == null || b == null) return false;
            static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool IsUuid(string? s) => Guid.TryParse(s, out _);
    }
}
