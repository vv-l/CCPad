using System.Diagnostics;
using CCPad.Settings;

// Fictional session metadata only. No terminal, SSH, model call or real profile.
if (args.Length == 3 && args[0] == "--receive")
{
    var result = await TabTransfer.ReceiveAsync(args[1], payload =>
    {
        var s = payload.State;
        Require(s.Name == "单标签窗口" && s.SessionId == "ccpad-offline-123" &&
            s.RemoteProfileId == "device-167" && s.RemoteWorkingDir == "/offline/project" &&
            s.Tag == "标签" && s.CustomWidth == 240 && !s.Frozen && s.CliMode == "codex-remote");
        File.WriteAllText(args[2], "destination-staged");
        return Task.FromResult(true);
    });
    Require(result == TabTransferResult.Committed);
    Console.WriteLine("committed");
    return;
}

Environment.SetEnvironmentVariable("CCPAD_DATA_DIR",
    Path.Combine(Path.GetTempPath(), "ccpad-transfer-recovery-" + Guid.NewGuid().ToString("N")));
var checks = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    checks++;
    Console.WriteLine("PASS " + name);
}

await Check("single-tab source moves to another process only after the destination stages it", async () =>
{
    // The old drag path depends on process-local static references; the new
    // process receives only a token, exercising the actual replacement transport.
    var stagedFile = Path.Combine(Path.GetTempPath(), "ccpad-tab-transfer-" + Guid.NewGuid().ToString("N"));
    int sourceTabs = 1;
    using var offer = new TabTransfer(Payload(), _ =>
    {
        Require(File.ReadAllText(stagedFile) == "destination-staged");
        sourceTabs--;
        return Task.FromResult(true);
    });
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        CreateNoWindow = true,
    };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        start.ArgumentList.Add(typeof(TabState).Assembly.Location);
    foreach (var arg in new[] { "--receive", offer.Token, stagedFile }) start.ArgumentList.Add(arg);
    using var child = Process.Start(start)!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    try { await child.WaitForExitAsync(timeout.Token); }
    catch { child.Kill(entireProcessTree: true); throw; }
    Require(child.ExitCode == 0, child.StandardError.ReadToEnd());
    await offer.Completion;
    Require(sourceTabs == 0 && child.StandardOutput.ReadToEnd().Trim() == "committed");
});

await Check("cancelled drag never removes its source", async () =>
{
    int removed = 0;
    var offer = new TabTransfer(Payload(), _ => { removed++; return Task.FromResult(true); });
    offer.Dispose();
    await offer.Completion;
    Require(removed == 0);
});

await Check("destination rejection leaves the source untouched", async () =>
{
    int removed = 0;
    using var offer = new TabTransfer(Payload(), _ => { removed++; return Task.FromResult(true); });
    var result = await TabTransfer.ReceiveAsync(offer.Token, _ => Task.FromResult(false));
    await offer.Completion;
    Require(result == TabTransferResult.Failed && removed == 0);
});

await Check("source disappearing before commit rolls back the destination", async () =>
{
    using var offer = new TabTransfer(Payload(), _ => Task.FromResult(false));
    bool staged = false;
    var result = await TabTransfer.ReceiveAsync(offer.Token, _ => { staged = true; return Task.FromResult(true); });
    await offer.Completion;
    Require(staged && result == TabTransferResult.Failed);
});

await Check("lost acknowledgement after source removal keeps destination recovery", async () =>
{
    TabTransfer? offer = null;
    bool removed = false;
    offer = new TabTransfer(Payload(), _ =>
    {
        removed = true;
        offer!.Dispose();
        return Task.FromResult(true);
    });
    using (offer)
    {
        var result = await TabTransfer.ReceiveAsync(offer.Token, _ => Task.FromResult(true));
        await offer.Completion;
        Require(removed && result == TabTransferResult.Uncertain);
    }
});

await Check("frozen local conversation retains identity, home and fork hint", async () =>
{
    var id = Guid.NewGuid().ToString();
    var state = new TabState { CliMode = "codex", Frozen = true, SessionId = id, WorkingDir = "D:\\offline" };
    using var offer = new TabTransfer(new(state, "D:\\offline-home", true), _ => Task.FromResult(true));
    var result = await TabTransfer.ReceiveAsync(offer.Token, p => Task.FromResult(
        p.State.SessionId == id && p.State.Frozen && p.State.WorkingDir == "D:\\offline" &&
        p.CodexHome == "D:\\offline-home" && p.ForkOnThaw));
    await offer.Completion;
    Require(result == TabTransferResult.Committed);
});

await Check("oversized payload cannot remove the source", async () =>
{
    int removed = 0;
    var payload = Payload();
    payload.State.Tag = new string('x', 1048577);
    using var offer = new TabTransfer(payload, _ => { removed++; return Task.FromResult(true); });
    var result = await TabTransfer.ReceiveAsync(offer.Token, _ => throw new Exception("oversized payload accepted"));
    await offer.Completion;
    Require(result == TabTransferResult.Failed && removed == 0);
});

await Check("foreign pipe names are rejected before connecting", async () =>
{
    foreach (var token in new[] { "other-app", "CCPad.TabTransfer.../bad", "", "CCPad.TabTransfer." })
        Require(await TabTransfer.ReceiveAsync(token, _ => throw new Exception("unexpected prepare")) == TabTransferResult.Failed);
});

await Check("recovery write failure prevents source removal", async () =>
{
    var snapshot = new WorkspaceEntry { Layout = new LayoutNode { Tabs = [Payload().State] } };
    Require(SessionRecovery.SaveSnapshot(snapshot));
    var path = Path.Combine(AppPaths.Root, "sessions", $"snapshot-{Environment.ProcessId}.json");
    var before = File.ReadAllBytes(path);
    using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    int removed = 0;
    using var offer = new TabTransfer(Payload(), _ => { removed++; return Task.FromResult(true); });
    var result = await TabTransfer.ReceiveAsync(offer.Token, _ => Task.FromResult(SessionRecovery.SaveSnapshot(snapshot)));
    await offer.Completion;
    Require(result == TabTransferResult.Failed && removed == 0 && File.ReadAllBytes(path).SequenceEqual(before));
});

await Check("cancelled handoff cannot remove a source waiting for its UI callback", async () =>
{
    var queued = new TaskCompletionSource();
    var resume = new TaskCompletionSource();
    bool removed = false;
    using var offer = new TabTransfer(Payload(), async cancellation =>
    {
        queued.SetResult();
        await resume.Task;
        if (cancellation.IsCancellationRequested) return false;
        removed = true;
        return true;
    });
    var receive = TabTransfer.ReceiveAsync(offer.Token, _ => Task.FromResult(true));
    await queued.Task;
    offer.Dispose();
    resume.SetResult();
    Require(await receive == TabTransferResult.Uncertain);
    await offer.Completion;
    Require(!removed);
});

await Check("per-tab staging survives workspace save, frozen restore and IPC", async () =>
{
    var state = new StagingState
    {
        Enabled = true, Queue = ["待发一", "待发二\n多行"], Draft = "草稿", BulkOpen = true,
        BulkDraft = "批量草稿", EditingIndex = 1, EditingDraft = "尚未确认的编辑", Paused = true,
        PendingCommand = "发送未确认"
    };
    var first = Payload().State;
    first.Staging = state;
    var other = new TabState { SessionId = "other", Staging = new StagingState { Enabled = false, Draft = "另一个栏位" } };
    var entry = new WorkspaceEntry { Layout = new LayoutNode { Tabs = [first, other] } };
    var path = Path.Combine(AppPaths.Root, "staging.ccpad-workspace");
    Require(WorkspaceConfig.SaveToFile(path, entry));
    var loaded = WorkspaceConfig.LoadFromFile(path)!;
    WorkspaceConfig.MarkAllTabsFrozen(loaded, resumeOriginal: true);
    var tab = loaded.Layout!.Tabs![0];
    Require(tab.Frozen && !tab.ForkOnThaw && tab.Staging!.Queue.SequenceEqual(state.Queue));
    Require(loaded.Layout.Tabs[1].Staging!.Draft == "另一个栏位" && !loaded.Layout.Tabs[1].Staging!.Enabled);
    using var offer = new TabTransfer(new(tab, "", false), _ => Task.FromResult(true));
    var result = await TabTransfer.ReceiveAsync(offer.Token, p =>
    {
        var s = p.State.Staging!;
        return Task.FromResult(s.Enabled && s.Queue.SequenceEqual(state.Queue) && s.Draft == state.Draft &&
            s.BulkOpen && s.BulkDraft == state.BulkDraft && s.EditingIndex == state.EditingIndex &&
            s.EditingDraft == state.EditingDraft && s.Paused && s.PendingCommand == state.PendingCommand);
    });
    await offer.Completion;
    Require(result == TabTransferResult.Committed);
});

await Check("handoff captures the latest queue rather than the drag-start queue", async () =>
{
    var payload = Payload();
    payload.State.Staging = new StagingState { Queue = ["already sent"] };
    bool captured = false;
    using var offer = new TabTransfer(payload, _ => Task.FromResult(true), () =>
    {
        captured = true;
        var latest = Payload();
        latest.State.Staging = new StagingState { Queue = ["still pending"], Enabled = true };
        return Task.FromResult(latest);
    });
    Require(!captured);
    var result = await TabTransfer.ReceiveAsync(offer.Token,
        p => Task.FromResult(p.State.Staging!.Queue.Single() == "still pending"));
    await offer.Completion;
    Require(captured && result == TabTransferResult.Committed);
});

Console.WriteLine($"{checks} offline tab-transfer checks passed.");

static void Require(bool condition, string? message = null)
{
    if (!condition) throw new Exception("Tab-transfer assertion failed: " + message);
}

static TabTransferPayload Payload() => new(new TabState
{
    Name = "单标签窗口", CliMode = "codex-remote", SessionId = "ccpad-offline-123",
    RemoteProfileId = "device-167", RemoteWorkingDir = "/offline/project", Tag = "标签", CustomWidth = 240,
}, "", false);
