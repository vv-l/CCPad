using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CCPad.Settings
{
    public sealed class RemoteDeviceTestResult
    {
        public bool SshConnected { get; init; }
        public bool IsLinux { get; init; }
        public bool DirectoryReady { get; init; }
        public bool TmuxReady { get; init; }
        public bool CodexReady { get; init; }
        public bool CodexAuthenticated { get; init; }
        public string Error { get; init; } = "";
        public bool CanUseCCPad => SshConnected && IsLinux && DirectoryReady && TmuxReady && CodexReady;
    }

    /// <summary>Read-only SSH probes used by the device editor and project validator.</summary>
    public static class RemoteDeviceConnection
    {
        public static Task<RemoteDeviceTestResult> TestAsync(RemoteDeviceEntry device) => Task.Run(() =>
        {
            string dir = QuoteShell(device.DefaultWorkingDir);
            // Probe the binary real launches will use (CodexCommand may be a
            // custom path plus flags — take its first word), not a literal `codex`.
            string codexBin = (device.CodexCommand ?? "codex").Trim();
            int space = codexBin.IndexOf(' ');
            if (space > 0) codexBin = codexBin[..space];
            if (codexBin.Length == 0) codexBin = "codex";
            string codex = QuoteShell(codexBin);
            string cmd =
                "printf 'CCPAD_OS=%s\\n' \"$(uname -s 2>/dev/null)\"; " +
                $"if [ -d {dir} ] && [ -x {dir} ]; then echo CCPAD_DIR=ok; else echo CCPAD_DIR=missing; fi; " +
                "command -v tmux >/dev/null 2>&1 && echo CCPAD_TMUX=ok || echo CCPAD_TMUX=missing; " +
                $"command -v {codex} >/dev/null 2>&1 && echo CCPAD_CODEX=ok || echo CCPAD_CODEX=missing; " +
                $"{codex} login status >/dev/null 2>&1 && echo CCPAD_AUTH=ok || echo CCPAD_AUTH=missing";
            var (code, stdout, stderr) = Run(device, cmd, 15000);
            bool connected = code >= 0 && stdout.Contains("CCPAD_OS=", StringComparison.Ordinal);
            return new RemoteDeviceTestResult
            {
                SshConnected = connected,
                IsLinux = stdout.Contains("CCPAD_OS=Linux", StringComparison.OrdinalIgnoreCase),
                DirectoryReady = stdout.Contains("CCPAD_DIR=ok", StringComparison.Ordinal),
                TmuxReady = stdout.Contains("CCPAD_TMUX=ok", StringComparison.Ordinal),
                CodexReady = stdout.Contains("CCPAD_CODEX=ok", StringComparison.Ordinal),
                CodexAuthenticated = stdout.Contains("CCPAD_AUTH=ok", StringComparison.Ordinal),
                Error = connected ? "" : FriendlyError(stderr, code),
            };
        });

        public static Task<(bool Exists, string Error)> TestDirectoryAsync(
            RemoteDeviceEntry device, string path) => Task.Run(() =>
        {
            var (code, stdout, stderr) = Run(device,
                $"test -d {QuoteShell(path)} && test -x {QuoteShell(path)} && echo CCPAD_DIR=ok", 10000);
            return (code == 0 && stdout.Contains("CCPAD_DIR=ok", StringComparison.Ordinal),
                code == 0 ? "" : FriendlyError(stderr, code));
        });

        /// <summary>Copy a local file onto the device over the same ssh transport
        /// the probes use (file bytes → stdin → `cat`), so nothing extra (scp,
        /// sftp-server) is required on either side. The remote path must be an
        /// absolute POSIX path; its parent directory is created on demand.</summary>
        public static Task<(bool Ok, string Error)> PushFileAsync(
            RemoteDeviceEntry device, string localPath, string remotePath) => Task.Run(() =>
        {
            try
            {
                if (!IsSafeUser(device.User) || !IsSafeHost(device.Host))
                    return (false, "invalid SSH user or host");
                int slash = remotePath.LastIndexOf('/');
                if (slash < 1) return (false, "invalid remote path");
                var psi = CreateSshPsi(device,
                    $"mkdir -p {QuoteShell(remotePath[..slash])} && cat > {QuoteShell(remotePath)}");
                psi.RedirectStandardInput = true;
                using var p = Process.Start(psi);
                if (p == null) return (false, "ssh launch failed");
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                // The stdin copy itself can stall forever on a dead link, so the
                // 30s bound must cover the whole transfer, not just the wait after
                // it — a watchdog kill unblocks the copy with a pipe error.
                bool timedOut = false;
                using (new System.Threading.Timer(_ =>
                {
                    timedOut = true;
                    try { p.Kill(entireProcessTree: true); } catch { }
                }, null, 30000, System.Threading.Timeout.Infinite))
                {
                    try
                    {
                        using var fs = File.OpenRead(localPath);
                        fs.CopyTo(p.StandardInput.BaseStream);
                        p.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // Broken pipe: ssh already died (auth/connect failure) —
                        // fall through and report from its stderr, not the pipe.
                        try { p.StandardInput.Close(); } catch { }
                    }
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(entireProcessTree: true); p.WaitForExit(); } catch { }
                        return (false, "timeout");
                    }
                }
                if (timedOut) return (false, "timeout");
                if (p.ExitCode == 0) return (true, "");
                // 255 is ssh's own transport/auth failure (worth the friendly
                // mapping); any other code is the remote command failing — its
                // stderr names the real cause (e.g. a cat permission error).
                string err = se.Result.Trim();
                return p.ExitCode == 255
                    ? (false, FriendlyError(err, p.ExitCode))
                    : (false, err.Length > 0 ? err : "ssh exit " + p.ExitCode);
            }
            catch (Exception ex) { return (false, ex.Message); }
        });

        internal static (int Code, string Out, string Err) Run(
            RemoteDeviceEntry device, string remoteCommand, int timeoutMs)
        {
            try
            {
                if (!IsSafeUser(device.User) || !IsSafeHost(device.Host))
                    return (-1, "", "invalid SSH user or host");
                var psi = CreateSshPsi(device, remoteCommand);
                using var p = Process.Start(psi);
                if (p == null) return (-1, "", "ssh launch failed");
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(); } catch { }
                    return (-1, "", "timeout");
                }
                return (p.ExitCode, so.Result, se.Result);
            }
            catch (Exception ex) { return (-1, "", ex.Message); }
        }

        private static ProcessStartInfo CreateSshPsi(RemoteDeviceEntry device, string remoteCommand)
        {
            string key = Environment.ExpandEnvironmentVariables(device.KeyPath ?? "");
            var psi = new ProcessStartInfo
            {
                FileName = CliMode.ResolveSsh(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(device.Port.ToString());
            if (!string.IsNullOrWhiteSpace(key))
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(key);
            }
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ConnectTimeout=5");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
            psi.ArgumentList.Add($"{device.User}@{device.Host}");
            psi.ArgumentList.Add(remoteCommand);
            return psi;
        }

        internal static bool IsSafeUser(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value!)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) return false;
            return true;
        }

        internal static bool IsSafeHost(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value!)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']' or '%')) return false;
            return true;
        }

        internal static string QuoteShell(string value) =>
            "'" + value.Replace("'", "'\"'\"'") + "'";

        private static string FriendlyError(string stderr, int code)
        {
            string text = stderr.Trim();
            if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "authentication failed";
            if (text.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)) return "host key mismatch";
            if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "connection timed out";
            if (text.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)) return "host not found";
            return text.Length > 0 ? text : "ssh exit " + code;
        }
    }
}
