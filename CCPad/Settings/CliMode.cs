using System;
using System.IO;

namespace CCPad.Settings
{
    /// <summary>
    /// Supported CLI launchers. Stored as a lowercase string ("claude"/"codex")
    /// in AppPrefs and per-tab state for forward-compat with future modes.
    /// </summary>
    public static class CliMode
    {
        public const string Claude = "claude";
        public const string Codex = "codex";

        public static string Normalize(string? value)
        {
            return value == Codex ? Codex : Claude;
        }

        public static string DisplayName(string mode) => Normalize(mode) switch
        {
            Codex => "Codex",
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
            Codex => ResolveLaunch("codex", JoinArgs(
                    $"resume {sessionId} --dangerously-bypass-approvals-and-sandbox",
                    extraArgs)),
            _ => ResolveLaunch("claude", JoinArgs(
                    JoinArgs($"--resume {sessionId}",
                        AppConfig.Load().BypassPermissions ? "--permission-mode bypassPermissions" : ""),
                    extraArgs)),
        };

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
