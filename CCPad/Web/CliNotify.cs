using System;
using System.Collections.Concurrent;
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
    /// keyed by its PaneId; the hook URL (with the pane id + port baked in) is
    /// generated into a per-pane settings file passed to
    /// <c>claude --settings &lt;file&gt;</c>, so the user's own ~/.claude/settings.json
    /// is never touched.
    ///
    /// Uses a raw <see cref="TcpListener"/> (not HttpListener/http.sys) because
    /// binding a loopback TCP socket needs no admin rights or URL ACL, whereas
    /// http.sys would refuse a non-admin process. curl's request is trivial — we
    /// only parse the first request line and reply 204.
    ///
    /// Hook → URL mapping (event= query param):
    ///   SessionStart, Notification, Stop → event=waiting  (started/resumed,
    ///                                       needs you, or turn done)
    ///   UserPromptSubmit                 → event=working  (you gave it work)
    /// </summary>
    internal static class CliNotify
    {
        private static readonly object _gate = new();
        private static TcpListener? _listener;
        private static volatile bool _started;

        public static int Port { get; private set; }
        public static bool IsRunning => _started;

        // PaneId -> handler(eventName). Handlers are invoked off the UI thread;
        // callers marshal to the dispatcher themselves.
        private static readonly ConcurrentDictionary<string, Action<string>> _handlers = new();

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
                for (int port = 9700; port <= 9799; port++)
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

        // Parse "/notify?session=<id>&event=<evt>" and invoke the pane's handler.
        private static void Dispatch(string target)
        {
            int q = target.IndexOf('?');
            if (q < 0) return;
            string query = target[(q + 1)..];

            string? session = null, evt = null;
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string key = pair[..eq];
                string val = pair[(eq + 1)..];
                if (key == "session") session = val;
                else if (key == "event") evt = val;
            }

            if (!string.IsNullOrEmpty(session) && evt != null &&
                _handlers.TryGetValue(session, out var handler))
            {
                try { handler(evt); } catch { }
            }
        }

        public static void Register(string paneId, Action<string> handler)
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
                string arr = $"['{exe}','--notify','{paneId}','{Port}']";
                return $"-c \"notify={arr}\"";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Send a single notify request to the listener in the main CCPad
        /// instance. Called by the short-lived <c>CCPad.exe --notify</c> helper
        /// that Codex spawns; builds the raw HTTP request line directly so there
        /// is no shell and no '&amp;'/quoting concern.
        /// </summary>
        public static void SendLocal(int port, string paneId, string evt)
        {
            try
            {
                using var client = new TcpClient();
                if (!client.ConnectAsync(IPAddress.Loopback, port).Wait(1500)) return;
                using var stream = client.GetStream();
                var req = Encoding.ASCII.GetBytes(
                    $"GET /notify?session={paneId}&event={evt} HTTP/1.0\r\n" +
                    "Host: 127.0.0.1\r\nConnection: close\r\n\r\n");
                stream.Write(req, 0, req.Length);
                stream.Flush();
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
        //   curl -s -m 2 "http://127.0.0.1:PORT/notify?session=PANEID&event=EVT"
        // The double-quoted URL is parsed identically by cmd.exe and Git Bash
        // (the two shells Claude may use to run a hook on Windows), so the '&' is
        // safe. Inner quotes are escaped as \" for the surrounding JSON string.
        private static string HookCmd(string paneId, string evt)
            => $"curl -s -m 2 \\\"http://127.0.0.1:{Port}/notify?session={paneId}&event={evt}\\\"";
    }
}
