using System.Text;
using System.Text.Json;
using CCPad.Terminal;

if (args is ["--inspect", var home, var session])
{
    var reader = new CodexObservationReader(home, session);
    ObservationSnapshot result;
    var polls = 0;
    do { result = reader.Read(); polls++; } while (result.CatchingUp && polls < 100);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        agents = result.Agents.Count, events = result.Events.Count,
        states = result.Agents.GroupBy(a => a.State).ToDictionary(g => g.Key, g => g.Count()),
        kinds = result.Events.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
        result.HistoryClipped, result.UnreadableLines, result.Error, polls
    }));
    return result.Error == null && result.Agents.Count > 0 ? 0 : 1;
}

var count = 0;
void Check(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
void Require(bool value) { if (!value) throw new Exception("Observation assertion failed"); }

Check("exact identity excludes a newer same-directory root and interactive fork", () =>
{
    var f = new Fixture();
    f.Agent();
    f.Agent(fork: f.Root);
    var result = f.Read();
    Require(result.Agents.Count == 1 && result.Agents[0].Id == f.Root);
    Require(CodexObservationReader.ValidateRoot(f.Home, f.Root, f.Cwd) == f.Root);
    Require(CodexObservationReader.ValidateRoot(f.Home, Guid.NewGuid().ToString(), f.Cwd) == null);
    Require(CodexObservationReader.ValidateRoot(f.Home, f.Root, Path.Combine(f.Home, "other")) == null);
    Require(CodexObservationReader.ValidateRoot(Path.Combine(f.Home, "another-account"), f.Root, f.Cwd) == null);
});
Check("native children and grandchildren follow explicit ancestry", () =>
{
    var f = new Fixture();
    var child = f.Agent(f.Root);
    var grandchild = f.Agent(child);
    f.Agent(Guid.NewGuid().ToString());
    var result = f.Read();
    Require(result.Agents.Count == 3 && result.Agents.Any(a => a.Id == grandchild && a.ParentId == child));
    Require(CodexObservationReader.ValidateRoot(f.Home, child, f.Cwd) == null);
});
Check("filename identity and conflicting parents are rejected", () =>
{
    var f = new Fixture();
    var fake = Guid.NewGuid().ToString();
    File.Copy(f.PathFor(f.Root), f.PathFor(fake));
    Require(CodexObservationReader.ReadMetadata(f.PathFor(fake)) == null);
    var bad = f.Agent(f.Root);
    var line = File.ReadAllText(f.PathFor(bad)).Replace("\"parent_thread_id\":\"" + f.Root + "\"", "\"parent_thread_id\":\"" + fake + "\"", StringComparison.Ordinal);
    // One explicit parent disagrees with the nested source parent.
    var first = line.IndexOf(fake, StringComparison.Ordinal);
    line = line[..first] + f.Root + line[(first + fake.Length)..];
    File.WriteAllText(f.PathFor(bad), line);
    Require(CodexObservationReader.ReadMetadata(f.PathFor(bad)) == null);
});
Check("tools pair by call ID and expose explicit exit failure and elapsed time", () =>
{
    var f = new Fixture();
    f.Event("task_started", new { turn_id = "turn-a" });
    f.Response(new { type = "custom_tool_call", call_id = "call-a", name = "exec", input = "python probe.py" });
    f.Response(new { type = "custom_tool_call_output", call_id = "call-a", output = "Process exited with code 1\nFileNotFoundError: rg" });
    var result = f.Read();
    var call = result.Events.Single(e => e.Kind == "tool");
    Require(call.State == "failed" && call.Seconds > 0 && call.Detail.Contains("python probe.py") && call.Detail.Contains("FileNotFoundError"));
    Require(result.Agents[0].State == "working");
});
Check("an arbitrary successful tool return is not labelled successful task completion", () =>
{
    var f = new Fixture();
    f.Response(new { type = "function_call", call_id = "a", name = "read", arguments = "{}" });
    f.Response(new { type = "function_call_output", call_id = "a", output = "ok" });
    Require(f.Read().Events.Single().State == "returned");
});
Check("structured tool errors are recognized without treating quoted error examples as failures", () =>
{
    var f = new Fixture();
    f.Response(new { type = "function_call_output", call_id = "a", output = "{\"exit_code\":1,\"output\":\"failed\"}" });
    f.Response(new { type = "function_call_output", call_id = "b", output = "Example documentation: {\"isError\":true}" });
    var events = f.Read().Events;
    Require(events[0].State == "failed" && events[1].State == "returned");
});
Check("incremental appends are not duplicated and UTF-8 split lines wait for completion", () =>
{
    var f = new Fixture();
    var reader = f.Reader();
    reader.Read();
    var row = f.Row("response_item", new { type = "message", role = "assistant", channel = "commentary", content = new[] { new { type = "output_text", text = "正在核查" } } });
    var bytes = Encoding.UTF8.GetBytes(row);
    var split = Array.IndexOf(bytes, (byte)0xe6) + 1;
    using (var stream = File.Open(f.PathFor(f.Root), FileMode.Append)) stream.Write(bytes.AsSpan(0, split));
    Require(reader.Read().Events.Count == 0);
    using (var stream = File.Open(f.PathFor(f.Root), FileMode.Append)) stream.Write(bytes.AsSpan(split));
    var result = reader.Read();
    Require(result.Events.Count == 1 && result.Events[0].Detail == "正在核查");
    Require(reader.Read().Events.Count == 1);
});
Check("private reasoning, system messages and mirrored item events never enter the timeline", () =>
{
    var f = new Fixture();
    foreach (var channel in new[] { "analysis", "commentary", "final" })
        f.Response(new { type = "message", role = "assistant", channel, content = new[] { new { type = "output_text", text = channel } } });
    f.Response(new { type = "reasoning", summary = new[] { new { text = "private" } } });
    f.Response(new { type = "message", role = "developer", content = new[] { new { text = "internal" } } });
    f.Event("item_completed", new { item = new { type = "message", text = "mirror" } });
    var result = f.Read();
    Require(result.Events.Count == 2 && result.Events.All(e => e.Detail is "commentary" or "final"));
});
Check("copied parent history is excluded from worker events", () =>
{
    var f = new Fixture();
    var child = f.Agent(f.Root, created: f.Start.AddSeconds(50));
    File.AppendAllText(f.PathFor(child), f.Row("response_item", new { type = "function_call", name = "inherited", call_id = "old", arguments = "{}" }));
    var result = f.Read();
    Require(result.Agents.Count == 2 && result.Events.Count == 0);
});
Check("current Codex phase messages and per-thread usage records are supported", () =>
{
    var f = new Fixture();
    f.Response(new { type = "message", role = "assistant", phase = "commentary", content = new[] { new { type = "output_text", text = "progress" } } });
    f.Response(new { type = "message", role = "assistant", phase = "final_answer", content = new[] { new { type = "output_text", text = "result" } } });
    File.AppendAllText(f.PathFor(f.Root), f.Row("token_usage_record", new { thread_id = f.Root, thread_token_usage = new { input_tokens = 150, cached_input_tokens = 100, output_tokens = 40 } }));
    File.AppendAllText(f.PathFor(f.Root), f.Row("token_usage_record", new { thread_id = Guid.NewGuid().ToString(), thread_token_usage = new { input_tokens = 999 } }));
    var result = f.Read();
    Require(result.Events.Count == 2 && result.Events[1].Title == "answer" && result.Agents[0].InputTokens == 150);
});
Check("usage is cumulative per session and cached input is not added twice", () =>
{
    var f = new Fixture();
    f.Event("token_count", new { info = new { total_token_usage = new { input_tokens = 100, cached_input_tokens = 80, output_tokens = 20 } } });
    f.Event("token_count", new { info = new { total_token_usage = new { input_tokens = 130, cached_input_tokens = 90, output_tokens = 30 } } });
    var agent = f.Read().Agents.Single();
    Require(agent.InputTokens == 130 && agent.CachedTokens == 90 && agent.OutputTokens == 30);
});
Check("a late old turn completion cannot end the current turn", () =>
{
    var f = new Fixture();
    f.Event("task_started", new { turn_id = "new" });
    f.Event("task_complete", new { turn_id = "old" });
    Require(f.Read().Agents.Single().State == "working");
    f.Event("task_complete", new { turn_id = "new" });
    Require(f.Read().Agents.Single().State == "waiting");
});
Check("file truncation resets old events, pending calls and usage", () =>
{
    var f = new Fixture();
    var meta = File.ReadAllText(f.PathFor(f.Root));
    f.Response(new { type = "function_call", name = "old", call_id = "a", arguments = new string('x', 3000) });
    var reader = f.Reader();
    Require(reader.Read().Events.Count == 1);
    File.WriteAllText(f.PathFor(f.Root), meta);
    f.Event("task_started", new { turn_id = "new" });
    var result = reader.Read();
    Require(result.Events.Count == 1 && result.Events[0].Title == "turn_started");
});
Check("large histories and malformed records are bounded and visibly marked", () =>
{
    var f = new Fixture();
    for (var i = 0; i < 600; i++)
        f.Response(new { type = "function_call", name = "probe", call_id = i.ToString(), arguments = new string('x', 4000) });
    File.AppendAllText(f.PathFor(f.Root), "{invalid json}\n");
    var reader = f.Reader();
    ObservationSnapshot result;
    do { result = reader.Read(); } while (result.CatchingUp);
    Require(result.HistoryClipped && result.Events.Count <= 400 && result.UnreadableLines == 1);
    Require(reader.Read().UnreadableLines == 1);
});
Console.WriteLine($"{count} observation checks passed; synthetic fixtures retained in the temporary directory.");
return 0;

sealed class Fixture
{
    public readonly string Home = Path.Combine(Path.GetTempPath(), "ccpad-observation-test-" + Guid.NewGuid().ToString("N"));
    public string Cwd => Path.Combine(Home, "project");
    public readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddMinutes(-10);
    public string Root { get; }
    private int _tick;
    public Fixture() { Directory.CreateDirectory(Path.Combine(Home, "sessions")); Root = Agent(); }
    public string PathFor(string id) => Path.Combine(Home, "sessions", "rollout-test-" + id + ".jsonl");
    public string Agent(string? parent = null, string? fork = null, DateTimeOffset? created = null)
    {
        var id = Guid.NewGuid().ToString();
        object source = parent == null ? "cli" : new { subagent = new { thread_spawn = new { parent_thread_id = parent } } };
        File.WriteAllText(PathFor(id), JsonSerializer.Serialize(new
        {
            type = "session_meta", payload = new { id, source, cwd = Cwd, timestamp = created ?? Start,
                parent_thread_id = parent, forked_from_id = fork }
        }) + "\n");
        return id;
    }
    public string Row(string type, object payload) => JsonSerializer.Serialize(new { type, timestamp = Start.AddSeconds(++_tick), payload }) + "\n";
    public void Response(object payload) => File.AppendAllText(PathFor(Root), Row("response_item", payload));
    public void Event(string kind, object rest)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(rest))!;
        fields["type"] = JsonSerializer.SerializeToElement(kind);
        File.AppendAllText(PathFor(Root), Row("event_msg", fields));
    }
    public CodexObservationReader Reader() => new(Home, Root);
    public ObservationSnapshot Read() => Reader().Read();
}
