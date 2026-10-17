using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CCPad.Settings;

namespace CCPad.CodexQuota
{
    internal enum CodexResetOutcome
    {
        Reset, AlreadyRedeemed, NothingToReset, NoCredit, Cancelled, AccountChanged, Unknown, Simulation,
    }

    internal sealed record CodexResetAttempt(string IdempotencyKey, string? CreditId, bool Completed);

    internal static class CodexResetAudit
    {
        private static readonly object Gate = new();

        internal static void Write(string directory, string phase, string outcome, bool simulation,
            Exception? exception = null, string? attemptId = null)
        {
            // Only fixed phase/outcome labels, exception type/code, and a transaction UUID.
            // No account email, token, credit ID, request/response body, or exception message.
            try
            {
                var line = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    phase, outcome, simulation, attemptId,
                    errorClass = exception?.GetType().Name,
                    rpcErrorCode = (exception as CodexResetRpcException)?.Code,
                });
                lock (Gate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, $"events-{Environment.ProcessId}.jsonl"), line + Environment.NewLine);
                }
            }
            catch { } // Logging failure must never change the outcome or initiate a retry.
        }
    }

    /// <summary>
    /// Opening/cancelling a confirmation is read-only. The only mutation is behind
    /// exact confirmation, account recheck, a cross-window file lock, and a durable
    /// idempotency key. Uncertain requests are never automatically retried.
    /// </summary>
    internal sealed class CodexResetCreditService
    {
        private readonly Func<CancellationToken, Task<ICodexResetRpc>> _connect;
        private readonly Func<string?> _identity;
        private readonly string _stateDirectory;
        internal bool IsSimulation { get; }

        internal CodexResetCreditService()
        {
#if DEBUG
            // Development builds cannot consume a real credit, even if a tester confirms.
            IsSimulation = true;
#else
            IsSimulation = Environment.GetEnvironmentVariable("CCPAD_CODEX_RESET_DRY_RUN") == "1";
#endif
            _connect = IsSimulation ? token => Task.FromResult<ICodexResetRpc>(new SimulationRpc())
                : token => CodexResetRpcClient.StartAsync(token, permitReset: true);
            _identity = IsSimulation ? () => "simulation-account" : ReadAccountIdentity;
            _stateDirectory = Path.Combine(AppPaths.Root, IsSimulation ? "codex-reset-simulation" : "codex-reset");
        }

        internal CodexResetCreditService(Func<CancellationToken, Task<ICodexResetRpc>> connect,
            Func<string?> identity, string stateDirectory)
        {
            _connect = connect;
            _identity = identity;
            _stateDirectory = stateDirectory;
        }

        internal bool HasPendingAttempt
        {
            get
            {
                try
                {
                    var identity = _identity();
                    if (string.IsNullOrEmpty(identity)) return false;
                    var file = Path.Combine(_stateDirectory, identity + ".json");
                    return File.Exists(file) && JsonSerializer.Deserialize<CodexResetAttempt>(File.ReadAllText(file))?.Completed == false;
                }
                catch { return false; }
            }
        }

        internal async Task<CodexResetPreparation> PrepareAsync(CancellationToken cancellationToken)
        {
            FileStream gate;
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                // FileShare.None works across processes and does not require a thread-affine mutex.
                gate = new FileStream(Path.Combine(_stateDirectory, "confirmation.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex)
            {
                CodexResetAudit.Write(_stateDirectory, "prepare-local", "failed-no-request", IsSimulation, ex);
                throw;
            }
            ICodexResetRpc? rpc = null;
            try
            {
                var identity = _identity();
                if (string.IsNullOrEmpty(identity)) throw new InvalidOperationException("Account identity unavailable");
                rpc = await _connect(cancellationToken).ConfigureAwait(false);
                var account = await rpc.CallAsync("account/read", new { refreshToken = false }, cancellationToken).ConfigureAwait(false);
                var value = account.GetProperty("account");
                if (value.ValueKind != JsonValueKind.Object || value.GetProperty("type").GetString() != "chatgpt")
                    throw new InvalidOperationException("A ChatGPT Codex account is required");
                var email = value.TryGetProperty("email", out var e) ? e.GetString() : null;
                var plan = value.TryGetProperty("planType", out var p) ? p.GetString() : null;
                var limits = await rpc.CallAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
                int count = 0;
                string? creditId = null;
                if (limits.TryGetProperty("rateLimitResetCredits", out var credits) && credits.ValueKind == JsonValueKind.Object)
                {
                    if (credits.TryGetProperty("availableCount", out var c) && c.TryGetInt32(out var n)) count = Math.Max(0, n);
                    if (credits.TryGetProperty("credits", out var details) && details.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in details.EnumerateArray())
                        {
                            if (detail.TryGetProperty("status", out var status) && status.GetString() == "available" &&
                                detail.TryGetProperty("resetType", out var resetType) && resetType.GetString() == "codexRateLimits" &&
                                detail.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                            { creditId = id.GetString(); break; }
                        }
                    }
                }
                if (_identity() != identity) throw new InvalidOperationException("Account changed while preparing");
                var journal = Path.Combine(_stateDirectory, identity + ".json");
                CodexResetAttempt? pending = null;
                if (File.Exists(journal))
                {
                    pending = JsonSerializer.Deserialize<CodexResetAttempt>(File.ReadAllText(journal));
                    if (pending == null || !Guid.TryParse(pending.IdempotencyKey, out _))
                        throw new InvalidOperationException("Unreadable reset receipt; refusing a new attempt");
                    if (pending.Completed) pending = null;
                }
                if (count <= 0 && pending == null) throw new InvalidOperationException("No reset credits available");
                return new CodexResetPreparation(rpc, gate, _identity, identity, journal,
                    email ?? "ChatGPT", plan ?? "", count, pending ?? new(Guid.NewGuid().ToString(), creditId, false),
                    pending != null, IsSimulation);
            }
            catch (Exception ex)
            {
                CodexResetAudit.Write(_stateDirectory, "prepare-account", "failed-no-reset-request", IsSimulation, ex);
                if (rpc != null) await rpc.DisposeAsync().ConfigureAwait(false);
                gate.Dispose();
                throw;
            }
        }

        private static string? ReadAccountIdentity()
        {
            try
            {
                var home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "auth.json")));
                var account = document.RootElement.GetProperty("tokens").GetProperty("account_id").GetString();
                return string.IsNullOrWhiteSpace(account) ? null
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(home) + "\n" + account)));
            }
            catch { return null; }
        }

        private sealed class SimulationRpc : ICodexResetRpc
        {
            public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
            {
                var json = method switch
                {
                    "account/read" => "{\"account\":{\"type\":\"chatgpt\",\"email\":\"demo@example.invalid\",\"planType\":\"pro\"}}",
                    "account/rateLimits/read" => "{\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":null}}",
                    "account/rateLimitResetCredit/consume" => "{\"outcome\":\"reset\"}",
                    _ => throw new InvalidOperationException("Unexpected simulation method"),
                };
                return Task.FromResult(JsonSerializer.Deserialize<JsonElement>(json));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class CodexResetPreparation : IAsyncDisposable
    {
        private readonly ICodexResetRpc _rpc;
        private readonly FileStream _gate;
        private readonly Func<string?> _identity;
        private readonly string _expectedIdentity;
        private readonly string _journal;
        private readonly CodexResetAttempt _attempt;
        private string LogDirectory => Path.GetDirectoryName(_journal)!;
        private int _submitted;
        private int _disposed;
        internal string AccountLabel { get; }
        internal string Plan { get; }
        internal int AvailableCount { get; }
        internal bool RetryPending { get; }
        internal bool IsSimulation { get; }

        internal CodexResetPreparation(ICodexResetRpc rpc, FileStream gate, Func<string?> identity,
            string expectedIdentity, string journal, string accountLabel, string plan, int count,
            CodexResetAttempt attempt, bool retryPending, bool simulation)
        {
            _rpc = rpc; _gate = gate; _identity = identity; _expectedIdentity = expectedIdentity;
            _journal = journal; _attempt = attempt; AccountLabel = accountLabel; Plan = plan;
            AvailableCount = count; RetryPending = retryPending; IsSimulation = simulation;
        }

        internal async Task<CodexResetOutcome> ConfirmAsync(string confirmation, CancellationToken cancellationToken)
        {
            if (confirmation != "RESET" || Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
            {
                CodexResetAudit.Write(LogDirectory, "confirm", "cancelled-no-request", IsSimulation);
                return CodexResetOutcome.Cancelled;
            }
            if (Interlocked.CompareExchange(ref _submitted, 1, 0) != 0)
            {
                CodexResetAudit.Write(LogDirectory, "confirm", "duplicate-blocked", IsSimulation);
                return CodexResetOutcome.Cancelled;
            }
            if (_identity() != _expectedIdentity)
            {
                CodexResetAudit.Write(LogDirectory, "confirm", "account-changed-no-request", IsSimulation);
                return CodexResetOutcome.AccountChanged;
            }
            // No consume call is permitted unless the retry key is safely on disk first.
            try { SaveAttempt(_attempt); }
            catch (Exception ex)
            {
                CodexResetAudit.Write(LogDirectory, "save-receipt", "failed-no-request", IsSimulation, ex);
                return CodexResetOutcome.Cancelled;
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_identity() != _expectedIdentity)
                {
                    CodexResetAudit.Write(LogDirectory, "confirm", "account-changed-no-request", IsSimulation);
                    return CodexResetOutcome.AccountChanged;
                }
                CodexResetAudit.Write(LogDirectory, "request", "user-confirmed", IsSimulation, attemptId: _attempt.IdempotencyKey);
                var response = await _rpc.CallAsync("account/rateLimitResetCredit/consume",
                    new { idempotencyKey = _attempt.IdempotencyKey, creditId = _attempt.CreditId }, cancellationToken).ConfigureAwait(false);
                var outcome = response.GetProperty("outcome").GetString() switch
                {
                    "reset" => CodexResetOutcome.Reset,
                    "alreadyRedeemed" => CodexResetOutcome.AlreadyRedeemed,
                    "nothingToReset" => CodexResetOutcome.NothingToReset,
                    "noCredit" => CodexResetOutcome.NoCredit,
                    _ => CodexResetOutcome.Unknown,
                };
                CodexResetAudit.Write(LogDirectory, "result", outcome.ToString(), IsSimulation, attemptId: _attempt.IdempotencyKey);
                if (outcome == CodexResetOutcome.Unknown) return outcome;
                SaveAttempt(_attempt with { Completed = true });
                return IsSimulation ? CodexResetOutcome.Simulation : outcome;
            }
            catch (Exception ex)
            {
                CodexResetAudit.Write(LogDirectory, "result", "unknown-no-auto-retry", IsSimulation, ex, _attempt.IdempotencyKey);
                return CodexResetOutcome.Unknown; // Keep the SAME key for a later manually confirmed retry.
            }
        }

        private void SaveAttempt(CodexResetAttempt attempt)
        {
            var temp = _journal + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(attempt));
            File.Move(temp, _journal, overwrite: true);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Volatile.Read(ref _submitted) == 0)
                CodexResetAudit.Write(LogDirectory, "confirmation", "dismissed-no-request", IsSimulation);
            try { await _rpc.DisposeAsync().ConfigureAwait(false); }
            finally { _gate.Dispose(); }
        }
    }
}
