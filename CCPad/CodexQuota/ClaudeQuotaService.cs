using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.CodexQuota
{
    public enum ClaudeQuotaState
    {
        Loading,
        Ready,
        ProfileOnly,
        Unavailable,
        Error,
    }

    public sealed record ClaudeQuotaWindow(
        string Label,
        double UsedPercent,
        DateTimeOffset? ResetsAt);

    public sealed record ClaudeQuotaSnapshot(
        ClaudeQuotaState State,
        string? PlanLabel,
        string? SubscriptionType,
        string? RateLimitTier,
        ClaudeQuotaWindow[] Windows,
        DateTimeOffset? UpdatedAt,
        string? Error)
    {
        public static ClaudeQuotaSnapshot Loading() =>
            new(ClaudeQuotaState.Loading, null, null, null, Array.Empty<ClaudeQuotaWindow>(), null, null);

        public static ClaudeQuotaSnapshot Unavailable(string error) =>
            new(ClaudeQuotaState.Unavailable, null, null, null, Array.Empty<ClaudeQuotaWindow>(), null, error);

        public double TightestUsedPercent
        {
            get
            {
                var value = 0d;
                foreach (var window in Windows)
                    value = Math.Max(value, window.UsedPercent);
                return value;
            }
        }
    }

    /// <summary>
    /// Reads Claude Code's local OAuth profile and its usage windows. When the
    /// organization blocks the usage endpoint, the subscription tier still stays
    /// visible from the local profile instead of being discarded with the error.
    /// </summary>
    public sealed class ClaudeQuotaService : IDisposable
    {
        private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
        private static readonly HttpClient Http = CreateHttpClient();
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private bool _disposed;

        public async Task<ClaudeQuotaSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                return ClaudeQuotaSnapshot.Unavailable("Claude quota service is closed");
            if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return ClaudeQuotaSnapshot.Loading();

            try
            {
                var profile = ReadProfile();
                if (profile == null)
                    return ClaudeQuotaSnapshot.Unavailable("Claude is not signed in");

                var profileOnly = new ClaudeQuotaSnapshot(
                    ClaudeQuotaState.ProfileOnly,
                    NormalizePlanLabel(profile.Value.SubscriptionType, profile.Value.RateLimitTier),
                    profile.Value.SubscriptionType,
                    profile.Value.RateLimitTier,
                    Array.Empty<ClaudeQuotaWindow>(),
                    DateTimeOffset.UtcNow,
                    "Claude usage details are unavailable");

                using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.Value.AccessToken);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");
                using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        return ClaudeQuotaSnapshot.Unavailable("Claude subscription login has expired");
                    // Keep the locally known tier visible even when the organization
                    // disallows OAuth usage reads (the current 403 response).
                    return profileOnly with
                    {
                        Error = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                            ? "Claude usage API is not available for this account"
                            : $"Claude usage request failed ({(int)response.StatusCode})"
                    };
                }

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var windows = ParseUsage(body);
                return windows.Count == 0
                    ? profileOnly
                    : new ClaudeQuotaSnapshot(
                        ClaudeQuotaState.Ready,
                        profileOnly.PlanLabel,
                        profileOnly.SubscriptionType,
                        profileOnly.RateLimitTier,
                        windows.ToArray(),
                        DateTimeOffset.UtcNow,
                        null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ClaudeQuotaSnapshot.Loading();
            }
            catch (HttpRequestException)
            {
                return MakeProfileOnlyFallback("Claude usage is temporarily unavailable");
            }
            catch (JsonException)
            {
                return MakeProfileOnlyFallback("Claude returned an unreadable usage response");
            }
            catch (Exception)
            {
                return MakeProfileOnlyFallback("Could not read Claude usage");
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _refreshGate.Dispose();
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CCPad/1.0");
            return client;
        }

        private static ClaudeProfile? ReadProfile()
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDir))
                configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            configDir = Environment.ExpandEnvironmentVariables(configDir);
            var credentialsPath = Path.Combine(configDir, ".credentials.json");
            if (!File.Exists(credentialsPath))
                return null;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
                if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                    oauth.ValueKind != JsonValueKind.Object)
                    return null;
                var token = GetString(oauth, "accessToken");
                var subscription = GetString(oauth, "subscriptionType");
                var tier = GetString(oauth, "rateLimitTier");
                // Claude Code can leave subscriptionType/rateLimitTier behind after
                // logout or a switch to API billing. Those fields alone do not
                // identify the account that is currently available for usage reads.
                if (string.IsNullOrWhiteSpace(token))
                    return null;
                return new ClaudeProfile(token, subscription, tier);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private ClaudeQuotaSnapshot MakeProfileOnlyFallback(string error)
        {
            var profile = ReadProfile();
            return profile == null
                ? ClaudeQuotaSnapshot.Unavailable("Claude is not signed in")
                : new ClaudeQuotaSnapshot(
                    ClaudeQuotaState.ProfileOnly,
                    NormalizePlanLabel(profile.Value.SubscriptionType, profile.Value.RateLimitTier),
                    profile.Value.SubscriptionType,
                    profile.Value.RateLimitTier,
                    Array.Empty<ClaudeQuotaWindow>(),
                    DateTimeOffset.UtcNow,
                    error);
        }

        private static List<ClaudeQuotaWindow> ParseUsage(Stream body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var windows = new List<ClaudeQuotaWindow>(2);
            AddWindow(root, "five_hour", "5h", windows);
            AddWindow(root, "seven_day", "7d", windows);
            AddWindow(root, "seven_day_sonnet", "7d Sonnet", windows);
            return windows;
        }

        private static void AddWindow(
            JsonElement root,
            string propertyName,
            string label,
            List<ClaudeQuotaWindow> windows)
        {
            if (!root.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.Object)
                return;
            var used = GetDouble(value, "utilization") ?? GetDouble(value, "used_percentage");
            if (!used.HasValue)
                return;
            windows.Add(new ClaudeQuotaWindow(
                label,
                Math.Clamp(used.Value, 0, 100),
                ReadResetTime(value)));
        }

        private static DateTimeOffset? ReadResetTime(JsonElement window)
        {
            if (!window.TryGetProperty("resets_at", out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return FromUnixValue(number);
            if (value.ValueKind != JsonValueKind.String)
                return null;
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (double.TryParse(raw, out var parsed))
                return FromUnixValue(parsed);
            return DateTimeOffset.TryParse(raw, out var date) ? date : null;
        }

        private static DateTimeOffset? FromUnixValue(double value)
        {
            try
            {
                return value > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)value)
                    : DateTimeOffset.FromUnixTimeSeconds((long)value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static string? NormalizePlanLabel(string? subscriptionType, string? rateLimitTier)
        {
            var tier = rateLimitTier?.Trim().ToLowerInvariant() ?? "";
            if (tier.Contains("20x", StringComparison.Ordinal)) return "20x";
            if (tier.Contains("5x", StringComparison.Ordinal)) return "5x";
            if (tier.Contains("free", StringComparison.Ordinal) ||
                subscriptionType?.Trim().Equals("free", StringComparison.OrdinalIgnoreCase) == true)
                return "Free";
            if (subscriptionType?.Trim().Equals("max", StringComparison.OrdinalIgnoreCase) == true)
                return "Max";
            if (subscriptionType?.Trim().Equals("pro", StringComparison.OrdinalIgnoreCase) == true)
                return "Pro";
            return string.IsNullOrWhiteSpace(rateLimitTier) ? subscriptionType : rateLimitTier;
        }

        private static string? GetString(JsonElement parent, string propertyName)
            => parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static double? GetDouble(JsonElement parent, string propertyName)
            => parent.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var result)
                ? result
                : null;

        private readonly record struct ClaudeProfile(
            string? AccessToken,
            string? SubscriptionType,
            string? RateLimitTier);
    }
}
