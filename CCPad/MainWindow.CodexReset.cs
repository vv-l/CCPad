using System;
using System.Threading;
using System.Threading.Tasks;
using CCPad.CodexQuota;
using CCPad.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CCPad
{
    public sealed partial class MainWindow
    {
        private readonly CodexResetCreditService _codexResetCreditService = new();
        private bool _codexResetInFlight;

        private void UpdateResetCreditButton()
        {
            if (QuotaResetCreditButton is null) return;
            var pending = _codexResetCreditService.HasPendingAttempt;
            QuotaResetCreditButton.Visibility = _quotaProvider == QuotaProvider.Codex && IsDefaultCodexQuotaSelected
                ? Visibility.Visible : Visibility.Collapsed;
            QuotaResetCreditButton.Content = pending ? Loc.T("quota_reset_retry_action") : Loc.T("quota_use_reset");
            QuotaResetCreditButton.IsEnabled = IsDefaultCodexQuotaSelected && !_codexResetInFlight &&
                (_codexResetCreditService.IsSimulation || pending ||
                 (_codexQuotaSnapshot.State == CodexQuotaState.Ready && _codexQuotaSnapshot.ResetCreditsAvailable > 0));
        }

        private async void OnQuotaResetCreditClick(object sender, RoutedEventArgs e)
        {
            if (_codexResetInFlight || !IsDefaultCodexQuotaSelected ||
                _quotaProvider != QuotaProvider.Codex || Content?.XamlRoot == null) return;
            _codexResetInFlight = true;
            UpdateResetCreditButton();
            QuotaFlyout.Hide();
            var submitted = false;
            try
            {
                CodexResetOutcome outcome;
                await using (var prepared = await _codexResetCreditService.PrepareAsync(_quotaRefreshCts.Token))
                {
                    var content = new StackPanel { Spacing = 12, MaxWidth = 420 };
                    content.Children.Add(new TextBlock
                    {
                        Text = Loc.T("quota_reset_account", prepared.AccountLabel, prepared.Plan),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text = prepared.IsSimulation ? Loc.T("quota_reset_simulation") :
                            prepared.RetryPending ? Loc.T("quota_reset_retry") :
                            Loc.T("quota_reset_warning", prepared.AvailableCount),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    var confirmation = new TextBox { Header = Loc.T("quota_reset_type"), PlaceholderText = "RESET", MaxLength = 5 };
                    content.Children.Add(confirmation);
                    var dialog = new ContentDialog
                    {
                        Title = Loc.T("quota_use_reset"), Content = content,
                        PrimaryButtonText = prepared.RetryPending ? Loc.T("quota_reset_retry_confirm") : Loc.T("quota_reset_confirm"),
                        CloseButtonText = Loc.T("cancel"),
                        DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
                        XamlRoot = Content.XamlRoot,
                    };
                    confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = confirmation.Text == "RESET";
                    dialog.PrimaryButtonClick += (_, args) => { if (confirmation.Text != "RESET") args.Cancel = true; };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary || confirmation.Text != "RESET") return;
                    submitted = true;
                    outcome = await prepared.ConfirmAsync(confirmation.Text, _quotaRefreshCts.Token);
                } // Release cross-window lock and the owned process before showing the result.
                // A refresh already in flight may predate the redemption. Wait for it,
                // then perform a new read instead of presenting that older result.
                while (_codexQuotaRefreshInFlight && !_quotaRefreshCts.IsCancellationRequested)
                    await Task.Delay(100, _quotaRefreshCts.Token);
                await RefreshQuotaAsync(QuotaProvider.Codex);
                var message = outcome switch
                {
                    CodexResetOutcome.Reset or CodexResetOutcome.AlreadyRedeemed => Loc.T("quota_reset_success"),
                    CodexResetOutcome.NothingToReset => Loc.T("quota_reset_nothing"),
                    CodexResetOutcome.NoCredit => Loc.T("quota_reset_no_credit"),
                    CodexResetOutcome.AccountChanged => Loc.T("quota_reset_changed"),
                    CodexResetOutcome.Simulation => Loc.T("quota_reset_simulation"),
                    CodexResetOutcome.Cancelled => Loc.T("quota_reset_cancelled"),
                    _ => Loc.T("quota_reset_unknown"),
                };
                await ShowResetMessageAsync(message);
            }
            catch
            {
                if (!_quotaRefreshCts.IsCancellationRequested)
                    await ShowResetMessageAsync(submitted ? Loc.T("quota_reset_unknown") : Loc.T("quota_reset_prepare_error"));
            }
            finally
            {
                _codexResetInFlight = false;
                UpdateResetCreditButton();
            }
        }

        private async Task ShowResetMessageAsync(string message)
        {
            if (_quotaRefreshCts.IsCancellationRequested || Content?.XamlRoot == null) return;
            try
            {
                await new ContentDialog
                {
                    Title = Loc.T("quota_use_reset"), Content = message,
                    CloseButtonText = Loc.T("close"), DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
            }
            catch { }
        }
    }
}
