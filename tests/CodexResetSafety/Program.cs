using System.Text.Json;
using CCPad.CodexQuota;

if (args.Contains("--readonly-probe"))
{
    // This client cannot send consume, login/logout, thread, turn, or command methods.
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
    await using var rpc = await CodexResetRpcClient.StartAsync(timeout.Token, permitReset: false);
    var account = await rpc.CallAsync("account/read", new { refreshToken = false }, timeout.Token);
    var limits = await rpc.CallAsync("account/rateLimits/read", new { }, timeout.Token);
    var details = limits.TryGetProperty("rateLimitResetCredits", out var c) ? c : default;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = "readonly", signedIn = account.GetProperty("account").ValueKind == JsonValueKind.Object,
        resetCreditMetadataSupported = details.ValueKind == JsonValueKind.Object,
        availableCount = details.ValueKind == JsonValueKind.Object ? details.GetProperty("availableCount").GetInt32() : (int?)null,
        mutationAllowed = false, threadsStarted = 0,
    }));
    return;
}

int passed = 0;
async Task Test(string name, Func<Task> body)
{
    await body();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
string State() => Path.Combine(Path.GetTempPath(), "CCPadResetSafety", Guid.NewGuid().ToString());
CodexResetCreditService Service(FakeRpc rpc, string state, Func<string?>? identity = null) =>
    new(_ => Task.FromResult<ICodexResetRpc>(rpc), identity ?? (() => "fixture-account"), state);

await Test("opening and cancelling confirmation never consumes a card", async () =>
{
    var rpc = new FakeRpc(); var state = State();
    await using (var preview = await Service(rpc, state).PrepareAsync(default))
        Assert(preview.AvailableCount == 2, "Fresh metadata missing");
    Assert(rpc.ConsumeCount == 0 && !Directory.EnumerateFiles(state, "*.json").Any(), "Preview had side effects");
});
await Test("empty, partial and lower-case confirmations cannot submit", async () =>
{
    var rpc = new FakeRpc();
    await using var preview = await Service(rpc, State()).PrepareAsync(default);
    foreach (var text in new[] { "", "RES", "reset", " RESET", "RESET " })
        Assert(await preview.ConfirmAsync(text, default) == CodexResetOutcome.Cancelled, "Unsafe confirmation accepted");
    Assert(rpc.ConsumeCount == 0, "Card consumed without exact confirmation");
});
await Test("account switch after opening cancels without a consume request", async () =>
{
    string identity = "fixture-A"; var rpc = new FakeRpc();
    await using var preview = await Service(rpc, State(), () => identity).PrepareAsync(default);
    identity = "fixture-B";
    Assert(await preview.ConfirmAsync("RESET", default) == CodexResetOutcome.AccountChanged, "Wrong account accepted");
    Assert(rpc.ConsumeCount == 0, "Wrong account consumed a card");
});
await Test("account switch during preview is rejected", async () =>
{
    string identity = "fixture-A";
    var rpc = new FakeRpc { AfterLimits = () => identity = "fixture-B" };
    try { await using var preview = await Service(rpc, State(), () => identity).PrepareAsync(default); throw new Exception("Changed preview accepted"); }
    catch (InvalidOperationException) { }
    Assert(rpc.ConsumeCount == 0, "Changed preview consumed a card");
});
await Test("a second window cannot open a concurrent redemption", async () =>
{
    var state = State(); var first = new FakeRpc(); var second = new FakeRpc();
    await using (var preview = await Service(first, state).PrepareAsync(default))
    {
        try { await using var other = await Service(second, state).PrepareAsync(default); throw new Exception("Second window was not blocked"); }
        catch (IOException) { }
        Assert(second.Calls.Count == 0, "Blocked window contacted account service");
    }
    await using var available = await Service(second, state).PrepareAsync(default);
    Assert(first.ConsumeCount == 0 && second.ConsumeCount == 0, "Cancelling previews consumed a card");
});
await Test("double-click submits exactly one logical redemption", async () =>
{
    var rpc = new FakeRpc();
    await using var preview = await Service(rpc, State()).PrepareAsync(default);
    var results = await Task.WhenAll(preview.ConfirmAsync("RESET", default), preview.ConfirmAsync("RESET", default));
    Assert(rpc.ConsumeCount == 1 && results.Contains(CodexResetOutcome.Reset), "Duplicate submission");
});
await Test("no available card blocks a new redemption", async () =>
{
    var rpc = new FakeRpc { Count = 0 };
    try { await using var preview = await Service(rpc, State()).PrepareAsync(default); throw new Exception("No-card preview accepted"); }
    catch (InvalidOperationException) { }
    Assert(rpc.ConsumeCount == 0, "No-card case contacted consume");
});
foreach (var pair in new[] { ("nothingToReset", CodexResetOutcome.NothingToReset), ("noCredit", CodexResetOutcome.NoCredit), ("alreadyRedeemed", CodexResetOutcome.AlreadyRedeemed) })
{
    await Test("server outcome " + pair.Item1 + " is handled without retries", async () =>
    {
        var rpc = new FakeRpc { Outcome = pair.Item1 }; var service = Service(rpc, State());
        await using var preview = await service.PrepareAsync(default);
        Assert(await preview.ConfirmAsync("RESET", default) == pair.Item2, "Incorrect server outcome");
        Assert(rpc.ConsumeCount == 1 && !service.HasPendingAttempt, "Unnecessary retry left pending");
    });
}
await Test("timeout persists key and explicit retry reuses it even with zero cards", async () =>
{
    var state = State(); var lostResponse = new FakeRpc { LoseResponse = true };
    var service = Service(lostResponse, state);
    await using (var preview = await service.PrepareAsync(default))
        Assert(await preview.ConfirmAsync("RESET", default) == CodexResetOutcome.Unknown, "Uncertain result not preserved");
    Assert(lostResponse.ConsumeCount == 1 && service.HasPendingAttempt, "Timeout retried or discarded key");
    var replay = new FakeRpc { Count = 0, Outcome = "alreadyRedeemed" }; var restartedService = Service(replay, state);
    await using (var preview = await restartedService.PrepareAsync(default))
    {
        Assert(preview.RetryPending, "Pending redemption lost after restart");
        Assert(await preview.ConfirmAsync("RESET", default) == CodexResetOutcome.AlreadyRedeemed, "Replay failed");
    }
    Assert(lostResponse.Keys.Single() == replay.Keys.Single(), "Retry used a NEW key");
    Assert(lostResponse.CreditIds.Single() == replay.CreditIds.Single(), "Retry selected a different card");
    Assert(!restartedService.HasPendingAttempt, "Completed replay remained pending");
});
await Test("unrecognized server outcome stays pending without an automatic retry", async () =>
{
    var rpc = new FakeRpc { Outcome = "futureOutcome" }; var service = Service(rpc, State());
    await using var preview = await service.PrepareAsync(default);
    Assert(await preview.ConfirmAsync("RESET", default) == CodexResetOutcome.Unknown, "Unknown outcome accepted");
    Assert(rpc.ConsumeCount == 1 && service.HasPendingAttempt, "Unknown outcome created extra redemption");
});
await Test("failed receipt write prevents the real request", async () =>
{
    var rpc = new FakeRpc(); var state = State();
    await using var preview = await Service(rpc, state).PrepareAsync(default);
    Directory.CreateDirectory(Path.Combine(state, "fixture-account.json.tmp"));
    Assert(await preview.ConfirmAsync("RESET", default) == CodexResetOutcome.Cancelled, "Unsafe receipt failure");
    Assert(rpc.ConsumeCount == 0, "Request sent without durable key");
});
await Test("cancelled token prevents submitting", async () =>
{
    var rpc = new FakeRpc();
    await using var preview = await Service(rpc, State()).PrepareAsync(default);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Assert(await preview.ConfirmAsync("RESET", cancellation.Token) == CodexResetOutcome.Cancelled, "Cancellation ignored");
    Assert(rpc.ConsumeCount == 0, "Cancelled confirmation consumed a card");
});
await Test("disposed confirmation cannot be submitted", async () =>
{
    var rpc = new FakeRpc(); var preview = await Service(rpc, State()).PrepareAsync(default);
    await preview.DisposeAsync();
    Assert(await preview.ConfirmAsync("RESET", default) == CodexResetOutcome.Cancelled && rpc.ConsumeCount == 0, "Disposed preview was usable");
});
await Test("simulation is fully offline and does not read real auth", async () =>
{
    Environment.SetEnvironmentVariable("CCPAD_CODEX_RESET_DRY_RUN", "1");
    Environment.SetEnvironmentVariable("CCPAD_DATA_DIR", State());
    Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(State(), "no-auth-here"));
    var simulation = new CodexResetCreditService();
    await using var preview = await simulation.PrepareAsync(default);
    Assert(preview.IsSimulation && preview.AccountLabel == "demo@example.invalid", "Simulation read real account");
    Assert(await preview.ConfirmAsync("RESET", default) == CodexResetOutcome.Simulation, "Simulation not isolated");
});
await Test("simulation blocks a real mutation transport before spawning Codex", async () =>
{
    try
    {
        await using var rpc = await CodexResetRpcClient.StartAsync(default, permitReset: true);
        throw new Exception("Simulation opened a real mutation transport");
    }
    catch (InvalidOperationException) { }
});

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed = 0, mode = "offline fixtures", realResetRequests = 0, realCredentialsRead = 0 }));

sealed class FakeRpc : ICodexResetRpc
{
    public int Count = 2;
    public string Outcome = "reset";
    public bool LoseResponse;
    public Action? AfterLimits;
    public int ConsumeCount;
    public List<string> Calls = new();
    public List<string> Keys = new();
    public List<string?> CreditIds = new();
    public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        Calls.Add(method);
        if (method == "account/read") return Reply("{\"account\":{\"type\":\"chatgpt\",\"email\":\"fixture@example.invalid\",\"planType\":\"pro\"}}");
        if (method == "account/rateLimits/read")
        {
            AfterLimits?.Invoke();
            return Reply(JsonSerializer.Serialize(new { rateLimitResetCredits = new { availableCount = Count, credits = new[] { new { id = "fixture-card-1", status = "available", resetType = "codexRateLimits" } } } }));
        }
        if (method == "account/rateLimitResetCredit/consume")
        {
            ConsumeCount++;
            var p = JsonSerializer.SerializeToElement(parameters);
            Keys.Add(p.GetProperty("idempotencyKey").GetString()!);
            CreditIds.Add(p.GetProperty("creditId").GetString());
            if (LoseResponse) throw new IOException("Fixture: response lost AFTER redemption");
            return Reply(JsonSerializer.Serialize(new { outcome = Outcome }));
        }
        throw new Exception("Unexpected RPC method");
    }
    private static Task<JsonElement> Reply(string json) => Task.FromResult(JsonSerializer.Deserialize<JsonElement>(json));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
