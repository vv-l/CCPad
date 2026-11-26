using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad;

internal sealed record FeishuStartRequest(string PaneId, string AppId, string ChatId,
    string PythonExe, string ScriptFile, string CredentialsFile, string StateDir,
    FeishuSessionBinding? Binding = null);

internal sealed class FeishuOwnedSidecar
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Func<ProcessStartInfo, (Func<bool> Alive, Action Stop)> _start;
    private readonly TimeSpan _timeout;
    private readonly string _reservedAppFingerprint;
    private Action? _stop;
    private bool _starting;
    private bool _closed;
    private string? _descriptorPath;
    private long _generation;
    internal string WindowId { get; } = Guid.NewGuid().ToString("N");
    internal bool IsActive { get { lock (_sync) return _starting || _stop != null; } }

    internal FeishuOwnedSidecar(
        Func<ProcessStartInfo, (Func<bool> Alive, Action Stop)>? start = null,
        TimeSpan? timeout = null, string reservedAppFingerprint = FeishuSessionBinding.ReservedAppFingerprint)
    {
        _reservedAppFingerprint = reservedAppFingerprint;
        _start = start ?? StartProcess;
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
    }

    internal async Task StartAsync(FeishuStartRequest request, string baseUrl, string token,
        Func<bool> targetStillValid)
    {
        if (request.Binding == null) throw new InvalidOperationException("Confirmed session binding required");
        request.Binding.Validate();
        if (string.IsNullOrWhiteSpace(request.PaneId) || string.IsNullOrWhiteSpace(request.AppId) ||
            string.IsNullOrWhiteSpace(request.ChatId) || FeishuSessionBinding.IsReservedApp(request.AppId, _reservedAppFingerprint))
            throw new InvalidOperationException("Explicit dedicated CCPad App/chat/pane required");
        foreach (var path in new[] { request.PythonExe, request.ScriptFile, request.CredentialsFile })
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new InvalidOperationException("Explicit sidecar file unavailable");
        if (!Path.IsPathFullyQualified(request.StateDir) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || !uri.IsLoopback || string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Private state and token-protected loopback required");
        long generation;
        lock (_sync)
        {
            if (_closed || _starting || _stop != null)
                throw new InvalidOperationException("Window closed or sidecar already owned");
            _starting = true;
            generation = ++_generation;
        }
        Action? ownedStop = null;
        try
        {
            _closing.Token.ThrowIfCancellationRequested();
            if (!targetStillValid()) throw new InvalidOperationException("Target pane changed");
            Directory.CreateDirectory(request.StateDir);
            var descriptor = Path.Combine(request.StateDir, $"webterminal-{WindowId}-{generation}.json");
            var ready = Path.Combine(request.StateDir, $"ready-{WindowId}-{generation}.json");
            WriteAtomic(descriptor, new { base_url = baseUrl, token, pid = Environment.ProcessId,
                window_id = WindowId, pane_id = request.PaneId, generation, app_id = request.AppId,
                engine = request.Binding.Engine, codex_home = request.Binding.CodexHome,
                cli_session_id = request.Binding.CliSessionId, session_provenance = request.Binding.SessionProvenance });
            _descriptorPath = descriptor;
            var info = new ProcessStartInfo(request.PythonExe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(request.ScriptFile)!
            };
            foreach (var argument in new[] { "-B", request.ScriptFile, "--credentials", request.CredentialsFile,
                "--web-descriptor", descriptor, "--state-dir", request.StateDir, "--allowed-chat-id", request.ChatId,
                "--expected-app-id", request.AppId, "--target-pane-id", request.PaneId,
                "--window-id", WindowId, "--generation", generation.ToString(), "--startup-ready", ready,
                "--allow-input", "--listen-seconds", "0" }) info.ArgumentList.Add(argument);
            // The explicit file is the only credential source for this owned launch.
            info.Environment.Remove("CCPAD_FEISHU_APP_ID");
            info.Environment.Remove("CCPAD_FEISHU_APP_SECRET");
            Func<bool> alive;
            lock (_sync)
            {
                if (_closed) throw new OperationCanceledException();
                var child = _start(info);
                alive = child.Alive;
                ownedStop = child.Stop;
                _stop = ownedStop;
            }
            var deadline = DateTime.UtcNow + _timeout;
            while (true)
            {
                _closing.Token.ThrowIfCancellationRequested();
                if (!alive() || !targetStillValid()) throw new InvalidOperationException("Owned startup lost target/process");
                if (File.Exists(ready))
                {
                    using var record = JsonDocument.Parse(File.ReadAllText(ready));
                    var data = record.RootElement;
                    if (data.GetProperty("window_id").GetString() != WindowId ||
                        data.GetProperty("pane_id").GetString() != request.PaneId ||
                        data.GetProperty("generation").GetInt64() != generation ||
                        data.GetProperty("app_id").GetString() != request.AppId ||
                        data.GetProperty("ack_scope").GetString() != "pty_write" ||
                        data.GetProperty("engine").GetString() != request.Binding.Engine ||
                        data.GetProperty("cli_session_id").GetString() != request.Binding.CliSessionId ||
                        data.GetProperty("session_provenance").GetString() != request.Binding.SessionProvenance ||
                        data.GetProperty("public_source").GetString() != "codex_rollout_v1")
                        throw new InvalidOperationException("Owned startup identity mismatch");
                    _ = MonitorTargetAsync(generation, ownedStop, alive, targetStillValid);
                    return;
                }
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Owned startup readiness absent");
                await Task.Delay(50, _closing.Token);
            }
        }
        catch
        {
            lock (_sync)
            {
                if (ownedStop != null && ReferenceEquals(_stop, ownedStop))
                {
                    _stop = null;
                    StopSafely(ownedStop);
                }
                RevokeDescriptor();
            }
            throw;
        }
        finally { lock (_sync) _starting = false; }
    }

    private async Task MonitorTargetAsync(long generation, Action ownedStop, Func<bool> alive,
        Func<bool> targetStillValid)
    {
        try
        {
            while (true)
            {
                await Task.Delay(250, _closing.Token);
                lock (_sync)
                {
                    if (_closed || generation != _generation || !ReferenceEquals(_stop, ownedStop)) return;
                    if (alive() && targetStillValid()) continue;
                    _stop = null;
                    StopSafely(ownedStop);
                    RevokeDescriptor();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_stop, ownedStop)) { _stop = null; StopSafely(ownedStop); RevokeDescriptor(); }
            }
        }
    }

    internal void Close()
    {
        lock (_sync)
        {
            _closed = true;
            _closing.Cancel();
            var stop = _stop;
            _stop = null;
            StopSafely(stop);
            RevokeDescriptor();
        }
    }

    private void RevokeDescriptor()
    {
        if (_descriptorPath == null) return;
        try { WriteAtomic(_descriptorPath, new { valid = false, window_id = WindowId, generation = _generation }); }
        catch (Exception error) { Trace.WriteLine("FeishuDescriptorRevoke:" + error.GetType().Name); }
        _descriptorPath = null;
    }

    private static void StopSafely(Action? stop)
    {
        try { stop?.Invoke(); }
        catch (Exception error) { Trace.WriteLine("FeishuOwnedStop:" + error.GetType().Name); }
    }

    internal static void WriteAtomic(string path, object record)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(record));
        File.Move(staging, path, overwrite: true);
    }

    private static (Func<bool>, Action) StartProcess(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new InvalidOperationException("Owned process not created");
        return (() => !process.HasExited, () =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            finally { process.Dispose(); }
        });
    }
}
