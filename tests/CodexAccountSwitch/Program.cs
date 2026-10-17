using System.Diagnostics;
using System.Text.Json;
using CCPad.CodexQuota;
using CCPad.Settings;

// All homes contain fictional logins. No Codex process, network, or real account is used.
if (args.Length == 4 && args[0] == "--resume-probe")
{
    var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
    using var lease = CodexSessionLease.TryAcquire(home, args[2]);
    if (args[3] == "busy")
    {
        if (lease != null) throw new Exception("Another window acquired an active conversation");
    }
    else
    {
        if (lease == null || !CliSessions.CodexSessionExists(args[2], home))
            throw new Exception("Closed conversation was not available in the new window");
        var startup = new CodexAccountSwitchService(args[1], home);
        if (startup.ApplyPending(() => false) != CodexAccountSwitchResult.None ||
            CodexAccountIdentity.Read(home)?.Key != Fixture.KeyFor("B"))
            throw new Exception("New window did not retain the active account");
        using var handler = new ResumeQuotaHandler();
        using var http = new HttpClient(handler);
        using var quota = new CodexQuotaService(home, http);
        if (quota.RefreshAsync().GetAwaiter().GetResult().State != CodexQuotaState.Ready ||
            handler.Calls != 1 || !handler.UsedCurrentLogin)
            throw new Exception("New window quota request did not use the current login");
    }
    return;
}
var checks = 0;
void Check(string name, Action action)
{
    action();
    Console.WriteLine("PASS " + name);
    checks++;
}
void Require(bool condition, string? failure = null)
{
    if (!condition) throw new Exception("Offline account-switch assertion failed: " + failure);
}

Check("selecting and cancelling an account never changes a running login", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    Require(f.Auth().SequenceEqual(before));
    Require(f.Switch.ReadPending()?.ProfileId == f.B.Id);
    Require(f.Switch.Request("default"));
    Require(f.Switch.ReadPending() == null && f.Auth().SequenceEqual(before));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.None);
});

Check("A to B to C to A preserves refreshed logins and the exact original history", () =>
{
    var f = new Fixture();
    var history = File.ReadAllBytes(f.History);
    var database = File.ReadAllBytes(Path.Combine(f.Native, "state_5.sqlite"));
    Fixture.Login(f.Native, "A", "renewed-A");
    var latestA = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(f.Identity() == Fixture.Key(f.B.Home));
    var savedA = f.Store.Load().Single(p => Fixture.Key(p.Home) == Fixture.KeyFor("A"));
    Require(File.ReadAllBytes(Path.Combine(savedA.Home, "auth.json")).SequenceEqual(latestA));
    Fixture.Login(f.Native, "B", "renewed-B");
    var latestB = f.Auth();
    Require(f.Switch.Request(f.C.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(File.ReadAllBytes(Path.Combine(f.B.Home, "auth.json")).SequenceEqual(latestB));
    Require(f.Switch.Request(savedA.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(f.Auth().SequenceEqual(latestA));
    Require(CliSessions.CodexSessionExists(f.SessionId, f.Native));
    Require(File.ReadAllBytes(f.History).SequenceEqual(history));
    Require(File.ReadAllBytes(Path.Combine(f.Native, "state_5.sqlite")).SequenceEqual(database));
    Require(f.Store.Load().Count == 3);
    Require(f.Store.LoadInactive().Count == 2);
});

Check("active writers leave both authentication and pending selection intact", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => true) == CodexAccountSwitchResult.Busy);
    Require(f.Auth().SequenceEqual(before));
    Require(f.Switch.ReadPending()?.ProfileId == f.B.Id && f.Store.Load().Count == 2);
});

Check("a writer appearing during save prevents replacement", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    var calls = 0;
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => ++calls > 1) == CodexAccountSwitchResult.Busy);
    Require(f.Auth().SequenceEqual(before) && f.Switch.ReadPending() != null);
});

Check("a native token refresh during save cannot be overwritten", () =>
{
    var f = new Fixture();
    var calls = 0;
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() =>
    {
        if (++calls == 2) Fixture.Login(f.Native, "A", "external-refresh-A");
        return false;
    }) == CodexAccountSwitchResult.Busy);
    Require(f.AuthText().Contains("external-refresh-A"));
});

Check("a changed target identity is rejected without overwriting the original login", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    Fixture.Login(f.B.Home, "other-account");
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.InvalidLogin);
    Require(f.Auth().SequenceEqual(before));
});

Check("a target refresh during save is not replaced with stale credentials", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    var calls = 0;
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() =>
    {
        if (++calls == 2) Fixture.Login(f.B.Home, "B", "newer-B");
        return false;
    }) == CodexAccountSwitchResult.Busy);
    Require(f.Auth().SequenceEqual(before));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(f.AuthText().Contains("newer-B"));
});

Check("failure to register the original account prevents the switch", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    // A valid target reference still resolves from Load; force registration to
    // fail by locking its metadata file against the atomic replacement.
    using var held = new FileStream(Path.Combine(f.Data, "quota-accounts.json"), FileMode.Open,
        FileAccess.Read, FileShare.Read);
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.StorageError);
    Require(f.Auth().SequenceEqual(before));
    Require(f.Switch.ReadPending() != null);
});

Check("failure to save refreshed credentials leaves the current login usable", () =>
{
    var f = new Fixture();
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Fixture.Login(f.Native, "B", "latest-B");
    var before = f.Auth();
    Require(f.Switch.Request(f.C.Id));
    using var held = new FileStream(Path.Combine(f.B.Home, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.StorageError);
    Require(f.Auth().SequenceEqual(before));
});

Check("restart after an already-applied switch preserves newer native tokens", () =>
{
    var f = new Fixture();
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(f.Switch.Request(f.B.Id));
    Fixture.Login(f.Native, "B", "refreshed-after-switch");
    var before = f.Auth();
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(f.Auth().SequenceEqual(before));
});

Check("an interrupted completion is retryable without losing either account", () =>
{
    var f = new Fixture();
    Require(f.Switch.Request(f.B.Id));
    using (var held = new FileStream(Path.Combine(f.Data, "codex-account-switch.json"), FileMode.Open,
        FileAccess.Read, FileShare.Read))
    {
        Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.StorageError);
        Require(f.Switch.FailureCode?.StartsWith("clear-request:") == true, f.Switch.FailureCode);
        Require(f.Identity() == Fixture.Key(f.B.Home));
        Require(f.Store.Load().Any(p => Fixture.Key(p.Home) == Fixture.KeyFor("A")));
    }
    Fixture.Login(f.Native, "B", "newest-after-interruption");
    var before = f.Auth();
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(f.Auth().SequenceEqual(before) && f.Switch.ReadPending() == null);
});

Check("an account changed outside CCPad is also saved before switching", () =>
{
    var f = new Fixture();
    Fixture.Login(f.Native, "external-D");
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Require(f.Store.Load().Any(p => Fixture.Key(p.Home) == Fixture.KeyFor("external-D")));
});

Check("pending selection is bound to the history home it was created for", () =>
{
    var f = new Fixture();
    var other = Path.Combine(f.Data, "other-home");
    Fixture.Login(other, "other");
    var before = File.ReadAllBytes(Path.Combine(other, "auth.json"));
    Require(f.Switch.Request(f.B.Id));
    var service = new CodexAccountSwitchService(f.Data, other);
    Require(service.ApplyPending(() => false) == CodexAccountSwitchResult.InvalidLogin);
    Require(File.ReadAllBytes(Path.Combine(other, "auth.json")).SequenceEqual(before));
});

Check("keyring and invalid saved logins fail without changing authentication", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    File.WriteAllText(Path.Combine(f.Native, "config.toml"), "cli_auth_credentials_store = \"keyring\"\n");
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.UnsupportedStore);
    File.WriteAllText(Path.Combine(f.B.Home, "auth.json"), "{}");
    Require(!f.Switch.Request(f.B.Id));
    Require(f.Auth().SequenceEqual(before));
});

Check("missing target credentials retain the original login and queued selection", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    File.Move(Path.Combine(f.B.Home, "auth.json"), Path.Combine(f.B.Home, "retained-fixture.json"));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.InvalidLogin);
    Require(f.Auth().SequenceEqual(before) && f.Switch.ReadPending() != null);
});

Check("a saved profile pointing at the active home cannot replace the only copy of an account", () =>
{
    var f = new Fixture();
    var before = f.Auth();
    Require(f.Switch.Request(f.B.Id));
    var profiles = f.Store.Load();
    profiles.Add(new CodexQuotaProfile(Guid.NewGuid().ToString(), "Native alias", f.Native));
    File.WriteAllText(Path.Combine(f.Data, "quota-accounts.json"), JsonSerializer.Serialize(profiles));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.InvalidLogin);
    Require(f.Auth().SequenceEqual(before));
});

Check("metadata contains only references, never login tokens", () =>
{
    var f = new Fixture();
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    foreach (var path in Directory.GetFiles(f.Data, "*.json"))
    {
        var json = File.ReadAllText(path);
        Require(!json.Contains("fictional-access") && !json.Contains("fictional-refresh") && !json.Contains("access_token"));
    }
});

Check("template resume retains original IDs and remote bindings", () =>
{
    var id = Guid.NewGuid().ToString();
    var local = new TabState { CliMode = "codex", SessionId = id, CodexAccountId = "default", WorkingDir = "D:\\project" };
    var remote = new TabState { CliMode = "codex-remote", SessionId = "ccpad-remote", RemoteProfileId = "device", RemoteWorkingDir = "/project" };
    var entry = new WorkspaceEntry { Layout = new LayoutNode { Type = "pane", Tabs = [local, remote] } };
    WorkspaceConfig.MarkAllTabsFrozen(entry, resumeOriginal: true);
    Require(local.Frozen && !local.ForkOnThaw && local.SessionId == id);
    Require(remote.SessionId == "ccpad-remote" && remote.RemoteProfileId == "device" && remote.RemoteWorkingDir == "/project");
});

Check("same conversation cannot be resumed twice until its first pane releases it", () =>
{
    var f = new Fixture();
    var first = CodexSessionLease.TryAcquire(f.Native, f.SessionId);
    Require(first != null);
    Require(CodexSessionLease.TryAcquire(f.Native, f.SessionId) == null);
    using var unrelated = CodexSessionLease.TryAcquire(f.Native, Guid.NewGuid().ToString());
    Require(unrelated != null);
    first!.Dispose();
    using var next = CodexSessionLease.TryAcquire(f.Native, f.SessionId);
    Require(next != null);
});

Check("close and reopen in a different process retains the original thread and latest B login", () =>
{
    var f = new Fixture();
    var history = File.ReadAllBytes(f.History);
    Require(f.Switch.Request(f.B.Id));
    Require(f.Switch.ApplyPending(() => false) == CodexAccountSwitchResult.Applied, f.Switch.FailureCode);
    Fixture.Login(f.Native, "B", "refreshed-native-B");
    var differentDirectory = Path.Combine(f.Data, "different-project");
    Directory.CreateDirectory(differentDirectory);

    void OpenWindow(string expected)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            WorkingDirectory = differentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            start.ArgumentList.Add(typeof(Fixture).Assembly.Location);
        foreach (var arg in new[] { "--resume-probe", f.Data, f.SessionId, expected })
            start.ArgumentList.Add(arg);
        start.Environment["CODEX_HOME"] = f.Native;
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new Exception("Offline resume probe timed out");
        }
        Require(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    using (var firstPane = CodexSessionLease.TryAcquire(f.Native, f.SessionId))
    {
        Require(firstPane != null);
        OpenWindow("busy");
    }
    OpenWindow("available");
    OpenWindow("available");
    Require(File.ReadAllBytes(f.History).SequenceEqual(history));
});

Console.WriteLine($"{checks} offline account-switch checks passed.");

sealed class Fixture
{
    public string Data { get; } = Path.Combine(Path.GetTempPath(), "ccpad-account-switch-offline", Guid.NewGuid().ToString("N"));
    public string Native { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString();
    public string History { get; }
    public CodexQuotaProfileStore Store { get; }
    public CodexAccountSwitchService Switch { get; }
    public CodexQuotaProfile B { get; }
    public CodexQuotaProfile C { get; }

    public Fixture()
    {
        Native = Path.Combine(Data, "native");
        Login(Native, "A");
        var b = Path.Combine(Data, "B");
        var c = Path.Combine(Data, "C");
        Login(b, "B"); Login(c, "C");
        Store = new(Data, Native);
        if (Store.TryAdd("B", b, out var bp) != CodexQuotaProfileError.None ||
            Store.TryAdd("C", c, out var cp) != CodexQuotaProfileError.None) throw new Exception("Fixture failed");
        B = bp!; C = cp!;
        Switch = new(Data, Native);
        var sessions = Path.Combine(Native, "sessions", "2026", "10", "13");
        Directory.CreateDirectory(sessions);
        History = Path.Combine(sessions, "rollout-offline-" + SessionId + ".jsonl");
        File.WriteAllText(History, "{\"type\":\"offline_history\",\"text\":\"original conversation\"}\n");
        File.WriteAllText(Path.Combine(Native, "state_5.sqlite"), "offline database sentinel");
    }
    public byte[] Auth() => File.ReadAllBytes(Path.Combine(Native, "auth.json"));
    public string AuthText() => File.ReadAllText(Path.Combine(Native, "auth.json"));
    public string Identity() => Key(Native);
    public static string Key(string directory) => CodexAccountIdentity.Read(directory)!.Key;
    public static string KeyFor(string account) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account)));
    public static void Login(string directory, string account, string? token = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            tokens = new { account_id = account, access_token = token ?? "fictional-access-" + account, refresh_token = "fictional-refresh-" + account }
        }));
    }
}

sealed class ResumeQuotaHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public bool UsedCurrentLogin { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        UsedCurrentLogin = request.Headers.Authorization?.Parameter == "refreshed-native-B" &&
            request.Headers.GetValues("chatgpt-account-id").Single() == "B";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"plan_type\":\"plus\",\"rate_limit\":{\"primary_window\":{\"used_percent\":12,\"limit_window_seconds\":18000}}}"),
        });
    }
}
