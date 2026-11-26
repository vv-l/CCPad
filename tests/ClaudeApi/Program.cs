using System.Net;
using System.Text.Json;
using CCPad.CodexQuota;
using CCPad.Settings;

if (args.Contains("--read-local-usage"))
{
    // Explicit read-only integration check; default test runs never read real credentials.
    using var service = new ClaudeQuotaService();
    var snapshot = await service.RefreshAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { state = snapshot.State.ToString(), snapshot.Error,
        balance = snapshot.Api?.Balance, unit = snapshot.Api?.Unit,
        todayCost = snapshot.Api?.Today.Cost, totalCost = snapshot.Api?.Total.Cost,
        todayTokens = snapshot.Api?.Today.Tokens, totalTokens = snapshot.Api?.Total.Tokens }));
    return snapshot.State == ClaudeQuotaState.Ready && snapshot.Api?.Balance != null ? 0 : 1;
}

var root = Path.Combine(Path.GetTempPath(), "ccpad-api-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var store = new ClaudeApiProfileStore(Path.Combine(root, "profiles"), root);
var count = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }

var a = store.Add("Window A", "http://127.0.0.1:9001", "fixture-A", "bearer");
var b = store.Add("Window B", "http://127.0.0.1:9002/v1/", "fixture-B", "api-key");
var aConnection = store.Resolve(a.Id)!;
var bConnection = store.Resolve(b.Id)!;
Check(aConnection.Key == "fixture-A" && bConnection.Key == "fixture-B", "DPAPI round trip for independent keys");
Check(!string.Join("", Directory.GetFiles(Path.Combine(root, "profiles")).Select(File.ReadAllText)).Contains("fixture-"), "stored keys are encrypted");
Check(!aConnection.ToString()!.Contains("fixture-A"), "connection formatting never reveals key");
var aEnv = aConnection.CreateEnvironment(); var bEnv = bConnection.CreateEnvironment();
// Only synthetic keys: consumed by the real-CLI loopback regression below.
File.WriteAllText(Path.Combine(root, "launch-environments.json"), JsonSerializer.Serialize(new[] { aEnv, bEnv }));
Check(aEnv["ANTHROPIC_AUTH_TOKEN"] == "fixture-A" && aEnv["ANTHROPIC_API_KEY"] == "" &&
    bEnv["ANTHROPIC_API_KEY"] == "fixture-B" && bEnv["ANTHROPIC_AUTH_TOKEN"] == "", "launch environments never mix auth types");
Check(aEnv["CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"] == "1" && aEnv["CLAUDE_CODE_OAUTH_TOKEN"] == "" &&
    aEnv["ANTHROPIC_CUSTOM_HEADERS"] == "", "global settings and stale authentication cannot override chosen key");
Check(new ClaudeApiProfileStore(Path.Combine(root, "profiles"), root).Load().Count == 2, "second window reads both profiles without shared selection");
try { store.Resolve("missing"); throw new Exception("unexpected fallback"); }
catch (InvalidOperationException ex) { Check(ex.Message == "claude_api_missing", "missing profile fails closed"); }
foreach (var url in new[] { "http://user:password@host", "http://host?key=secret", "file:///tmp/test", "http://host/#key" })
{
    try { ClaudeApiProfileStore.NormalizeBaseUrl(url); throw new Exception("accepted " + url); }
    catch (ArgumentException) { count++; }
}
Console.WriteLine("PASS credential-bearing and non-HTTP URLs rejected");
var workspaceA = new WorkspaceEntry { ClaudeApiProfileId = a.Id };
var workspaceB = new WorkspaceEntry { ClaudeApiProfileId = b.Id };
WorkspaceConfig.SaveToFile(Path.Combine(root, "a.json"), workspaceA);
WorkspaceConfig.SaveToFile(Path.Combine(root, "b.json"), workspaceB);
Check(WorkspaceConfig.LoadFromFile(Path.Combine(root, "a.json"))!.ClaudeApiProfileId == a.Id &&
    WorkspaceConfig.LoadFromFile(Path.Combine(root, "b.json"))!.ClaudeApiProfileId == b.Id, "independent workspace restore keeps selected profile ID");
Check(!File.ReadAllText(Path.Combine(root, "a.json")).Contains("fixture-A"), "workspace contains no key");
var threadA = Guid.NewGuid().ToString();
var threadB = Guid.NewGuid().ToString();
var mixed = new WorkspaceEntry { ClaudeApiProfileId = b.Id, Layout = new LayoutNode { Type = "split",
    First = new LayoutNode { Tabs = [new TabState { CliMode = "claude", SessionId = threadA,
        ClaudeApiProfileId = aConnection.ProfileId, UseWindowClaudeProfileOnThaw = true }] },
    Second = new LayoutNode { Tabs = [new TabState { CliMode = "claude", SessionId = threadB, ClaudeApiProfileId = bConnection.ProfileId }] }
} };
foreach (var template in new[] { false, true })
{
    if (template) WorkspaceConfig.MarkAllTabsFrozen(mixed);
    var path = Path.Combine(root, template ? "mixed.ccpad-template" : "snapshot.json");
    Check(WorkspaceConfig.SaveToFile(path, mixed), "mixed snapshot saved");
    var saved = WorkspaceConfig.LoadFromFile(path)!;
    var left = saved.Layout!.First!.Tabs!.Single();
    var right = saved.Layout.Second!.Tabs!.Single();
    Check(left.SessionId == threadA && store.Resolve(left.ClaudeApiProfileId ?? saved.ClaudeApiProfileId)!.Key == "fixture-A" &&
        right.SessionId == threadB && store.Resolve(right.ClaudeApiProfileId ?? saved.ClaudeApiProfileId)!.Key == "fixture-B" &&
        left.Frozen == template && right.Frozen == template && !left.UseWindowClaudeProfileOnThaw && !File.ReadAllText(path).Contains("fixture-"),
        template ? "template retains both thread/key pairs through freeze and reload" : "last window restores each thread with its own key");
}
var legacyTab = JsonSerializer.Deserialize<TabState>("""{"CliMode":"claude"}""")!;
Check(legacyTab.ClaudeApiProfileId == null && store.Resolve(legacyTab.ClaudeApiProfileId ?? mixed.ClaudeApiProfileId)!.ProfileId == b.Id,
    "legacy tab inherits its workspace account");
Check(ClaudeApiUsage.UsageUri("https://example.test/anthropic").AbsolutePath == "/anthropic/v1/usage" &&
    ClaudeApiUsage.UsageUri(bConnection.BaseUrl).AbsolutePath == "/v1/usage", "base URL paths and v1 suffix respected");

const string data = """
{"balance":1234.5678,"unit":"USD","isValid":true,"usage":{"today":{"cost":2,"actual_cost":3,"total_tokens":12345,"requests":5},"total":{"cost":20,"actual_cost":30,"total_tokens":5000000000,"requests":50}}}
""";
using var handler = new FixtureHandler(data);
using var client = new HttpClient(handler);
var outcomes = await Task.WhenAll(ClaudeApiUsage.ReadAsync(client, aConnection, default), ClaudeApiUsage.ReadAsync(client, bConnection, default));
Check(outcomes.All(s => s.State == ClaudeQuotaState.Ready && s.Api!.Balance == 1234.5678m), "two concurrent window usage requests succeed");
Check(handler.Seen.Contains("9001:Bearer fixture-A:") && handler.Seen.Contains("9002::fixture-B"), "each usage request uses only its window credential");
Check(outcomes[0].Api!.Today.Cost == 3 && outcomes[0].Api!.Total.Cost == 30, "actual billed cost takes precedence over list price");
Check(outcomes[0].Api!.Total.Tokens == 5000000000, "large token totals retain precision");
using (var doc = JsonDocument.Parse("""{"mode":"quota_limited","unit":"USD","remaining":8,"quota":{"limit":10,"used":2,"remaining":8}}"""))
{
    var parsed = ClaudeApiUsage.Parse(doc.RootElement, aConnection);
    Check(parsed.Balance == null && parsed.KeyLimit == 10 && parsed.KeyUsed == 2 && parsed.DisplayBalance == 8, "key allowance is distinct from account balance");
}
handler.Status = HttpStatusCode.Unauthorized;
handler.Body = "fixture-A must never appear in errors";
var denied = await ClaudeApiUsage.ReadAsync(client, aConnection, default);
Check(denied.Error == "claude_api_unauthorized" && !denied.ToString().Contains("fixture-A"), "HTTP errors are sanitized");
handler.Status = HttpStatusCode.OK; handler.Body = "<html>gateway login</html>";
Check((await ClaudeApiUsage.ReadAsync(client, aConnection, default)).Error == "claude_api_unsupported", "HTML fallback is not interpreted as zero balance");
handler.Body = "{}";
Check((await ClaudeApiUsage.ReadAsync(client, aConnection, default)).Error == "claude_api_unsupported", "missing usage fields never show invented zeroes");
handler.Body = """{"balance":12,"isValid":false}""";
Check((await ClaudeApiUsage.ReadAsync(client, aConnection, default)).Error == "claude_api_unauthorized", "disabled key never displayed as usable");
File.WriteAllText(Path.Combine(root, "settings.json"), """{"env":{"ANTHROPIC_AUTH_TOKEN":"fixture-local","ANTHROPIC_API_KEY":"","ANTHROPIC_BASE_URL":"http://127.0.0.1:9003"}}""");
Check(store.Resolve("")!.Key == "fixture-local" && store.Resolve(a.Id)!.Key == "fixture-A", "local settings fallback does not replace explicit profile");
Check(File.ReadAllText(Path.Combine(root, "settings.json")).Contains("fixture-local"), "profile selection never modifies global settings");
File.WriteAllText(Path.Combine(root, "settings.json"), """{"env":{"ANTHROPIC_AUTH_TOKEN":"fixture-A","ANTHROPIC_API_KEY":"","ANTHROPIC_BASE_URL":"http://127.0.0.1:9001/"}}""");
Check(store.ReadLocalConnection()!.Name == "Window A", "local account is identified by matching endpoint, auth type and key");
File.WriteAllText(Path.Combine(root, "settings.json"), """{"env":{"ANTHROPIC_AUTH_TOKEN":"fixture-B","ANTHROPIC_API_KEY":"","ANTHROPIC_BASE_URL":"http://127.0.0.1:9001"}}""");
Check(store.ReadLocalConnection()!.Name == "", "same endpoint with a different key is not mislabeled as a saved account");
var unmatchedSession = store.ReadLocalConnection()!;
Check(store.Resolve(unmatchedSession.ProfileId)!.Key == unmatchedSession.Key && !unmatchedSession.ProfileId.Contains("fixture-"),
    "unchanged unnamed local key restores without putting credentials in its reference");
File.WriteAllText(Path.Combine(root, "settings.json"), """{"env":{"ANTHROPIC_AUTH_TOKEN":"fixture-A","ANTHROPIC_BASE_URL":"http://127.0.0.1:9001"}}""");
Check(store.ReadLocalConnection()!.ProfileId == a.Id, "local key matching a named account pins that account ID");
try { store.Resolve(unmatchedSession.ProfileId); throw new Exception("restored a different local key"); }
catch (InvalidOperationException ex) { Check(ex.Message == "claude_api_missing", "changed unnamed local key cannot take over a saved tab"); }
Check(store.SessionProfileName(unmatchedSession) == "" && store.SessionProfileName(aConnection) == "Window A" &&
    store.SessionProfileName(bConnection) == "Window B", "session label follows each launched pane after the window or local key changes");
store.Add("Later saved", unmatchedSession.BaseUrl, unmatchedSession.Key, unmatchedSession.AuthType);
Check(store.SessionProfileName(unmatchedSession) == "Later saved", "already-running local session recognizes a subsequently named key");
Check(store.Resolve(unmatchedSession.ProfileId)!.Name == "Later saved", "unnamed local reference can resume with the same key saved later");
Console.WriteLine($"{count} checks passed. Fixture: {root}");
return 0;

sealed class FixtureHandler(string body) : HttpMessageHandler
{
    public string Body = body;
    public HttpStatusCode Status = HttpStatusCode.OK;
    public System.Collections.Concurrent.ConcurrentBag<string> Seen = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Seen.Add(request.RequestUri!.Port + ":" + request.Headers.Authorization + ":" +
            (request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : ""));
        if (request.Method != HttpMethod.Get) throw new Exception("Only read-only requests allowed");
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}
