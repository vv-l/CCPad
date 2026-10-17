using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CCPad.CodexQuota;
using CCPad.Localization;
using CCPad.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CCPad
{
    public sealed partial class MainWindow
    {
        private sealed class AdditionalCodexQuota
        {
            public CodexQuotaProfile Profile { get; }
            public CodexQuotaService Service { get; }
            public CodexQuotaSnapshot Snapshot { get; set; } = CodexQuotaSnapshot.Loading();
            public bool Refreshing { get; set; }
            public AdditionalCodexQuota(CodexQuotaProfile profile)
            {
                Profile = profile;
                Service = new CodexQuotaService(profile.Home);
            }
        }

        private readonly CodexQuotaProfileStore _quotaProfileStore = new();
        private readonly Dictionary<string, AdditionalCodexQuota> _additionalCodexQuotas = new();
        private string _selectedCodexQuotaId = CodexQuotaProfileStore.DefaultId;
        private bool _syncingQuotaAccountSelector;
        private bool _quotaAccountDialogOpen;
        private QuotaOverviewWindow? _quotaOverviewWindow;
        private FileSystemWatcher? _quotaAccountsWatcher;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _quotaAccountsDebounce;

        private string DefaultCodexQuotaName =>
            CodexAccountIdentity.Read(_quotaProfileStore.DefaultHome)?.Email ?? Loc.T("quota_account_default");

        private void StartQuotaAccountWatcher()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                _quotaAccountsDebounce = DispatcherQueue.CreateTimer();
                _quotaAccountsDebounce.Interval = TimeSpan.FromMilliseconds(250);
                _quotaAccountsDebounce.IsRepeating = false;
                _quotaAccountsDebounce.Tick += (_, _) =>
                {
                    if (_quotaRefreshCts.IsCancellationRequested) return;
                    SynchronizeCodexQuotaAccounts();
                    UpdateQuotaUi();
                    foreach (var entry in _additionalCodexQuotas.Values)
                        if (entry.Snapshot.State == CodexQuotaState.Loading)
                            _ = RefreshAdditionalCodexQuotaAsync(entry);
                };
                _quotaAccountsWatcher = new FileSystemWatcher(AppPaths.Root, "quota-accounts.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };
                void Changed(object sender, FileSystemEventArgs args) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_quotaRefreshCts.IsCancellationRequested) return;
                    _quotaAccountsDebounce.Stop();
                    _quotaAccountsDebounce.Start();
                });
                _quotaAccountsWatcher.Created += Changed;
                _quotaAccountsWatcher.Changed += Changed;
                _quotaAccountsWatcher.Deleted += Changed;
                _quotaAccountsWatcher.Renamed += (sender, args) => Changed(sender, args);
                _quotaAccountsWatcher.EnableRaisingEvents = true;
            }
            catch
            {
                _quotaAccountsWatcher?.Dispose();
                _quotaAccountsWatcher = null;
                App.LogDiag("quota-accounts watcher unavailable; periodic refresh remains active");
            }
        }

        private void OnQuotaOverviewClick(object sender, RoutedEventArgs e)
        {
            QuotaFlyout.Hide();
            if (_quotaOverviewWindow is null)
            {
                _quotaOverviewWindow = new QuotaOverviewWindow();
                _quotaOverviewWindow.Closed += (_, _) => _quotaOverviewWindow = null;
            }
            _quotaOverviewWindow.Activate();
        }
        private bool IsDefaultCodexQuotaSelected => _selectedCodexQuotaId == CodexQuotaProfileStore.DefaultId;
        private CodexQuotaSnapshot SelectedCodexQuotaSnapshot =>
            _additionalCodexQuotas.TryGetValue(_selectedCodexQuotaId, out var entry)
                ? entry.Snapshot : _codexQuotaSnapshot;
        private string SelectedCodexQuotaName =>
            _additionalCodexQuotas.TryGetValue(_selectedCodexQuotaId, out var entry)
                ? entry.Profile.Name : DefaultCodexQuotaName;

        /// <summary>Display the account that new local Codex panes will use.
        /// This is intentionally separate from the quota-details selector: the
        /// latter is read-only display state and must not switch a running CLI.</summary>
        private string CurrentCodexLaunchAccountName()
        {
            var current = DefaultCodexQuotaName;
            try
            {
                var pending = new CodexAccountSwitchService().ReadPending();
                if (pending != null && _quotaProfileStore.TryResolveLaunchProfile(pending.ProfileId, out var profile))
                    return Loc.T("account_switch_pending", current, profile.Name);
            }
            catch { }
            return current;
        }

        private void SynchronizeCodexQuotaAccounts()
        {
            try
            {
                var profiles = _quotaProfileStore.LoadInactive();
                foreach (var id in _additionalCodexQuotas.Keys.ToArray())
                {
                    var current = _additionalCodexQuotas[id];
                    if (profiles.Any(p => p == current.Profile)) continue;
                    current.Service.Dispose();
                    _additionalCodexQuotas.Remove(id);
                }
                foreach (var profile in profiles)
                    if (!_additionalCodexQuotas.ContainsKey(profile.Id))
                        _additionalCodexQuotas.Add(profile.Id, new AdditionalCodexQuota(profile));
                if (!IsDefaultCodexQuotaSelected && !_additionalCodexQuotas.ContainsKey(_selectedCodexQuotaId))
                    _selectedCodexQuotaId = CodexQuotaProfileStore.DefaultId;
            }
            catch
            {
                // Keep the last good list if another process or an invalid config prevents a read.
            }
            RebuildQuotaAccountSelector();
            UpdateQuotaToolbar();
        }

        private void RebuildQuotaAccountSelector()
        {
            if (QuotaAccountSelector is null) return;
            _syncingQuotaAccountSelector = true;
            try
            {
                QuotaAccountSelector.Items.Clear();
                QuotaAccountSelector.Items.Add(new ComboBoxItem
                {
                    Content = DefaultCodexQuotaName == Loc.T("quota_account_default") ? DefaultCodexQuotaName :
                        DefaultCodexQuotaName + " · " + Loc.T("quota_account_default"), Tag = CodexQuotaProfileStore.DefaultId,
                });
                foreach (var entry in _additionalCodexQuotas.Values)
                    QuotaAccountSelector.Items.Add(new ComboBoxItem { Content = entry.Profile.Name, Tag = entry.Profile.Id });
                QuotaAccountSelector.SelectedItem = QuotaAccountSelector.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => (string)item.Tag == _selectedCodexQuotaId);
            }
            finally { _syncingQuotaAccountSelector = false; }
            UpdateQuotaAccountControls();
        }

        private void UpdateQuotaAccountControls()
        {
            if (QuotaAccountPanel is null) return;
            QuotaAccountPanel.Visibility = _quotaProvider == QuotaProvider.Codex ? Visibility.Visible : Visibility.Collapsed;
            QuotaAccountSelector.Header = Loc.T("quota_account");
            QuotaAccountAddButton.Content = Loc.T("quota_account_add");
            QuotaAccountRemoveButton.Content = Loc.T("quota_account_remove");
            QuotaAccountRemoveButton.IsEnabled = !IsDefaultCodexQuotaSelected && !_codexResetInFlight;
            QuotaAccountSelector.IsEnabled = !_codexResetInFlight;
            QuotaAccountAddButton.IsEnabled = !_codexResetInFlight;
            QuotaAccountNote.Text = Loc.T("quota_account_note");
            QuotaAccountKind.Text = IsDefaultCodexQuotaSelected
                ? Loc.T("quota_account_default_kind") : Loc.T("quota_account_extra_kind_launchable");
            if (QuotaAccountUsage is not null)
                QuotaAccountUsage.Text = AccountUsageDisplay.Format(
                    new AccountUsageRegistry(_quotaProfileStore).GetAccountUsage(_selectedCodexQuotaId),
                    Loc.T("account_usage_template"), Loc.T("account_usage_unknown"),
                    Loc.T("account_usage_empty"), Loc.T("account_usage_unavailable"));
            QuotaAccountPath.Text = _additionalCodexQuotas.TryGetValue(_selectedCodexQuotaId, out var entry)
                ? entry.Profile.Home : _quotaProfileStore.DefaultHome;
        }

        private void OnQuotaAccountChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingQuotaAccountSelector || !_initialized || _codexResetInFlight ||
                QuotaAccountSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string id)
                return;
            _selectedCodexQuotaId = id;
            UpdateQuotaUi();
            _ = RefreshSelectedQuotaAsync();
        }

        private async Task RefreshAdditionalCodexQuotaAsync(AdditionalCodexQuota entry)
        {
            if (entry.Refreshing || _quotaRefreshCts.IsCancellationRequested) return;
            entry.Refreshing = true;
            try
            {
                var snapshot = await entry.Service.RefreshAsync(_quotaRefreshCts.Token);
                if (!_quotaRefreshCts.IsCancellationRequested && snapshot.State != CodexQuotaState.Loading)
                {
                    entry.Snapshot = snapshot;
                    // Store by profile, then render the CURRENT selection: late results cannot switch accounts.
                    UpdateQuotaUi();
                }
            }
            catch
            {
                if (!_quotaRefreshCts.IsCancellationRequested)
                {
                    entry.Snapshot = CodexQuotaSnapshot.ErrorSnapshot(Loc.T("quota_unavailable"));
                    UpdateQuotaUi();
                }
            }
            finally { entry.Refreshing = false; }
        }

        private async void OnQuotaAccountAddClick(object sender, RoutedEventArgs e)
        {
            if (_quotaAccountDialogOpen || _codexResetInFlight || Content?.XamlRoot is null) return;
            _quotaAccountDialogOpen = true;
            QuotaFlyout.Hide();
            try
            {
                var added = await CodexQuotaAccountDialog.ShowAsync(this, _quotaProfileStore);
                if (added is null || _quotaRefreshCts.IsCancellationRequested) return;
                SynchronizeCodexQuotaAccounts();
                _selectedCodexQuotaId = added.Id;
                RebuildQuotaAccountSelector();
                ShowQuotaDetails(QuotaProvider.Codex, openFlyout: true);
            }
            catch
            {
                if (!_quotaRefreshCts.IsCancellationRequested)
                    await ShowQuotaAccountMessageAsync(Loc.T("quota_account_storage_error"));
            }
            finally { _quotaAccountDialogOpen = false; }
        }

        private async void OnQuotaAccountRemoveClick(object sender, RoutedEventArgs e)
        {
            if (IsDefaultCodexQuotaSelected || _codexResetInFlight) return;
            if (!_quotaProfileStore.TryRemove(_selectedCodexQuotaId))
            {
                QuotaFlyout.Hide();
                await ShowQuotaAccountMessageAsync(Loc.T("quota_account_storage_error"));
                return;
            }
            SynchronizeCodexQuotaAccounts();
            UpdateQuotaUi();
        }

        private async Task ShowQuotaAccountMessageAsync(string message)
        {
            if (_quotaRefreshCts.IsCancellationRequested || Content?.XamlRoot is null) return;
            try
            {
                await new ContentDialog
                {
                    Title = Loc.T("quota_account"), Content = message,
                    CloseButtonText = Loc.T("close"), XamlRoot = Content.XamlRoot,
                }.ShowAsync();
            }
            catch { }
        }
    }
}
