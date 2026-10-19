using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CCPad.Terminal;

public sealed record ObservedAgent(string Id, string? ParentId, string Name, string Cwd,
    DateTimeOffset CreatedAt, string Path, string State = "unknown", string Model = "",
    long? InputTokens = null, long? CachedTokens = null, long? OutputTokens = null,
    DateTimeOffset? LastEventAt = null);

public sealed record ObservedEvent(string Id, string AgentId, DateTimeOffset At,
    string Kind, string Title, string Detail, string State = "", double? Seconds = null);

public sealed record ObservationSnapshot(IReadOnlyList<ObservedAgent> Agents,
    IReadOnlyList<ObservedEvent> Events, bool HistoryClipped, int UnreadableLines,
    bool CatchingUp, string? Error);

/// <summary>Read-only, bounded JSONL tailing. A directory never identifies a conversation.</summary>
public sealed class CodexObservationReader
{
    private const int InitialBytes = 2 * 1024 * 1024;
    private const int TickBytes = 512 * 1024;
    private const int MaxEvents = 400;
    private const int MaxAgents = 100;
    private readonly string _sessions;
    private readonly string _rootId;
    private readonly Dictionary<string, ObservedAgent> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ObservedEvent> _events = new();
    private DateTime _nextDiscovery;
    private bool _clipped;
    private int _unreadable;

    private sealed class Cursor
    {
        public required ObservedAgent Agent;
        public long Offset;
        public long LastLength;
        public DateTime CreationUtc;
        public byte[] Pending = Array.Empty<byte>();
        public bool SkipLine;
        public bool Initialized;
        public string? TurnId;
        public string? StateBeforePartial;
        public readonly Dictionary<string, ObservedEvent> Calls = new();
    }

    public CodexObservationReader(string codexHome, string sessionId)
    {
        _sessions = System.IO.Path.Combine(System.IO.Path.GetFullPath(codexHome), "sessions");
        _rootId = Guid.Parse(sessionId).ToString("D");
    }

    public static ObservedAgent? ReadMetadata(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Metadata can include long instructions, but never read an unbounded first line.
            var bytes = new byte[Math.Min(1024 * 1024, stream.Length)];
            var length = stream.Read(bytes, 0, bytes.Length);
            var end = Array.IndexOf(bytes, (byte)'\n', 0, length);
            if (end < 0) return null;
            using var document = JsonDocument.Parse(bytes.AsMemory(0, end));
            var root = document.RootElement;
            if (Text(root, "type") != "session_meta") return null;
            var p = Property(root, "payload");
            var id = Text(p, "id") ?? Text(p, "session_id");
            if (!Guid.TryParse(id, out var uuid) || !path.EndsWith("-" + uuid.ToString("D") + ".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
            var parent = Text(p, "parent_thread_id");
            var agentPath = Text(p, "agent_path");
            var source = Property(p, "source");
            var spawn = Property(Property(source, "subagent"), "thread_spawn");
            var sourceParent = Text(spawn, "parent_thread_id");
            if (parent != null && sourceParent != null && !string.Equals(parent, sourceParent, StringComparison.OrdinalIgnoreCase)) return null;
            parent ??= sourceParent;
            agentPath ??= Text(spawn, "agent_path");
            var isChild = parent != null || (agentPath != null && agentPath != "/root") ||
                Property(source, "subagent").ValueKind != JsonValueKind.Undefined || StringValue(source) == "subagent";
            if (isChild) parent ??= Text(p, "forked_from_id");
            if (isChild && !Guid.TryParse(parent, out _)) return null;
            if (!isChild && StringValue(source) is not ("cli" or "tui" or "vscode")) return null;
            var name = agentPath ?? Text(p, "agent_nickname") ?? Text(spawn, "agent_nickname") ?? uuid.ToString("D")[..8];
            return new ObservedAgent(uuid.ToString("D"), parent?.ToLowerInvariant(), name,
                Text(p, "cwd") ?? "", Timestamp(p, "timestamp") ?? Timestamp(root, "timestamp") ?? DateTimeOffset.MinValue, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static string? ValidateRoot(string codexHome, string sessionId, string workingDir)
    {
        if (!Guid.TryParse(sessionId, out var id)) return null;
        try
        {
            var dir = System.IO.Path.Combine(codexHome, "sessions");
            if (!Directory.Exists(dir)) return null;
            foreach (var path in Directory.EnumerateFiles(dir, "rollout-*-" + id.ToString("D") + ".jsonl", SearchOption.AllDirectories))
            {
                var meta = ReadMetadata(path);
                if (meta != null && meta.ParentId == null && SameDirectory(meta.Cwd, workingDir)) return meta.Id;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    public ObservationSnapshot Read()
    {
        string? error = null;
        bool catchingUp = false;
        try
        {
            if (!Directory.Exists(_sessions)) return Snapshot(false, "sessions_missing");
            if (DateTime.UtcNow >= _nextDiscovery)
            {
                Discover();
                _nextDiscovery = DateTime.UtcNow.AddSeconds(10);
            }
            if (!_cursors.ContainsKey(_rootId)) return Snapshot(false, "session_missing");
            foreach (var cursor in _cursors.Values)
            {
                try { catchingUp |= ReadTail(cursor); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = "read_failed";
                    cursor.Agent = cursor.Agent with { State = "unknown" };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "read_failed"; }
        return Snapshot(catchingUp, error);
    }

    private ObservationSnapshot Snapshot(bool catchingUp, string? error) => new(
        _cursors.Values.Select(c => c.Agent).ToArray(), _events.OrderBy(e => e.At).ToArray(),
        _clipped, _unreadable, catchingUp, error);

    private void Discover()
    {
        foreach (var path in Directory.EnumerateFiles(_sessions, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.Length < 36) continue;
            var id = name[^36..];
            if (_catalog.ContainsKey(id)) continue;
            var meta = ReadMetadata(path);
            if (meta != null) _catalog[meta.Id] = meta;
        }
        if (!_catalog.TryGetValue(_rootId, out var root)) return;
        if (!_cursors.ContainsKey(_rootId)) _cursors[_rootId] = new Cursor { Agent = root };
        // Follow explicit ancestry only. Same-cwd roots and interactive forks stay separate.
        bool added;
        do
        {
            added = false;
            foreach (var meta in _catalog.Values.OrderByDescending(a => a.CreatedAt))
            {
                if (meta.ParentId == null || !_cursors.ContainsKey(meta.ParentId) || _cursors.ContainsKey(meta.Id)) continue;
                if (_cursors.Count >= MaxAgents) { _clipped = true; continue; }
                _cursors[meta.Id] = new Cursor { Agent = meta };
                added = true;
            }
        } while (added);
    }

    private bool ReadTail(Cursor cursor)
    {
        using var stream = new FileStream(cursor.Agent.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var created = File.GetCreationTimeUtc(cursor.Agent.Path);
        if (cursor.Initialized && (stream.Length < cursor.LastLength || created != cursor.CreationUtc))
        {
            cursor.Offset = 0;
            cursor.Pending = Array.Empty<byte>();
            cursor.SkipLine = false;
            cursor.Initialized = false;
            cursor.Calls.Clear();
            cursor.TurnId = null;
            cursor.StateBeforePartial = null;
            cursor.Agent = cursor.Agent with { State = "unknown", InputTokens = null, CachedTokens = null, OutputTokens = null };
            _events.RemoveAll(e => e.AgentId == cursor.Agent.Id);
        }
        if (!cursor.Initialized)
        {
            cursor.Offset = Math.Max(0, stream.Length - InitialBytes);
            cursor.SkipLine = cursor.Offset > 0;
            _clipped |= cursor.SkipLine;
            cursor.Initialized = true;
            cursor.CreationUtc = created;
        }
        cursor.LastLength = stream.Length;
        stream.Position = cursor.Offset;
        var size = (int)Math.Min(TickBytes, stream.Length - cursor.Offset);
        if (size <= 0) return false;
        if (cursor.StateBeforePartial is string previousState)
        {
            cursor.Agent = cursor.Agent with { State = previousState };
            cursor.StateBeforePartial = null;
        }
        var bytes = new byte[cursor.Pending.Length + size];
        cursor.Pending.CopyTo(bytes, 0);
        var count = stream.Read(bytes, cursor.Pending.Length, size);
        cursor.Offset += count;
        var length = cursor.Pending.Length + count;
        var start = 0;
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (cursor.SkipLine) cursor.SkipLine = false;
            else if (i > start)
            {
                try
                {
                    using var doc = JsonDocument.Parse(bytes.AsMemory(start, i - start));
                    Parse(cursor, doc.RootElement, cursor.Offset - count - (bytes.Length - size) + start);
                }
                catch (JsonException) { _unreadable++; cursor.Agent = cursor.Agent with { State = "unknown" }; }
            }
            start = i + 1;
        }
        // An incomplete line is retried on the next append, including split UTF-8 characters.
        cursor.Pending = bytes.AsSpan(start, length - start).ToArray();
        if (cursor.Pending.Length > InitialBytes)
        {
            cursor.Pending = Array.Empty<byte>();
            cursor.SkipLine = true;
            _clipped = true;
        }
        if (cursor.Pending.Length > 0)
        {
            cursor.StateBeforePartial = cursor.Agent.State;
            cursor.Agent = cursor.Agent with { State = "unknown" };
        }
        return cursor.Offset < stream.Length;
    }

    private void Parse(Cursor cursor, JsonElement record, long offset)
    {
        var p = Property(record, "payload");
        var type = Text(record, "type");
        var kind = Text(p, "type");
        var at = Timestamp(record, "timestamp");
        if (at == null || at < cursor.Agent.CreatedAt) return; // excludes inherited fork history
        var id = cursor.Agent.Id + ":" + offset;
        if (type == "turn_context")
        {
            cursor.TurnId = Text(p, "turn_id");
            cursor.Agent = cursor.Agent with { Model = Text(p, "model") ?? cursor.Agent.Model };
            return;
        }
        if (type == "token_usage_record" && Text(p, "thread_id") == cursor.Agent.Id)
        {
            var usage = Property(p, "thread_token_usage");
            if (usage.ValueKind == JsonValueKind.Object)
                cursor.Agent = cursor.Agent with { InputTokens = Number(usage, "input_tokens"),
                    CachedTokens = Number(usage, "cached_input_tokens"), OutputTokens = Number(usage, "output_tokens") };
            return;
        }
        if (type == "event_msg")
        {
            switch (kind)
            {
                case "task_started":
                    cursor.TurnId = Text(p, "turn_id");
                    SetState(cursor, "working", at.Value);
                    Add(new(id, cursor.Agent.Id, at.Value, "turn", "turn_started", "", "working"));
                    break;
                case "task_complete":
                case "turn_aborted":
                    var turnId = Text(p, "turn_id");
                    if (turnId != null && cursor.TurnId != null && turnId != cursor.TurnId) break;
                    var failed = kind == "turn_aborted" || Property(p, "error").ValueKind == JsonValueKind.Object;
                    SetState(cursor, failed ? "interrupted" : "waiting", at.Value);
                    Add(new(id, cursor.Agent.Id, at.Value, "turn", failed ? "turn_interrupted" : "turn_completed",
                        Text(Property(p, "error"), "message") ?? "", failed ? "interrupted" : "waiting"));
                    break;
                case "token_count":
                    var usage = Property(Property(p, "info"), "total_token_usage");
                    if (usage.ValueKind == JsonValueKind.Object)
                        cursor.Agent = cursor.Agent with { InputTokens = Number(usage, "input_tokens"),
                            CachedTokens = Number(usage, "cached_input_tokens"), OutputTokens = Number(usage, "output_tokens") };
                    break;
                case "error":
                    SetState(cursor, "unknown", at.Value);
                    Add(new(id, cursor.Agent.Id, at.Value, "error", "error", Text(p, "message") ?? "", "failed"));
                    break;
            }
            return;
        }
        // response_item is the canonical transcript; item_completed mirrors it in newer CLIs.
        if (type != "response_item") return;
        if (kind == "message")
        {
            var role = Text(p, "role");
            var channel = Text(p, "channel") ?? Text(p, "phase");
            if (role != "user" && !(role == "assistant" && channel is "commentary" or "final" or "final_answer")) return;
            var detail = ContentText(Property(p, "content"));
            if (string.IsNullOrWhiteSpace(detail)) return;
            Add(new(id, cursor.Agent.Id, at.Value, role == "user" ? "prompt" : "message",
                role == "user" ? "user_message" : channel is "final" or "final_answer" ? "answer" : "progress", detail));
            cursor.Agent = cursor.Agent with { LastEventAt = at };
            return;
        }
        if (kind is "function_call" or "custom_tool_call")
        {
            var callId = Text(p, "call_id");
            var detail = Text(p, kind == "function_call" ? "arguments" : "input") ?? "";
            var e = new ObservedEvent(id, cursor.Agent.Id, at.Value, "tool", Text(p, "name") ?? "tool", Clip(detail), "running");
            Add(e);
            SetState(cursor, "working", at.Value);
            if (callId != null)
            {
                if (cursor.Calls.Count >= MaxEvents) cursor.Calls.Remove(cursor.Calls.Keys.First());
                cursor.Calls[callId] = e;
            }
        }
        else if (kind is "function_call_output" or "custom_tool_call_output")
        {
            var callId = Text(p, "call_id");
            var output = Property(p, "output");
            var detail = output.ValueKind == JsonValueKind.String ? output.GetString()! : ContentText(output);
            var state = ExplicitFailure(detail) ? "failed" : "returned";
            if (callId != null && cursor.Calls.Remove(callId, out var call))
            {
                var updated = call with { Detail = Clip(call.Detail + "\n\n── OUTPUT ──\n" + detail), State = state,
                    Seconds = Math.Max(0, (at.Value - call.At).TotalSeconds) };
                var index = _events.FindIndex(e => e.Id == call.Id);
                if (index >= 0) _events[index] = updated;
                else Add(updated);
            }
            else Add(new(id, cursor.Agent.Id, at.Value, "tool", "tool_output", Clip(detail), state));
            cursor.Agent = cursor.Agent with { LastEventAt = at };
        }
    }

    private void Add(ObservedEvent item)
    {
        _events.Add(item with { Detail = Clip(item.Detail) });
        if (_events.Count > MaxEvents)
        {
            var oldest = _events.MinBy(e => e.At)!;
            _events.Remove(oldest);
            _clipped = true;
        }
    }

    private static void SetState(Cursor cursor, string state, DateTimeOffset at) =>
        cursor.Agent = cursor.Agent with { State = state, LastEventAt = at };
    private static string Clip(string value) => value.Length <= 16000 ? value : value[..8000] + "\n…\n" + value[^8000..];
    private static bool ExplicitFailure(string value)
    {
        if (Regex.IsMatch(value, @"(?im)^Process exit(?:ed with code | code: )(?:-[1-9]\d*|[1-9]\d*)\b")) return true;
        try
        {
            using var doc = JsonDocument.Parse(value);
            bool Failed(JsonElement result) => Property(result, "isError").ValueKind == JsonValueKind.True ||
                Property(result, "is_error").ValueKind == JsonValueKind.True || Number(result, "exit_code") is long code && code != 0;
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Any(Failed) : Failed(doc.RootElement);
        }
        catch (JsonException) { return false; }
    }
    private static bool SameDirectory(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    private static string? StringValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Text(JsonElement value, string key) => StringValue(Property(value, key));
    private static long? Number(JsonElement value, string key) => Property(value, key) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static DateTimeOffset? Timestamp(JsonElement value, string key) =>
        DateTimeOffset.TryParse(Text(value, key), out var at) ? at : null;
    private static string ContentText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.ValueKind == JsonValueKind.Array) return string.Join("\n", value.EnumerateArray().Select(v => Text(v, "text") ?? "").Where(v => v.Length > 0));
        return value.ValueKind == JsonValueKind.Object ? value.GetRawText() : "";
    }
}
