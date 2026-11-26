using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.Web
{
    /// <summary>
    /// Mirrors an existing desktop terminal session over a WebSocket connection.
    /// Subscribes to the ConPtySession's OutputReceived event and forwards input back.
    /// </summary>
    internal class WebTerminalSession : IDisposable
    {
        private WebSocket? _socket;
        private SessionEntry? _entry;
        private volatile bool _disposed;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        internal const int MaxInputMessageBytes = 1024 * 1024;
        private const int MaxInputHistory = 4096;
        private readonly Dictionary<string, (string Fingerprint, string Ack)> _inputHistory = new();
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public string? AttachedSessionId { get; private set; }

        /// <summary>
        /// Run the WebSocket read loop. The client sends messages to select a session,
        /// provide input, or resize.
        /// </summary>
        public async Task RunAsync(WebSocket socket, CancellationToken ct)
        {
            _socket = socket;
            var buffer = new byte[8192];
            using var message = new MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    if (message.Length + result.Count > MaxInputMessageBytes)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig,
                            "input_message_too_large", ct);
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    try
                    {
                        var json = StrictUtf8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        await HandleClientMessageAsync(json);
                    }
                    catch (DecoderFallbackException)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData,
                            "invalid_utf8", ct);
                        break;
                    }
                    message.SetLength(0);
                    message.Position = 0;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                Detach();
            }
        }

        private async Task HandleClientMessageAsync(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var type = doc.RootElement.GetProperty("type").GetString() ?? "";

                switch (type)
                {
                    case "attach":
                        var sessionId = doc.RootElement.GetProperty("sessionId").GetString() ?? "";
                        AttachTo(sessionId);
                        break;

                    case "input":
                        await HandleInputAsync(doc.RootElement);
                        break;

                    case "ping":
                        _ = SendAsync("{\"type\":\"pong\"}");
                        break;

                    case "resize":
                        // Resize is intentionally NOT forwarded to the desktop session
                        // because it would affect the desktop user's terminal size.
                        // The web client adapts to whatever size the desktop session uses.
                        break;
                }
            }
            catch { }
        }

        private async Task HandleInputAsync(JsonElement body)
        {
            var data = body.GetProperty("data").GetString() ?? "";
            if (!body.TryGetProperty("inputId", out var idValue))
            {
                if (body.TryGetProperty("expectedCliSessionId", out _) ||
                    body.TryGetProperty("expectedSessionProvenance", out _) || body.TryGetProperty("expectedTurnId", out _))
                {
                    await SendAsync("{\"type\":\"error\",\"message\":\"bound_input_id_required\"}");
                    return;
                }
                _entry?.WriteInput(data); // preserve the existing browser protocol
                return;
            }
            var inputId = idValue.GetString() ?? "";
            if (inputId.Length is < 1 or > 128)
            {
                await SendAsync("{\"type\":\"error\",\"message\":\"invalid_input_id\"}");
                return;
            }
            var pane = body.TryGetProperty("sessionId", out var paneValue) ? paneValue.GetString() ?? "" : "";
            bool hasIdentity = body.TryGetProperty("expectedCliSessionId", out var expectedValue);
            bool hasProvenance = body.TryGetProperty("expectedSessionProvenance", out var provenanceValue);
            var expected = hasIdentity && expectedValue.ValueKind == JsonValueKind.String ? expectedValue.GetString() : null;
            var provenance = hasProvenance && provenanceValue.ValueKind == JsonValueKind.String ? provenanceValue.GetString() : null;
            bool hasTurn = body.TryGetProperty("expectedTurnId", out var turnValue);
            var expectedTurn = hasTurn && turnValue.ValueKind == JsonValueKind.String ? turnValue.GetString() : null;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { sessionId = pane, data, hasIdentity, hasProvenance, hasTurn,
                    expected, provenance, expectedTurn }))));
            if (_inputHistory.TryGetValue(inputId, out var previous))
            {
                await SendAsync(previous.Fingerprint == fingerprint ? previous.Ack :
                    InputAck(inputId, pane, "rejected", "input_id_conflict"));
                return;
            }
            if (_inputHistory.Count >= MaxInputHistory)
            {
                await SendAsync(InputAck(inputId, pane, "rejected", "input_history_full"));
                return;
            }
            string ack;
            if (_entry == null || AttachedSessionId == null)
                ack = InputAck(inputId, pane, "rejected", "not_attached");
            else if (pane != AttachedSessionId || TerminalSessionRegistry.Get(pane) != _entry)
                ack = InputAck(inputId, pane, "rejected", "pane_mismatch");
            else if (hasIdentity || hasProvenance || hasTurn)
            {
                if (!Guid.TryParse(expected, out _) || provenance is not ("official_notify" or "explicit_resume") ||
                    _entry.WriteBoundInputAsync == null ||
                    (data == "\x03" && !hasTurn) ||
                    (hasTurn && (data != "\x03" || string.IsNullOrWhiteSpace(expectedTurn) || expectedTurn.Length > 128)))
                    ack = InputAck(inputId, pane, "rejected", "cli_identity_mismatch");
                else
                {
                    try
                    {
                        var result = await _entry.WriteBoundInputAsync(data, expected!, provenance, expectedTurn);
                        ack = !result.IdentityMatched ? InputAck(inputId, pane, "rejected", "cli_identity_mismatch") :
                            result.Written ? InputAck(inputId, pane, "written", "", "pty_write") :
                            InputAck(inputId, pane, "unknown", "input_delivery_unknown");
                    }
                    catch { ack = InputAck(inputId, pane, "unknown", "input_delivery_unknown"); }
                }
            }
            else
            {
                try
                {
                    ack = _entry.Session.TryWriteInput(data)
                        ? InputAck(inputId, pane, "written", "", "pty_write")
                        : InputAck(inputId, pane, "unknown", "input_delivery_unknown");
                }
                catch
                {
                    ack = InputAck(inputId, pane, "unknown", "input_delivery_unknown");
                }
            }
            // Retain all IDs for this connection; never evict and permit replay.
            // The bridge's durable ledger owns cross-connection deduplication.
            _inputHistory.Add(inputId, (fingerprint, ack));
            await SendAsync(ack);
        }

        private static string InputAck(string inputId, string pane, string status,
            string errorCode, string ackScope = "") => JsonSerializer.Serialize(new
            { type = "input_ack", inputId, sessionId = pane, status, errorCode, ackScope });

        private void AttachTo(string sessionId)
        {
            Detach();

            var entry = TerminalSessionRegistry.Get(sessionId);
            if (entry == null)
            {
                _ = SendAsync("{\"type\":\"error\",\"message\":\"Session not found\"}");
                return;
            }

            _entry = entry;
            AttachedSessionId = sessionId;

            // 1. Send terminal size so web client locks to desktop dimensions
            _ = SendAsync($"{{\"type\":\"size\",\"cols\":{entry.Cols},\"rows\":{entry.Rows}}}");

            // 2. Replay buffered output so web client can see current TUI state
            var snapshot = entry.Buffer.GetSnapshot();
            if (snapshot.Length > 0)
            {
                var b64 = Convert.ToBase64String(snapshot);
                _ = SendAsync($"{{\"type\":\"replay\",\"data\":\"{b64}\"}}");
            }

            // 3. Subscribe to live output AFTER replay
            entry.Session.OutputReceived += OnOutput;

            // 4. Confirm attachment
            _ = SendAsync(JsonSerializer.Serialize(new { type = "attached", sessionId,
                inputAckVersion = 1, maxInputMessageBytes = MaxInputMessageBytes }));
        }

        private void Detach()
        {
            if (_entry != null)
            {
                _entry.Session.OutputReceived -= OnOutput;
                _entry = null;
                AttachedSessionId = null;
            }
        }

        private void OnOutput(byte[] data)
        {
            if (_disposed || _socket?.State != WebSocketState.Open) return;
            var b64 = Convert.ToBase64String(data);
            _ = SendAsync($"{{\"type\":\"output\",\"data\":\"{b64}\"}}");
        }

        private async Task SendAsync(string message)
        {
            if (_disposed || _socket?.State != WebSocketState.Open) return;
            await _sendLock.WaitAsync();
            try
            {
                if (_socket?.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(message);
                await _socket!.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
            _sendLock.Dispose();
            if (_socket?.State == WebSocketState.Open)
            {
                try { _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposed",
                    CancellationToken.None).Wait(2000); } catch { }
            }
        }
    }
}
