using System;
using System.Collections.Generic;
using System.Linq;
using CCPad.CodexQuota;
using CCPad.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CCPad
{
    public sealed partial class MainWindow
    {
        private sealed record QuotaBadge(Button Button, TextBlock Name, ProgressBar Progress, TextBlock Percent);
        private readonly QuotaToolbarSettings _quotaToolbarSettings = new();
        private readonly Dictionary<string, QuotaBadge> _quotaBadges = new();
        private QuotaToolbarView _quotaToolbarView;
        private bool _quotaFooterLayoutQueued;
        private bool ShowsAllQuotaAccounts => _quotaToolbarView == QuotaToolbarView.AllAccounts;

        private void OnQuotaViewCompactClick(object sender, RoutedEventArgs e)
            => SetQuotaToolbarView(QuotaToolbarView.Compact);

        private void OnQuotaViewAllClick(object sender, RoutedEventArgs e)
            => SetQuotaToolbarView(QuotaToolbarView.AllAccounts);

        private void SetQuotaToolbarView(QuotaToolbarView view)
        {
            _quotaToolbarView = view;
            if (!_quotaToolbarSettings.TrySave(view))
                App.LogDiag("quota-toolbar preference save failed");
            UpdateQuotaUi();
        }

        private void UpdateQuotaToolbar()
        {
            AdditionalQuotaBadges.Visibility = ShowsAllQuotaAccounts ? Visibility.Visible : Visibility.Collapsed;
            QuotaResetLabel.Visibility = ShowsAllQuotaAccounts ? Visibility.Collapsed : Visibility.Visible;
            ClaudeQuotaResetLabel.Visibility = ShowsAllQuotaAccounts ? Visibility.Collapsed : Visibility.Visible;
            QuotaProgress.Width = ClaudeQuotaProgress.Width = ShowsAllQuotaAccounts ? 40 : 54;
            QuotaViewButton.Content = ShowsAllQuotaAccounts ? Loc.T("quota_view_all") : Loc.T("quota_view_compact");
            ToolTipService.SetToolTip(QuotaViewButton, Loc.T("quota_view"));
            if (QuotaViewAllItem is not null)
            {
                QuotaViewAllItem.Text = Loc.T("quota_view_all");
                QuotaViewCompactItem.Text = Loc.T("quota_view_compact");
                QuotaViewAllItem.IsChecked = ShowsAllQuotaAccounts;
                QuotaViewCompactItem.IsChecked = !ShowsAllQuotaAccounts;
                QuotaOverviewButton.Text = Loc.T("quota_overview_open");
            }

            foreach (var id in _quotaBadges.Keys.ToArray())
            {
                if (_additionalCodexQuotas.ContainsKey(id)) continue;
                AdditionalQuotaBadges.Children.Remove(_quotaBadges[id].Button);
                _quotaBadges.Remove(id);
            }
            foreach (var entry in _additionalCodexQuotas.Values)
            {
                if (!_quotaBadges.TryGetValue(entry.Profile.Id, out var badge))
                {
                    var name = new TextBlock { FontSize = 12, MaxWidth = 150,
                        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                    var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 40, Height = 5,
                        VerticalAlignment = VerticalAlignment.Center };
                    var percent = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
                    var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    content.Children.Add(name);
                    content.Children.Add(progress);
                    content.Children.Add(percent);
                    var button = new Button { Content = content, Padding = new Thickness(7, 3, 7, 3), Tag = entry.Profile.Id };
                    button.Click += OnAdditionalQuotaBadgeClick;
                    badge = new QuotaBadge(button, name, progress, percent);
                    _quotaBadges.Add(entry.Profile.Id, badge);
                    AdditionalQuotaBadges.Children.Add(button);
                }
                var snapshot = entry.Snapshot;
                var ready = snapshot.State == CodexQuotaState.Ready && snapshot.Windows.Length > 0;
                badge.Name.Text = Loc.T("quota_account_badge", entry.Profile.Name);
                badge.Progress.Value = ready ? snapshot.TightestUsedPercent : 0;
                badge.Percent.Text = ready ? $"{snapshot.TightestUsedPercent:0}%" : "--";
                var description = badge.Name.Text + " · " + Loc.T("quota_account_extra") + " · " +
                    (ready ? Loc.T("quota_tooltip", snapshot.TightestUsedPercent) : Loc.T("quota_tooltip_empty"));
                ToolTipService.SetToolTip(badge.Button, description);
                AutomationProperties.SetName(badge.Button, description);
            }
            QueueQuotaFooterLayout();
        }

        private void OnAdditionalQuotaBadgeClick(object sender, RoutedEventArgs e)
        {
            if (_codexResetInFlight || sender is not Button { Tag: string id } button ||
                !_additionalCodexQuotas.ContainsKey(id)) return;
            _selectedCodexQuotaId = id;
            RebuildQuotaAccountSelector();
            ShowQuotaDetails(QuotaProvider.Codex, openFlyout: true, anchor: button);
        }

        private void OnQuotaFooterSizeChanged(object sender, SizeChangedEventArgs e) => QueueQuotaFooterLayout();

        private void QueueQuotaFooterLayout()
        {
            if (_quotaFooterLayoutQueued || FooterGrid is null) return;
            _quotaFooterLayoutQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _quotaFooterLayoutQueued = false;
                if (FooterGrid.ActualWidth <= 0) return;
                // Keep every account badge. Shorten names before moving actions to a second
                // row; very large account lists can still be scrolled instead of being dropped.
                var stacked = FooterGrid.ActualWidth < QuotaToolbarPanel.ActualWidth + FooterActions.ActualWidth + 16;
                if (ShowsAllQuotaAccounts)
                {
                    var count = _quotaBadges.Count + 1;
                    var fixedWidth = Math.Max(0, QuotaToolbarPanel.ActualWidth - QuotaProviderLabel.ActualWidth -
                        _quotaBadges.Values.Sum(badge => badge.Name.ActualWidth));
                    var singleRowRoom = FooterGrid.ActualWidth - FooterActions.ActualWidth - 16;
                    stacked = singleRowRoom < fixedWidth + count * 60;
                    var room = stacked ? FooterGrid.ActualWidth : singleRowRoom;
                    var nameWidth = Math.Clamp((room - fixedWidth) / count, 60, 150);
                    QuotaProviderLabel.MaxWidth = nameWidth;
                    foreach (var badge in _quotaBadges.Values) badge.Name.MaxWidth = nameWidth;
                }
                else QuotaProviderLabel.MaxWidth = 150;
                Grid.SetRow(FooterActions, stacked ? 1 : 0);
                Grid.SetColumnSpan(QuotaToolbarScroll, stacked ? 2 : 1);
            });
        }
    }
}
