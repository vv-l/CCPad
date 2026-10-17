using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CCPad.Terminal
{
    public enum CodexTurnActivity { Working, Waiting }

    public sealed record CodexTurnObservation(
        CodexTurnActivity Activity,
        DateTimeOffset At,
        string? Error = null)
    {
        // An earlier completion must never release a command submitted after it.
        public bool CanApplyAfter(DateTime lastSubmissionUtc) =>
            lastSubmissionUtc == DateTime.MinValue || At.UtcDateTime >= lastSubmissionUtc;
    }

    /// <summary>
    /// Reads lifecycle events for one exact local Codex conversation. No cwd/newest-file
    /// guess, credential access, log mutation, or inference from output/silence is used.
    /// </summary>
    public sealed class CodexTurnActivityReader
    {
        private const int MaxTailBytes = 1024 * 1024;
        private readonly string _sessionsDirectory;
        private readonly string _sessionId;
        private string? _path;
        private DateTime _nextFindUtc;
        private long _length = -1;
        private DateTime _writeUtc;
        private CodexTurnObservation? _cached;

        public CodexTurnActivityReader(string codexHome, string sessionId)
        {
            _sessionId = Guid.Parse(sessionId).ToString("D");
            _sessionsDirectory = Path.Combine(Path.GetFullPath(codexHome), "sessions");
        }

        public CodexTurnObservation? Read()
        {
            try
            {
                if (_path == null || !File.Exists(_path))
                {
                    _cached = null;
                    _length = -1;
                    if (DateTime.UtcNow < _nextFindUtc) return null;
                    _nextFindUtc = DateTime.UtcNow.AddSeconds(10);
                    if (!Directory.Exists(_sessionsDirectory)) return null;
                    _path = Directory.EnumerateFiles(_sessionsDirectory,
                        "rollout-*-" + _sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
                    if (_path == null) return null;
                }

                var info = new FileInfo(_path);
                if (info.Length == _length && info.LastWriteTimeUtc == _writeUtc) return _cached;
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                var start = Math.Max(0, length - MaxTailBytes);
                stream.Position = start;
                var bytes = new byte[(int)(length - start)];
                stream.ReadExactly(bytes);
                // Do not reuse an old boundary while the file is changing under this read.
                if (stream.Length != length) return null;
                var observation = ParseTail(bytes, start > 0);
                _length = length;
                _writeUtc = info.LastWriteTimeUtc;
                _cached = observation;
                return observation;
            }
            catch { return null; }
        }

        private static CodexTurnObservation? ParseTail(byte[] bytes, bool clipped)
        {
            // A partial newest line could be a task_started event. Never skip it and
            // accidentally return the previous task_complete as the current state.
            if (bytes.Length == 0 || bytes[^1] != (byte)'\n') return null;
            var text = Encoding.UTF8.GetString(bytes);
            var first = clipped ? text.IndexOf('\n') + 1 : 0;
            if (first < 0 || first >= text.Length) return null;
            var end = text.Length - 1;
            while (end > first)
            {
                var previous = text.LastIndexOf('\n', end - 1);
                var begin = Math.Max(first, previous + 1);
                var line = text.AsSpan(begin, end - begin).Trim();
                if (!line.IsEmpty)
                {
                    // Corrupt newer data is unknown, not evidence of completion.
                    using var document = JsonDocument.Parse(line.ToString());
                    var root = document.RootElement;
                    if (root.TryGetProperty("type", out var type) && type.GetString() == "event_msg" &&
                        root.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("type", out var eventType))
                    {
                        CodexTurnActivity? activity = eventType.GetString() switch
                        {
                            "task_started" or "user_message" => CodexTurnActivity.Working,
                            "task_complete" or "turn_aborted" => CodexTurnActivity.Waiting,
                            _ => null,
                        };
                        if (activity.HasValue)
                        {
                            if (!root.TryGetProperty("timestamp", out var timestamp) ||
                                !timestamp.TryGetDateTimeOffset(out var at)) return null;
                            string? error = null;
                            if (activity.Value == CodexTurnActivity.Waiting &&
                                payload.TryGetProperty("error", out var errorValue) &&
                                errorValue.ValueKind == JsonValueKind.Object &&
                                errorValue.TryGetProperty("message", out var message) &&
                                message.ValueKind == JsonValueKind.String)
                            {
                                error = message.GetString();
                            }
                            return new CodexTurnObservation(activity.Value, at, error);
                        }
                    }
                }
                end = previous;
            }
            return null;
        }
    }
}
