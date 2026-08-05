using System;
using System.IO;

namespace CCPad.Settings
{
    /// <summary>
    /// Supported CLI launchers. Stored as a lowercase string
    /// ("claude"/"codex"/"codex-remote") in AppPrefs and per-tab state for
    /// forward-compat with future modes.
    /// </summary>
    public static class CliMode
    {
        public const string Claude = "claude";
        public const string Codex = "codex";
        /// <summary>Codex on the 192.168.32.167 box: ssh + tmux attach. All
        /// connection parameters live in <see cref="AppPrefs.RemoteCodex"/>.</summary>
        public const string CodexRemote = "codex-remote";

        public static string Normalize(string? value) => value switch
        {
            Codex => Codex,
            CodexRemote => CodexRemote,
            _ => Claude,
        };

        public static string DisplayName(string mode) => Normalize(mode) switch
        {
            Codex => "Codex",
            CodexRemote => "Codex@167",
            _ => "Claude",
        };

        /// <summary>
        /// Resolve a CLI mode to the actual command line passed to CreateProcess.
        /// Codex uses --yolo (auto-approve / sandbox bypass). <paramref name="extraArgs"/>
        /// is appended verbatim (already quoted by the caller) — used to inject
        /// the per-pane "--settings &lt;hooks.json&gt;" for Claude notifications.
        /// </summary>
        public static string BuildCommand(string mode, string extraArgs = "") => Normalize(mode) switch
        {
            Codex => ResolveLaunch("codex", JoinArgs("--yolo", extraArgs)),
            // extraArgs is deliberately dropped: it carries local-CLI flags
            // (--settings / -c notify) that would be parsed by ssh, not codex.
            CodexRemote => BuildRemoteCommand(),
            _ => ResolveLaunch("claude", JoinArgs(
                    AppConfig.Load().BypassPermissions ? "--permission-mode bypassPermissions" : "",
                    extraArgs)),
        };

        /// <summary>
        /// Command line that resumes an existing conversation. Claude keeps the same
        /// session ID across resumes (no --fork-session), so the stored ID stays valid
        /// over repeated close/reopen cycles. Codex resume is a subcommand and doesn't
        /// accept --yolo, so the long-form bypass flag is used instead.
        /// </summary>
        public static string BuildResumeCommand(string mode, string sessionId, string extraArgs = "") => Normalize(mode) switch
        {
            // "tmux new -A" IS the resume: reattaching the named session brings
            // the remote conversation back, so resume == a fresh connect and no
            // session id is needed.
            CodexRemote => BuildCommand(CodexRemote),
            Codex => ResolveLaunch("codex", JoinArgs(
                    $"resume {sessionId} --dangerously-bypass-approvals-and-sandbox",
                    extraArgs)),
            _ => ResolveLaunch("claude", JoinArgs(
                    JoinArgs($"--resume {sessionId}",
                        AppConfig.Load().BypassPermissions ? "--permission-mode bypassPermissions" : ""),
                    extraArgs)),
        };

        /// <summary>
        /// Command line for a Codex@167 pane: ssh straight into the remote tmux
        /// session running codex. Every parameter comes from
        /// <see cref="AppPrefs.RemoteCodex"/> (hand-editable prefs.json — no
        /// settings UI in v1). -t forces a tty (tmux needs one),
        /// accept-new pins the host key on first connect without prompting, and
        /// ServerAliveInterval keeps NAT/firewall state from silently dropping
        /// an idle session. The key path is env-expanded here because the
        /// command goes straight to CreateProcess — no shell ever expands it.
        /// </summary>
        private static string BuildRemoteCommand()
        {
            var rc = AppConfig.Load().RemoteCodex ?? new RemoteCodexConfig();
            string key = Environment.ExpandEnvironmentVariables(rc.KeyPath ?? "");
            string remoteCmd = (rc.RemoteCommand ?? "")
                .Replace("{dir}", rc.RemoteDir ?? "")
                .Replace("{session}", rc.TmuxSession ?? "");
            return $"\"{ResolveSsh()}\" -t -i \"{key}\" " +
                   "-o StrictHostKeyChecking=accept-new -o ServerAliveInterval=30 " +
                   $"{rc.User}@{rc.Host} \"{remoteCmd}\"";
        }

        /// <summary>
        /// Locate ssh.exe: PATH first, then the stock Windows OpenSSH install
        /// dir (present even when the optional-feature dir isn't on PATH), else
        /// the bare name and let CreateProcess's own search have a go.
        /// </summary>
        private static string ResolveSsh()
        {
            var onPath = FindOnPath("ssh");
            if (onPath != null) return onPath;
            var stock = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "OpenSSH", "ssh.exe");
            return File.Exists(stock) ? stock : "ssh.exe";
        }

        private static string JoinArgs(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) return a;
            if (string.IsNullOrEmpty(a)) return b;
            return a + " " + b;
        }

        /// <summary>
        /// Walk %PATH% + %PATHEXT% to locate the launcher. If it's a batch
        /// shim (.cmd / .bat) we must invoke it via cmd.exe — CreateProcess
        /// can't execute batch files directly. If we can't find it, fall
        /// back to cmd /c so the shell does the search at runtime.
        /// </summary>
        private static string ResolveLaunch(string exeName, string args)
        {
            string trailing = string.IsNullOrEmpty(args) ? "" : " " + args;
            var resolved = FindOnPath(exeName);
            if (resolved == null)
            {
                // Let cmd.exe do the PATH search at runtime (handles PATHEXT).
                return $"cmd.exe /c {exeName}{trailing}";
            }

            var ext = Path.GetExtension(resolved).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                return $"cmd.exe /c \"\"{resolved}\"{trailing}\"";
            }
            // .exe (or no extension / shell script) — run directly.
            return $"\"{resolved}\"{trailing}";
        }

        private static string? FindOnPath(string name)
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                var pathExt = Environment.GetEnvironmentVariable("PATHEXT")
                    ?? ".COM;.EXE;.BAT;.CMD";

                var dirs = pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var exts = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);

                foreach (var rawDir in dirs)
                {
                    var dir = rawDir.Trim().Trim('"');
                    if (dir.Length == 0) continue;

                    foreach (var rawExt in exts)
                    {
                        var ext = rawExt.Trim();
                        if (ext.Length == 0) continue;
                        var full = Path.Combine(dir, name + ext);
                        if (File.Exists(full)) return full;
                    }

                    var bare = Path.Combine(dir, name);
                    if (File.Exists(bare)) return bare;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Currently configured default for new tabs.</summary>
        public static string LoadDefault() => Normalize(AppConfig.Load().DefaultCli);

        public static void SaveDefault(string mode)
        {
            var prefs = AppConfig.Load();
            prefs.DefaultCli = Normalize(mode);
            AppConfig.Save(prefs);
        }
    }
}
