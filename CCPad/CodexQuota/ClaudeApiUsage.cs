using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.CodexQuota;

public sealed record ClaudeApiTotals(decimal? Cost, long? Tokens, long? Requests);
public sealed record ClaudeApiUsage(string Name, string Endpoint, string Unit, decimal? Balance,
    ClaudeApiTotals Today, ClaudeApiTotals Total, decimal? KeyLimit = null, decimal? KeyUsed = null)
{
    public decimal? Remaining { get; init; }
    public decimal? DisplayBalance => Balance ?? Remaining;
    public static ClaudeApiUsage Empty(ClaudeApiConnection connection) => new(connection.Name,
        connection.BaseUrl, "USD", null, new(null, null, null), new(null, null, null));

    public static string Money(decimal value, string unit = "USD") =>
        (unit == "USD" ? "$" : unit + " ") + value.ToString("N2", CultureInfo.CurrentCulture);

    public static Uri UsageUri(string baseUrl)
    {
        var value = ClaudeApiProfileStore.NormalizeBaseUrl(baseUrl);
        return new Uri(value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? value + "/usage" : value + "/v1/usage");
    }

    internal static async Task<ClaudeQuotaSnapshot> ReadAsync(HttpClient client, ClaudeApiConnection connection,
        CancellationToken cancellationToken)
    {
        var empty = Empty(connection);
        ClaudeQuotaSnapshot Failure(string key) => new(ClaudeQuotaState.Error, "API", null, null,
            Array.Empty<ClaudeQuotaWindow>(), null, key) { Api = empty };
        // A normal Anthropic inference key does not expose the Sub2API quota endpoint.
        if (new Uri(connection.BaseUrl).Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
            return Failure("claude_api_unsupported");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri(connection.BaseUrl));
            if (connection.AuthType == "api-key") request.Headers.Add("x-api-key", connection.Key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Key);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failure((int)response.StatusCode is 401 or 403 ? "claude_api_unauthorized" :
                    (int)response.StatusCode is 404 or 405 ? "claude_api_unsupported" : "claude_api_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var usage = Parse(document.RootElement, connection);
            return new(ClaudeQuotaState.Ready, "API", null, null, Array.Empty<ClaudeQuotaWindow>(), DateTimeOffset.UtcNow, null) { Api = usage };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JsonException) { return Failure("claude_api_unsupported"); }
        catch (UnauthorizedAccessException) { return Failure("claude_api_unauthorized"); }
        catch { return Failure("claude_api_unavailable"); }
    }

    // Sub2API /v1/usage: balance is the account wallet; usage totals are scoped to the queried key.
    // actual_cost includes the configured billing multiplier; cost is the model's list price.
    internal static ClaudeApiUsage Parse(JsonElement root, ClaudeApiConnection connection)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (root.TryGetProperty("isValid", out var valid) && valid.ValueKind == JsonValueKind.False)
            throw new UnauthorizedAccessException();
        var empty = Empty(connection);
        var balance = Decimal(root, "balance");
        var remaining = Decimal(root, "remaining");
        var quota = root.TryGetProperty("quota", out var q) && q.ValueKind == JsonValueKind.Object ? q : default;
        var limit = Decimal(quota, "limit");
        var used = Decimal(quota, "used");
        var unit = root.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : "USD";
        if (unit is not ("USD" or "CNY" or "EUR")) throw new JsonException();
        ClaudeApiTotals today = empty.Today, total = empty.Total;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("today", out var t)) today = Totals(t);
            if (usage.TryGetProperty("total", out var all)) total = Totals(all);
        }
        if (balance == null && remaining == null && limit == null && total.Cost == null && total.Tokens == null && today.Cost == null)
            throw new JsonException();
        return empty with { Unit = unit, Balance = balance, Remaining = remaining,
            Today = today, Total = total, KeyLimit = limit, KeyUsed = used };
    }

    private static ClaudeApiTotals Totals(JsonElement value) => new(
        Decimal(value, "actual_cost") ?? Decimal(value, "cost"), Integer(value, "total_tokens"), Integer(value, "requests"));
    private static decimal? Decimal(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
    private static long? Integer(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
