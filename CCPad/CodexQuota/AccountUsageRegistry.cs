using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CCPad.Settings;

namespace CCPad.CodexQuota;

public sealed record AccountUsageEntry(string EntryId, string AccountRef, string AccountLabel,
    string BotRef, string Provider, string[] Capability, string HostRef, string DataHomeRef,
    string Model, string Reasoning, JsonElement Fast, string ServiceTier, string UsageState,
    string ObservedAt, string[] EvidenceRef);
public sealed record AccountUsageResult(AccountUsageEntry Entry, string ProfileState);
public sealed record AccountUsageReadResult(string State, AccountUsageResult[] Entries);
internal sealed record AccountUsageDocument(int RegistryVersion, AccountUsageEntry[] Entries);

/// <summary>Reads usage facts without resolving homes or reading authentication.</summary>
public sealed class AccountUsageRegistry
{
    private readonly string _file;
    private readonly CodexQuotaProfileStore _profiles;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public AccountUsageRegistry(CodexQuotaProfileStore profiles, string? dataDirectory = null)
    {
        _profiles = profiles;
        _file = Path.Combine(dataDirectory ?? AppPaths.Root, "account-usage.json");
    }
    public AccountUsageReadResult GetAccountUsage(string accountRef)
    {
        var all = GetAllAccountUsage();
        return all with { Entries = all.Entries.Where(x => x.Entry.AccountRef == accountRef).ToArray() };
    }
    public AccountUsageReadResult GetAllAccountUsage()
    {
        try
        {
            if (!File.Exists(_file)) return new("missing", []);
            var doc = JsonSerializer.Deserialize<AccountUsageDocument>(File.ReadAllText(_file), Options);
            if (doc is null || doc.RegistryVersion != 1 || doc.Entries is null) return new("invalid", []);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in doc.Entries)
                if (!Valid(e) || !ids.Add(e.EntryId)) return new("invalid", []);
            HashSet<string> profiles;
            try { profiles = _profiles.LoadLaunchProfiles().Select(p => p.Id).ToHashSet(StringComparer.Ordinal); }
            catch { return new("profiles-unavailable", doc.Entries.Select(e => new AccountUsageResult(e, "unavailable")).ToArray()); }
            return new("loaded", doc.Entries.Select(e => new AccountUsageResult(e,
                profiles.Contains(e.AccountRef) ? "available" : "unavailable")).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Exception messages can contain full paths or raw input.
            return new("invalid", []);
        }
    }
    private static bool Ref(string? s) => s is { Length: > 0 and <= 100 } && Regex.IsMatch(s, @"\A[A-Za-z0-9_.-]+\z");
    private static bool Label(string? s) => s is { Length: > 0 and <= 100 } &&
        Regex.IsMatch(s, @"\A[\p{L}\p{M}\p{N} _().\-·]+\z");
    private static bool One(string? s, params string[] values) => values.Contains(s, StringComparer.Ordinal);
    private static bool Valid(AccountUsageEntry? e) => e is not null &&
        Ref(e.EntryId) && Ref(e.AccountRef) && Label(e.AccountLabel) && Label(e.BotRef) &&
        Ref(e.HostRef) && Ref(e.Model) && Ref(e.ServiceTier) &&
        One(e.Provider, "openai-codex", "anthropic-claude", "unknown") &&
        e.Capability is { Length: > 0 } && e.Capability.All(c => One(c, "quota-read", "local-launch", "remote-cli", "unknown")) &&
        (e.DataHomeRef == "unknown" || Regex.IsMatch(e.DataHomeRef ?? "", @"\A(CODEX_HOME|APP_DATA):[A-Za-z0-9_.-]+\z")) &&
        One(e.Reasoning, "low", "medium", "high", "xhigh", "max", "ultra", "unknown") &&
        (e.Fast.ValueKind is JsonValueKind.True or JsonValueKind.False || e.Fast.ValueKind == JsonValueKind.String && e.Fast.GetString() == "unknown") &&
        One(e.UsageState, "ready", "unavailable", "error", "not-checked", "unknown") &&
        Regex.IsMatch(e.ObservedAt ?? "", @"(Z|[+-]\d{2}:\d{2})\z") && DateTimeOffset.TryParse(e.ObservedAt, out _) &&
        e.EvidenceRef is { Length: > 0 } && e.EvidenceRef.All(p => p is { Length: > 0 and <= 240 } &&
            Regex.IsMatch(p, @"\A[A-Za-z0-9_./-]+\z") && !p.StartsWith('/') &&
            p.Split('/').All(s => s is not ".." and not "." and not ""));
}
