using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CCPad.Terminal;
using CCPad.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using static CCPad.Terminal.PseudoConsoleApi;

// Exercises the production receiver and real Windows input-pipe writes.
// No CCPad window, pseudoconsole, shell, model or Feishu client is started.
internal static class Program
{
    private static int _passed;
    public static async Task Main(string[] args)
    {
        if (args.Contains("--serve")) { await Serve(); return; }
        await Case("split UTF-8 and long input", async () =>
        {
            var data = string.Concat(Enumerable.Repeat("中文🙂", 12000)) + "\r";
            using var pipe = new PipeFixture();
            using var socket = new FixtureSocket();
            socket.Add(Attach());
            socket.Add(Input("long", data), 1);
            using var session = new WebTerminalSession();
            await session.RunAsync(socket, CancellationToken.None);
            Equal(data, pipe.ReadAvailable());
            Ack(socket, "long", "written", "pty_write");
            True(socket.Sent.Any(x => x.GetProperty("type").GetString() == "attached"
                && x.GetProperty("inputAckVersion").GetInt32() == 1));
        });
        await Case("multiple complete messages", async () =>
        {
            using var pipe = new PipeFixture();
            var socket = await Run(Attach(), Input("a", "one\r"), Input("b", "two\r"));
            Equal("one\rtwo\r", pipe.ReadAvailable());
            Ack(socket, "a", "written", "pty_write"); Ack(socket, "b", "written", "pty_write");
        });
        await Case("duplicate ID never writes twice", async () =>
        {
            using var pipe = new PipeFixture();
            var socket = await Run(Attach(), Input("same", "once\r"), Input("same", "once\r"));
            Equal("once\r", pipe.ReadAvailable());
            Equal(2, socket.Sent.Count(x => x.TryGetProperty("inputId", out var v) && v.GetString() == "same"));
        });
        await Case("conflicting duplicate ID rejected", async () =>
        {
            using var pipe = new PipeFixture();
            var socket = await Run(Attach(), Input("same", "once\r"), Input("same", "different\r"));
            Equal("once\r", pipe.ReadAvailable());
            Ack(socket, "same", "rejected", "", "input_id_conflict");
        });
        await Case("wrong pane rejected", async () =>
        {
            using var pipe = new PipeFixture();
            var socket = await Run(Attach(), Input("wrong", "never", "other"));
            Equal("", pipe.ReadAvailable()); Ack(socket, "wrong", "rejected", "", "pane_mismatch");
        });
        await Case("unattached input rejected", async () =>
        {
            using var pipe = new PipeFixture();
            var socket = await Run(Input("none", "never"));
            Equal("", pipe.ReadAvailable()); Ack(socket, "none", "rejected", "", "not_attached");
        });
        await Case("no pipe does not acknowledge success", async () =>
        {
            using var pipe = new PipeFixture();
            pipe.ClearHandle();
            var socket = await Run(Attach(), Input("failed", "never"));
            Equal("", pipe.ReadAvailable()); Ack(socket, "failed", "unknown", "", "input_delivery_unknown");
        });
        await Case("legacy browser input preserved", async () =>
        {
            using var pipe = new PipeFixture();
            await Run(Attach(), JsonSerializer.Serialize(new { type = "input", data = "legacy\r" }));
            Equal("legacy\r", pipe.ReadAvailable());
        });
        await Case("incomplete input at EOF never writes", async () =>
        {
            using var pipe = new PipeFixture();
            using var socket = new FixtureSocket();
            socket.Add(Attach()); socket.AddRaw(Encoding.UTF8.GetBytes(Input("eof", "never")), false);
            using var session = new WebTerminalSession();
            await session.RunAsync(socket, CancellationToken.None);
            Equal("", pipe.ReadAvailable());
        });
        await Case("oversized fragmented input never writes", async () =>
        {
            using var pipe = new PipeFixture();
            using var socket = new FixtureSocket();
            socket.Add(Attach()); socket.Add(Input("large", new string('x', WebTerminalSession.MaxInputMessageBytes)));
            using var session = new WebTerminalSession();
            await session.RunAsync(socket, CancellationToken.None);
            Equal("", pipe.ReadAvailable()); Equal(WebSocketCloseStatus.MessageTooBig, socket.OutputClose);
        });
        await Case("invalid UTF-8 never writes", async () =>
        {
            using var pipe = new PipeFixture();
            using var socket = new FixtureSocket();
            socket.Add(Attach()); socket.AddRaw(new byte[] { 0xff }, true);
            using var session = new WebTerminalSession();
            await session.RunAsync(socket, CancellationToken.None);
            Equal("", pipe.ReadAvailable()); Equal(WebSocketCloseStatus.InvalidPayloadData, socket.OutputClose);
        });
        await Case("malformed JSON never writes", async () =>
        {
            using var pipe = new PipeFixture();
            var socket = await Run(Attach(), "{invalid}");
            Equal("", pipe.ReadAvailable());
        });
        await Case("registry replacement invalidates attachment", async () =>
        {
            using var pipe = new PipeFixture();
            using var socket = new FixtureSocket();
            socket.Add(Attach()); socket.Add(Input("stale", "never"));
            socket.BeforeReceive = count => { if (count == 1) TerminalSessionRegistry.Unregister("fixture-pane"); };
            using var session = new WebTerminalSession();
            await session.RunAsync(socket, CancellationToken.None);
            Equal("", pipe.ReadAvailable()); Ack(socket, "stale", "rejected", "", "pane_mismatch");
        });
        await Case("bounded ledger rejects new IDs without eviction", async () =>
        {
            using var pipe = new PipeFixture();
            using var socket = new FixtureSocket();
            socket.Add(Attach());
            for (int i = 0; i < 4097; i++) socket.Add(Input("id" + i, ""));
            socket.Add(Input("id0", ""));
            using var session = new WebTerminalSession();
            await session.RunAsync(socket, CancellationToken.None);
            Equal("", pipe.ReadAvailable()); Ack(socket, "id4096", "rejected", "", "input_history_full");
            Equal(2, socket.Sent.Count(x => x.TryGetProperty("inputId", out var v) && v.GetString() == "id0"));
        });
        await Case("real pipe reports entire UTF-8 write", () =>
        {
            using var pipe = new PipeFixture(); True(pipe.Session.TryWriteInput("中文🙂\r"));
            Equal("中文🙂\r", pipe.ReadAvailable()); return Task.CompletedTask;
        });
        await Case("real pipe missing handle reports failure", () =>
        {
            using var pipe = new PipeFixture(); pipe.ClearHandle(); True(!pipe.Session.TryWriteInput("never"));
            Equal("", pipe.ReadAvailable()); return Task.CompletedTask;
        });
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, tests = _passed, ccpad_started = false, model_calls = 0 }));
    }

    private static async Task Case(string name, Func<Task> test)
    { await test(); _passed++; Console.WriteLine("PASS " + name); }
    private static void True(bool value) { if (!value) throw new Exception("fixture_assertion_failed"); }
    private static void Equal<T>(T expected, T actual) { True(EqualityComparer<T>.Default.Equals(expected, actual)); }
    private static string Attach() => JsonSerializer.Serialize(new { type = "attach", sessionId = "fixture-pane" });
    private static string Input(string id, string data, string pane = "fixture-pane") =>
        JsonSerializer.Serialize(new { type = "input", inputId = id, sessionId = pane, data },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static void Ack(FixtureSocket socket, string id, string status, string scope, string? error = null) =>
        True(socket.Sent.Any(x => x.TryGetProperty("inputId", out var v) && v.GetString() == id
            && x.GetProperty("status").GetString() == status && x.GetProperty("ackScope").GetString() == scope
            && (error == null || x.GetProperty("errorCode").GetString() == error)));
    private static async Task<FixtureSocket> Run(params string[] frames)
    {
        var socket = new FixtureSocket(); foreach (var frame in frames) socket.Add(frame);
        using var session = new WebTerminalSession(); await session.RunAsync(socket, CancellationToken.None);
        return socket;
    }

    private static async Task Serve()
    {
        using var pipe = new PipeFixture();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build(); app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (context.Request.Query["token"] != "offline-fixture-token") { context.Response.StatusCode = 403; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var session = new WebTerminalSession(); await session.RunAsync(socket, context.RequestAborted);
        });
        app.MapGet("/api/sessions", () => new[] { new { id = "fixture-pane", sessionId = PipeFixture.CliSessionId,
            sessionProvenance = "official_notify",
            cliMode = "codex", workingDir = "D:/isolated-demo", status = "waiting", processId = Environment.ProcessId } });
        app.MapGet("/fixture/results", () => new { input = pipe.ReadAvailable() });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Console.WriteLine(JsonSerializer.Serialize(new { base_url = address, pid = Environment.ProcessId }));
        await app.WaitForShutdownAsync();
    }
}

internal sealed class PipeFixture : IDisposable
{
    internal const string CliSessionId = "5b9c7e03-f171-4d86-a902-9d7c1df72013";
    private readonly IntPtr _read, _write;
    private static readonly FieldInfo Handle = typeof(ConPtySession).GetField("_hPipeInWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
    public ConPtySession Session { get; } = new();
    public PipeFixture()
    {
        if (!CreatePipe(out _read, out _write, IntPtr.Zero, 2 * 1024 * 1024)) throw new Exception("fixture_pipe_failed");
        Handle.SetValue(Session, _write);
        TerminalSessionRegistry.Register("fixture-pane", new SessionEntry
            { Id = "fixture-pane", Label = "fixture", Command = "unused", Session = Session,
                CliMode = "codex", CliSessionId = CliSessionId, SessionProvenance = "official_notify",
                WriteBoundInputAsync = (text, id, proof, turn) =>
                {
                    var matched = id == CliSessionId && proof == "official_notify" &&
                        (turn == null || (text == "\u0003" && turn == "fixture-turn"));
                    return Task.FromResult(new BoundInputResult(matched, matched && Session.TryWriteInput(text)));
                } });
    }
    public void ClearHandle() => Handle.SetValue(Session, IntPtr.Zero);
    public string ReadAvailable()
    {
        if (!PeekNamedPipe(_read, IntPtr.Zero, 0, IntPtr.Zero, out var size, IntPtr.Zero)) throw new Exception("fixture_peek_failed");
        if (size == 0) return "";
        var bytes = new byte[size];
        if (!ReadFile(_read, bytes, bytes.Length, out var count, IntPtr.Zero)) throw new Exception("fixture_read_failed");
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
    public void Dispose()
    {
        TerminalSessionRegistry.Unregister("fixture-pane"); ClearHandle(); Session.Dispose(); CloseHandle(_read); CloseHandle(_write);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(IntPtr handle, IntPtr buffer, int bufferSize,
        IntPtr read, out int available, IntPtr left);
}

internal sealed class FixtureSocket : WebSocket
{
    private readonly Queue<(byte[] Bytes, bool End)> _frames = new();
    private WebSocketState _state = WebSocketState.Open;
    private int _received;
    public List<JsonElement> Sent { get; } = new();
    public Action<int>? BeforeReceive { get; set; }
    public WebSocketCloseStatus? OutputClose { get; private set; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => _state;
    public void Add(string text, int split = 8192)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (int at = 0; at < bytes.Length; at += split)
            AddRaw(bytes[at..Math.Min(at + split, bytes.Length)], at + split >= bytes.Length);
    }
    public void AddRaw(byte[] bytes, bool end) => _frames.Enqueue((bytes, end));
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        BeforeReceive?.Invoke(_received++);
        if (_frames.Count == 0) { _state = WebSocketState.CloseReceived; return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)); }
        var frame = _frames.Dequeue(); frame.Bytes.AsSpan().CopyTo(buffer.AsSpan());
        return Task.FromResult(new WebSocketReceiveResult(frame.Bytes.Length, WebSocketMessageType.Text, frame.End));
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
    { using var json = JsonDocument.Parse(buffer); Sent.Add(json.RootElement.Clone()); return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
    { OutputClose = status; _state = WebSocketState.CloseSent; return Task.CompletedTask; }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
    { _state = WebSocketState.Closed; return Task.CompletedTask; }
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() => _state = WebSocketState.Closed;
}
