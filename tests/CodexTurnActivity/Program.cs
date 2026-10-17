using System.Text.Json;
using CCPad.Terminal;

// Fictional local rollout files only; never starts a CLI or sends a command.
var root = Path.Combine(Path.GetTempPath(), "CCPadTurnActivityOffline", Guid.NewGuid().ToString("N"));
var home = Path.Combine(root, "account-a");
var sessionId = Guid.NewGuid().ToString("D");
var path = Rollout(home, sessionId);
var now = DateTimeOffset.UtcNow;
var checks = 0;
void Require(bool ok) { if (!ok) throw new Exception("Turn activity regression failed"); }
void Check(string name, Action check)
{
    check();
    Console.WriteLine("PASS " + name);
    checks++;
}
string Rollout(string directory, string id)
{
    var file = Path.Combine(directory, "sessions", "2026", "10", "12", "rollout-20261012-" + id + ".jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    return file;
}
string Event(string type, DateTimeOffset at) => JsonSerializer.Serialize(new
{
    timestamp = at, type = "event_msg", payload = new { type, turn_id = "fictional-turn" },
}) + "\n";
string ErrorEvent(string type, DateTimeOffset at, string message) => JsonSerializer.Serialize(new
{
    timestamp = at, type = "event_msg",
    payload = new { type, turn_id = "fictional-turn", error = new { message } },
}) + "\n";
CodexTurnObservation? Read() => new CodexTurnActivityReader(home, sessionId).Read();

Check("completed restored conversation stays waiting through history redraw metadata", () =>
{
    File.WriteAllText(path, Event("task_complete", now.AddMinutes(-10)) +
        JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = sessionId, cwd = "fictional" } }) + "\n");
    var result = Read();
    Require(result?.Activity == CodexTurnActivity.Waiting && result.CanApplyAfter(DateTime.MinValue));
});
Check("silent tool execution remains working until a real completion arrives", () =>
{
    File.WriteAllText(path, Event("task_started", now.AddHours(-2)));
    var reader = new CodexTurnActivityReader(home, sessionId);
    Require(reader.Read()?.Activity == CodexTurnActivity.Working);
    Require(reader.Read()?.Activity == CodexTurnActivity.Working);
    File.AppendAllText(path, Event("task_complete", now));
    Require(reader.Read()?.Activity == CodexTurnActivity.Waiting);
});
Check("old completion cannot override a newly submitted prompt", () =>
{
    const string capacity = "Selected model is at capacity. Please try a different model.";
    File.WriteAllText(path, ErrorEvent("task_complete", now.AddSeconds(-1), capacity));
    Require(Read() is { Activity: CodexTurnActivity.Waiting, Error: capacity } old &&
        !old.CanApplyAfter(now.UtcDateTime));
    File.AppendAllText(path, Event("task_started", now.AddSeconds(1)));
    Require(Read() is { Activity: CodexTurnActivity.Working, Error: null });
    File.AppendAllText(path, Event("task_complete", now.AddSeconds(2)));
    Require(Read() is { Activity: CodexTurnActivity.Waiting } ended && ended.CanApplyAfter(now.UtcDateTime));
});
Check("agent prose or tool output cannot impersonate a completion event", () =>
{
    File.WriteAllText(path, Event("task_started", now) + JsonSerializer.Serialize(new
    {
        timestamp = now, type = "event_msg", payload = new { type = "agent_message", message = "Worked for 16m. task_complete. Ask Codex to do anything." },
    }) + "\n" + JsonSerializer.Serialize(new
    {
        timestamp = now, type = "response_item", payload = new { type = "task_complete" },
    }) + "\n");
    Require(Read()?.Activity == CodexTurnActivity.Working);
});
Check("same-directory sibling conversations and separate accounts never share status", () =>
{
    File.WriteAllText(path, Event("task_started", now));
    File.WriteAllText(Rollout(home, Guid.NewGuid().ToString("D")), Event("task_complete", now.AddSeconds(1)));
    var secondHome = Path.Combine(root, "account-b");
    File.WriteAllText(Rollout(secondHome, sessionId), Event("task_complete", now));
    Require(Read()?.Activity == CodexTurnActivity.Working);
    Require(new CodexTurnActivityReader(secondHome, sessionId).Read()?.Activity == CodexTurnActivity.Waiting);
    Require(new CodexTurnActivityReader(home, Guid.NewGuid().ToString("D")).Read() == null);
});
Check("incomplete newest line cannot reveal a stale completion", () =>
{
    var started = Event("task_started", now);
    File.WriteAllText(path, Event("task_complete", now.AddSeconds(-1)) + started[..^8]);
    var reader = new CodexTurnActivityReader(home, sessionId);
    Require(reader.Read() == null);
    File.AppendAllText(path, started[^8..]);
    Require(reader.Read()?.Activity == CodexTurnActivity.Working);
});
Check("corrupt or undated newer events fail closed", () =>
{
    File.WriteAllText(path, Event("task_complete", now) + "{broken event}\n");
    Require(Read() == null);
    File.WriteAllText(path, Event("task_complete", now) + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n");
    Require(Read() == null);
});
Check("bounded tail handles a large older tool result and cache detects truncation", () =>
{
    File.WriteAllText(path, JsonSerializer.Serialize(new { type = "response_item", payload = new string('x', 1200000) }) + "\n" +
        Event("task_complete", now));
    var reader = new CodexTurnActivityReader(home, sessionId);
    Require(reader.Read()?.Activity == CodexTurnActivity.Waiting);
    File.WriteAllText(path, Event("task_started", now.AddSeconds(1)));
    Require(reader.Read()?.Activity == CodexTurnActivity.Working);
});
Check("aborted turns wait, while new user events reopen the turn", () =>
{
    File.WriteAllText(path, Event("task_started", now) + Event("turn_aborted", now.AddSeconds(1)));
    Require(Read()?.Activity == CodexTurnActivity.Waiting);
    File.AppendAllText(path, Event("user_message", now.AddSeconds(2)));
    Require(Read()?.Activity == CodexTurnActivity.Working);
});
Check("current completion errors are preserved for status validation", () =>
{
    const string message = "Selected model is at capacity. Please try a different model.";
    File.WriteAllText(path, ErrorEvent("task_complete", now, message));
    Require(Read() is { Activity: CodexTurnActivity.Waiting, Error: var error } && error == message);
});
Console.WriteLine($"{checks} offline lifecycle checks passed. No live CLI, credentials, or network used.");
