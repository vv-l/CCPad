using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
