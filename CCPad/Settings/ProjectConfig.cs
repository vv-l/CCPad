using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCPad.Settings
{
    public class ProjectEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    [JsonSerializable(typeof(List<ProjectEntry>))]
    internal partial class ProjectJsonContext : JsonSerializerContext { }

    public static class ProjectConfig
    {
        private static readonly string ConfigDir = AppPaths.Root;

        private static readonly string ConfigFile = System.IO.Path.Combine(ConfigDir, "projects.json");

        public static List<ProjectEntry> Load()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile);
                    return JsonSerializer.Deserialize(json, ProjectJsonContext.Default.ListProjectEntry) ?? new();
                }
            }
            catch { }
            return new();
        }

        public static void Save(List<ProjectEntry> projects)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var json = JsonSerializer.Serialize(projects, ProjectJsonContext.Default.ListProjectEntry);
                File.WriteAllText(ConfigFile, json);
            }
            catch { }
        }
    }
}
