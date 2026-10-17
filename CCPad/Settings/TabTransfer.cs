using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.Settings;

internal sealed record TabTransferPayload(TabState State, string CodexHome, bool ForkOnThaw);

[JsonSerializable(typeof(TabTransferPayload))]
internal partial class TabTransferJsonContext : JsonSerializerContext { }

internal enum TabTransferResult { Failed, Committed, Uncertain }

// The drag contains only a random pipe name. Session metadata crosses a local,
// current-user-only pipe; the source is removed only after the destination stages it.
internal sealed class TabTransfer : IDisposable
{
    internal const string Format = "CCPad.TabTransfer.v1";
    private const string Prefix = "CCPad.TabTransfer.";
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(2));
    private readonly NamedPipeServerStream _pipe;
    public string Token { get; } = Prefix + Guid.NewGuid().ToString("N");
    public Task Completion { get; }

    public TabTransfer(TabTransferPayload payload, Func<CancellationToken, Task<bool>> commit,
        Func<Task<TabTransferPayload>>? refresh = null)
    {
        _pipe = new NamedPipeServerStream(Token, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Completion = ServeAsync(payload, commit, refresh);
    }

    public static bool IsToken(string? token) => token != null &&
        token.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(token[Prefix.Length..], "N", out _);

    private async Task ServeAsync(TabTransferPayload payload, Func<CancellationToken, Task<bool>> commit,
        Func<Task<TabTransferPayload>>? refresh)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            if (refresh != null) payload = await refresh().WaitAsync(_stop.Token).ConfigureAwait(false);
            await WriteAsync(_pipe, JsonSerializer.Serialize(payload,
                TabTransferJsonContext.Default.TabTransferPayload), _stop.Token).ConfigureAwait(false);
            if (await ReadAsync(_pipe, _stop.Token).ConfigureAwait(false) != "commit") return;
            _stop.Token.ThrowIfCancellationRequested();
            bool removed = await commit(_stop.Token).ConfigureAwait(false);
            await WriteAsync(_pipe, removed ? "committed" : "rejected", _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        finally { _pipe.Dispose(); }
    }

    // Uncertain means the commit may have reached the source. Keep the staged
    // tab frozen so a lost acknowledgement cannot lose the only recovery entry.
    public static async Task<TabTransferResult> ReceiveAsync(string token,
        Func<TabTransferPayload, Task<bool>> prepare)
    {
        if (!IsToken(token)) return TabTransferResult.Failed;
        bool commitSent = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var pipe = new NamedPipeClientStream(".", token, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            var payload = JsonSerializer.Deserialize(await ReadAsync(pipe, timeout.Token),
                TabTransferJsonContext.Default.TabTransferPayload);
            if (payload == null || !await prepare(payload)) return TabTransferResult.Failed;
            timeout.Token.ThrowIfCancellationRequested();
            commitSent = true;
            await WriteAsync(pipe, "commit", timeout.Token);
            return await ReadAsync(pipe, timeout.Token) == "committed"
                ? TabTransferResult.Committed : TabTransferResult.Failed;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or
            ObjectDisposedException or JsonException or InvalidDataException)
        {
            return commitSent ? TabTransferResult.Uncertain : TabTransferResult.Failed;
        }
    }

    // A bounded length-prefixed frame avoids unbounded reads from a malformed peer.
    private static async Task WriteAsync(Stream stream, string value, CancellationToken cancellation)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 1048576) throw new InvalidDataException("Tab transfer is too large");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation);
        await stream.WriteAsync(bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }

    private static async Task<string> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation);
        int length = BitConverter.ToInt32(header);
        if (length < 0 || length > 1048576) throw new InvalidDataException("Invalid tab transfer size");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellation);
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _pipe.Dispose();
    }
}
