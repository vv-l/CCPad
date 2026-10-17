using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CCPad.CodexQuota;
using CCPad.Localization;
using CCPad.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CCPad
{
    public sealed partial class QuotaOverviewWindow : Window
    {
        private sealed class QuotaCard
        {
            public CodexQuotaProfile Profile { get; }
            public CodexQuotaSnapshot Snapshot { get; set; } = CodexQuotaSnapshot.Loading();
            public CodexAccountIdentity? Identity { get; set; }
            public CodexSubscriptionNote Subscription { get; set; } = new();
            public bool SubscriptionReadable { get; set; } = true;
            public Border Border { get; }
            public StackPanel Details { get; } = new() { Spacing = 9 };
            public QuotaCard(CodexQuotaProfile profile, Style style)
            {
                Profile = profile;
                Border = new Border { Style = style, Child = Details, VerticalAlignment = VerticalAlignment.Top };
            }
        }

        private readonly CodexQuotaProfileStore _store = new();
        private readonly CodexSubscriptionStore _subscriptions = new();
        private readonly CodexQuotaOverviewService _service = new();
        private readonly List<QuotaCard> _cards = new();
        private readonly CancellationTokenSource _closedCts = new();
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _clockTimer;
        private bool _closed;
        private bool _refreshing;
        private bool _adding;
        private bool _editingSubscription;
        private int _refreshEpoch;
        private int _columns;

        public QuotaOverviewWindow()
        {
            App.LogDiag("quota-overview initialize xaml");
            InitializeComponent();
            App.LogDiag("quota-overview xaml initialized");
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            OverviewRoot.RequestedTheme = ThemeManager.ToElementTheme(ThemeManager.Pref);
            AppWindow.Resize(new SizeInt32(1040, 650));
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "claude.ico"));
            Localize();
            OverviewRoot.SizeChanged += (_, _) => LayoutCards();
            OverviewRoot.Loaded += OnLoaded;
            Loc.LanguageChanged += OnLanguageChanged;
            ThemeManager.PrefChanged += OnThemeChanged;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            OverviewRoot.Loaded -= OnLoaded;
            var scale = OverviewRoot.XamlRoot?.RasterizationScale ?? 1;
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min(area.Width - 40, (int)(1040 * scale));
            var height = Math.Min(area.Height - 60, (int)(650 * scale));
            AppWindow.Resize(new SizeInt32(Math.Max(320, width), Math.Max(320, height)));
            _refreshTimer = DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMinutes(5);
            _refreshTimer.IsRepeating = true;
            _refreshTimer.Tick += (_, _) => _ = RefreshAllAsync();
            _refreshTimer.Start();
            _clockTimer = DispatcherQueue.CreateTimer();
            _clockTimer.Interval = TimeSpan.FromMinutes(1);
            _clockTimer.IsRepeating = true;
            _clockTimer.Tick += (_, _) =>
            {
                if (_editingSubscription) return;
                var identityChanged = false;
                foreach (var card in _cards) identityChanged |= RefreshCardMetadata(card);
                RenderCards();
                if (identityChanged) _ = RefreshAllAsync();
            };
            _clockTimer.Start();
            _ = RefreshAllAsync();
        }

        private void Localize()
        {
            Title = Loc.T("quota_overview_title");
            OverviewTitle.Text = Title;
            OverviewSubtitle.Text = Loc.T("quota_overview_help");
            OverviewRefreshButton.Content = Loc.T("quota_overview_refresh");
            OverviewAddButton.Content = Loc.T("quota_account_add");
            RenderCards();
        }

        private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(() => { if (!_closed) Localize(); });
        private void OnThemeChanged() => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) OverviewRoot.RequestedTheme = ThemeManager.ToElementTheme(ThemeManager.Pref);
        });

        private void LayoutCards(bool force = false)
        {
            var columns = Math.Min(Math.Max(1, _cards.Count),
                Math.Clamp((int)((Math.Max(0, OverviewRoot.ActualWidth - 40) + 14) / 310), 1, 3));
            if (!force && columns == _columns) return;
            _columns = columns;
            OverviewCards.Children.Clear();
            OverviewCards.ColumnDefinitions.Clear();
            OverviewCards.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) OverviewCards.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < (_cards.Count + columns - 1) / columns; i++)
                OverviewCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < _cards.Count; i++)
            {
                Grid.SetColumn(_cards[i].Border, i % columns);
                Grid.SetRow(_cards[i].Border, i / columns);
                OverviewCards.Children.Add(_cards[i].Border);
            }
        }

        private async Task RefreshAllAsync()
        {
            if (_closed || _refreshing || _adding || _editingSubscription) return;
            _refreshing = true;
            var epoch = ++_refreshEpoch;
            OverviewRefreshButton.IsEnabled = OverviewAddButton.IsEnabled = false;
            OverviewStatus.Text = Loc.T("quota_loading");
            try
            {
                var profiles = new List<CodexQuotaProfile>
                {
                    new(CodexQuotaProfileStore.DefaultId, "Default", _store.DefaultHome),
                };
                profiles.AddRange(_store.LoadInactive());
                var old = _cards.ToDictionary(card => card.Profile.Id);
                _cards.Clear();
                foreach (var profile in profiles)
                {
                    if (old.TryGetValue(profile.Id, out var card) && card.Profile == profile)
                        _cards.Add(card);
                    else
                        _cards.Add(new QuotaCard(profile, (Style)OverviewRoot.Resources["QuotaOverviewCardStyle"]));
                }
                foreach (var card in _cards) RefreshCardMetadata(card);
                var requestedIdentities = _cards.ToDictionary(card => card.Profile.Id, card => card.Identity?.Key);
                RenderCards();
                LayoutCards(force: true);
                void Apply(CodexQuotaOverviewEntry entry)
                {
                    if (_closed || epoch != _refreshEpoch) return;
                    var card = _cards.FirstOrDefault(card => card.Profile.Id == entry.Profile.Id);
                    if (card is null) return;
                    if (CodexAccountIdentity.Read(card.Profile.Home)?.Key != requestedIdentities[entry.Profile.Id])
                    {
                        RefreshCardMetadata(card);
                        RenderCard(card);
                        return;
                    }
                    card.Snapshot = entry.Snapshot;
                    RenderCard(card);
                }
                var entries = await _service.RefreshAsync(profiles, new Progress<CodexQuotaOverviewEntry>(Apply), _closedCts.Token);
                if (_closed) return;
                foreach (var entry in entries) Apply(entry);
                var ready = entries.Count(entry => entry.Snapshot.State == CodexQuotaState.Ready);
                OverviewStatus.Text = Loc.T("quota_overview_status", ready, entries.Length);
                App.LogDiag($"quota-overview refresh accounts={entries.Length} ready={ready} failed={entries.Length - ready} cards={OverviewCards.Children.Count}");
            }
            catch
            {
                if (!_closed) OverviewStatus.Text = Loc.T("quota_account_storage_error");
            }
            finally
            {
                _refreshing = false;
                if (!_closed) OverviewRefreshButton.IsEnabled = OverviewAddButton.IsEnabled = !_editingSubscription;
            }
        }

        private void RenderCards()
        {
            foreach (var card in _cards) RenderCard(card);
            if (!_refreshing && _cards.Count > 0)
                OverviewStatus.Text = Loc.T("quota_overview_status", _cards.Count(card => card.Snapshot.State == CodexQuotaState.Ready), _cards.Count);
        }

        private void RenderCard(QuotaCard card)
        {
            var snapshot = card.Snapshot;
            var panel = card.Details;
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = CardName(card),
                FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = (card.Profile.Id == CodexQuotaProfileStore.DefaultId ? Loc.T("quota_account_default") + " · " : "") +
                    Loc.T("quota_overview_readonly"), FontSize = 11, Opacity = 0.65,
            });
            if (!string.IsNullOrWhiteSpace(snapshot.PlanType))
                panel.Children.Add(new TextBlock { Text = Loc.T("quota_plan", snapshot.PlanType!), FontSize = 12, Opacity = 0.75 });
            if (snapshot.State == CodexQuotaState.Ready)
            {
                foreach (var window in snapshot.Windows)
                {
                    var remaining = Math.Clamp(100 - window.UsedPercent, 0, 100);
                    panel.Children.Add(new TextBlock
                    {
                        Text = window.Label == "Weekly" ? Loc.T("quota_weekly") :
                            window.Label == "Session" ? Loc.T("quota_session") : window.Label,
                        FontSize = 12,
                    });
                    panel.Children.Add(new TextBlock { Text = Loc.T("quota_overview_remaining", remaining), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    panel.Children.Add(new ProgressBar
                    {
                        Minimum = 0, Maximum = 100, Value = remaining, Height = 7,
                        Foreground = new SolidColorBrush(remaining <= 10 ? Windows.UI.Color.FromArgb(255, 239, 68, 68) :
                            remaining <= 25 ? Windows.UI.Color.FromArgb(255, 245, 158, 11) : Windows.UI.Color.FromArgb(255, 49, 196, 141)),
                    });
                    if (window.ResetsAt.HasValue)
                        panel.Children.Add(new TextBlock
                        {
                            Text = Loc.T("quota_resets", Countdown(window.ResetsAt.Value - DateTimeOffset.UtcNow)),
                            FontSize = 11, Opacity = 0.7,
                        });
                }
                panel.Children.Add(new TextBlock { Text = Loc.T("quota_reset_credits", snapshot.ResetCreditsAvailable), FontSize = 12 });
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = snapshot.State == CodexQuotaState.Loading ? Loc.T("quota_loading") :
                        snapshot.State == CodexQuotaState.Unavailable ? Loc.T("quota_not_signed_in") :
                        snapshot.Error == "Codex login expired; run codex login" ? Loc.T("quota_account_expired") : Loc.T("quota_unavailable"),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap,
                });
            }
            if (snapshot.UpdatedAt.HasValue)
                panel.Children.Add(new TextBlock { Text = Loc.T("quota_updated", snapshot.UpdatedAt.Value.ToLocalTime().ToString("HH:mm")), FontSize = 11, Opacity = 0.65 });
            var subscription = new StackPanel { Spacing = 5, Margin = new Thickness(0, 4, 0, 2) };
            subscription.Children.Add(new TextBlock { Text = Loc.T("subscription_manual"), FontSize = 11, Opacity = 0.65 });
            if (card.SubscriptionReadable)
            {
                subscription.Children.Add(new TextBlock
                {
                    Text = Loc.T("subscription_paid_on") + ": " + FormatDate(card.Subscription.RechargedOn), FontSize = 12,
                });
                subscription.Children.Add(new TextBlock
                {
                    Text = Loc.T("subscription_expires_on") + ": " + FormatDate(card.Subscription.ExpiresOn), FontSize = 12,
                });
                if (!string.IsNullOrWhiteSpace(card.Subscription.Note))
                    subscription.Children.Add(new TextBlock { Text = card.Subscription.Note, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }
            else subscription.Children.Add(new TextBlock
            {
                Text = Loc.T("subscription_storage_error"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            var edit = new Button
            {
                Content = Loc.T("subscription_edit"), FontSize = 12, Padding = new Thickness(8, 4, 8, 4),
                IsEnabled = card.Identity != null && card.SubscriptionReadable,
            };
            ToolTipService.SetToolTip(edit, card.Identity == null ? Loc.T("subscription_identity_unavailable") : Loc.T("subscription_help"));
            edit.Click += async (_, _) => await EditSubscriptionAsync(card);
            subscription.Children.Add(edit);
            panel.Children.Add(subscription);
            var usages = new AccountUsageRegistry(_store).GetAccountUsage(card.Profile.Id);
            panel.Children.Add(new TextBlock
            {
                Text = AccountUsageDisplay.Format(usages, Loc.T("account_usage_template"),
                    Loc.T("account_usage_unknown"), Loc.T("account_usage_empty"), Loc.T("account_usage_unavailable")),
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            var directory = new TextBlock { Text = card.Profile.Home, FontSize = 11, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };
            ToolTipService.SetToolTip(directory, card.Profile.Home);
            panel.Children.Add(directory);
        }

        private static string CardName(QuotaCard card) => card.Profile.Id == CodexQuotaProfileStore.DefaultId
            ? card.Identity?.Email ?? Loc.T("quota_account_default") : card.Profile.Name;

        private static string FormatDate(DateOnly? value) => value?.ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture) ?? Loc.T("subscription_unset");

        private bool RefreshCardMetadata(QuotaCard card)
        {
            var previousKey = card.Identity?.Key;
            card.Identity = CodexAccountIdentity.Read(card.Profile.Home);
            var changed = previousKey != card.Identity?.Key;
            if (changed) card.Snapshot = CodexQuotaSnapshot.Loading();
            card.Subscription = new();
            card.SubscriptionReadable = true;
            if (card.Identity is { } identity)
            {
                card.SubscriptionReadable = _subscriptions.TryLoad(identity.Key, out var subscription);
                card.Subscription = subscription;
            }
            return changed;
        }

        private async Task EditSubscriptionAsync(QuotaCard card)
        {
            if (_closed || _adding || _editingSubscription) return;
            RefreshCardMetadata(card);
            RenderCard(card);
            if (card.Identity is not { } identity) return;
            _editingSubscription = true;
            OverviewRefreshButton.IsEnabled = OverviewAddButton.IsEnabled = false;
            try
            {
                await CodexSubscriptionDialog.ShowAsync(this, CardName(card), card.Profile.Home, identity, _subscriptions);
            }
            catch { if (!_closed) OverviewStatus.Text = Loc.T("subscription_storage_error"); }
            finally
            {
                _editingSubscription = false;
                if (!_closed)
                {
                    RefreshCardMetadata(card);
                    RenderCard(card);
                    OverviewRefreshButton.IsEnabled = OverviewAddButton.IsEnabled = !_refreshing;
                }
            }
        }

        private static string Countdown(TimeSpan remaining) => remaining <= TimeSpan.Zero ? Loc.T("quota_now") :
            remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays}d {remaining.Hours}h" :
            remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m" : $"{Math.Max(1, remaining.Minutes)}m";

        private void OnRefreshAllClick(object sender, RoutedEventArgs e) => _ = RefreshAllAsync();
        private async void OnAddAccountClick(object sender, RoutedEventArgs e)
        {
            if (_closed || _adding || _refreshing || _editingSubscription) return;
            _adding = true;
            OverviewRefreshButton.IsEnabled = OverviewAddButton.IsEnabled = false;
            try { await CodexQuotaAccountDialog.ShowAsync(this, _store); }
            catch { if (!_closed) OverviewStatus.Text = Loc.T("quota_account_storage_error"); }
            finally
            {
                _adding = false;
                if (!_closed)
                {
                    OverviewRefreshButton.IsEnabled = OverviewAddButton.IsEnabled = true;
                    await RefreshAllAsync();
                }
            }
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _closed = true;
            _closedCts.Cancel();
            _refreshTimer?.Stop();
            _clockTimer?.Stop();
            Loc.LanguageChanged -= OnLanguageChanged;
            ThemeManager.PrefChanged -= OnThemeChanged;
        }
    }
}
