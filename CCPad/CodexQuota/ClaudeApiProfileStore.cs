using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CCPad.Settings;

namespace CCPad.CodexQuota;

public sealed record ClaudeApiProfile(string Id, string Name, string BaseUrl, string ProtectedKey, string AuthType);

// Deliberately not a record: its generated ToString must never include a credential.
public sealed class ClaudeApiConnection
{
    public string ProfileId { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Key { get; init; } = "";
    public string AuthType { get; init; } = "bearer";

    public Dictionary<string, string> CreateEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Claude otherwise reapplies settings.json's env over the child's environment.
        // Host-owned routing also covers manually relaunched Claude in the fallback shell.
        ["CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"] = "1",
        ["ANTHROPIC_BASE_URL"] = BaseUrl,
        ["ANTHROPIC_AUTH_TOKEN"] = AuthType == "bearer" ? Key : "",
        ["ANTHROPIC_API_KEY"] = AuthType == "api-key" ? Key : "",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "",
        ["CLAUDE_CODE_USE_BEDROCK"] = "",
        ["CLAUDE_CODE_USE_VERTEX"] = "",
        ["CLAUDE_CODE_USE_FOUNDRY"] = "",
        ["ANTHROPIC_CUSTOM_HEADERS"] = "",
    };
}

public sealed class ClaudeApiProfileStore
{
    private readonly string _directory;
    private readonly string _configDirectory;
    public ClaudeApiProfileStore(string? directory = null, string? configDirectory = null)
    {
        _directory = directory ?? AppPaths.Sub("claude-api-profiles");
        _configDirectory = configDirectory ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public IReadOnlyList<ClaudeApiProfile> Load()
    {
        if (!Directory.Exists(_directory)) return Array.Empty<ClaudeApiProfile>();
        var profiles = new List<ClaudeApiProfile>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<ClaudeApiProfile>(File.ReadAllText(path));
                if (profile != null && Guid.TryParseExact(profile.Id, "N", out _) &&
                    Path.GetFileNameWithoutExtension(path) == profile.Id)
                    profiles.Add(profile);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Invalid API address");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public ClaudeApiProfile Add(string name, string baseUrl, string key, string authType)
    {
        name = name.Trim(); key = key.Trim();
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl) || key.Length == 0 ||
            key.Any(char.IsWhiteSpace) || authType is not ("bearer" or "api-key"))
            throw new ArgumentException("Invalid API profile");
        var profile = new ClaudeApiProfile(Guid.NewGuid().ToString("N"), name,
            NormalizeBaseUrl(baseUrl), ProtectKey(key), authType);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, profile.Id + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile));
        File.Move(temp, path);
        return profile;
    }

    public ClaudeApiConnection? Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id)) return ReadLocalConnection();
        if (id.StartsWith("local:", StringComparison.Ordinal))
        {
            var local = ReadLocalConnection();
            if (local != null && LocalProfileId(local.BaseUrl, local.AuthType, local.Key) == id) return local;
            foreach (var saved in Load())
            {
                try
                {
                    var candidate = OpenProfile(saved);
                    if (LocalProfileId(candidate.BaseUrl, candidate.AuthType, candidate.Key) == id) return candidate;
                }
                catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException("claude_api_missing");
        }
        var profile = Load().FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("claude_api_missing");
        return OpenProfile(profile);
    }

    private static ClaudeApiConnection OpenProfile(ClaudeApiProfile profile)
    {
        try
        {
            var key = UnprotectKey(profile.ProtectedKey);
            if (key.Length == 0 || key.Any(char.IsWhiteSpace) || profile.AuthType is not ("bearer" or "api-key"))
                throw new InvalidOperationException();
            return new ClaudeApiConnection { ProfileId = profile.Id, Name = profile.Name, BaseUrl = NormalizeBaseUrl(profile.BaseUrl),
                Key = key, AuthType = profile.AuthType };
        }
        catch { throw new InvalidOperationException("claude_api_missing"); }
    }

    public ClaudeApiConnection? ReadLocalConnection()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL" })
            env[key] = Environment.GetEnvironmentVariable(key);
        var path = Path.Combine(_configDirectory, "settings.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("env", out var settings) && settings.ValueKind == JsonValueKind.Object &&
                Environment.GetEnvironmentVariable("CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST") != "1")
                foreach (var key in env.Keys.ToArray())
                    if (settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        env[key] = value.GetString();
        }
        var bearer = env["ANTHROPIC_AUTH_TOKEN"];
        var apiKey = env["ANTHROPIC_API_KEY"];
        if (string.IsNullOrWhiteSpace(bearer) && string.IsNullOrWhiteSpace(apiKey)) return null;
        var baseUrl = NormalizeBaseUrl(string.IsNullOrWhiteSpace(env["ANTHROPIC_BASE_URL"])
            ? "https://api.anthropic.com" : env["ANTHROPIC_BASE_URL"]!);
        var credentialKey = !string.IsNullOrWhiteSpace(bearer) ? bearer! : apiKey!;
        var authType = !string.IsNullOrWhiteSpace(bearer) ? "bearer" : "api-key";
        var matches = FindMatchingProfiles(baseUrl, authType, credentialKey);
        return new ClaudeApiConnection { ProfileId = matches.FirstOrDefault()?.Id ?? LocalProfileId(baseUrl, authType, credentialKey),
            Name = string.Join(" / ", matches.Select(p => p.Name).Distinct()),
            BaseUrl = baseUrl, Key = credentialKey, AuthType = authType };
    }

    // An unnamed local key may change between runs. Store a comparison token only.
    private static string LocalProfileId(string baseUrl, string authType, string key)
        => "local:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl + "\n" + authType + "\n" + key)));

    public string SessionProfileName(ClaudeApiConnection connection)
    {
        if (connection.Name.Length > 0) return connection.Name;
        try { return string.Join(" / ", FindMatchingProfiles(connection.BaseUrl, connection.AuthType, connection.Key).Select(p => p.Name).Distinct()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private List<ClaudeApiProfile> FindMatchingProfiles(string baseUrl, string authType, string credentialKey)
    {
        var matches = new List<ClaudeApiProfile>();
        foreach (var profile in Load())
        {
            try
            {
                if (profile.AuthType == authType && NormalizeBaseUrl(profile.BaseUrl) == baseUrl &&
                    string.Equals(UnprotectKey(profile.ProtectedKey), credentialKey, StringComparison.Ordinal))
                    matches.Add(profile);
            }
            catch { /* An unreadable saved profile must not prevent using the local configuration. */ }
        }
        return matches;
    }

    // Windows DPAPI CurrentUser protection; no plaintext key in settings, workspaces or command lines.
    internal static string ProtectKey(string key) => Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(key), true));
    internal static string UnprotectKey(string encrypted) => Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(encrypted), false));
    private static byte[] Crypt(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.Security.Cryptography.CryptographicException();
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Array.Clear(bytes);
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
