using System;
using System.Threading.Tasks;
using CCPad.Web;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CCPad.Localization;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CCPad
{
    public sealed partial class MainWindow
    {
        private FeishuOwnedSidecar _ownedFeishu = new();
        private string? _connectedFeishuBot;
        private TerminalPane? _feishuInputPane;
        private WebTerminalServer? _feishuWebServer;
        private bool _feishuClosing;
        private FeishuBindingDescriptor? _bindingDescriptor;
        private long _bindingGeneration;
        private readonly CancellationTokenSource _bindingClosing = new();

        internal async Task StartOwnedFeishuSidecarAsync(FeishuStartRequest request)
        {
            if (_feishuWebServer != null && !_ownedFeishu.IsActive)
            {
                DisposeOwnedWebServer(_feishuWebServer);
                _feishuWebServer = null;
            }
            var pane = _splitHost?.ActiveTerminal;
            var entry = TerminalSessionRegistry.Get(request.PaneId);
            if (_feishuClosing || _feishuWebServer != null || pane?.PaneId != request.PaneId || entry == null ||
                FeishuSessionBinding.IsReservedApp(request.AppId))
                throw new InvalidOperationException("Explicit active pane unavailable or already owned");
            var binding = new FeishuSessionBinding(pane.CliMode ?? "", pane.CodexHome ?? "",
                pane.SessionId ?? "", pane.SessionProvenance);
            binding.Validate();
            if (entry.CliSessionId != pane.SessionId || entry.SessionProvenance != pane.SessionProvenance)
                throw new InvalidOperationException("Session metadata not confirmed");
            request = request with { Binding = binding };
            pane.AcquireFeishuInput();
            _feishuInputPane = pane;
            var server = new WebTerminalServer
            {
                UseToken = true, BindLoopbackOnly = true, Host = "127.0.0.1"
            };
            _feishuWebServer = server;
            try
            {
                var result = await server.StartAsync(9220, autoIncrement: true);
                if (!result.Success || _feishuClosing || string.IsNullOrWhiteSpace(server.Token))
                    throw new InvalidOperationException("Owned loopback startup unavailable");
                await _ownedFeishu.StartAsync(request, $"http://127.0.0.1:{result.ActualPort}", server.Token,
                    () => !_feishuClosing && ReferenceEquals(TerminalSessionRegistry.Get(request.PaneId), entry) &&
                        binding.Matches(pane.CliMode, pane.CodexHome, pane.SessionId, pane.SessionProvenance));
                _ = ReleaseDesktopAfterSidecarAsync(_ownedFeishu, pane, server);
            }
            catch
            {
                pane.FeishuInputOwned = false;
                _feishuInputPane = null;
                DisposeOwnedWebServer(server);
                if (ReferenceEquals(_feishuWebServer, server)) _feishuWebServer = null;
                throw;
            }
        }

        private async Task ShowFeishuBindingAsync()
        {
            if (_ownedFeishu.IsActive)
            {
                var connected = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot, Title = Loc.T("feishu_bind_menu"),
                    Content = string.Format(Loc.T("feishu_connected"), _connectedFeishuBot),
                    PrimaryButtonText = Loc.T("feishu_return_desktop"),
                    CloseButtonText = Loc.T("cancel")
                };
                if (await connected.ShowAsync() == ContentDialogResult.Primary)
                {
                    _ownedFeishu.Close();
                    if (_feishuInputPane != null) _feishuInputPane.FeishuInputOwned = false;
                    _feishuInputPane = null;
                    DisposeOwnedWebServer(_feishuWebServer);
                    _feishuWebServer = null;
                    _ownedFeishu = new();
                    _connectedFeishuBot = null;
                    await new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot, Title = Loc.T("feishu_return_desktop"),
                        Content = Loc.T("feishu_desktop_restored"), CloseButtonText = Loc.T("ok")
                    }.ShowAsync();
                }
                return;
            }
            var pane = _splitHost?.ActiveTerminal;
            if (pane == null) return;
            try
            {
                var catalog = FeishuDemoCatalog.Read(Settings.AppPaths.Sub("feishu-demo-bots.json"));
                var bots = new ComboBox { Header = Loc.T("feishu_select_bot"), MinWidth = 320 };
                foreach (var bot in catalog.Bots) bots.Items.Add(bot.Name);
                bots.SelectedIndex = 0;
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(bots);
                content.Children.Add(new TextBlock { Text = Loc.T("feishu_connect_help"),
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot, Title = Loc.T("feishu_bind_menu"), Content = content,
                    PrimaryButtonText = Loc.T("feishu_bind_connect"), CloseButtonText = Loc.T("cancel"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || _feishuClosing) return;
                var selected = catalog.Bots[bots.SelectedIndex];
                if (string.IsNullOrWhiteSpace(selected.ChatId))
                {
                    var chat = await PairDemoBotAsync(catalog, selected);
                    if (chat == null) return;
                    var index = bots.SelectedIndex;
                    selected = selected with { ChatId = chat };
                    catalog.Bots[index] = selected;
                }
                var request = catalog.Request(selected, pane.PaneId, Settings.AppPaths.Sub("feishu-demo-state"));
                await StartOwnedFeishuSidecarAsync(request);
                _connectedFeishuBot = selected.Name;
                await new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot, Title = Loc.T("feishu_bind_menu"),
                    Content = string.Format(Loc.T("feishu_connected"), selected.Name),
                    CloseButtonText = Loc.T("ok")
                }.ShowAsync();
            }
            catch (Exception error)
            {
                App.LogStartupError("FeishuAutoBotBinding", new InvalidOperationException(error.GetType().Name));
                await new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot, Title = Loc.T("feishu_bind_menu"),
                    Content = Loc.T("feishu_bind_failed"),
                    CloseButtonText = Loc.T("cancel")
                }.ShowAsync();
            }
        }

        private async Task ReleaseDesktopAfterSidecarAsync(FeishuOwnedSidecar owner, TerminalPane pane, WebTerminalServer server)
        {
            while (owner.IsActive && !_feishuClosing) await Task.Delay(250);
            if (ReferenceEquals(owner, _ownedFeishu))
            {
                pane.FeishuInputOwned = false;
                _feishuInputPane = null;
                DisposeOwnedWebServer(server);
                if (ReferenceEquals(_feishuWebServer, server)) _feishuWebServer = null;
            }
        }

        private async Task<string?> PairDemoBotAsync(FeishuDemoCatalog catalog, FeishuDemoBot bot)
        {
            if (!Path.IsPathFullyQualified(bot.CredentialsFile) || !File.Exists(bot.CredentialsFile))
                throw new InvalidOperationException("Demo credential reference unavailable");
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(bot.AppId))).ToLowerInvariant();
            var result = Settings.AppPaths.Sub(Path.Combine("feishu-pairing", key + ".json"));
            string ReadChat()
            {
                using var record = JsonDocument.Parse(File.ReadAllText(result));
                if (record.RootElement.GetProperty("app_id").GetString() != bot.AppId)
                    throw new InvalidOperationException("Pairing identity mismatch");
                var chat = record.RootElement.GetProperty("chat_id").GetString();
                return !string.IsNullOrWhiteSpace(chat) ? chat : throw new InvalidOperationException("Pairing chat missing");
            }
            if (File.Exists(result)) return ReadChat();
            var code = "CCPAD-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            var helper = Path.Combine(Path.GetDirectoryName(catalog.ScriptFile)!, "pair_feishu_demo.py");
            var info = new ProcessStartInfo(catalog.PythonExe)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(helper)!
            };
            foreach (var item in new[] { "-B", helper, "--credentials", bot.CredentialsFile,
                "--expected-app-id", bot.AppId, "--code", code, "--result", result }) info.ArgumentList.Add(item);
            info.Environment.Remove("CCPAD_FEISHU_APP_ID"); info.Environment.Remove("CCPAD_FEISHU_APP_SECRET");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Pairing process unavailable");
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = string.Format(Loc.T("feishu_pair_help"), bot.Name),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
            content.Children.Add(new TextBox { Text = code, IsReadOnly = true });
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = Loc.T("feishu_pair_title"),
                Content = content, CloseButtonText = Loc.T("cancel") };
            bool dismissed = false;
            async Task Watch()
            {
                while (!dismissed && !process.HasExited && !_feishuClosing) await Task.Delay(100);
                if (!dismissed) dialog.Hide();
            }
            try
            {
                var shown = dialog.ShowAsync();
                var watch = Watch();
                await shown; dismissed = true; await watch;
                if (process.HasExited && process.ExitCode == 0 && File.Exists(result)) return ReadChat();
                if (process.HasExited && process.ExitCode != 0)
                    throw new InvalidOperationException("Demo pairing incomplete");
                return null;
            }
            finally
            {
                dismissed = true;
                if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); }
            }
        }

        private async Task PublishAutoBotBindingAsync(FeishuBridgeRequest request, string paneId)
        {
            var pane = _splitHost?.ActiveTerminal;
            var entry = TerminalSessionRegistry.Get(paneId);
            if (_feishuClosing || _feishuWebServer != null || pane?.PaneId != paneId || entry == null)
                throw new InvalidOperationException("Explicit active pane unavailable or already bound");
            var binding = new FeishuSessionBinding(pane.CliMode ?? "", pane.CodexHome ?? "",
                pane.SessionId ?? "", pane.SessionProvenance);
            binding.Validate();
            var descriptor = new FeishuBindingDescriptor(request.StateDir, _ownedFeishu.WindowId, ++_bindingGeneration,
                Settings.AppPaths.Sub("feishu-autobot"));
            var server = new WebTerminalServer { UseToken = true, BindLoopbackOnly = true, Host = "127.0.0.1" };
            _feishuWebServer = server;
            _bindingDescriptor = descriptor;
            bool StillValid() => !_feishuClosing && ReferenceEquals(TerminalSessionRegistry.Get(paneId), entry) &&
                binding.Matches(pane.CliMode, pane.CodexHome, pane.SessionId, pane.SessionProvenance);
            try
            {
                var result = await server.StartAsync(9220, autoIncrement: true);
                if (!result.Success || !StillValid() || string.IsNullOrEmpty(server.Token))
                    throw new InvalidOperationException("Owned loopback unavailable");
                descriptor.Publish(request, paneId, binding, $"http://127.0.0.1:{result.ActualPort}", server.Token);
                _ = MonitorAutoBotBindingAsync(descriptor, server, StillValid);
            }
            catch
            {
                descriptor.Dispose(); DisposeOwnedWebServer(server);
                if (ReferenceEquals(_feishuWebServer, server)) _feishuWebServer = null;
                if (ReferenceEquals(_bindingDescriptor, descriptor)) _bindingDescriptor = null;
                throw;
            }
        }

        private async Task MonitorAutoBotBindingAsync(FeishuBindingDescriptor descriptor, WebTerminalServer server,
            Func<bool> stillValid)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(250, _bindingClosing.Token);
                    if (!stillValid())
                    {
                        descriptor.Invalidate("session_identity_changed");
                        descriptor.Dispose(); DisposeOwnedWebServer(server);
                        if (ReferenceEquals(_feishuWebServer, server)) _feishuWebServer = null;
                        if (ReferenceEquals(_bindingDescriptor, descriptor)) _bindingDescriptor = null;
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                App.LogStartupError("FeishuBindingMonitor", new InvalidOperationException(error.GetType().Name));
                try { descriptor.Dispose(); }
                finally { DisposeOwnedWebServer(server); }
                if (ReferenceEquals(_feishuWebServer, server)) _feishuWebServer = null;
                if (ReferenceEquals(_bindingDescriptor, descriptor)) _bindingDescriptor = null;
            }
        }

        private void StopOwnedFeishuSidecar()
        {
            _feishuClosing = true;
            if (_feishuInputPane != null) _feishuInputPane.FeishuInputOwned = false;
            _feishuInputPane = null;
            _bindingClosing.Cancel();
            try { _ownedFeishu.Close(); _bindingDescriptor?.Dispose(); _bindingDescriptor = null; }
            finally { DisposeOwnedWebServer(_feishuWebServer); _feishuWebServer = null; }
        }

        private static void DisposeOwnedWebServer(WebTerminalServer? server)
        {
            try { server?.Dispose(); }
            catch (Exception error)
            {
                App.LogStartupError("FeishuOwnedServerClose", new InvalidOperationException(error.GetType().Name));
            }
        }
    }
}
