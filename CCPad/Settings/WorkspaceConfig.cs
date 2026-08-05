using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace CCPad.Settings
{
    public class TabState
    {
        public string Name { get; set; } = "";
        public string WorkingDir { get; set; } = "";

        /// <summary>"claude", "codex", or "codex-remote". Empty/missing → use
        /// AppPrefs default at restore time.</summary>
        public string CliMode { get; set; } = "";

        /// <summary>CLI conversation ID (UUID). Claude: assigned by us via --session-id at
        /// launch. Local Codex: harvested from ~/.codex/sessions at snapshot time.
        /// Remote Codex: always empty because its named tmux session owns continuity.
        /// Empty → the restored local-CLI tab starts a fresh conversation.</summary>
        public string SessionId { get; set; } = "";

        /// <summary>User-defined tag shown as a badge next to the tab title. Empty → none.</summary>
        public string Tag { get; set; } = "";

        /// <summary>Tab was frozen (processes shut down, placeholder shown) when this
        /// snapshot was taken. Restore recreates it as a frozen placeholder — no
        /// WebView2 or CLI is started until the user thaws it.</summary>
        public bool Frozen { get; set; }
    }

    public class LayoutNode
    {
        public string Type { get; set; } = "pane"; // "pane" | "split"

        // Type == "pane"
        public List<TabState>? Tabs { get; set; }
        public int ActiveTabIndex { get; set; }

        // Type == "split"
        public string? Orientation { get; set; } // "horizontal" | "vertical"
        public double SplitRatio { get; set; } = 0.5;
        public LayoutNode? First { get; set; }
        public LayoutNode? Second { get; set; }
    }

    public class WorkspaceEntry
    {
        public int WindowWidth { get; set; } = 1200;
        public int WindowHeight { get; set; } = 800;
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public bool IsMaximized { get; set; }
        public LayoutNode? Layout { get; set; }

        /// <summary>Set only by the confirmed-close path when the user leaves the
        /// "restore next launch" checkbox ticked. Autosave snapshots always write
        /// false, so a crash never silently restores via this flag.</summary>
        public bool RestoreOnLaunch { get; set; }

        /// <summary>Set when the crash sweep archives a dead instance's autosave
        /// into the closed-session history. Like RestoreOnLaunch it marks the
        /// entry pending auto-restore; unlike it, restoring goes through the
        /// crash-loop guard. Cleared once any launch consumes the pending set.</summary>
        public bool Crashed { get; set; }

        // Optional metadata used by .ccpad-template files. Ordinary workspaces
        // and recovery snapshots leave these unset and remain fully compatible.
        public string TemplateId { get; set; } = "";
        public string TemplateName { get; set; } = "";
        public int TemplateOrder { get; set; }
        public DateTime? TemplateCreatedAt { get; set; }
        public DateTime? TemplateUpdatedAt { get; set; }
    }

    [JsonSerializable(typeof(WorkspaceEntry))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class WorkspaceJsonContext : JsonSerializerContext { }

    /// <summary>
    /// Workspace files are standalone .ccpad-workspace JSON files (like VS Code's .code-workspace).
    /// Only the last-session auto-save lives in %LOCALAPPDATA%\CCPad.
    /// </summary>
    public static class WorkspaceConfig
    {
        public const string FileExtension = ".ccpad-workspace";
        public const string TemplateExtension = ".ccpad-template";

        public static bool IsWorkspaceOrTemplateFile(string path)
            => path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(TemplateExtension, StringComparison.OrdinalIgnoreCase);

        public static bool IsTemplateFile(string path)
            => path.EndsWith(TemplateExtension, StringComparison.OrdinalIgnoreCase);

        // ── File-based workspace ──────────────────────────────────────

        public static WorkspaceEntry? LoadFromFile(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.WorkspaceEntry);
            }
            catch { return null; }
        }

        public static bool SaveToFile(string path, WorkspaceEntry entry)
        {
            try
            {
                var json = JsonSerializer.Serialize(entry, WorkspaceJsonContext.Default.WorkspaceEntry);
                File.WriteAllText(path, json);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Turn a freshly-created window snapshot into a reusable frozen template.
        /// Layout, names, directories, tags and conversation IDs are preserved, but
        /// every tab starts as a lightweight placeholder when the template is opened.
        /// </summary>
        public static void MarkAllTabsFrozen(WorkspaceEntry entry)
        {
            entry.RestoreOnLaunch = false;
            entry.Crashed = false;
            MarkNodeFrozen(entry.Layout);
        }

        private static void MarkNodeFrozen(LayoutNode? node)
        {
            if (node == null) return;
            if (node.Tabs != null)
                foreach (var tab in node.Tabs)
                    tab.Frozen = true;
            MarkNodeFrozen(node.First);
            MarkNodeFrozen(node.Second);
        }

    }
}
