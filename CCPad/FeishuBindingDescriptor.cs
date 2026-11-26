using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CCPad;

internal sealed record FeishuBridgeRequest(string AppId, string ChatId, string StateDir);
internal sealed record FeishuProcessIdentity(int Pid, string StartUtc, string ExePath, string ExeSha256);

internal sealed class FeishuBindingDescriptor : IDisposable
{
    private readonly object _sync = new();
    private readonly FileStream _lease;
    private readonly string _windowId;
    private readonly long _generation;
    private bool _disposed;
    internal string DescriptorPath { get; }

    internal FeishuBindingDescriptor(string stateDir, string windowId, long generation, string controlledStateDir)
    {
        if (!Path.IsPathFullyQualified(stateDir) || stateDir.StartsWith(@"\\") ||
            !Path.GetFullPath(stateDir).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(controlledStateDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Controlled local state directory required");
        for (var parent = new DirectoryInfo(stateDir); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse state directory rejected");
        Directory.CreateDirectory(stateDir);
        DescriptorPath = Path.Combine(stateDir, "ccpad-owned-binding.json");
        foreach (var file in new[] { DescriptorPath, DescriptorPath + ".owner" })
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse binding file rejected");
        _windowId = windowId; _generation = generation;
        _lease = new FileStream(DescriptorPath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(DescriptorPath))
            {
                using var old = JsonDocument.Parse(File.ReadAllText(DescriptorPath));
                if (old.RootElement.GetProperty("valid").GetBoolean() &&
                    old.RootElement.GetProperty("window_id").GetString() != windowId)
                    throw new InvalidOperationException("Another window owns this binding");
            }
        }
        catch { _lease.Dispose(); throw; }
    }

    internal void Publish(FeishuBridgeRequest request, string paneId, FeishuSessionBinding binding,
        string baseUrl, string token, FeishuProcessIdentity? process = null)
    {
        binding.Validate();
        if (string.IsNullOrWhiteSpace(request.AppId) || FeishuSessionBinding.IsReservedApp(request.AppId) ||
            string.IsNullOrWhiteSpace(request.ChatId) || string.IsNullOrWhiteSpace(paneId) || string.IsNullOrEmpty(token) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback)
            throw new InvalidOperationException("Dedicated existing AutoBot binding required");
        if (process == null)
        {
            using var own = Process.GetCurrentProcess();
            var exe = own.MainModule?.FileName ?? throw new InvalidOperationException("Owned executable unavailable");
            process = new(Environment.ProcessId, own.StartTime.ToUniversalTime().ToString("O"), exe,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant());
        }
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FeishuBindingDescriptor));
            FeishuOwnedSidecar.WriteAtomic(DescriptorPath, new
            {
                valid = true, base_url = baseUrl, token, pid = process.Pid,
                process_start_utc = process.StartUtc, exe_path = process.ExePath, exe_sha256 = process.ExeSha256,
                window_id = _windowId, pane_id = paneId, generation = _generation,
                app_id = request.AppId, chat_id = request.ChatId, engine = binding.Engine,
                codex_home = binding.CodexHome, cli_session_id = binding.CliSessionId,
                session_provenance = binding.SessionProvenance
            });
        }
    }

    internal void Invalidate(string reason)
    {
        lock (_sync)
        {
            if (_disposed || !File.Exists(DescriptorPath)) return;
            using var current = JsonDocument.Parse(File.ReadAllText(DescriptorPath));
            var value = current.RootElement;
            if (!value.GetProperty("valid").GetBoolean()) return;
            if (value.GetProperty("window_id").GetString() != _windowId ||
                value.GetProperty("generation").GetInt64() != _generation) return;
            FeishuOwnedSidecar.WriteAtomic(DescriptorPath, new { valid = false, reason,
                window_id = _windowId, generation = _generation });
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try { Invalidate("window_closed"); }
            catch (Exception error) { Trace.WriteLine("FeishuBindingInvalidation:" + error.GetType().Name); }
            finally { _disposed = true; _lease.Dispose(); }
        }
    }
}
