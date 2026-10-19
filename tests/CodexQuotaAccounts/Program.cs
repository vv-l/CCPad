using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CCPad.CodexQuota;
using CCPad.Settings;

// Only fictional credentials and injected HTTP handlers. No real Codex process or network.
var fixture = new Fixture();
var checks = 0;
async Task Check(string name, Func<Task> action)
{
    await action();
    checks++;
    Console.WriteLine($"PASS {name}");
}
void Require(bool condition) { if (!condition) throw new Exception("Offline quota assertion failed"); }

await Check("names and paths persist without credential material", () =>
{
    var store = fixture.Store("persist");
    Require(store.TryAdd("Brain", fixture.Brain, out var added) == CodexQuotaProfileError.None);
    Require(new CodexQuotaProfileStore(fixture.Data("persist"), fixture.Default).Load().Single() == added);
    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Data("persist"), "quota-accounts.json")));
    Require(doc.RootElement[0].EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "Home", "Id", "Name" }));
    return Task.CompletedTask;
});
await Check("launch profiles resolve the default and added account directories", () =>
{
    var store = fixture.Store("launch-profiles");
    Require(store.TryAdd("Brain", fixture.Brain, out var added) == CodexQuotaProfileError.None);
    var profiles = store.LoadLaunchProfiles("Default");
    Require(profiles.Count == 2 && profiles[0].Id == CodexQuotaProfileStore.DefaultId &&
        profiles[0].Home == fixture.Default && profiles[1] == added);
    Require(store.TryResolveLaunchProfile("", out var defaultProfile) &&
        defaultProfile.Id == CodexQuotaProfileStore.DefaultId && defaultProfile.Home == fixture.Default);
    Require(store.TryResolveLaunchProfile(added!.Id, out var brainProfile) && brainProfile.Home == fixture.Brain);
    Require(!store.TryResolveLaunchProfile("missing-profile", out _));
    return Task.CompletedTask;
});
await Check("Codex session discovery is isolated by CODEX_HOME", () =>
{
    var cwd = fixture.Data("session-cwd");
    Directory.CreateDirectory(cwd);
    var homeA = fixture.Data("codex-home-a");
    var homeB = fixture.Data("codex-home-b");
    var id = Guid.NewGuid().ToString("D");
    var path = Path.Combine(homeA, "sessions", "2026", "10", "03",
        $"rollout-20261003-test-{id}.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        payload = new { cwd, source = "tui", id }
    }) + "\n");
    Require(CliSessions.CodexSessionExists(id, homeA));
    Require(!CliSessions.CodexSessionExists(id, homeB));
    Require(CliSessions.FindLatestCodexSessionId(cwd, null, null, homeA) == id);
    Require(CliSessions.FindLatestCodexSessionId(cwd, null, null, homeB) == null);
    return Task.CompletedTask;
});
await Check("duplicate paths, names, accounts and default account are rejected", () =>
{
    var store = fixture.Store("duplicates");
    Require(store.TryAdd("Default copy", fixture.Default, out _) == CodexQuotaProfileError.Duplicate);
    Require(store.TryAdd("Brain", fixture.Brain, out _) == CodexQuotaProfileError.None);
    Require(store.TryAdd("Other", fixture.Brain + Path.DirectorySeparatorChar, out _) == CodexQuotaProfileError.Duplicate);
    Require(store.TryAdd("Other", fixture.Login("same-account", "brain-account"), out _) == CodexQuotaProfileError.Duplicate);
    Require(store.TryAdd("brain", fixture.Login("other-account", "other-account"), out _) == CodexQuotaProfileError.Duplicate);
    return Task.CompletedTask;
});
await Check("re-login replaces the existing account row and keeps its id", () =>
{
    var store = fixture.Store("replace");
    Require(store.TryAdd("Brain", fixture.Brain, out var original) == CodexQuotaProfileError.None);
    var refreshedHome = fixture.Login("brain-refreshed", "brain-account", "refreshed-fictional-token");
    Require(store.TryAddOrReplace("Brain refreshed", refreshedHome, out var replaced) == CodexQuotaProfileError.None);
    Require(replaced != null && original != null && replaced.Id == original.Id &&
        replaced.Name == "Brain refreshed" && replaced.Home == refreshedHome);
    var saved = store.Load();
    Require(saved.Count == 1 && saved[0] == replaced &&
        File.Exists(Path.Combine(refreshedHome, "auth.json")));
    return Task.CompletedTask;
});
await Check("invalid and API logins cannot fall back to the default", () =>
{
    var store = fixture.Store("invalid");
    Require(store.TryAdd("Missing", fixture.Data("missing"), out _) == CodexQuotaProfileError.InvalidLogin);
    Require(store.TryAdd("Empty", "", out _) == CodexQuotaProfileError.InvalidLogin);
    Require(store.TryAdd(" ", fixture.Brain, out _) == CodexQuotaProfileError.InvalidName);
    var api = fixture.Login("api", "api-account");
    File.WriteAllText(Path.Combine(api, "auth.json"), "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fictional\"}");
    Require(store.TryAdd("API", api, out _) == CodexQuotaProfileError.InvalidLogin);
    return Task.CompletedTask;
});
await Check("new login homes are isolated and validation never exposes credentials", () =>
{
    var data = fixture.Data("new-login");
    var first = CodexLoginService.CreateProfileHome(data);
    var second = CodexLoginService.CreateProfileHome(data);
    Require(first != second && Directory.Exists(first) && Directory.Exists(second));
    Require(CodexQuotaProfileStore.HasReadableChatGptLogin(fixture.Brain));
    Require(!CodexQuotaProfileStore.HasReadableChatGptLogin(fixture.Data("no-auth")));
    var start = CodexLoginService.BuildStartInfo(first);
    Require(start.Environment["CODEX_HOME"] == first &&
        start.Arguments == "/d /c codex login" &&
        !start.Arguments.Contains(first, StringComparison.Ordinal));
    return Task.CompletedTask;
});
await Check("removing a display leaves login files unchanged", () =>
{
    var store = fixture.Store("remove");
    var before = File.ReadAllBytes(Path.Combine(fixture.Brain, "auth.json"));
    Require(store.TryAdd("Brain", fixture.Brain, out var added) == CodexQuotaProfileError.None);
    Require(!store.TryRemove(CodexQuotaProfileStore.DefaultId));
    Require(store.TryRemove(added!.Id) && store.Load().Count == 0);
    Require(File.ReadAllBytes(Path.Combine(fixture.Brain, "auth.json")).SequenceEqual(before));
    return Task.CompletedTask;
});
await Check("multiple store instances do not lose concurrent additions", async () =>
{
    var homes = Enumerable.Range(0, 6).Select(i => fixture.Login($"parallel-{i}", $"parallel-account-{i}")).ToArray();
    var results = await Task.WhenAll(homes.Select((home, i) => Task.Run(() =>
        fixture.Store("parallel").TryAdd($"Account {i}", home, out _))));
    Require(results.All(r => r == CodexQuotaProfileError.None));
    Require(fixture.Store("parallel").Load().Count == homes.Length);
});
await Check("invalid persisted configuration is preserved instead of overwritten", () =>
{
    var data = fixture.Data("corrupt");
    Directory.CreateDirectory(data);
    var file = Path.Combine(data, "quota-accounts.json");
    File.WriteAllText(file, "invalid saved document");
    Require(fixture.Store("corrupt").TryAdd("Brain", fixture.Brain, out _) == CodexQuotaProfileError.Storage);
    Require(File.ReadAllText(file) == "invalid saved document");
    return Task.CompletedTask;
});
await Check("parallel reads retain separate credentials and out-of-order results", async () =>
{
    var releaseDefault = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = new HttpClient(new OfflineHandler(async token =>
    {
        if (token == "default-account-access") await releaseDefault.Task;
        return OfflineHandler.Usage(token == "default-account-access" ? 12 : 78);
    }));
    using var defaultService = new CodexQuotaService(fixture.Default, http);
    using var brainService = new CodexQuotaService(fixture.Brain, http);
    var slow = defaultService.RefreshAsync();
    var fast = await brainService.RefreshAsync();
    Require(!slow.IsCompleted && fast.TightestUsedPercent == 78);
    releaseDefault.SetResult(true);
    Require((await slow).TightestUsedPercent == 12 && fast.TightestUsedPercent == 78);
});
await Check("expired account neither renews credentials nor breaks another account", async () =>
{
    var before = File.ReadAllBytes(Path.Combine(fixture.Brain, "auth.json"));
    using var http = new HttpClient(new OfflineHandler(token => Task.FromResult(
        token == "brain-account-access" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : OfflineHandler.Usage(25))));
    using var brainService = new CodexQuotaService(fixture.Brain, http);
    using var defaultService = new CodexQuotaService(fixture.Default, http);
    Require((await brainService.RefreshAsync()).State == CodexQuotaState.Error);
    Require((await defaultService.RefreshAsync()).State == CodexQuotaState.Ready);
    Require(File.ReadAllBytes(Path.Combine(fixture.Brain, "auth.json")).SequenceEqual(before));
});
await Check("missing additional login causes zero requests instead of fallback", async () =>
{
    var handler = new OfflineHandler(_ => Task.FromResult(OfflineHandler.Usage(10)));
    using var http = new HttpClient(handler);
    using var service = new CodexQuotaService(fixture.Data("no-login"), http);
    Require((await service.RefreshAsync()).State == CodexQuotaState.Unavailable && handler.Requests.Count == 0);
});
await Check("Free plan remains visible when no usage window is reported", async () =>
{
    var handler = new OfflineHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"plan_type\":\"free_workspace\",\"rate_limit\":null}")
    }));
    using var http = new HttpClient(handler);
    using var service = new CodexQuotaService(fixture.Default, http);
    var snapshot = await service.RefreshAsync();
    Require(snapshot.State == CodexQuotaState.ProfileOnly && snapshot.PlanType == "Free" &&
        snapshot.Windows.Length == 0);
});
await Check("Codex Free plan is normalized when usage windows are present", async () =>
{
    var handler = new OfflineHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"plan_type\":\"free\",\"rate_limit\":{\"primary_window\":{\"used_percent\":5,\"limit_window_seconds\":18000}}}")
    }));
    using var http = new HttpClient(handler);
    using var service = new CodexQuotaService(fixture.Default, http);
    var snapshot = await service.RefreshAsync();
    Require(snapshot.State == CodexQuotaState.Ready && snapshot.PlanType == "Free" &&
        snapshot.TightestUsedPercent == 5);
});
await Check("Free plan survives a usage endpoint 403 response", async () =>
{
    var handler = new OfflineHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("{\"plan_type\":\"free_workspace\"}")
    }));
    using var http = new HttpClient(handler);
    using var service = new CodexQuotaService(fixture.Default, http);
    var snapshot = await service.RefreshAsync();
    Require(snapshot.State == CodexQuotaState.ProfileOnly && snapshot.PlanType == "Free");
});
await Check("credential renewal is read from the original directory on each refresh", async () =>
{
    var home = fixture.Login("renew", "renew-account");
    var handler = new OfflineHandler(_ => Task.FromResult(OfflineHandler.Usage(10)));
    using var http = new HttpClient(handler);
    using var service = new CodexQuotaService(home, http);
    await service.RefreshAsync();
    fixture.Login("renew", "renew-account", "renewed-fictional-token");
    await service.RefreshAsync();
    Require(handler.Requests.Select(r => r.Token).SequenceEqual(new[] { "renew-account-access", "renewed-fictional-token" }));
});
await Check("closing a service while a read is active completes safely", async () =>
{
    var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = new HttpClient(new OfflineHandler(_ => gate.Task));
    var service = new CodexQuotaService(fixture.Default, http);
    var read = service.RefreshAsync();
    service.Dispose();
    gate.SetResult(OfflineHandler.Usage(30));
    Require((await read).State == CodexQuotaState.Ready);
    Require((await service.RefreshAsync()).State == CodexQuotaState.Error);
});
await Check("overview refreshes three accounts together with separate results", async () =>
{
    var thirdHome = fixture.Login("overview-third", "third-account");
    using var http = new HttpClient(new OfflineHandler(token => Task.FromResult(OfflineHandler.Usage(
        token == "default-account-access" ? 12 : token == "brain-account-access" ? 78 : 33))));
    var overview = new CodexQuotaOverviewService(home => new CodexQuotaService(home, http));
    var profiles = new[]
    {
        new CodexQuotaProfile("default", "Default", fixture.Default),
        new CodexQuotaProfile("brain", "Brain", fixture.Brain),
        new CodexQuotaProfile("third", "Third", thirdHome),
    };
    var entries = await overview.RefreshAsync(profiles);
    Require(entries.Length == 3 && entries.All(entry => entry.Snapshot.State == CodexQuotaState.Ready));
    Require(entries.Select(entry => entry.Profile.Id).SequenceEqual(new[] { "default", "brain", "third" }));
    Require(entries.Select(entry => entry.Snapshot.TightestUsedPercent).SequenceEqual(new[] { 12d, 78d, 33d }));
});
await Check("overview retains healthy accounts when one profile cannot be read", async () =>
{
    using var http = new HttpClient(new OfflineHandler(_ => Task.FromResult(OfflineHandler.Usage(20))));
    var overview = new CodexQuotaOverviewService(home => home == "broken" ?
        throw new InvalidOperationException() : new CodexQuotaService(home, http));
    var entries = await overview.RefreshAsync(new[]
    {
        new CodexQuotaProfile("default", "Default", fixture.Default),
        new CodexQuotaProfile("broken", "Broken", "broken"),
        new CodexQuotaProfile("brain", "Brain", fixture.Brain),
    });
    Require(entries[0].Snapshot.State == CodexQuotaState.Ready &&
        entries[1].Snapshot.State == CodexQuotaState.Error && entries[2].Snapshot.State == CodexQuotaState.Ready);
});
await Check("quota view defaults to all and survives restart without changing account configuration", () =>
{
    var data = fixture.Data("toolbar");
    var store = fixture.Store("toolbar");
    Require(store.TryAdd("Brain", fixture.Brain, out _) == CodexQuotaProfileError.None);
    var accountFile = Path.Combine(data, "quota-accounts.json");
    var originalAccounts = File.ReadAllBytes(accountFile);
    var originalLogin = File.ReadAllBytes(Path.Combine(fixture.Brain, "auth.json"));
    var settings = new QuotaToolbarSettings(data);
    Require(settings.Load() == QuotaToolbarView.AllAccounts);
    Require(settings.TrySave(QuotaToolbarView.Compact));
    Require(new QuotaToolbarSettings(data).Load() == QuotaToolbarView.Compact);
    Require(settings.TrySave(QuotaToolbarView.AllAccounts));
    Require(new QuotaToolbarSettings(data).Load() == QuotaToolbarView.AllAccounts);
    Require(File.ReadAllBytes(accountFile).SequenceEqual(originalAccounts));
    Require(File.ReadAllBytes(Path.Combine(fixture.Brain, "auth.json")).SequenceEqual(originalLogin));
    return Task.CompletedTask;
});
await Check("invalid quota view recovers safely and invalid writes preserve the saved preference", () =>
{
    var data = fixture.Data("toolbar-invalid");
    Directory.CreateDirectory(data);
    var file = Path.Combine(data, "quota-toolbar-view.txt");
    File.WriteAllText(file, "unrecognized");
    var settings = new QuotaToolbarSettings(data);
    Require(settings.Load() == QuotaToolbarView.AllAccounts);
    Require(!settings.TrySave((QuotaToolbarView)99));
    Require(File.ReadAllText(file) == "unrecognized");
    return Task.CompletedTask;
});
await Check("display identity reads email without exposing credentials or changing login files", () =>
{
    var jwt = Fixture.Jwt(new { email = "default@example.invalid", exp = 100, iat = 50 });
    var home = fixture.Login("identity-email", "display-account", idToken: jwt);
    var original = File.ReadAllBytes(Path.Combine(home, "auth.json"));
    var identity = CodexAccountIdentity.Read(home)!;
    Require(identity.Email == "default@example.invalid" && identity.Key.Length == 64);
    var serialized = JsonSerializer.Serialize(identity);
    Require(!serialized.Contains(jwt) && !serialized.Contains("fictional-refresh") && !serialized.Contains("display-account-access"));
    Require(File.ReadAllBytes(Path.Combine(home, "auth.json")).SequenceEqual(original));
    return Task.CompletedTask;
});
await Check("missing or malformed email never breaks account identity or borrows another login", () =>
{
    foreach (var idToken in new[] { "not-a-jwt", "header.!.signature", Fixture.Jwt(new { email = 42 }),
        Fixture.Jwt(new { email = "bad\n@example.invalid" }), Fixture.Jwt(new { name = "No email" }) })
    {
        var home = fixture.Login("bad-email", "bad-email-account", idToken: idToken);
        var identity = CodexAccountIdentity.Read(home);
        Require(identity != null && identity.Email == null);
    }
    Require(CodexAccountIdentity.Read(fixture.Data("no-identity")) == null);
    var fallback = Fixture.Jwt(new Dictionary<string, object>
    {
        ["https://api.openai.com/profile"] = new { email = "fallback@example.invalid" },
    });
    var accessHome = fixture.Login("access-email", "access-account", access: fallback, idToken: "malformed");
    Require(CodexAccountIdentity.Read(accessHome)?.Email == "fallback@example.invalid");
    return Task.CompletedTask;
});
await Check("manual dates follow the real account through directory moves and default login changes", () =>
{
    var home = fixture.Login("switch-default", "subscription-a", idToken: Fixture.Jwt(new { email = "a@example.invalid" }));
    var accountA = CodexAccountIdentity.Read(home)!;
    var data = fixture.Data("subscription-identity");
    var store = new CodexSubscriptionStore(data);
    var note = new CodexSubscriptionNote(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "primary");
    Require(store.TrySave(accountA.Key, note));
    fixture.Login("switch-default", "subscription-b", idToken: Fixture.Jwt(new { email = "b@example.invalid" }));
    var accountB = CodexAccountIdentity.Read(home)!;
    Require(accountA.Key != accountB.Key && accountB.Email == "b@example.invalid");
    Require(store.TryLoad(accountB.Key, out var noNote) && noNote.IsEmpty);
    var movedHome = fixture.Login("moved-a", "subscription-a", idToken: Fixture.Jwt(new { email = "a@example.invalid" }));
    Require(new CodexSubscriptionStore(data).TryLoad(CodexAccountIdentity.Read(movedHome)!.Key, out var restored) && restored == note);
    return Task.CompletedTask;
});
await Check("manual dates survive restart, permit empty fields and do not modify credentials", () =>
{
    var data = fixture.Data("subscription-persist");
    var identity = CodexAccountIdentity.Read(fixture.Default)!;
    var original = File.ReadAllBytes(Path.Combine(fixture.Default, "auth.json"));
    var store = new CodexSubscriptionStore(data);
    var note = new CodexSubscriptionNote(null, new DateOnly(2026, 11, 15), "backup");
    Require(store.TrySave(identity.Key, note));
    Require(new CodexSubscriptionStore(data).TryLoad(identity.Key, out var loaded) && loaded == note);
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "codex-subscriptions.json")));
    Require(document.RootElement.GetProperty(identity.Key).EnumerateObject().Select(p => p.Name).Order()
        .SequenceEqual(new[] { "ExpiresOn", "Note", "RechargedOn" }));
    Require(File.ReadAllBytes(Path.Combine(fixture.Default, "auth.json")).SequenceEqual(original));
    Require(store.TrySave(identity.Key, new()) && store.TryLoad(identity.Key, out loaded) && loaded.IsEmpty);
    return Task.CompletedTask;
});
await Check("parallel subscription edits preserve other accounts and clearing only affects its account", async () =>
{
    var data = fixture.Data("subscription-parallel");
    var keys = Enumerable.Range(0, 6).Select(i => CodexAccountIdentity.Read(fixture.Login($"note-{i}", $"note-account-{i}"))!.Key).ToArray();
    var results = await Task.WhenAll(keys.Select((key, i) => Task.Run(() => new CodexSubscriptionStore(data)
        .TrySave(key, new CodexSubscriptionNote(new DateOnly(2026, 10, i + 1), null, $"account {i}")))));
    Require(results.All(value => value));
    var store = new CodexSubscriptionStore(data);
    Require(store.TrySave(keys[0], new()));
    for (var i = 1; i < keys.Length; i++)
        Require(store.TryLoad(keys[i], out var note) && note.Note == $"account {i}");
});
await Check("invalid subscription dates and damaged storage cannot overwrite existing notes", () =>
{
    var data = fixture.Data("subscription-corrupt");
    var store = new CodexSubscriptionStore(data);
    var key = CodexAccountIdentity.Read(fixture.Default)!.Key;
    var valid = new CodexSubscriptionNote(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "saved");
    Require(store.TrySave(key, valid));
    Require(!store.TrySave(key, valid with { ExpiresOn = new DateOnly(2026, 9, 1) }));
    Require(!store.TrySave(key, valid with { Note = new string('x', 161) }));
    Require(store.TryLoad(key, out var kept) && kept == valid);
    var file = Path.Combine(data, "codex-subscriptions.json");
    File.WriteAllText(file, "damaged notes");
    Require(!store.TryLoad(key, out _) && !store.TrySave(key, valid));
    Require(File.ReadAllText(file) == "damaged notes");
    return Task.CompletedTask;
});
Console.WriteLine($"{checks} offline checks passed. No live API, Codex CLI or reset transport used.");

sealed class Fixture
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CCPadQuotaAccountsOffline", Guid.NewGuid().ToString("N"));
    public string Default { get; }
    public string Brain { get; }
    public Fixture() { Default = Login("default", "default-account"); Brain = Login("brain", "brain-account"); }
    public string Data(string name) => Path.Combine(_root, "data", name);
    public CodexQuotaProfileStore Store(string name) => new(Data(name), Default);
    public string Login(string name, string id, string? access = null, string? idToken = null)
    {
        var home = Path.Combine(_root, "profiles", name);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt", tokens = new { access_token = access ?? id + "-access", refresh_token = "fictional-refresh", account_id = id, id_token = idToken },
        }));
        return home;
    }
    public static string Jwt(object claims) => "offline." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fictional";
}

sealed class OfflineHandler(Func<string, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    public ConcurrentQueue<(string Token, string Account)> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://chatgpt.com/backend-api/wham/usage")
            throw new Exception("Only quota GET is allowed in the offline handler");
        var token = request.Headers.Authorization?.Parameter ?? "";
        var account = request.Headers.GetValues("chatgpt-account-id").Single();
        if (!token.StartsWith(account, StringComparison.Ordinal) && token != "renewed-fictional-token")
            throw new Exception("Account and token were mixed");
        Requests.Enqueue((token, account));
        return response(token);
    }
    public static HttpResponseMessage Usage(int percent) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            plan_type = "fictional", rate_limit = new { primary_window = new { used_percent = percent, limit_window_seconds = 18000 } },
            rate_limit_reset_credits = new { available_count = 2 },
        })),
    };
}
