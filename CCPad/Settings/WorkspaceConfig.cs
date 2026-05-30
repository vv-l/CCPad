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

        /// <summary>"claude" or "codex". Empty/missing → use AppPrefs default at restore time.</summary>
        public string CliMode { get; set; } = "";
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

    }
}
