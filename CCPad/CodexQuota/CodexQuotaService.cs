using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.CodexQuota
{
    public enum CodexQuotaState
    {
        Loading,
        Ready,
        Unavailable,
        Error,
    }

    public sealed record CodexQuotaWindow(
        string Label,
        double UsedPercent,
        TimeSpan Duration,
        DateTimeOffset? ResetsAt);

    public sealed record CodexQuotaSnapshot(
        CodexQuotaState State,
        string? PlanType,
        CodexQuotaWindow[] Windows,
        int ResetCreditsAvailable,
        DateTimeOffset? UpdatedAt,
        string? Error)
    {
        public static CodexQuotaSnapshot Loading() =>
            new(CodexQuotaState.Loading, null, Array.Empty<CodexQuotaWindow>(), 0, null, null);

        public static CodexQuotaSnapshot Unavailable(string error) =>
            new(CodexQuotaState.Unavailable, null, Array.Empty<CodexQuotaWindow>(), 0, null, error);

        public static CodexQuotaSnapshot ErrorSnapshot(string error) =>
            new(CodexQuotaState.Error, null, Array.Empty<CodexQuotaWindow>(), 0, null, error);

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
    /// Reads the signed-in local Codex account and fetches its quota metadata.
    /// Credentials stay in the auth file and are never included in snapshots or logs.
    /// </summary>
    public sealed class CodexQuotaService : IDisposable
    {
        private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
        private static readonly HttpClient Http = CreateHttpClient();
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private bool _disposed;

        public async Task<CodexQuotaSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                return CodexQuotaSnapshot.ErrorSnapshot("Codex quota service is closed");

            if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return CodexQuotaSnapshot.Loading();

            try
            {
                var credentials = ReadCredentials();
                if (credentials == null)
                    return CodexQuotaSnapshot.Unavailable("Codex is not signed in");

                using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Value.AccessToken);
                if (!string.IsNullOrWhiteSpace(credentials.Value.AccountId))
                    request.Headers.TryAddWithoutValidation("chatgpt-account-id", credentials.Value.AccountId);

                using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    return CodexQuotaSnapshot.ErrorSnapshot("Codex login expired; run codex login");
                if (!response.IsSuccessStatusCode)
                    return CodexQuotaSnapshot.ErrorSnapshot($"Codex usage request failed ({(int)response.StatusCode})");

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return ParseUsage(body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CodexQuotaSnapshot.Loading();
            }
            catch (HttpRequestException)
            {
                return CodexQuotaSnapshot.ErrorSnapshot("Codex usage is temporarily unavailable");
            }
            catch (JsonException)
            {
                return CodexQuotaSnapshot.ErrorSnapshot("Codex returned an unreadable usage response");
            }
            catch (Exception)
            {
                return CodexQuotaSnapshot.ErrorSnapshot("Could not read Codex usage");
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
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CCPad/1.0");
            return client;
        }

        private static CodexCredentials? ReadCredentials()
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

            var authPath = Path.Combine(home, "auth.json");
            if (!File.Exists(authPath))
                return null;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(authPath));
                if (!document.RootElement.TryGetProperty("tokens", out var tokens))
                    return null;
                if (!tokens.TryGetProperty("access_token", out var accessTokenElement))
                    return null;

                var accessToken = accessTokenElement.GetString();
                if (string.IsNullOrWhiteSpace(accessToken))
                    return null;

                var accountId = tokens.TryGetProperty("account_id", out var accountElement)
                    ? accountElement.GetString()
                    : null;
                return new CodexCredentials(accessToken, accountId);
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

        private static CodexQuotaSnapshot ParseUsage(Stream body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var windows = new System.Collections.Generic.List<CodexQuotaWindow>(2);

            if (root.TryGetProperty("rate_limit", out var rateLimit) &&
                rateLimit.ValueKind == JsonValueKind.Object)
            {
                AddWindow(rateLimit, "primary_window", windows);
                AddWindow(rateLimit, "secondary_window", windows);
            }

            var credits = 0;
            if (root.TryGetProperty("rate_limit_reset_credits", out var resetCredits) &&
                resetCredits.ValueKind == JsonValueKind.Object &&
                resetCredits.TryGetProperty("available_count", out var available) &&
                available.TryGetInt32(out var parsedCredits))
            {
                credits = Math.Max(0, parsedCredits);
            }

            var planType = root.TryGetProperty("plan_type", out var planElement)
                ? planElement.GetString()
                : null;

            if (windows.Count == 0)
                return CodexQuotaSnapshot.ErrorSnapshot("Codex did not report a usage window");

            return new CodexQuotaSnapshot(
                CodexQuotaState.Ready,
                planType,
                windows.ToArray(),
                credits,
                DateTimeOffset.UtcNow,
                null);
        }

        private static void AddWindow(
            JsonElement rateLimit,
            string propertyName,
            System.Collections.Generic.List<CodexQuotaWindow> windows)
        {
            if (!rateLimit.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.Object)
                return;
            if (!value.TryGetProperty("used_percent", out var usedElement) ||
                !usedElement.TryGetDouble(out var usedPercent))
                return;

            var durationSeconds = value.TryGetProperty("limit_window_seconds", out var durationElement) &&
                                  durationElement.TryGetDouble(out var parsedDuration)
                ? parsedDuration
                : 0;
            var duration = TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
            var resetAt = ReadResetTime(value);
            var label = duration.TotalHours <= 8 ? "Session" :
                duration.TotalDays <= 8 ? "Weekly" : "Usage";
            windows.Add(new CodexQuotaWindow(
                label,
                Math.Clamp(usedPercent, 0, 100),
                duration,
                resetAt));
        }

        private static DateTimeOffset? ReadResetTime(JsonElement window)
        {
            if (!window.TryGetProperty("reset_at", out var resetElement) ||
                !resetElement.TryGetDouble(out var raw) || raw <= 0)
                return null;

            try
            {
                // The backend currently returns Unix seconds; accept milliseconds as a
                // compatibility fallback for older or proxied responses.
                return raw > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)raw)
                    : DateTimeOffset.FromUnixTimeSeconds((long)raw);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private readonly record struct CodexCredentials(string AccessToken, string? AccountId);
    }
}
