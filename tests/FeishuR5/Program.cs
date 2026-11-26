using System.Text.Json;
using CCPad.Web;
using CCPad.Terminal;

int passed = 0;
var sessionId = Guid.NewGuid().ToString("D");
string Input(string id, string data, string? expected = null, string? provenance = null, string? turn = null) =>
    JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "input", ["inputId"] = id,
        ["sessionId"] = "pane", ["data"] = data }.Concat(expected == null ? [] : new Dictionary<string, object?>
        { ["expectedCliSessionId"] = expected, ["expectedSessionProvenance"] = provenance })
        .Concat(turn == null ? [] : new Dictionary<string, object?> { ["expectedTurnId"] = turn })
        .ToDictionary(x => x.Key, x => x.Value));
void Check(bool value) { if (!value) throw new Exception("fixture assertion failed"); }
async Task Case(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
async Task<(FixtureSocket Socket, ConPtySession Pty)> Run(string current, string provenance,
    params string[] inputs)
{
    var pty = new ConPtySession();
    var entry = new SessionEntry { Id = "pane", Label = "fixture", Command = "unused", Session = pty,
        CliMode = "codex", CliSessionId = current, SessionProvenance = provenance,
        WriteBoundInputAsync = (text, expected, source, turn) =>
        {
            var matched = current == expected && provenance == source &&
                (turn == null || text == "\x03" && turn == "active-turn");
            return Task.FromResult(new BoundInputResult(matched, matched && pty.TryWriteInput(text)));
        } };
    TerminalSessionRegistry.Register("pane", entry);
    var socket = new FixtureSocket(); socket.Add("{\"type\":\"attach\",\"sessionId\":\"pane\"}");
    foreach (var input in inputs) socket.Add(input);
    try { using var server = new WebTerminalSession(); await server.RunAsync(socket, CancellationToken.None); }
    finally { TerminalSessionRegistry.Unregister("pane"); }
    return (socket, pty);
}
void Ack(FixtureSocket socket, string status, string? error = null)
{
    var ack = socket.Sent.Last(x => x.GetProperty("type").GetString() == "input_ack");
    Check(ack.GetProperty("status").GetString() == status);
    if (error != null) Check(ack.GetProperty("errorCode").GetString() == error);
}
await Case("bound confirmed input uses existing PTY ACK", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "hello\r", sessionId, "official_notify"));
    Check(p.Writes.SequenceEqual(["hello\r"])); Ack(s, "written");
});
await Case("session changed after API discovery rejects without write", async () =>
{
    var (s, p) = await Run(Guid.NewGuid().ToString(), "official_notify", Input("a", "hello\r", sessionId, "official_notify"));
    Check(p.Writes.Count == 0); Ack(s, "rejected", "cli_identity_mismatch");
});
await Case("provenance mismatch rejects without write", async () =>
{
    var (s, p) = await Run(sessionId, "latest-file", Input("a", "hello\r", sessionId, "official_notify"));
    Check(p.Writes.Count == 0); Ack(s, "rejected", "cli_identity_mismatch");
});
await Case("missing expected provenance rejects without write", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "hello\r", sessionId));
    Check(p.Writes.Count == 0); Ack(s, "rejected", "cli_identity_mismatch");
});
await Case("duplicate input ID with changed identity is conflict", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "hello\r", sessionId, "official_notify"),
        Input("a", "hello\r", Guid.NewGuid().ToString(), "official_notify"));
    Check(p.Writes.Count == 1); Ack(s, "rejected", "input_id_conflict");
});
await Case("legacy input maintains its protocol", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "legacy\r"));
    Check(p.Writes.Count == 1); Ack(s, "written");
});
await Case("abort exact active turn writes Ctrl-C once", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "\x03", sessionId, "official_notify", "active-turn"));
    Check(p.Writes.SequenceEqual(["\x03"])); Ack(s, "written");
});
await Case("stale turn abort refuses another task", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "\x03", sessionId, "official_notify", "old-turn"));
    Check(p.Writes.Count == 0); Ack(s, "rejected", "cli_identity_mismatch");
});
await Case("turn expectation only permits guarded Ctrl-C", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "hello\r", sessionId, "official_notify", "active-turn"));
    Check(p.Writes.Count == 0); Ack(s, "rejected", "cli_identity_mismatch");
});
await Case("bound Ctrl-C requires explicit turn ID", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "\x03", sessionId, "official_notify"));
    Check(p.Writes.Count == 0); Ack(s, "rejected", "cli_identity_mismatch");
});
await Case("guarded fields without input ID cannot take legacy write path", async () =>
{
    var input = JsonSerializer.Serialize(new { type = "input", data = "hello\r", sessionId = "pane",
        expectedCliSessionId = sessionId, expectedSessionProvenance = "official_notify" });
    var (s, p) = await Run(sessionId, "official_notify", input);
    Check(p.Writes.Count == 0 && s.Sent.Any(x => x.GetProperty("type").GetString() == "error"));
});
await Case("adding invalid expectation cannot reuse legacy ACK fingerprint", async () =>
{
    var invalid = JsonSerializer.Serialize(new { type = "input", inputId = "a", sessionId = "pane", data = "hello\r",
        expectedCliSessionId = 12, expectedSessionProvenance = "official_notify" });
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "hello\r"), invalid);
    Check(p.Writes.Count == 1); Ack(s, "rejected", "input_id_conflict");
});
await Case("turn ID participates in duplicate fingerprint", async () =>
{
    var (s, p) = await Run(sessionId, "official_notify", Input("a", "\x03", sessionId, "official_notify", "active-turn"),
        Input("a", "\x03", sessionId, "official_notify", "old-turn"));
    Check(p.Writes.Count == 1); Ack(s, "rejected", "input_id_conflict");
});
await Case("exact rollout lifecycle preserves turn ID and fails closed", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "ccpad-turn-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(home, "sessions"));
    var file = Path.Combine(home, "sessions", "rollout-fixture-" + sessionId + ".jsonl");
    string Line(string kind, string? turn) => JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow,
        type = "event_msg", payload = new { type = kind, turn_id = turn } }) + "\n";
    string Meta(string id) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } }) + "\n";
    File.WriteAllText(file, Meta(sessionId) + Line("task_started", "active-turn"));
    Check(new CodexTurnActivityReader(home, sessionId).ReadForAbort()?.CanAbort("active-turn") == true);
    Check(new CodexTurnActivityReader(home, sessionId).Read()?.CanAbort("other-turn") == false);
    File.WriteAllText(file, Line("task_complete", "active-turn"));
    Check(new CodexTurnActivityReader(home, sessionId).Read()?.CanAbort("active-turn") == false);
    File.WriteAllText(file, Line("task_started", null));
    Check(new CodexTurnActivityReader(home, sessionId).Read()?.CanAbort("active-turn") == false);
    File.WriteAllText(file, "{broken\n");
    Check(new CodexTurnActivityReader(home, sessionId).Read() == null);
    File.WriteAllText(file, Meta(Guid.NewGuid().ToString()) + Line("task_started", "active-turn"));
    Check(new CodexTurnActivityReader(home, sessionId).ReadForAbort() == null);
    File.WriteAllText(file, Line("task_started", "active-turn"));
    Check(new CodexTurnActivityReader(home, sessionId).ReadForAbort() == null);
    File.WriteAllText(file, Meta(sessionId) + Line("task_started", "active-turn"));
    File.WriteAllText(Path.Combine(home, "sessions", "rollout-duplicate-" + sessionId + ".jsonl"),
        Meta(sessionId) + Line("task_started", "active-turn"));
    Check(new CodexTurnActivityReader(home, sessionId).ReadForAbort() == null);
    return Task.CompletedTask;
});
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, tests = passed, real_processes = 0, network = 0, model_calls = 0 }));
