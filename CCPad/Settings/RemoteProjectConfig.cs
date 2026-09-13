using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCPad.Settings
{
    /// <summary>A project whose files and CLI both live on an SSH host.</summary>
    public sealed class RemoteProjectEntry
    {
        public string Name { get; set; } = "";
        public string ProfileId { get; set; } = RemoteDeviceConfig.LegacyDeviceId;
        public string RemotePath { get; set; } = "";
    }

    [JsonSerializable(typeof(List<RemoteProjectEntry>))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class RemoteProjectJsonContext : JsonSerializerContext { }

    /// <summary>
    /// Remote projects are deliberately stored separately from local projects:
    /// a Linux path must never be passed to ConPTY as a Windows working directory.
    /// ProfileId is persisted now even though the first UI exposes the existing
    /// Codex@167 profile only, so adding more SSH profiles later is migration-free.
    /// </summary>
    public static class RemoteProjectConfig
    {
        public const string DefaultProfileId = RemoteDeviceConfig.LegacyDeviceId;
        /// <summary>Working directory used by the external menu's generic
        /// "new" and "recover" shortcuts. Saved project entries always use
        /// their own <see cref="RemoteProjectEntry.RemotePath"/> instead.</summary>
        public const string DefaultWorkingDir = RemoteDeviceConfig.DefaultWorkingDir;
        private static readonly string ConfigFile = AppPaths.Sub("remote-projects.json");

        public static event Action? Changed;

        public static List<RemoteProjectEntry> Load()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile);
                    return JsonSerializer.Deserialize(
                        json, RemoteProjectJsonContext.Default.ListRemoteProjectEntry) ?? new();
                }
            }
            catch { }
            return new();
        }

        public static void Save(List<RemoteProjectEntry> projects)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                string json = JsonSerializer.Serialize(
                    projects, RemoteProjectJsonContext.Default.ListRemoteProjectEntry);
                File.WriteAllText(ConfigFile, json);
                Changed?.Invoke();
            }
            catch { }
        }
    }
}
