using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCPad.Settings
{
    /// <summary>One Linux host that CC Pad can reach with Windows OpenSSH.</summary>
    public sealed class RemoteDeviceEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string User { get; set; } = "root";
        /// <summary>Path only. Private-key contents and passwords are never stored.</summary>
        public string KeyPath { get; set; } = "";
        public string DefaultWorkingDir { get; set; } = RemoteDeviceConfig.DefaultWorkingDir;
        public string CodexCommand { get; set; } = "codex";
        /// <summary>POSIX-shell command with {dir}, {session}, and {codex} placeholders.</summary>
        public string LaunchCommand { get; set; } = RemoteDeviceConfig.DefaultLaunchCommand;
        public string SessionPrefix { get; set; } = "ccpad";
        public int SweepIdleHours { get; set; } = 48;
    }

    public sealed class RemoteDeviceDocument
    {
        public int Version { get; set; } = 1;
        public string SelectedDeviceId { get; set; } = "";
        public List<RemoteDeviceEntry> Devices { get; set; } = new();
    }

    [JsonSerializable(typeof(RemoteDeviceDocument))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class RemoteDeviceJsonContext : JsonSerializerContext { }

    /// <summary>Persistent SSH device registry shared by the UI and onboarding skill.</summary>
    public static class RemoteDeviceConfig
    {
        public const string LegacyDeviceId = "codex-167";
        public const string DefaultWorkingDir = "/zettos/pool/1/agents";
        public const string DefaultLaunchCommand =
            "cd {dir} && export LANG=C.UTF-8 LC_ALL=C.UTF-8 && tmux -u new -A -s {session} {codex}";

        private static readonly string ConfigFile = AppPaths.Sub("remote-devices.json");
        public static event Action? Changed;

        public static RemoteDeviceDocument Load()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var doc = JsonSerializer.Deserialize(
                        File.ReadAllText(ConfigFile), RemoteDeviceJsonContext.Default.RemoteDeviceDocument)
                        ?? new RemoteDeviceDocument();
                    Normalize(doc);
                    return doc;
                }
            }
            catch { }

            // One-time compatibility bridge for the former single Codex@167
            // object in prefs.json. It deliberately preserves customized launch
            // commands and key paths.
            var legacy = AppConfig.Load().RemoteCodex ?? new RemoteCodexConfig();
            var migrated = new RemoteDeviceDocument
            {
                SelectedDeviceId = LegacyDeviceId,
                Devices = new List<RemoteDeviceEntry>
                {
                    new()
                    {
                        Id = LegacyDeviceId,
                        Name = "Codex@167",
                        Host = legacy.Host ?? "",
                        User = legacy.User ?? "root",
                        KeyPath = legacy.KeyPath ?? "",
                        DefaultWorkingDir = string.IsNullOrWhiteSpace(legacy.RemoteDir)
                            ? DefaultWorkingDir : legacy.RemoteDir,
                        LaunchCommand = string.IsNullOrWhiteSpace(legacy.RemoteCommand)
                            ? DefaultLaunchCommand : legacy.RemoteCommand,
                        SessionPrefix = legacy.SessionPrefix ?? "ccpad",
                        SweepIdleHours = legacy.SweepIdleHours,
                    }
                }
            };
            SaveCore(migrated, notify: false);
            return migrated;
        }

        public static void Save(RemoteDeviceDocument document) => SaveCore(document, notify: true);

        public static RemoteDeviceEntry? Find(string? id = null)
        {
            var doc = Load();
            string wanted = string.IsNullOrWhiteSpace(id) ? doc.SelectedDeviceId : id!;
            return doc.Devices.FirstOrDefault(d => string.Equals(d.Id, wanted, StringComparison.Ordinal))
                ?? doc.Devices.FirstOrDefault();
        }

        public static string NewId(string? name)
        {
            string basis = string.IsNullOrWhiteSpace(name) ? "linux" : name!;
            var chars = basis.ToLowerInvariant().Select(c =>
                char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
            string slug = new string(chars).Trim('-');
            while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
            if (slug.Length > 28) slug = slug[..28].TrimEnd('-');
            if (slug.Length == 0) slug = "linux";
            return $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 37)];
        }

        private static void SaveCore(RemoteDeviceDocument document, bool notify)
        {
            try
            {
                Normalize(document);
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllText(ConfigFile, JsonSerializer.Serialize(
                    document, RemoteDeviceJsonContext.Default.RemoteDeviceDocument));
                if (notify) Changed?.Invoke();
            }
            catch { }
        }

        private static void Normalize(RemoteDeviceDocument doc)
        {
            doc.Version = 1;
            doc.Devices ??= new();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in doc.Devices)
            {
                if (string.IsNullOrWhiteSpace(d.Id) || !used.Add(d.Id))
                {
                    d.Id = NewId(d.Name);
                    used.Add(d.Id);
                }
                if (string.IsNullOrWhiteSpace(d.Name)) d.Name = d.Host;
                if (d.Port is < 1 or > 65535) d.Port = 22;
                if (string.IsNullOrWhiteSpace(d.User)) d.User = "root";
                if (string.IsNullOrWhiteSpace(d.DefaultWorkingDir)) d.DefaultWorkingDir = DefaultWorkingDir;
                if (string.IsNullOrWhiteSpace(d.CodexCommand)) d.CodexCommand = "codex";
                if (string.IsNullOrWhiteSpace(d.LaunchCommand)) d.LaunchCommand = DefaultLaunchCommand;
                // The built-in 167 profile is intentionally the trusted,
                // highest-permission device. Normalize both schema variants:
                // newer templates use {codex}; migrated v1 templates ended in
                // a literal `codex` and otherwise ignored CodexCommand.
                if (d.Id == LegacyDeviceId)
                {
                    if (string.Equals(d.CodexCommand.Trim(), "codex", StringComparison.Ordinal))
                        d.CodexCommand = "codex --yolo";
                    if (d.LaunchCommand.TrimEnd().EndsWith(" codex", StringComparison.Ordinal))
                        d.LaunchCommand = d.LaunchCommand.TrimEnd() + " --yolo";
                }
                if (string.IsNullOrWhiteSpace(d.SessionPrefix)) d.SessionPrefix = "ccpad";
                d.SweepIdleHours = Math.Max(1, d.SweepIdleHours);
            }
            if (doc.Devices.Count == 0) doc.SelectedDeviceId = "";
            else if (!doc.Devices.Any(d => d.Id == doc.SelectedDeviceId))
                doc.SelectedDeviceId = doc.Devices[0].Id;
        }
    }
}
