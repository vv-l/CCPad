using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace CCPad.Web
{
    /// <summary>
    /// Always-on loopback listener that receives "this CLI is waiting for you"
    /// callbacks from Claude Code hooks. Each terminal pane registers a handler
    /// keyed by its PaneId. The hook is generated into a per-pane settings file
    /// passed to <c>claude --settings &lt;file&gt;</c>, so the user's own
    /// ~/.claude/settings.json is never touched. The hook runs
    /// <c>CCPad.exe --notify &lt;paneId&gt; &lt;evt&gt;</c>, which broadcasts the event
    /// to every listener in the range (see <see cref="Broadcast"/>) so it reaches
    /// the live process that owns the pane no matter which port that process bound.
    ///
    /// Uses a raw <see cref="TcpListener"/> (not HttpListener/http.sys) because
    /// binding a loopback TCP socket needs no admin rights or URL ACL, whereas
    /// http.sys would refuse a non-admin process. The notify request is trivial —
    /// we only parse the first request line and reply 204.
    ///
    /// Hook → event mapping:
    ///   SessionStart, Notification, Stop → waiting  (started/resumed,
    ///                                       needs you, or turn done)
    ///   UserPromptSubmit                 → working  (you gave it work)
    /// </summary>
    internal static class CliNotify
    {
        private static readonly object _gate = new();
        private static TcpListener? _listener;
        private static volatile bool _started;

        public static int Port { get; private set; }
        public static bool IsRunning => _started;

        // Loopback range the notify listeners live in. One process binds one port
        // (first-come), so several CCPad windows occupy 9700, 9701, ... in turn.
        private const int PortRangeStart = 9700;
        private const int PortRangeEnd = 9799;

        // PaneId -> handler(eventName, cliSessionId). The session ID is the CLI's
        // real conversation UUID (from the Claude hook payload), null when the
        // event source doesn't report one (Codex, legacy hooks). Handlers are
        // invoked off the UI thread; callers marshal to the dispatcher themselves.
        private static readonly ConcurrentDictionary<string, Action<string, string?>> _handlers = new();

        private static readonly string HookDir = CCPad.Settings.AppPaths.Sub("hooks");

        /// <summary>
        /// Ensure the listener is up. Idempotent and safe to call from any thread.
        /// Returns false if it could not bind (notifications silently disabled).
        /// </summary>
        public static bool EnsureStarted()
        {
            if (_started) return true;
            lock (_gate)
            {
                if (_started) return true;
                for (int port = PortRangeStart; port <= PortRangeEnd; port++)
                {
                    try
                    {
                        var listener = new TcpListener(IPAddress.Loopback, port);
                        listener.Start();
                        _listener = listener;
                        Port = port;
                        _started = true;
                        _ = Task.Run(AcceptLoopAsync);
                        return true;
                    }
                    catch
                    {
                        // Port in use / unavailable — try the next one.
                    }
                }
                return false;
            }
        }

        private static async Task AcceptLoopAsync()
        {
            var listener = _listener;
            if (listener == null) return;
            while (_started)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleClient(client));
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 2000;
                    // Read only the request line: "GET /notify?session=..&event=.. HTTP/1.1"
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 256, leaveOpen: true);
                    string? requestLine = null;
                    try { requestLine = reader.ReadLine(); } catch { }

                    if (requestLine != null)
                    {
                        var parts = requestLine.Split(' ');
                        if (parts.Length >= 2)
                            Dispatch(parts[1]); // the request target / path?query
                    }

                    var resp = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    try { stream.Write(resp, 0, resp.Length); } catch { }
                }
            }
            catch { }
        }

        // Parse "/notify?session=<paneId>&event=<evt>[&sid=<cliSessionId>]" and
        // invoke the pane's handler.
        private static void Dispatch(string target)
        {
            int q = target.IndexOf('?');
            if (q < 0) return;
            string query = target[(q + 1)..];

            string? session = null, evt = null, sid = null;
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string key = pair[..eq];
                string val = pair[(eq + 1)..];
                if (key == "session") session = val;
                else if (key == "event") evt = val;
                else if (key == "sid") sid = val;
            }

            if (!string.IsNullOrEmpty(session) && evt != null &&
                _handlers.TryGetValue(session, out var handler))
            {
                try { handler(evt, string.IsNullOrEmpty(sid) ? null : sid); } catch { }
            }
        }

        public static void Register(string paneId, Action<string, string?> handler)
            => _handlers[paneId] = handler;

        public static void Unregister(string paneId)
            => _handlers.TryRemove(paneId, out _);

        /// <summary>
        /// Write a per-pane Claude settings file containing the notification
        /// hooks and return the <c>--settings "&lt;path&gt;"</c> argument to append
        /// to the launch command. Returns "" if the listener can't start.
        /// </summary>
        public static string PrepareClaudeHooks(string paneId)
        {
            if (!EnsureStarted()) return "";
            try
            {
                Directory.CreateDirectory(HookDir);
                string path = Path.Combine(HookDir, paneId + ".json");
                File.WriteAllText(path, BuildHookJson(paneId), new UTF8Encoding(false));
                return $"--settings \"{path}\"";
            }
            catch
            {
                return "";
            }
        }

        public static void CleanupHooks(string paneId)
        {
            try
            {
                string path = Path.Combine(HookDir, paneId + ".json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// The <c>--settings "&lt;hookfile&gt;"</c> arg for a pane whose hook file
        /// still exists (written at launch, removed on Dispose), or "" otherwise.
        /// Used to re-instrument a Claude resumed from inside the fallback shell so
        /// its SessionStart/Stop/etc. hooks drive the status light again.
        /// </summary>
        public static string ClaudeSettingsArg(string paneId)
        {
            try
            {
                string path = Path.Combine(HookDir, paneId + ".json");
                if (File.Exists(path)) return $"--settings \"{path}\"";
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Build the per-launch Codex notify override:
        ///   -c "notify=['&lt;CCPad.exe&gt;','--notify','&lt;paneId&gt;','&lt;port&gt;']"
        /// Codex direct-execs this program on agent-turn-complete and appends a
        /// JSON payload as a final arg. Routing through <c>CCPad.exe --notify</c>
        /// (instead of curl) lets us ignore that trailing arg and build the
        /// loopback request in-process — curl would treat the JSON as a second
        /// URL and hang on its connect timeout. Single-quoted TOML literals keep
        /// the backslashes + space in the exe path intact, and the double-quoted
        /// value survives both the codex.exe and codex.cmd launch forms.
        /// Codex emits only agent-turn-complete, so the helper always reports
        /// "waiting" (your turn). Returns "" if the listener can't start.
        /// </summary>
        public static string PrepareCodexNotify(string paneId)
        {
            if (!EnsureStarted()) return "";
            try
            {
                string exe = Path.Combine(AppContext.BaseDirectory, "CCPad.exe");
                // Pass the event word, not a port: the helper broadcasts to find
                // the live process that owns this pane (see Broadcast). Codex emits
                // only agent-turn-complete, so it always means "waiting" (your turn).
                string arr = $"['{exe}','--notify','{paneId}','waiting']";
                return $"-c \"notify={arr}\"";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Deliver a notify event to whichever live CCPad process currently owns
        /// the pane — fanning the request out to every loopback listener in the
        /// range rather than trusting a single port. The owning process is NOT
        /// reliably the one that launched the pane: a session resumed after a
        /// restart, or any setup with several CCPad windows opened/closed in a
        /// different order, can leave the pane's handler on a different listener
        /// port than the one baked into its hook file hours earlier. A baked port
        /// would then reach the wrong (or a dead) process and the event would be
        /// silently dropped — that is the stuck-status-light bug this replaces.
        /// Only the process whose <c>_handlers</c> contains this paneId acts; every
        /// other process no-ops in <see cref="Dispatch"/>. paneIds are unique, so
        /// there is no double-fire. Called by the short-lived
        /// <c>CCPad.exe --notify &lt;paneId&gt; &lt;evt&gt;</c> helper.
        /// </summary>
        public static void Broadcast(string paneId, string evt, string? cliSessionId = null)
        {
            var tasks = new List<Task>(PortRangeEnd - PortRangeStart + 1);
            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
                tasks.Add(SendLocalAsync(port, paneId, evt, cliSessionId));
            try { Task.WhenAll(tasks).Wait(3000); } catch { }
        }

        /// <summary>
        /// Send a single notify request to the listener on <paramref name="port"/>.
        /// Retained for the legacy baked-port path (older hook files / Codex
        /// configs still running). Builds the raw HTTP request line directly so
        /// there is no shell and no '&amp;'/quoting concern.
        /// </summary>
        public static void SendLocal(int port, string paneId, string evt, string? cliSessionId = null)
        {
            try { SendLocalAsync(port, paneId, evt, cliSessionId).Wait(2000); } catch { }
        }

        private static async Task SendLocalAsync(int port, string paneId, string evt, string? cliSessionId = null)
        {
            try
            {
                using var client = new TcpClient();
                // Closed loopback ports refuse instantly; the 500ms cap only matters
                // for the rare port that accepts-but-stalls, so a full 100-port sweep
                // finishes in milliseconds in the normal case. WaitAsync re-throws a
                // refused connection and throws on timeout — both caught below, and
                // it observes the connect task so there is no unobserved-exception.
                await client.ConnectAsync(IPAddress.Loopback, port)
                    .WaitAsync(TimeSpan.FromMilliseconds(500));
                if (!client.Connected) return;
                using var stream = client.GetStream();
                string sidPart = string.IsNullOrEmpty(cliSessionId) ? "" : $"&sid={cliSessionId}";
                var req = Encoding.ASCII.GetBytes(
                    $"GET /notify?session={paneId}&event={evt}{sidPart} HTTP/1.0\r\n" +
                    "Host: 127.0.0.1\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(req, 0, req.Length);
                await stream.FlushAsync();
            }
            catch { }
        }

        private static string BuildHookJson(string paneId)
        {
            string waiting = HookCmd(paneId, "waiting");
            string working = HookCmd(paneId, "working");

            string one(string evt, string cmd) =>
                $"\"{evt}\":[{{\"hooks\":[{{\"type\":\"command\",\"command\":\"{cmd}\"}}]}}]";

            // SessionStart fires on launch AND on --resume, so a Claude resumed
            // from the fallback shell flips the (red) light back to amber the moment
            // it comes up — no process-tracking needed.
            return "{\"hooks\":{" +
                   one("SessionStart", waiting) + "," +
                   one("Notification", waiting) + "," +
                   one("Stop", waiting) + "," +
                   one("UserPromptSubmit", working) +
                   "}}";
        }

        // The JSON-escaped hook command:
        //   "<CCPad.exe>" --notify <paneId> <evt>
        // Routing through CCPad.exe (instead of curl to a baked port) lets the
        // helper BROADCAST the event to whichever live process owns the pane, so a
        // session that moved listener ports — resume after restart, several windows
        // — still lights up (see Broadcast). The exe path is double-quoted for the
        // shell (cmd.exe / Git Bash both accept it) and its backslashes are doubled
        // so the value is valid inside the surrounding JSON string; inner quotes are
        // escaped as \". No '&' anymore, so no shell-quoting concern at all.
        private static string HookCmd(string paneId, string evt)
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "CCPad.exe")
                .Replace("\\", "\\\\");
            return $"\\\"{exe}\\\" --notify {paneId} {evt}";
        }
    }
}
