using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace CCPad.Web
{
    internal class WebTerminalServer : IDisposable
    {
        private WebApplication? _app;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, WebTerminalSession> _clients = new();

        public string? Token { get; private set; }
        public int Port { get; private set; }
        public bool IsRunning => _app != null;
        public bool UseToken { get; set; } = false;
        public string Host { get; set; } = "localhost";
        public int MaxPortRetries { get; set; } = 10;

        public event Action<string>? StatusChanged;

        public static bool IsPortAvailable(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<(bool Success, int ActualPort, string? Error)> StartAsync(int port = 9220, bool autoIncrement = true)
        {
            if (_app != null) return (true, Port, null);

            // Find available port
            int actualPort = port;
            int attempts = 0;
            while (!IsPortAvailable(actualPort))
            {
                if (!autoIncrement || attempts >= MaxPortRetries)
                {
                    return (false, port, Localization.Loc.T("port_in_use", port)
                        + (autoIncrement ? Localization.Loc.T("port_retry_failed", MaxPortRetries) : ""));
                }
                actualPort++;
                attempts++;
            }

            Token = UseToken ? GenerateToken() : null;
            _cts = new CancellationTokenSource();

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(opts =>
            {
                opts.Listen(IPAddress.Any, actualPort);
            });
            builder.Logging.ClearProviders();

            _app = builder.Build();
            _app.UseWebSockets();

            // Serve xterm static files
            var xtermPath = GetXtermFolder();
            if (Directory.Exists(xtermPath))
            {
                _app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(xtermPath),
                    RequestPath = "/xterm"
                });
            }

            // Terminal HTML page
            _app.MapGet("/", (HttpContext ctx) =>
            {
                if (!CheckToken(ctx)) return Results.Text("Unauthorized", "text/plain", statusCode: 401);
                return Results.Text(WebTerminalHtml.GetHtml(Token ?? ""), "text/html");
            });

            // API: list active sessions (uses JsonArray to avoid trimming issues with anonymous types)
            _app.MapGet("/api/sessions", (HttpContext ctx) =>
            {
                if (!CheckToken(ctx))
                    return Results.Text("{\"error\":\"Unauthorized\"}", "application/json", statusCode: 401);

                var all = TerminalSessionRegistry.GetAll();
                var nodes = new JsonNode?[all.Count];
                for (int i = 0; i < all.Count; i++)
                {
                    var s = all[i];
                    nodes[i] = new JsonObject
                    {
                        ["id"] = s.Id,
                        ["label"] = s.Label,
                        ["command"] = s.Command,
                        ["workingDir"] = s.WorkingDir,
                        ["cols"] = s.Cols,
                        ["rows"] = s.Rows
                    };
                }
                return Results.Text(new JsonArray(nodes).ToJsonString(), "application/json");
            });

            // WebSocket endpoint — mirrors existing sessions
            _app.Map("/ws", async (HttpContext ctx) =>
            {
                if (!CheckToken(ctx))
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }

                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    return;
                }

                var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(30)
                });
                var client = new WebTerminalSession();
                var clientId = Guid.NewGuid().ToString("N")[..8];
                _clients[clientId] = client;

                try
                {
                    await client.RunAsync(ws, _cts!.Token);
                }
                finally
                {
                    _clients.TryRemove(clientId, out _);
                    client.Dispose();
                }
            });

            await _app.StartAsync(_cts.Token);

            // Resolve actual port
            var server = _app.Services.GetService<IServer>();
            var addressFeature = server?.Features.Get<IServerAddressesFeature>();
            if (addressFeature != null)
            {
                foreach (var address in addressFeature.Addresses)
                {
                    if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    {
                        Port = uri.Port;
                        break;
                    }
                }
            }

            StatusChanged?.Invoke($"Server started on port {Port}");
            return (true, Port, null);
        }

        public async Task StopAsync()
        {
            if (_app == null) return;

            foreach (var client in _clients.Values)
                client.Dispose();
            _clients.Clear();

            _cts?.Cancel();
            try { await _app.StopAsync(); } catch { }
            await _app.DisposeAsync();
            _app = null;
            _cts?.Dispose();
            _cts = null;
            StatusChanged?.Invoke("Server stopped");
        }

        public string GetAccessUrl()
        {
            var tokenParam = Token != null ? $"?token={Token}" : "";
            return $"http://{Host}:{Port}/{tokenParam}";
        }

        public int ClientCount => _clients.Count;

        private bool CheckToken(HttpContext ctx)
        {
            if (Token == null) return true; // no auth
            return ctx.Request.Query["token"].ToString() == Token;
        }

        public static List<string> GetLanAddresses()
        {
            var addresses = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(addr.Address))
                        {
                            addresses.Add(addr.Address.ToString());
                        }
                    }
                }
            }
            catch { }
            addresses.Reverse();
            return addresses;
        }

        private static string GetXtermFolder()
        {
            try
            {
                return Path.Combine(
                    Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                    "Assets", "xterm");
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "Assets", "xterm");
            }
        }

        private static string GenerateToken()
        {
            var bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public void Dispose()
        {
            StopAsync().Wait(3000);
        }
    }
}
