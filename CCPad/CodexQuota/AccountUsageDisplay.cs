using System;
using System.Globalization;
using System.Linq;

namespace CCPad.CodexQuota;

/// <summary>Shared read-only text for account details and the overview.</summary>
public static class AccountUsageDisplay
{
    public static string Format(AccountUsageReadResult result, string template,
        string unknown, string empty, string unavailable)
    {
        if (result.State is "invalid" or "profiles-unavailable") return unavailable;
        if (result.Entries.Length == 0) return empty;
        string Value(string value) => value == "unknown" ? unknown : value;
        return string.Join("\n\n", result.Entries.Select(row =>
        {
            var e = row.Entry;
            var fast = e.Fast.ValueKind == System.Text.Json.JsonValueKind.String
                ? unknown : e.Fast.ValueKind == System.Text.Json.JsonValueKind.True ? "true" : "false";
            return string.Format(CultureInfo.CurrentCulture, template,
                e.BotRef, Value(e.HostRef), Value(e.Model), Value(e.Reasoning), fast,
                Value(e.ServiceTier), Value(e.UsageState), row.ProfileState,
                e.ObservedAt, string.Join(", ", e.EvidenceRef));
        }));
    }
}
