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

        private static string CodexSessionsDir(string? codexHome = null)
        {
            var root = codexHome;
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            try { root = Environment.ExpandEnvironmentVariables(root.Trim()); }
            catch { }
            return Path.Combine(root, "sessions");
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

        public static bool CodexSessionExists(string sessionId, string? codexHome = null)
        {
            if (!IsUuid(sessionId)) return false;
            try
            {
                var dir = CodexSessionsDir(codexHome);
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
        public static string? FindLatestCodexSessionId(string? workingDir, DateTime? notBeforeUtc,
            ISet<string>? excludeIds = null, string? codexHome = null)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            try
            {
                var dir = CodexSessionsDir(codexHome);
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

        /// <summary>Old snapshots may contain an inherited worker notify ID. Follow only
        /// explicit subagent ancestry; a user-created fork is its own conversation.</summary>
        public static string? ResolveCodexResumeSessionId(string sessionId, string? codexHome = null)
        {
            try
            {
                var dir = CodexSessionsDir(codexHome);
                if (!Directory.Exists(dir)) return null;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (IsUuid(sessionId) && seen.Count < 32 && seen.Add(sessionId))
                {
                    var path = Directory.EnumerateFiles(dir, "rollout-*-" + sessionId + ".jsonl",
                        SearchOption.AllDirectories).FirstOrDefault();
                    var meta = path == null ? null : ReadCodexMeta(path);
                    if (meta == null || !string.Equals(meta.Id, sessionId, StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (meta.Interactive) return meta.Id;
                    if (meta.ParentId == null) return null;
                    sessionId = meta.ParentId;
                }
            }
            catch { }
            return null;
        }

        private sealed record CodexMeta(string Id, string? Cwd, bool Interactive, string? ParentId);

        private static CodexMeta? ReadCodexMeta(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return null;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                // Current Codex writes an explicit session_meta marker. Older
                // rollouts (and saved CCPad snapshots) only had the payload on
                // the first line, so keep accepting that legacy shape while
                // rejecting an explicitly different record type.
                if (root.TryGetProperty("type", out var type) &&
                    (type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta"))
                    return null;
                if (!root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object) return null;

                string? Text(JsonElement obj, string name) =>
                    obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() : null;
                var id = Text(payload, "id") ?? Text(payload, "session_id");
                if (!IsUuid(id)) return null;
                var parent = Text(payload, "parent_thread_id");
                var agentPath = Text(payload, "agent_path");
                bool child = !string.IsNullOrEmpty(parent) ||
                    (!string.IsNullOrEmpty(agentPath) && agentPath != "/root");
                bool interactive = true; // Legacy rollouts can omit source.
                if (payload.TryGetProperty("source", out var source))
                {
                    interactive = source.ValueKind == JsonValueKind.String &&
                        (source.GetString() == "cli" || source.GetString() == "vscode" ||
                         source.GetString() == "tui");
                    if (source.ValueKind == JsonValueKind.String && source.GetString() == "subagent")
                        child = true;
                    if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out var subagent))
                    {
                        child = true;
                        if (subagent.ValueKind == JsonValueKind.Object &&
                            subagent.TryGetProperty("thread_spawn", out var spawn) &&
                            spawn.ValueKind == JsonValueKind.Object)
                        {
                            var sourceParent = Text(spawn, "parent_thread_id");
                            if (parent != null && sourceParent != null &&
                                !string.Equals(parent, sourceParent, StringComparison.OrdinalIgnoreCase)) return null;
                            parent ??= sourceParent;
                        }
                    }
                }
                if (child) parent ??= Text(payload, "forked_from_id");
                return new CodexMeta(id!, Text(payload, "cwd"), interactive && !child,
                    child && IsUuid(parent) ? parent : null);
            }
            catch { return null; }
        }

        private static string? MatchCodexMeta(string path, string workingDir)
        {
            var meta = ReadCodexMeta(path);
            return meta is { Interactive: true } && PathsEqual(meta.Cwd, workingDir) &&
                path.EndsWith("-" + meta.Id + ".jsonl", StringComparison.OrdinalIgnoreCase) ? meta.Id : null;
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
