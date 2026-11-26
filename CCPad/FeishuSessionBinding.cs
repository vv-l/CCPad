using System;
using System.IO;
using System.Text.Json;

namespace CCPad;

internal sealed record FeishuSessionBinding(string Engine, string CodexHome,
    string CliSessionId, string SessionProvenance)
{
    // Keep the existing exclusion without publishing the deployment's App ID.
    internal const string ReservedAppFingerprint = "EAE420922385B408B4A4B11F0E1F412898A52C8506500E7D90B80791DF39CA10";

    internal static string AppFingerprint(string appId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(appId)));

    internal static bool IsReservedApp(string appId, string fingerprint = ReservedAppFingerprint) =>
        appId != null && AppFingerprint(appId) == fingerprint;

    internal void Validate()
    {
        if (Engine != "codex" || !Path.IsPathFullyQualified(CodexHome) ||
            !Guid.TryParse(CliSessionId, out _) ||
            SessionProvenance is not ("official_notify" or "explicit_resume"))
            throw new InvalidOperationException("Confirmed local Codex session required");
    }

    internal bool Matches(string? engine, string? home, string? session, string? provenance) =>
        engine == Engine && string.Equals(home, CodexHome, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(session, CliSessionId, StringComparison.OrdinalIgnoreCase) && provenance == SessionProvenance;

    internal static string ProvenanceAfterLaunch(string? sessionId, string? confirmedResume) =>
        Guid.TryParse(sessionId, out _) && string.Equals(sessionId, confirmedResume, StringComparison.OrdinalIgnoreCase)
            ? "explicit_resume" : "";

    internal static FeishuBridgeRequest ReadBridgeRequest(string configFile)
    {
        if (!Path.IsPathFullyQualified(configFile))
            throw new InvalidOperationException("Explicit private config path required");
        using var document = JsonDocument.Parse(File.ReadAllText(configFile));
        var data = document.RootElement;
        return new(data.GetProperty("app_id").GetString()!, data.GetProperty("chat_id").GetString()!,
            data.GetProperty("state_dir").GetString()!);
    }
}
