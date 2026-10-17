using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.CodexQuota
{
    internal interface ICodexResetRpc : IAsyncDisposable
    {
        Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken);
    }

    internal sealed class CodexResetRpcException : Exception
    {
        internal int? Code { get; }
        internal CodexResetRpcException(int? code) : base("Codex account request was rejected") => Code = code;
    }

    /// <summary>Short-lived account-only stdio connection. Never starts a thread or turn.</summary>
    internal sealed class CodexResetRpcClient : ICodexResetRpc
    {
        private readonly Process _process;
        private readonly bool _permitReset;
        private int _id;

        private CodexResetRpcClient(Process process, bool permitReset)
        {
            _process = process;
            _permitReset = permitReset;
            _ = DrainErrorsAsync(); // Discard diagnostics; never log tokens or raw protocol messages.
        }

        internal static async Task<ICodexResetRpc> StartAsync(CancellationToken cancellationToken, bool permitReset = false)
        {
#if DEBUG
            if (permitReset) throw new InvalidOperationException("Real reset is disabled in development builds");
#endif
            if (permitReset && Environment.GetEnvironmentVariable("CCPAD_CODEX_RESET_DRY_RUN") == "1")
                throw new InvalidOperationException("Real reset is disabled in simulation mode");
            string? launcher = null;
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                var dir = directory.Trim().Trim('"');
                if (dir.Length == 0) continue;
                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    var candidate = Path.Combine(dir, "codex" + extension);
                    if (File.Exists(candidate)) { launcher = candidate; break; }
                }
                if (launcher != null) break;
            }
            if (launcher == null) throw new InvalidOperationException("Codex CLI is unavailable");
            var batch = !launcher.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo
            {
                FileName = batch ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : launcher,
                Arguments = batch ? $"/d /s /c \"\"{launcher}\" app-server --stdio\"" : "app-server --stdio",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex account service");
            var client = new CodexResetRpcClient(process, permitReset);
            try
            {
                await client.CallAsync("initialize", new
                {
                    clientInfo = new { name = "ccpad", title = "CC Pad", version = "1.10.32" },
                    capabilities = new { experimentalApi = true },
                }, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
        }

        public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            if (method != "initialize" && method != "account/read" && method != "account/rateLimits/read" &&
                !(method == "account/rateLimitResetCredit/consume" && _permitReset))
                throw new InvalidOperationException("Operation not allowed on this account connection");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var id = ++_id;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), timeout.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line == null) throw new IOException("Codex account connection closed");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetInt32(out var responseId) || responseId != id)
                    continue;
                if (root.TryGetProperty("error", out var error))
                {
                    int? code = error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.Number &&
                        codeValue.TryGetInt32(out var parsedCode) ? parsedCode : null;
                    throw new CodexResetRpcException(code); // Never retain server message/body or credentials.
                }
                return root.GetProperty("result").Clone();
            }
        }

        private async Task DrainErrorsAsync()
        {
            try
            {
                var buffer = new char[2048];
                while (await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
            }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close();
                if (!await Task.Run(() => _process.WaitForExit(2000)).ConfigureAwait(false))
                    _process.Kill(entireProcessTree: true); // Only this owned account-service process.
            }
            catch { }
            _process.Dispose();
        }
    }
}
