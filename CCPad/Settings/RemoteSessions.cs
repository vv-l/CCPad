using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.Settings
{
    /// <summary>One remote tmux session as reported by `tmux list-sessions`.</summary>
    public sealed class RemoteSessionInfo
    {
        public string Name = "";
        public DateTimeOffset Created;
        public int Attached;
        public DateTimeOffset LastActivity;
    }

    /// <summary>
    /// Lifecycle helpers for per-tab Codex@167 tmux sessions. Every remote tab
    /// owns a private session named "{SessionPrefix}-yyMMdd-HHmmss-xxxx"; the
    /// name is persisted as the tab's SessionId so freeze/snapshot/restore all
    /// reattach it through the existing plumbing. Cleanup is two-layered:
    /// closing a tab fires a best-effort one-shot `tmux kill-session` here, and
    /// an hourly cron sweeper installed on the box reaps detached prefix-named
    /// sessions older than SweepIdleHours — the backstop for kill commands lost
    /// to crashes, power cuts or network drops (tmux itself cannot distinguish
    /// an intentional close from a broken connection; both are just a detach).
    /// </summary>
    public static class RemoteSessions
    {
        private static readonly ConcurrentDictionary<string, byte> _sweeperEnsured = new();
        private static readonly ConcurrentDictionary<string, string> _sweeperStatus = new();

        /// <summary>null = not attempted yet this run; "ok" = installed; anything else = error text.</summary>
        public static string? SweeperStatus => GetSweeperStatus(null);

        public static string? GetSweeperStatus(string? deviceId)
        {
            var device = RemoteDeviceConfig.Find(deviceId);
            if (device == null) return null;
            return _sweeperStatus.TryGetValue(device.Id, out var value) ? value : null;
        }

        public static string Prefix
        {
            get
            {
                return PrefixFor(null);
            }
        }

        public static string PrefixFor(string? deviceId) =>
            NormalizePrefix(RemoteDeviceConfig.Find(deviceId)?.SessionPrefix);

        public static string NewSessionName(string? deviceId = null)
        {
            // Avoid collisions across several CC Pad processes creating tabs
            // during the same second.
            string suffix = Guid.NewGuid().ToString("N")[..8];
            return $"{PrefixFor(deviceId)}-{DateTime.Now:yyMMdd-HHmmss}-{suffix}";
        }

        /// <summary>Only sessions we named ourselves are ever killed automatically —
        /// a legacy shared session (e.g. "deploy") or anything hand-created on the
        /// box is not ours to reap.</summary>
        public static bool IsOwnedName(string? name, string? deviceId = null) =>
            !string.IsNullOrEmpty(name) &&
            name!.StartsWith(PrefixFor(deviceId) + "-", StringComparison.Ordinal);

        /// <summary>Best-effort kill on tab close. Failures are swallowed on
        /// purpose: the cron sweeper is the guaranteed cleanup path.</summary>
        public static void KillSessionFireAndForget(string name, string? deviceId = null)
        {
            if (!IsOwnedName(name, deviceId)) return;
            _ = Task.Run(() => RunSsh(deviceId,
                $"tmux kill-session -t {QuoteShell("=" + name)}", 15000));
        }

        /// <summary>Explicit kill from the session-manager UI — no ownership
        /// check, the user pointed at the session themselves.</summary>
        public static Task<bool> KillSessionAsync(string name, string? deviceId = null) => Task.Run(() =>
        {
            var (code, _, _) = RunSsh(deviceId,
                $"tmux kill-session -t {QuoteShell("=" + name)}", 15000);
            return code == 0;
        });

        /// <summary>All tmux sessions on the box. (null, error) on connection
        /// failure; an absent tmux server is an empty list, not an error.</summary>
        public static Task<(List<RemoteSessionInfo>? Sessions, string? Error)> ListSessionsAsync(
            string? deviceId = null) => Task.Run(() =>
        {
            var (code, stdout, stderr) = RunSsh(deviceId,
                "tmux list-sessions -F '#{session_name}|#{session_created}|#{session_attached}|#{session_activity}'",
                15000);
            if (code != 0)
            {
                if (stderr.Contains("no server running", StringComparison.OrdinalIgnoreCase))
                    return (new List<RemoteSessionInfo>(), (string?)null);
                var msg = (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
                return ((List<RemoteSessionInfo>?)null, msg.Length > 0 ? msg : "exit " + code);
            }

            var list = new List<RemoteSessionInfo>();
            foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.TrimEnd('\r').Split('|');
                if (parts.Length < 4) continue;
                list.Add(new RemoteSessionInfo
                {
                    Name = parts[0],
                    Created = long.TryParse(parts[1], out var c)
                        ? DateTimeOffset.FromUnixTimeSeconds(c).ToLocalTime() : default,
                    Attached = int.TryParse(parts[2], out var a) ? a : 0,
                    LastActivity = long.TryParse(parts[3], out var act)
                        ? DateTimeOffset.FromUnixTimeSeconds(act).ToLocalTime() : default,
                });
            }
            return (list, (string?)null);
        });

        /// <summary>Idempotently install the hourly cron sweeper and the tmux
        /// quality-of-life settings on the box. Ran once per app run, in the
        /// background, on the first remote tab launch (and when the
        /// session-manager dialog opens). Rewrites everything every time so
        /// changed settings take effect; failures surface as SweeperStatus in
        /// the session-manager footer.</summary>
        public static void EnsureSweeperInBackground(string? deviceId = null)
        {
            var device = RemoteDeviceConfig.Find(deviceId);
            if (device == null || !_sweeperEnsured.TryAdd(device.Id, 1)) return;
            _sweeperStatus.TryRemove(device.Id, out _);
            _ = Task.Run(() =>
            {
                int hours = Math.Max(1, device.SweepIdleHours);
                string prefix = NormalizePrefix(device.SessionPrefix);
                string script =
                    "#!/bin/sh\n" +
                    "# Installed by CC Pad: reap detached per-tab codex tmux sessions.\n" +
                    "PATH=/usr/local/bin:/usr/bin:/bin\n" +
                    $"MAX_IDLE={hours * 3600}\n" +
                    "now=$(date +%s)\n" +
                    "tmux list-sessions -F '#{session_name} #{session_attached} #{session_activity}' 2>/dev/null |\n" +
                    "while read -r name attached activity; do\n" +
                    $"  case \"$name\" in {prefix}-*) ;; *) continue ;; esac\n" +
                    "  [ \"$attached\" -eq 0 ] || continue\n" +
                    "  [ $((now - activity)) -ge \"$MAX_IDLE\" ] || continue\n" +
                    "  tmux kill-session -t \"=$name\"\n" +
                    "done\n";
                // Wheel-scrollable history and a snappy Esc for the codex TUI.
                // Fenced block so a rewrite (or a hand-removal) never touches
                // the rest of the remote user's ~/.tmux.conf.
                string tmuxConf =
                    "# BEGIN ccpad (managed by CC Pad; rewritten on every app start)\n" +
                    "# Wheel scrolls history; hold Shift and drag to select text.\n" +
                    "set -g mouse on\n" +
                    "# Codex reads lone Esc presses; drop tmux's 500ms escape wait.\n" +
                    "set -sg escape-time 10\n" +
                    "set -g history-limit 50000\n" +
                    "# Relay copies (copy-mode drags and inner-app OSC 52) to the\n" +
                    "# outer terminal so CC Pad lands them on the Windows clipboard.\n" +
                    "set -sg set-clipboard on\n" +
                    "set -as terminal-overrides ',*:Ms=\\E]52;%p1%s;%p2%s\\007'\n" +
                    "# Typing while scrolled up jumps back to the bottom (the key\n" +
                    "# itself is consumed). Any needs tmux >= 3.1; on older tmux\n" +
                    "# these two lines error at source time but the rest of the\n" +
                    "# block still applies. Both key tables so it works whether\n" +
                    "# mode-keys resolved to emacs or vi (EDITOR/VISUAL decide).\n" +
                    "bind -T copy-mode Any send -X cancel\n" +
                    "bind -T copy-mode-vi Any send -X cancel\n" +
                    "# Sentinel for CC Pad's programmatic injections (staged-command\n" +
                    "# flush): F12 exits copy-mode when active and is swallowed\n" +
                    "# otherwise, so an injection can't be eaten — or truncated by\n" +
                    "# the Any bind above — when the user is reading scrollback.\n" +
                    "bind -n F12 if -F '#{pane_in_mode}' 'send-keys -X cancel'\n" +
                    "# END ccpad\n";
                // base64 keeps the payloads' quotes/newlines out of the ssh
                // command line, which must stay free of double quotes.
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
                string confB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(tmuxConf));
                string cmd =
                    $"echo {b64} | base64 -d > /usr/local/bin/ccpad-tmux-sweep.sh" +
                    " && chmod 755 /usr/local/bin/ccpad-tmux-sweep.sh" +
                    " && printf '%s\\n' '17 * * * * root /usr/local/bin/ccpad-tmux-sweep.sh' > /etc/cron.d/ccpad-tmux-sweep" +
                    " && chmod 644 /etc/cron.d/ccpad-tmux-sweep" +
                    " && /usr/local/bin/ccpad-tmux-sweep.sh" +
                    // Replace our fenced block in ~/.tmux.conf (config for future
                    // tmux servers), then push the same options into the live
                    // server so existing sessions pick them up right now; the
                    // trailing || true keeps "no server running" from failing
                    // the whole bootstrap. history-limit only affects panes
                    // created after it is set — acceptable.
                    " && touch $HOME/.tmux.conf" +
                    " && sed -i '/^# BEGIN ccpad/,/^# END ccpad/d' $HOME/.tmux.conf" +
                    $" && echo {confB64} | base64 -d >> $HOME/.tmux.conf" +
                    " && (tmux set -g mouse on \\; set -sg escape-time 10 \\; set -g history-limit 50000 \\; set -sg set-clipboard on >/dev/null 2>&1 || true)" +
                    // Separate group: Any needs tmux >= 3.1, so a failure here
                    // must not take the mouse/escape-time push down with it.
                    " && (tmux bind -T copy-mode Any send -X cancel \\; bind -T copy-mode-vi Any send -X cancel >/dev/null 2>&1 || true)" +
                    " && (tmux bind -n F12 if -F '#{pane_in_mode}' 'send-keys -X cancel' >/dev/null 2>&1 || true)" +
                    // -a appends on every run, so only add the Ms override if the
                    // live server does not carry one yet (fresh servers get it
                    // from the conf block instead).
                    " && (tmux show -sv terminal-overrides 2>/dev/null | grep -q 'Ms=' || tmux set -as terminal-overrides ',*:Ms=\\E]52;%p1%s;%p2%s\\007' >/dev/null 2>&1 || true)";
                var (code, _, stderr) = RunSsh(device.Id, cmd, 20000);
                _sweeperStatus[device.Id] = code == 0 ? "ok"
                    : stderr.Trim().Length > 0 ? stderr.Trim() : "exit " + code;
                // Do not let a transient network or authentication failure
                // suppress installation attempts for the rest of this run.
                if (code != 0)
                    _sweeperEnsured.TryRemove(device.Id, out _);
            });
        }

        /// <summary>Keep the configured prefix safe both as a tmux name component
        /// and inside the sweeper's shell case pattern.</summary>
        private static string NormalizePrefix(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "ccpad";
            var safe = new StringBuilder(Math.Min(value.Length, 32));
            foreach (char c in value)
            {
                bool allowed = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                    or >= '0' and <= '9' or '-' or '_';
                if (allowed) safe.Append(c);
                if (safe.Length == 32) break;
            }
            return safe.Length == 0 ? "ccpad" : safe.ToString();
        }

        /// <summary>Quote one value for the POSIX shell used by ssh on the host.</summary>
        private static string QuoteShell(string value) =>
            "'" + value.Replace("'", "'\"'\"'") + "'";

        private static bool IsSafeUser(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) return false;
            return true;
        }

        private static bool IsSafeHost(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']' or '%')) return false;
            return true;
        }

        /// <summary>One-shot remote command over the same key/host the terminal
        /// panes use. BatchMode: never prompt (there is no terminal to prompt on).</summary>
        private static (int Code, string Out, string Err) RunSsh(
            string? deviceId, string remoteCmd, int timeoutMs)
        {
            var device = RemoteDeviceConfig.Find(deviceId);
            return device == null
                ? (-1, "", "external device not found")
                : RemoteDeviceConnection.Run(device, remoteCmd, timeoutMs);
        }
    }
}
