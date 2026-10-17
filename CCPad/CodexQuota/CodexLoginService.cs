using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CCPad.Settings;

namespace CCPad.CodexQuota
{
    public enum CodexLoginOutcome
    {
        Completed,
        Cancelled,
        CliMissing,
        Failed,
    }

    /// <summary>
    /// Starts the official Codex CLI login in an isolated CODEX_HOME. The CLI
    /// owns the browser flow and writes its own auth.json; CCPad only checks the
    /// resulting profile and never copies or logs credential material.
    /// </summary>
    public static class CodexLoginService
    {
        private const int CmdNotFoundExitCode = 9009;

        public static string CreateProfileHome(string? dataDirectory = null)
        {
            var root = Path.Combine(dataDirectory ?? AppPaths.Root, "codex-accounts");
            Directory.CreateDirectory(root);
            string home;
            do home = Path.Combine(root, Guid.NewGuid().ToString("N"));
            while (Directory.Exists(home));
            Directory.CreateDirectory(home);
            return home;
        }

        /// <summary>Build the visible Windows console used for the official login.</summary>
        public static ProcessStartInfo BuildStartInfo(string home)
        {
            home = CodexQuotaProfileStore.ResolveHome(home);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                // ShellExecute cannot accept a custom environment block. Starting
                // cmd.exe directly still gives the user a normal console window
                // while allowing CODEX_HOME to be isolated for this login.
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = home,
                Arguments = "/d /c codex login",
            };
            // Environment is inherited by cmd.exe and then by the Codex shim.
            // Do not put the path into the command string: ArgumentList/Arguments
            // must never become a shell-injection surface for a user-selected path.
            start.Environment["CODEX_HOME"] = home;
            return start;
        }

        public static async Task<CodexLoginOutcome> RunAsync(
            string home, CancellationToken cancellationToken = default)
            => await RunAsync(home, Process.Start, CodexQuotaProfileStore.HasReadableChatGptLogin,
                cancellationToken).ConfigureAwait(true);

        internal static async Task<CodexLoginOutcome> RunAsync(string home,
            Func<ProcessStartInfo, Process?> startProcess, Func<string, bool> hasLogin,
            CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                home = CodexQuotaProfileStore.ResolveHome(home);
                Directory.CreateDirectory(home);
                cancellationToken.ThrowIfCancellationRequested();
                process = startProcess(BuildStartInfo(home));
                if (process is null) return CodexLoginOutcome.Failed;
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
                if (process.ExitCode == CmdNotFoundExitCode) return CodexLoginOutcome.CliMissing;
                if (process.ExitCode != 0) return CodexLoginOutcome.Failed;
                return hasLogin(home) ? CodexLoginOutcome.Completed : CodexLoginOutcome.Cancelled;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Only this invocation's process is owned; never stop another Codex session.
                if (process is not null)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(true);
                    }
                    catch { }
                }
                return CodexLoginOutcome.Cancelled;
            }
            catch
            {
                return CodexLoginOutcome.Failed;
            }
            finally
            {
                process?.Dispose();
            }
        }
    }
}
