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
    public sealed record CodexQuotaProfile(string Id, string Name, string Home);

    public enum CodexQuotaProfileError { None, InvalidName, InvalidLogin, Duplicate, Storage }

    [JsonSerializable(typeof(List<CodexQuotaProfile>))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class CodexQuotaProfilesJsonContext : JsonSerializerContext { }

    /// <summary>Stores display names and directory references only. Never writes a Codex profile.</summary>
    public sealed class CodexQuotaProfileStore
    {
        public const string DefaultId = "default";
        private readonly string _file;
        private readonly string _mutexName;
        public string DefaultHome { get; }

        public CodexQuotaProfileStore(string? dataDirectory = null, string? defaultHome = null)
        {
            _file = Path.Combine(dataDirectory ?? AppPaths.Root, "quota-accounts.json");
            DefaultHome = ResolveHome(defaultHome);
            _mutexName = "CCPad.QuotaAccounts." + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(_file).ToUpperInvariant())));
        }

        public static string ResolveHome(string? home = null)
        {
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(home.Trim())));
        }

        public List<CodexQuotaProfile> Load()
        {
            if (!File.Exists(_file)) return new();
            var profiles = JsonSerializer.Deserialize(File.ReadAllText(_file),
                CodexQuotaProfilesJsonContext.Default.ListCodexQuotaProfile)
                ?? throw new InvalidDataException("Invalid quota account configuration");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                if (profile is null || !Guid.TryParse(profile.Id, out _) ||
                    !ids.Add(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) ||
                    string.IsNullOrWhiteSpace(profile.Home) || !Path.IsPathFullyQualified(profile.Home))
                    throw new InvalidDataException("Invalid quota account configuration");
            }
            return profiles;
        }

        /// <summary>
        /// Return the profiles that may be used to launch a local Codex process.
        /// The built-in default is not written to quota-accounts.json; it is
        /// synthesized here so a launch selector can treat every account the same.
        /// </summary>
        public List<CodexQuotaProfile> LoadLaunchProfiles(string defaultName = "Default account")
        {
            var result = new List<CodexQuotaProfile>
            {
                new(DefaultId, string.IsNullOrWhiteSpace(defaultName) ? "Default account" : defaultName,
                    DefaultHome),
            };
            result.AddRange(Load());
            return result;
        }

        /// <summary>
        /// Check a profile without returning any credential or account identity.
        /// This is used after the official Codex login process exits.
        /// </summary>
        public static bool HasReadableChatGptLogin(string? home)
        {
            try
            {
                _ = ReadAccountKey(ResolveHome(home));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolve a launch profile without ever copying or mutating its auth files.
        /// Empty/unknown IDs are reported as unavailable so callers can refuse to
        /// resume a conversation under the wrong account.
        /// </summary>
        public bool TryResolveLaunchProfile(string? id, out CodexQuotaProfile profile,
            string defaultName = "Default account")
        {
            var normalized = string.IsNullOrWhiteSpace(id) ? DefaultId : id.Trim();
            if (string.Equals(normalized, DefaultId, StringComparison.Ordinal))
            {
                profile = new CodexQuotaProfile(DefaultId,
                    string.IsNullOrWhiteSpace(defaultName) ? "Default account" : defaultName,
                    DefaultHome);
                return true;
            }

            try
            {
                profile = Load().FirstOrDefault(p => p.Id == normalized)!;
                return profile != null;
            }
            catch
            {
                profile = null!;
                return false;
            }
        }

        public List<CodexQuotaProfile> LoadInactive()
        {
            var currentKey = CodexAccountIdentity.Read(DefaultHome)?.Key;
            return Load().Where(p => currentKey == null || CodexAccountIdentity.Read(p.Home)?.Key != currentKey).ToList();
        }

        public CodexQuotaProfileError TryAdd(string name, string home, out CodexQuotaProfile? added)
            => TryAddCore(name, home, rememberCurrent: false, replaceExisting: false, out added);

        /// <summary>
        /// Add a quota profile, replacing the existing profile when the login
        /// identity is already registered. Official login creates a fresh
        /// CODEX_HOME each time, so rejecting the same account would leave the
        /// refreshed credentials stranded in an untracked directory.
        /// </summary>
        public CodexQuotaProfileError TryAddOrReplace(string name, string home, out CodexQuotaProfile? added)
            => TryAddCore(name, home, rememberCurrent: false, replaceExisting: true, out added);

        internal CodexQuotaProfileError TryRememberCurrentAccount(string name, string home, out CodexQuotaProfile? added)
            => TryAddCore(name.Length > 40 ? name[..40] : name, home,
                rememberCurrent: true, replaceExisting: false, out added);

        private CodexQuotaProfileError TryAddCore(string name, string home, bool rememberCurrent,
            bool replaceExisting, out CodexQuotaProfile? added)
        {
            added = null;
            name = name.Trim();
            if (name.Length == 0 || name.Length > 40 || name.Any(char.IsControl))
                return CodexQuotaProfileError.InvalidName;
            string canonicalHome;
            string accountKey;
            try
            {
                // Resolve a directory junction as well as normalizing case/trailing separators.
                canonicalHome = ResolveHome(home);
                if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(canonicalHome))
                    return CodexQuotaProfileError.InvalidLogin;
                canonicalHome = CanonicalDirectory(canonicalHome);
                accountKey = ReadAccountKey(canonicalHome);
            }
            catch { return CodexQuotaProfileError.InvalidLogin; }
            try
            {
                using var mutex = new Mutex(false, _mutexName);
                if (!Acquire(mutex)) return CodexQuotaProfileError.Storage;
                try
                {
                    var profiles = Load(); // Reload under the lock: another window may have added an account.
                    if (rememberCurrent && profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        name = name[..Math.Min(name.Length, 30)] + " " + accountKey[..8];
                    // The built-in default account has no editable profile row.
                    // Never turn a re-login of it into an additional row.
                    if (SameDirectory(canonicalHome, DefaultHome) ||
                        (!rememberCurrent && accountKey == TryReadAccountKey(DefaultHome)))
                        return CodexQuotaProfileError.Duplicate;

                    var existing = profiles.FirstOrDefault(p =>
                        SameDirectory(canonicalHome, p.Home) || accountKey == TryReadAccountKey(p.Home));
                    if (replaceExisting && existing != null)
                    {
                        // A display-name collision with a different identity is
                        // still an error; replacing that row would hide the
                        // other account from the user.
                        if (profiles.Any(p => p.Id != existing.Id &&
                            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                            return CodexQuotaProfileError.Duplicate;
                        var replacement = existing with { Name = name, Home = canonicalHome };
                        profiles[profiles.IndexOf(existing)] = replacement;
                        Save(profiles);
                        added = replacement;
                        return CodexQuotaProfileError.None;
                    }

                    if (existing != null || profiles.Any(p =>
                        string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return CodexQuotaProfileError.Duplicate;
                    var candidate = new CodexQuotaProfile(Guid.NewGuid().ToString("D"), name, canonicalHome);
                    profiles.Add(candidate);
                    Save(profiles);
                    added = candidate;
                    return CodexQuotaProfileError.None;
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch { return CodexQuotaProfileError.Storage; }
        }

        public bool TryRemove(string id)
        {
            if (id == DefaultId) return false;
            try
            {
                using var mutex = new Mutex(false, _mutexName);
                if (!Acquire(mutex)) return false;
                try
                {
                    var profiles = Load();
                    profiles.RemoveAll(p => p.Id == id);
                    Save(profiles);
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch { return false; }
        }

        private static bool Acquire(Mutex mutex)
        {
            try { return mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { return true; }
        }

        private void Save(List<CodexQuotaProfile> profiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles,
                CodexQuotaProfilesJsonContext.Default.ListCodexQuotaProfile));
            File.Move(temporary, _file, overwrite: true);
        }

        private static string CanonicalDirectory(string home)
        {
            var info = new DirectoryInfo(home);
            return ResolveHome(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
        }

        private static bool SameDirectory(string left, string right)
        {
            try { return string.Equals(CanonicalDirectory(left), CanonicalDirectory(right), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        }

        private static string? TryReadAccountKey(string home)
        {
            try { return ReadAccountKey(home); }
            catch { return null; }
        }

        private static string ReadAccountKey(string home)
        {
            return CodexAccountIdentity.Read(home)?.Key ??
                throw new InvalidDataException("A saved ChatGPT login is required");
        }
    }
}
