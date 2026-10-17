using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using CCPad.Settings;

namespace CCPad.CodexQuota
{
    public sealed record CodexSubscriptionNote(DateOnly? RechargedOn = null, DateOnly? ExpiresOn = null, string Note = "")
    {
        [JsonIgnore]
        public bool IsValid => (RechargedOn == null || ExpiresOn == null || ExpiresOn >= RechargedOn) &&
            Note != null && Note.Length <= 160 && !Note.Any(char.IsControl);
        [JsonIgnore]
        public bool IsEmpty => RechargedOn == null && ExpiresOn == null && string.IsNullOrWhiteSpace(Note);
    }

    [JsonSerializable(typeof(Dictionary<string, CodexSubscriptionNote>))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class CodexSubscriptionsJsonContext : JsonSerializerContext { }

    /// <summary>Manual subscription notes keyed by account identity, including the default account.
    /// Stored outside login directories and kept across profile removal/re-addition.</summary>
    public sealed class CodexSubscriptionStore
    {
        private readonly string _file;
        private readonly string _mutexName;

        public CodexSubscriptionStore(string? dataDirectory = null)
        {
            _file = Path.Combine(dataDirectory ?? AppPaths.Root, "codex-subscriptions.json");
            _mutexName = "CCPad.Subscriptions." + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(_file).ToUpperInvariant())));
        }

        public bool TryLoad(string accountKey, out CodexSubscriptionNote note)
        {
            note = new();
            if (!ValidKey(accountKey)) return false;
            try
            {
                note = Load().GetValueOrDefault(accountKey) ?? new();
                return true;
            }
            catch { return false; }
        }

        public bool TrySave(string accountKey, CodexSubscriptionNote note)
        {
            if (!ValidKey(accountKey) || !note.IsValid) return false;
            try
            {
                using var mutex = new Mutex(false, _mutexName);
                try { if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) return false; }
                catch (AbandonedMutexException) { }
                try
                {
                    // Reload under the lock so separate CCPad windows preserve each other's notes.
                    var notes = Load();
                    if (note.IsEmpty) notes.Remove(accountKey);
                    else notes[accountKey] = note with { Note = note.Note.Trim() };
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temporary, JsonSerializer.Serialize(notes,
                            CodexSubscriptionsJsonContext.Default.DictionaryStringCodexSubscriptionNote));
                        File.Move(temporary, _file, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch { return false; }
        }

        private Dictionary<string, CodexSubscriptionNote> Load()
        {
            if (!File.Exists(_file)) return new();
            var notes = JsonSerializer.Deserialize(File.ReadAllText(_file),
                CodexSubscriptionsJsonContext.Default.DictionaryStringCodexSubscriptionNote)
                ?? throw new InvalidDataException("Invalid subscription notes");
            if (notes.Any(pair => !ValidKey(pair.Key) || pair.Value == null || !pair.Value.IsValid))
                throw new InvalidDataException("Invalid subscription notes");
            return notes;
        }

        private static bool ValidKey(string key) => key is { Length: 64 } &&
            key.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    }
}
