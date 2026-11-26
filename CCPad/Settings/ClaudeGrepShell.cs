using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CCPad.Settings
{
    internal static class ClaudeGrepShell
    {
        internal const string LogSearchArgs = "--append-system-prompt \"For Claude session JSONL, use rg -F for literal searches. To find recent Bash commands use ccpad-log-commands FILE TEXT --last 2, or parse JSON one line at a time.\"";

        private static readonly object Sync = new();
        private static Task<string?>? _preparing;

        internal static Task<string?> PrepareAsync()
        {
            lock (Sync)
            {
                if (_preparing == null || _preparing.IsFaulted)
                    _preparing = PrepareCoreAsync();
                return _preparing;
            }
        }

        private static async Task<string?> PrepareCoreAsync()
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", Path.Combine(AppContext.BaseDirectory, "Assets", "claude-shell", "prepare.ps1"),
                "-CacheRoot", AppPaths.Sub("claude-git-bash") })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new IOException("Could not prepare Claude's Git Bash.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync()).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("Could not prepare Claude's grep memory limit. " + (await stderr).Trim());
            string path = (await stdout).Trim();
            if (path.Length == 0) return null; // No Git for Windows installation.
            if (!File.Exists(path)) throw new IOException("The prepared Git Bash is missing.");
            return path;
        }
    }
}
