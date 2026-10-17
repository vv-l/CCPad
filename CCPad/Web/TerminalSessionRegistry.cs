using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CCPad.Terminal;

namespace CCPad.Web
{
    /// <summary>
    /// Tracks all active terminal panes so the web server can mirror them.
    /// </summary>
    internal static class TerminalSessionRegistry
    {
        private static readonly ConcurrentDictionary<string, SessionEntry> _entries = new();

        public static void Register(string id, SessionEntry entry)
        {
            entry.StartBuffering();
            _entries[id] = entry;
        }

        public static void Unregister(string id)
        {
            if (_entries.TryRemove(id, out var entry))
                entry.StopBuffering();
        }

        public static SessionEntry? Get(string id) => _entries.GetValueOrDefault(id);
        public static List<SessionEntry> GetAll() => _entries.Values.ToList();

        /// <summary>
        /// Update bridge-visible metadata without replacing the live ConPTY entry.
        /// Session IDs and lifecycle status can change after a pane is registered,
        /// so the WebTerminal API must read the current values.
        /// </summary>
        public static bool UpdateMetadata(string id, Action<SessionEntry> update,
            bool notify = false)
        {
            if (!_entries.TryGetValue(id, out var entry)) return false;
            update(entry);
            if (notify) NotifyChanged();
            return true;
        }

        public static event Action? Changed;
        internal static void NotifyChanged() => Changed?.Invoke();
    }

    internal class SessionEntry
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string Command { get; init; }
        public string? WorkingDir { get; init; }
        public required ConPtySession Session { get; set; }

        // Bridge metadata. These fields are deliberately descriptive only: the
        // bridge still writes input through this live entry and never starts a
        // second model process.
        public string? CliMode { get; set; }
        public string? CliSessionId { get; set; }
        public string? CodexAccountId { get; set; }
        public string? RemoteProfileId { get; set; }
        public string? RemoteWorkingDir { get; set; }
        public string Status { get; set; } = "waiting";
        public DateTime LaunchedAtUtc { get; set; }
        public DateTime LastActivityUtc { get; set; }

        /// <summary>Ring buffer capturing recent output for replay to new web clients.</summary>
        public OutputRingBuffer Buffer { get; } = new(512 * 1024); // 512KB

        /// <summary>Current terminal dimensions (from the desktop pane).</summary>
        public int Cols => Session.Cols;
        public int Rows => Session.Rows;

        private Action<byte[]>? _bufferHandler;

        internal void StartBuffering()
        {
            _bufferHandler = data => Buffer.Write(data);
            Session.OutputReceived += _bufferHandler;
        }

        internal void StopBuffering()
        {
            if (_bufferHandler != null)
            {
                Session.OutputReceived -= _bufferHandler;
                _bufferHandler = null;
            }
        }

        /// <summary>Write input to the underlying ConPTY process.</summary>
        public void WriteInput(string data) => Session.WriteInput(data);

        /// <summary>Resize the underlying ConPTY process.</summary>
        public void Resize(int cols, int rows) => Session.Resize(cols, rows);
    }

    /// <summary>
    /// Lock-free-ish ring buffer for terminal output. Stores the most recent N bytes
    /// so web clients can replay the TUI state on connect.
    /// </summary>
    internal class OutputRingBuffer
    {
        private readonly byte[] _buf;
        private int _writePos;
        private int _length;
        private readonly object _lock = new();

        public OutputRingBuffer(int capacity)
        {
            _buf = new byte[capacity];
        }

        public void Write(byte[] data)
        {
            lock (_lock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    _buf[_writePos] = data[i];
                    _writePos = (_writePos + 1) % _buf.Length;
                }
                _length = Math.Min(_length + data.Length, _buf.Length);
            }
        }

        public byte[] GetSnapshot()
        {
            lock (_lock)
            {
                if (_length == 0) return Array.Empty<byte>();

                var result = new byte[_length];
                if (_length < _buf.Length)
                {
                    // Buffer hasn't wrapped yet
                    Array.Copy(_buf, 0, result, 0, _length);
                }
                else
                {
                    // Buffer has wrapped — oldest data starts at _writePos
                    int firstPart = _buf.Length - _writePos;
                    Array.Copy(_buf, _writePos, result, 0, firstPart);
                    Array.Copy(_buf, 0, result, firstPart, _writePos);
                }
                return result;
            }
        }
    }
}
