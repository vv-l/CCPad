using CCPad;
using System.Diagnostics;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "ccpad-lifecycle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var exe = Path.Combine(root, "fake-python"); File.WriteAllText(exe, "fixture");
var script = Path.Combine(root, "fake-sidecar"); File.WriteAllText(script, "fixture");
var credentials = Path.Combine(root, "fake-credentials"); File.WriteAllText(credentials, "fixture");
int passed = 0;
var sessionId = Guid.NewGuid().ToString("D");
FeishuStartRequest Request(string name) => new("pane", "cli_ccpad_fixture", "chat", exe, script, credentials, Path.Combine(root, name),
    new("codex", root, sessionId, "official_notify"));
string Arg(ProcessStartInfo info, string name) => info.ArgumentList[info.ArgumentList.IndexOf(name) + 1];
void Ready(ProcessStartInfo info, bool wrong = false)
{
    using var descriptor = JsonDocument.Parse(File.ReadAllText(Arg(info, "--web-descriptor")));
    var data = descriptor.RootElement;
    FeishuOwnedSidecar.WriteAtomic(Arg(info, "--startup-ready"), new
    {
        window_id = data.GetProperty("window_id").GetString(), pane_id = wrong ? "other" : "pane",
        app_id = data.GetProperty("app_id").GetString(), generation = data.GetProperty("generation").GetInt64(),
        ack_scope = "pty_write", engine = data.GetProperty("engine").GetString(),
        cli_session_id = data.GetProperty("cli_session_id").GetString(),
        session_provenance = data.GetProperty("session_provenance").GetString(), public_source = "codex_rollout_v1"
    });
}
void Check(bool value) { if (!value) throw new Exception("fixture assertion failed"); }
async Task Reject(Func<Task> action)
{
    bool rejected = false;
    try { await action(); } catch { rejected = true; }
    Check(rejected);
}
async Task Test(string label, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + label); }

await Test("local binding readiness and close only owned handle", async () =>
{
    int stopped = 0; bool alive = true; ProcessStartInfo? launch = null;
    var owner = new FeishuOwnedSidecar(info => { launch = info; Ready(info); return (() => alive, () => { stopped++; alive = false; }); });
    await owner.StartAsync(Request("ready"), "http://127.0.0.1:9220", "fixture-token", () => true);
    Check(stopped == 0 && launch != null && !launch.UseShellExecute && launch.CreateNoWindow);
    Check(!launch!.Environment.ContainsKey("CCPAD_FEISHU_APP_SECRET"));
    Check(Arg(launch, "--target-pane-id") == "pane" && Arg(launch, "--expected-app-id") == "cli_ccpad_fixture");
    using var identity = JsonDocument.Parse(File.ReadAllText(Arg(launch, "--web-descriptor")));
    Check(identity.RootElement.GetProperty("engine").GetString() == "codex" &&
        identity.RootElement.GetProperty("codex_home").GetString() == root &&
        identity.RootElement.GetProperty("cli_session_id").GetString() == sessionId &&
        identity.RootElement.GetProperty("session_provenance").GetString() == "official_notify");
    File.Copy(Arg(launch, "--web-descriptor"), Path.Combine(AppContext.BaseDirectory, "r5-descriptor-fixture.json"), true);
    File.Copy(Arg(launch, "--startup-ready"), Path.Combine(AppContext.BaseDirectory, "r5-ready-fixture.json"), true);
    owner.Close(); owner.Close(); Check(stopped == 1 && !alive);
});
await Test("second start does not stop current owned process", async () =>
{
    int started = 0, stopped = 0;
    var owner = new FeishuOwnedSidecar(info => { started++; Ready(info); return (() => true, () => stopped++); });
    await owner.StartAsync(Request("first"), "http://127.0.0.1:1", "fixture", () => true);
    await Reject(() => owner.StartAsync(Request("second"), "http://127.0.0.1:1", "fixture", () => true));
    Check(started == 1 && stopped == 0); owner.Close(); Check(stopped == 1);
});
await Test("Reserved App rejected before launch or descriptor", async () =>
{
    int started = 0; var request = Request("reserved") with { AppId = "cli_reserved_fixture" };
    var owner = new FeishuOwnedSidecar(info => { started++; return (() => true, () => { }); },
        reservedAppFingerprint: FeishuSessionBinding.AppFingerprint(request.AppId));
    await Reject(() => owner.StartAsync(request, "http://127.0.0.1:1", "fixture", () => true));
    Check(started == 0 && !Directory.Exists(request.StateDir));
});
await Test("wrong target rejected before launch", async () =>
{
    int started = 0; var owner = new FeishuOwnedSidecar(info => { started++; return (() => true, () => { }); });
    await Reject(() => owner.StartAsync(Request("missing-pane"), "http://127.0.0.1:1", "fixture", () => false));
    Check(started == 0);
});
await Test("ready identity mismatch stops only new process", async () =>
{
    int stopped = 0; var owner = new FeishuOwnedSidecar(info => { Ready(info, true); return (() => true, () => stopped++); });
    await Reject(() => owner.StartAsync(Request("wrong-ready"), "http://127.0.0.1:1", "fixture", () => true));
    owner.Close(); Check(stopped == 1);
});
await Test("early process exit is reclaimed", async () =>
{
    int stopped = 0; var owner = new FeishuOwnedSidecar(info => (() => false, () => stopped++));
    await Reject(() => owner.StartAsync(Request("exited"), "http://127.0.0.1:1", "fixture", () => true));
    Check(stopped == 1);
});
await Test("readiness timeout is reclaimed", async () =>
{
    int stopped = 0; var owner = new FeishuOwnedSidecar(info => (() => true, () => stopped++), TimeSpan.FromMilliseconds(1));
    await Reject(() => owner.StartAsync(Request("timeout"), "http://127.0.0.1:1", "fixture", () => true));
    Check(stopped == 1);
});
await Test("close during pending start leaves no orphan", async () =>
{
    int stopped = 0; bool alive = true;
    var owner = new FeishuOwnedSidecar(info => (() => alive, () => { alive = false; stopped++; }));
    var task = owner.StartAsync(Request("closing"), "http://127.0.0.1:1", "fixture", () => true);
    owner.Close(); await Reject(() => task); Check(!alive && stopped == 1);
});
await Test("close before start cannot launch later", async () =>
{
    int started = 0; var owner = new FeishuOwnedSidecar(info => { started++; return (() => true, () => { }); });
    owner.Close(); await Reject(() => owner.StartAsync(Request("already-closed"), "http://127.0.0.1:1", "fixture", () => true));
    Check(started == 0);
});
await Test("late ready after close cannot leave owned process", async () =>
{
    int stopped = 0; ProcessStartInfo? launch = null;
    var owner = new FeishuOwnedSidecar(info => { launch = info; return (() => true, () => stopped++); });
    var task = owner.StartAsync(Request("late-ready"), "http://127.0.0.1:1", "fixture", () => true);
    Ready(launch!);
    var lateReady = File.ReadAllText(Arg(launch!, "--startup-ready"));
    owner.Close(); File.WriteAllText(Arg(launch!, "--startup-ready"), lateReady);
    await Reject(() => task); Check(stopped == 1);
});
await Test("pane replacement during startup reclaims only owned process", async () =>
{
    int stopped = 0; bool valid = true;
    var owner = new FeishuOwnedSidecar(info => { valid = false; return (() => true, () => stopped++); });
    await Reject(() => owner.StartAsync(Request("pane-replaced"), "http://127.0.0.1:1", "fixture", () => valid));
    owner.Close(); Check(stopped == 1);
});
await Test("process creation failure permits reviewed retry", async () =>
{
    int started = 0, stopped = 0;
    var owner = new FeishuOwnedSidecar(info => { if (++started == 1) throw new InvalidOperationException("fixture"); Ready(info); return (() => true, () => stopped++); });
    await Reject(() => owner.StartAsync(Request("failed"), "http://127.0.0.1:1", "fixture", () => true));
    await owner.StartAsync(Request("retry"), "http://127.0.0.1:1", "fixture", () => true);
    owner.Close(); Check(started == 2 && stopped == 1);
});
await Test("stop failure does not interrupt close and log is sanitized", async () =>
{
    var text = new StringWriter(); var listener = new TextWriterTraceListener(text); Trace.Listeners.Add(listener);
    try
    {
        int stopped = 0;
        var owner = new FeishuOwnedSidecar(info => { Ready(info); return (() => true, () => { stopped++; throw new Exception("fixture-secret-not-for-logs"); }); });
        await owner.StartAsync(Request("stop-failed"), "http://127.0.0.1:1", "fixture", () => true);
        owner.Close(); owner.Close(); Trace.Flush();
        Check(stopped == 1 && text.ToString().Contains("FeishuOwnedStop:Exception") && !text.ToString().Contains("fixture-secret"));
    }
    finally { Trace.Listeners.Remove(listener); listener.Dispose(); }
});
await Test("only loopback and atomic descriptor accepted", async () =>
{
    int started = 0; var owner = new FeishuOwnedSidecar(info => { started++; return (() => true, () => { }); });
    await Reject(() => owner.StartAsync(Request("lan"), "http://192.0.2.10:80", "fixture", () => true));
    Check(started == 0);
    var path = Path.Combine(root, "atomic.json"); FeishuOwnedSidecar.WriteAtomic(path, new { generation = 1 });
    FeishuOwnedSidecar.WriteAtomic(path, new { generation = 2 });
    Check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("generation").GetInt32() == 2);
});
await Test("unconfirmed engine/home/UUID/provenance never starts", async () =>
{
    foreach (var binding in new FeishuSessionBinding?[] { null, new("codex-remote", root, sessionId, "official_notify"),
        new("codex", "relative", sessionId, "official_notify"), new("codex", root, "not-uuid", "official_notify"),
        new("codex", root, sessionId, "latest-file") })
    {
        int started = 0;
        var owner = new FeishuOwnedSidecar(info => { started++; return (() => true, () => { }); });
        await Reject(() => owner.StartAsync(Request(Guid.NewGuid().ToString()) with { Binding = binding },
            "http://127.0.0.1:1", "fixture", () => true));
        Check(started == 0);
    }
});
await Test("home/session/engine/provenance drift invalidates exact binding", () =>
{
    var binding = Request("binding").Binding!;
    Check(binding.Matches("codex", root, sessionId, "official_notify"));
    Check(!binding.Matches("codex", root + "-other", sessionId, "official_notify"));
    Check(!binding.Matches("codex", root, Guid.NewGuid().ToString(), "official_notify"));
    Check(!binding.Matches("codex-remote", root, sessionId, "official_notify"));
    Check(!binding.Matches("codex", root, sessionId, "latest-file"));
    return Task.CompletedTask;
});
await Test("fresh and same-UUID relaunch cannot inherit prior notify proof", () =>
{
    Check(FeishuSessionBinding.ProvenanceAfterLaunch(null, null) == "");
    Check(FeishuSessionBinding.ProvenanceAfterLaunch(sessionId, null) == "");
    Check(FeishuSessionBinding.ProvenanceAfterLaunch(sessionId, Guid.NewGuid().ToString()) == "");
    Check(FeishuSessionBinding.ProvenanceAfterLaunch(sessionId, sessionId) == "explicit_resume");
    return Task.CompletedTask;
});
await Test("explicit resume startup uses exact provenance", async () =>
{
    var owner = new FeishuOwnedSidecar(info => { Ready(info); return (() => true, () => { }); });
    var request = Request("explicit-resume") with { Binding = new("codex", root, sessionId, "explicit_resume") };
    await owner.StartAsync(request, "http://127.0.0.1:1", "fixture", () => true);
    owner.Close();
});
await Test("post-ready target drift stops only owned child", async () =>
{
    bool valid = true; int stopped = 0;
    var owner = new FeishuOwnedSidecar(info => { Ready(info); return (() => true, () => Interlocked.Increment(ref stopped)); });
    await owner.StartAsync(Request("later-drift"), "http://127.0.0.1:1", "fixture", () => valid);
    Check(owner.IsActive);
    valid = false;
    for (int i = 0; i < 40 && stopped == 0; i++) await Task.Delay(25);
    Check(stopped == 1 && !owner.IsActive); owner.Close(); Check(stopped == 1);
});
await Test("legacy readiness cannot enable tail-only bridge", async () =>
{
    int stopped = 0;
    var owner = new FeishuOwnedSidecar(info =>
    {
        using var descriptor = JsonDocument.Parse(File.ReadAllText(Arg(info, "--web-descriptor")));
        var d = descriptor.RootElement;
        FeishuOwnedSidecar.WriteAtomic(Arg(info, "--startup-ready"), new { window_id = d.GetProperty("window_id").GetString(),
            pane_id = "pane", app_id = "cli_ccpad_fixture", generation = d.GetProperty("generation").GetInt64(), ack_scope = "pty_write" });
        return (() => true, () => stopped++);
    });
    await Reject(() => owner.StartAsync(Request("legacy-ready"), "http://127.0.0.1:1", "fixture", () => true));
    Check(stopped == 1);
});
await Test("descriptor lease excludes another window", async () =>
{
    var state = Path.Combine(root, "lease");
    using var owner = new FeishuBindingDescriptor(state, "window-a", 1, state);
    await Reject(() => { using var other = new FeishuBindingDescriptor(state, "window-b", 1, state); return Task.CompletedTask; });
});
await Test("descriptor invalidation removes token and permits reconnect", () =>
{
    var state = Path.Combine(root, "reconnect");
    var request = new FeishuBridgeRequest("cli_ccpad_fixture", "chat", state);
    var process = new FeishuProcessIdentity(42, "2026-10-13T00:00:00.0000000Z", exe, new string('a', 64));
    using (var owner = new FeishuBindingDescriptor(state, "window-a", 1, state))
    {
        owner.Publish(request, "pane", Request("unused").Binding!, "http://127.0.0.1:9220", "fixture-private", process);
        using var published = JsonDocument.Parse(File.ReadAllText(owner.DescriptorPath));
        Check(published.RootElement.GetProperty("pid").GetInt32() == 42 &&
            published.RootElement.GetProperty("cli_session_id").GetString() == sessionId &&
            published.RootElement.GetProperty("codex_home").GetString() == root);
        owner.Invalidate("session_identity_changed");
        using var invalid = JsonDocument.Parse(File.ReadAllText(owner.DescriptorPath));
        Check(!invalid.RootElement.GetProperty("valid").GetBoolean() && !invalid.RootElement.TryGetProperty("token", out _));
    }
    using var reconnected = new FeishuBindingDescriptor(state, "window-b", 2, state);
    reconnected.Publish(request, "pane-b", Request("unused").Binding!, "http://127.0.0.1:9221", "fixture-new", process);
    using var data = JsonDocument.Parse(File.ReadAllText(reconnected.DescriptorPath));
    Check(data.RootElement.GetProperty("window_id").GetString() == "window-b" && data.RootElement.GetProperty("generation").GetInt64() == 2);
    return Task.CompletedTask;
});
await Test("descriptor refuses stale cross-window valid marker and preserves foreign generation", async () =>
{
    var state = Path.Combine(root, "foreign"); Directory.CreateDirectory(state);
    var path = Path.Combine(state, "ccpad-owned-binding.json");
    FeishuOwnedSidecar.WriteAtomic(path, new { valid = true, window_id = "foreign", generation = 3 });
    await Reject(() => { using var owner = new FeishuBindingDescriptor(state, "ours", 1, state); return Task.CompletedTask; });
    FeishuOwnedSidecar.WriteAtomic(path, new { valid = false, window_id = "foreign", generation = 3 });
    using var ours = new FeishuBindingDescriptor(state, "ours", 1, state);
    FeishuOwnedSidecar.WriteAtomic(path, new { valid = true, window_id = "ours", generation = 2 });
    ours.Invalidate("drift");
    using var after = JsonDocument.Parse(File.ReadAllText(path)); Check(after.RootElement.GetProperty("valid").GetBoolean());
});
await Test("controlled state rejects other root UNC and reparse before token write", async () =>
{
    var state = Path.Combine(root, "controlled");
    await Reject(() => { using var owner = new FeishuBindingDescriptor(state + "-other", "a", 1, state); return Task.CompletedTask; });
    await Reject(() => { using var owner = new FeishuBindingDescriptor(@"\\server\share\state", "a", 1, state); return Task.CompletedTask; });
    var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
    Directory.CreateSymbolicLink(state, target);
    await Reject(() => { using var owner = new FeishuBindingDescriptor(state, "a", 1, state); return Task.CompletedTask; });
    Check(!File.Exists(Path.Combine(target, "ccpad-owned-binding.json")));
});
await Test("desktop handoff revokes token and reconnect owns a different queue", async () =>
{
    int stopped = 0;
    var bot = new FeishuDemoBot("Demo", "cli_ccpad_fixture", "chat", credentials);
    var catalog = new FeishuDemoCatalog(exe, script, new[] { bot });
    var first = catalog.Request(bot, "pane", root) with { Binding = Request("binding").Binding };
    ProcessStartInfo? launch = null;
    var owner = new FeishuOwnedSidecar(info => { launch = info; Ready(info); return (() => true, () => stopped++); });
    await owner.StartAsync(first, "http://127.0.0.1:1", "private-test-token", () => true);
    owner.Close();
    using var revoked = JsonDocument.Parse(File.ReadAllText(Arg(launch!, "--web-descriptor")));
    Check(stopped == 1 && !revoked.RootElement.GetProperty("valid").GetBoolean());
    Check(!revoked.RootElement.TryGetProperty("token", out _));
    var second = catalog.Request(bot, "pane", root) with { Binding = first.Binding };
    Check(second.StateDir != first.StateDir && second.Binding == first.Binding);
    var reconnected = new FeishuOwnedSidecar(info => { Ready(info); return (() => true, () => stopped++); });
    await reconnected.StartAsync(second, "http://127.0.0.1:2", "new-test-token", () => true);
    reconnected.Close(); Check(stopped == 2);
});
await Test("unconfigured or unlisted Bot cannot create a connection", async () =>
{
    var bot = new FeishuDemoBot("Demo", "cli_ccpad_fixture", "", credentials);
    var catalog = new FeishuDemoCatalog(exe, script, new[] { bot });
    await Reject(() => { catalog.Request(bot, "pane", root); return Task.CompletedTask; });
    await Reject(() => { catalog.Request(bot with { ChatId = "other" }, "pane", root); return Task.CompletedTask; });
});
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, tests = passed, real_processes = 0, network = 0, model_calls = 0 }));
