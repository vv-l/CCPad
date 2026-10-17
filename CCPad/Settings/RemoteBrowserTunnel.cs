using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.Settings
{
    /// <summary>
    /// Owns a short-lived local SSH forward for a remote browser link.  Remote
    /// web servers are often bound to the Linux host (or to its loopback only),
    /// while the Windows browser is on a different network path.  The tunnel is
    /// deliberately scoped to one pane and one remote port; arbitrary terminal
    /// text never gets a tunnel automatically.
    /// </summary>
    internal sealed class RemoteBrowserTunnel : IDisposable
    {
        private readonly RemoteDeviceEntry _device;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Process? _process;
        private int _localPort;
        private string? _remoteHost;
        private int _remotePort;
        private bool _disposed;

        public RemoteBrowserTunnel(RemoteDeviceEntry device)
        {
            _device = device;
        }

        public async Task<Uri?> GetLocalUriAsync(Uri target, CancellationToken cancellationToken = default)
        {
            if (!IsUsableTarget(target) || !IsUsableDevice()) return null;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) return null;
                int remotePort = target.Port;
                if (IsAliveFor(target.Host, remotePort))
                    return Rewrite(target, _localPort);

                StopProcess();

                // A service advertised as 192.168.x.x is commonly bound to
                // 127.0.0.1 on the Linux box.  Try that first, then the exact
                // URL host for services bound to a LAN address only.
                string[] hosts = string.Equals(target.Host, "127.0.0.1",
                        StringComparison.OrdinalIgnoreCase)
                    ? new[] { target.Host }
                    : new[] { "127.0.0.1", target.Host };
                foreach (string host in hosts)
                {
                    if (!IsSafeForwardHost(host)) continue;
                    int localPort = GetFreePort();
                    if (localPort <= 0) continue;
                    Process? process = null;
                    try
                    {
                        process = StartForward(localPort, host, remotePort);
                        if (process == null ||
                            !await WaitForListenerAsync(process, localPort, cancellationToken)
                                .ConfigureAwait(false))
                        {
                            StopProcess(process);
                            continue;
                        }

                        _process = process;
                        _localPort = localPort;
                        _remoteHost = host;
                        _remotePort = remotePort;
                        return Rewrite(target, localPort);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        StopProcess(process);
                    }
                    catch
                    {
                        StopProcess(process);
                    }
                }

                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool IsUsableDevice()
            => RemoteDeviceConnection.IsSafeUser(_device.User) &&
               RemoteDeviceConnection.IsSafeHost(_device.Host) &&
               _device.Port is >= 1 and <= 65535;

        private static bool IsUsableTarget(Uri target)
            => target.IsAbsoluteUri &&
               (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps) &&
               target.Port is >= 1 and <= 65535 &&
               string.IsNullOrEmpty(target.UserInfo) &&
               IsSafeForwardHost(target.Host);

        private bool IsAliveFor(string remoteHost, int remotePort)
            => _process is { HasExited: false } &&
               _localPort > 0 &&
               _remotePort == remotePort &&
               string.Equals(_remoteHost, remoteHost, StringComparison.OrdinalIgnoreCase);

        private Process? StartForward(int localPort, string remoteHost, int remotePort)
        {
            string key = Environment.ExpandEnvironmentVariables(_device.KeyPath ?? "");
            var psi = new ProcessStartInfo
            {
                FileName = CliMode.ResolveSsh(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-N");
            psi.ArgumentList.Add("-T");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(_device.Port.ToString());
            if (!string.IsNullOrWhiteSpace(key))
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(key);
            }
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ConnectTimeout=5");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ExitOnForwardFailure=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ServerAliveInterval=30");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ServerAliveCountMax=2");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add($"127.0.0.1:{localPort}:{remoteHost}:{remotePort}");
            psi.ArgumentList.Add($"{_device.User}@{_device.Host}");

            var process = Process.Start(psi);
            if (process == null) return null;
            // Drain both pipes for the whole tunnel lifetime.  OpenSSH can write
            // a warning during reconnect; an undrained pipe would eventually
            // block the forward and look like a random browser failure.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process;
        }

        private static async Task<bool> WaitForListenerAsync(
            Process process, int localPort, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (process.HasExited) return false;
                using var client = new TcpClient();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(250);
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, localPort, attemptCts.Token)
                        .ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (SocketException) { }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        private static int GetFreePort()
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch { return 0; }
        }

        private static Uri Rewrite(Uri target, int localPort)
        {
            var builder = new UriBuilder(target)
            {
                Host = "127.0.0.1",
                Port = localPort,
            };
            return builder.Uri;
        }

        private static bool IsSafeForwardHost(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']' or '%'))
                    return false;
            }
            return true;
        }

        private void StopProcess()
        {
            var process = _process;
            _process = null;
            _localPort = 0;
            _remoteHost = null;
            _remotePort = 0;
            StopProcess(process);
        }

        private static void StopProcess(Process? process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch { }
            finally { process.Dispose(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopProcess();
            _gate.Dispose();
        }
    }
}
