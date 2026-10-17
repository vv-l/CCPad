using System.Text.Json;
using CCPad.Settings;

// Synthetic rollouts only; no CLI, login, network, or real conversation changes.
var checks = 0;
void Check(string name, Action test)
{
    test();
    Console.WriteLine("PASS " + name);
    checks++;
}
void Require(bool value) { if (!value) throw new Exception("Session recovery assertion failed"); }

Check("interactive root and legacy root retain their exact IDs", () =>
{
    var f = new Fixture();
    Require(f.Resolve(f.Root) == f.Root);
    var legacy = f.Meta();
    legacy.Remove("source");
    var id = f.Write(legacy);
    Require(f.Resolve(id) == id);
});
Check("legacy payload-only and tui rollouts remain resumable", () =>
{
    var f = new Fixture();
    var tui = f.Write(f.Meta("tui"));
    Require(f.Resolve(tui) == tui);
    File.WriteAllText(f.PathFor(f.Root), JsonSerializer.Serialize(new
    {
        payload = new { id = f.Root, cwd = f.Cwd, source = "cli" }
    }) + "\n");
    Require(f.Resolve(f.Root) == f.Root);
});
Check("explicit non-metadata and malformed record types cannot be resumed", () =>
{
    var f = new Fixture();
    foreach (object kind in new object[] { "event_msg", 123, new { invalid = true } })
    {
        File.WriteAllText(f.PathFor(f.Root), JsonSerializer.Serialize(new
        {
            type = kind, payload = new { id = f.Root, cwd = f.Cwd, source = "cli" }
        }) + "\n");
        Require(f.Resolve(f.Root) == null);
    }
});
Check("a user fork is resumed as itself, not its fork source", () =>
{
    var f = new Fixture();
    var fork = f.Meta();
    fork["forked_from_id"] = f.Root;
    var id = f.Write(fork);
    Require(f.Resolve(id) == id);
});
Check("nested worker source resolves to the actual parent", () =>
{
    var f = new Fixture();
    Require(f.Resolve(f.Child(f.Root)) == f.Root);
});
Check("grandchildren resolve all the way to the interactive root", () =>
{
    var f = new Fixture();
    Require(f.Resolve(f.Child(f.Child(f.Root))) == f.Root);
});
Check("legacy worker and agent-path ancestry resolve without guessing by directory", () =>
{
    var f = new Fixture();
    var worker = f.Meta("subagent");
    worker["forked_from_id"] = f.Root;
    Require(f.Resolve(f.Write(worker)) == f.Root);
    var pathWorker = f.Meta();
    pathWorker["agent_path"] = "/root/worker";
    pathWorker["parent_thread_id"] = f.Root;
    Require(f.Resolve(f.Write(pathWorker)) == f.Root);
});
Check("missing parent never falls back to another conversation in the same project", () =>
{
    var f = new Fixture();
    Require(f.Resolve(f.Child(Guid.NewGuid().ToString())) == null);
    Require(f.Resolve(f.Write(f.Meta("subagent"))) == null);
});
Check("cycles and conflicting parents stop recovery", () =>
{
    var f = new Fixture();
    var first = Guid.NewGuid().ToString();
    var second = f.Child(first);
    f.Child(second, first);
    Require(f.Resolve(first) == null);
    var conflict = f.Meta(new { subagent = new { thread_spawn = new { parent_thread_id = f.Root } } });
    conflict["parent_thread_id"] = second;
    Require(f.Resolve(f.Write(conflict)) == null);
});
Check("exec and unknown source kinds are not interactive resume targets", () =>
{
    var f = new Fixture();
    Require(f.Resolve(f.Write(f.Meta("exec"))) == null);
    Require(f.Resolve(f.Write(f.Meta(new { other = "source" }))) == null);
});
Check("latest lookup skips newer workers and exec tasks", () =>
{
    var f = new Fixture();
    var ids = new[] { f.Child(f.Root), f.Write(f.Meta("subagent")), f.Write(f.Meta("exec")) };
    foreach (var id in ids) File.SetLastWriteTimeUtc(f.PathFor(id), DateTime.UtcNow.AddMinutes(1));
    Require(CliSessions.FindLatestCodexSessionId(f.Cwd, null, codexHome: f.Home) == f.Root);
    Require(CliSessions.FindLatestCodexSessionId(f.Cwd, null, new HashSet<string> { f.Root }, f.Home) == null);
});
Check("filename and metadata identity must agree", () =>
{
    var f = new Fixture();
    var other = Guid.NewGuid().ToString();
    File.Copy(f.PathFor(f.Root), f.PathFor(other));
    Require(f.Resolve(other) == null);
});
Check("inherited metadata cannot override the child's first metadata record", () =>
{
    var f = new Fixture();
    var child = f.Child(f.Root);
    File.AppendAllText(f.PathFor(child), File.ReadAllText(f.PathFor(f.Root)));
    Require(f.Resolve(child) == f.Root);
    File.WriteAllText(f.PathFor(f.Root), "{unfinished metadata\n");
    Require(f.Resolve(child) == null);
});
Check("old saved worker ID uses the parent lease and cannot open the same conversation twice", () =>
{
    var f = new Fixture();
    var child = f.Child(f.Root);
    var saved = JsonSerializer.Serialize(new { SessionId = child });
    var savedId = JsonDocument.Parse(saved).RootElement.GetProperty("SessionId").GetString()!;
    var target = f.Resolve(savedId);
    Require(target == f.Root);
    using (var activeParent = CodexSessionLease.TryAcquire(f.Home, f.Root))
    {
        Require(activeParent != null);
        using var restored = CodexSessionLease.TryAcquire(f.Home, target!);
        Require(restored == null);
    }
    using var reopened = CodexSessionLease.TryAcquire(f.Home, target!);
    Require(reopened != null);
});
Check("ancestry inspection leaves root and worker history unchanged", () =>
{
    var f = new Fixture();
    var child = f.Child(f.Root);
    var rootBefore = File.ReadAllBytes(f.PathFor(f.Root));
    var childBefore = File.ReadAllBytes(f.PathFor(child));
    Require(f.Resolve(child) == f.Root);
    Require(rootBefore.SequenceEqual(File.ReadAllBytes(f.PathFor(f.Root))));
    Require(childBefore.SequenceEqual(File.ReadAllBytes(f.PathFor(child))));
});
Console.WriteLine($"{checks} offline session-recovery checks passed.");

sealed class Fixture
{
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "ccpad-session-recovery-offline", Guid.NewGuid().ToString("N"));
    public string Cwd { get; } = Path.GetFullPath("offline-project");
    public string Root { get; }
    public Fixture() { Directory.CreateDirectory(Path.Combine(Home, "sessions")); Root = Write(Meta()); }
    public string PathFor(string id) => Path.Combine(Home, "sessions", "rollout-offline-" + id + ".jsonl");
    public Dictionary<string, object?> Meta(object? source = null) => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["cwd"] = Cwd, ["source"] = source ?? "cli"
    };
    public string Write(Dictionary<string, object?> meta)
    {
        var id = (string)meta["id"]!;
        File.WriteAllText(PathFor(id), JsonSerializer.Serialize(new { type = "session_meta", payload = meta }) + "\n");
        return id;
    }
    public string Child(string parent, string? id = null)
    {
        var meta = Meta(new { subagent = new { thread_spawn = new { parent_thread_id = parent } } });
        if (id != null) meta["id"] = id;
        return Write(meta);
    }
    public string? Resolve(string id) => CliSessions.ResolveCodexResumeSessionId(id, Home);
}
