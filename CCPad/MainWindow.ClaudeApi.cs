using System;
using System.Linq;
using System.Threading.Tasks;
using CCPad.CodexQuota;
using CCPad.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CCPad;

public sealed partial class MainWindow
{
    private readonly ClaudeApiProfileStore _claudeApiProfiles = new();
    private string _claudeApiProfileId = "";
    private bool _claudeApiDialogOpen;
    internal ClaudeApiConnection? ResolveClaudeApiForLaunch(string? restoredProfileId = null)
    {
        try { return _claudeApiProfiles.Resolve(restoredProfileId ?? _claudeApiProfileId); }
        catch { throw new InvalidOperationException(Loc.T(restoredProfileId == null ? "claude_api_missing" : "claude_api_restore_missing")); }
    }

    private void RestoreClaudeApiSelection(string? id)
    {
        _claudeApiProfileId = id ?? "";
        UpdateClaudeQuotaUi(ClaudeQuotaSnapshot.Loading());
        _ = RefreshQuotaAsync(QuotaProvider.Claude);
    }

    private void UpdateClaudeApiControls()
    {
        if (ClaudeApiPanel == null) return;
        ClaudeApiPanel.Visibility = _quotaProvider == QuotaProvider.Claude ? Visibility.Visible : Visibility.Collapsed;
        ClaudeApiAddButton.Content = Loc.T("claude_api_add");
        RefreshClaudeSessionProfile();
    }

    private void RefreshClaudeSessionProfile()
    {
        if (_quotaProvider != QuotaProvider.Claude || QuotaFlyout?.IsOpen != true) return;
        var next = ClaudeApiProfileMenu.Items.OfType<RadioMenuFlyoutItem>()
            .FirstOrDefault(item => Equals(item.Tag, _claudeApiProfileId))?.Text
            ?? Loc.T("claude_api_missing_short");
        var pane = _splitHost?.ActiveTerminal;
        if (pane is not { CliMode: Settings.CliMode.Claude, IsObservationPaneAlive: true })
        {
            ClaudeApiSelector.Content = next;
            ClaudeApiNote.Text = Loc.T("claude_api_window_note");
            return;
        }

        // The window's selector can change while this pane keeps its launch credential.
        var name = ClaudeSessionProfileName(pane.ClaudeApiConnection);
        ClaudeApiSelector.Content = Loc.T("claude_api_current_session", name);
        ClaudeApiNote.Text = Loc.T("claude_api_next_profile", next);
    }

    internal string ClaudeSessionProfileName(ClaudeApiConnection? connection)
    {
        if (connection == null) return Loc.T("claude_api_local");
        var name = _claudeApiProfiles.SessionProfileName(connection);
        return name.Length > 0 ? name : Loc.T("claude_api_local_unmatched");
    }

    private void RebuildClaudeApiSelector()
    {
        ClaudeApiProfileMenu.Items.Clear();
        void AddOption(string id, string name, bool enabled = true)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = name, Tag = id, IsChecked = id == _claudeApiProfileId, IsEnabled = enabled,
                GroupName = "ClaudeApiProfiles"
            };
            item.Click += (_, _) => SelectClaudeApiProfile(id);
            ClaudeApiProfileMenu.Items.Add(item);
        }

        var localLabel = Loc.T("claude_api_local");
        try
        {
            if (_claudeApiProfiles.ReadLocalConnection() is { } connection)
                localLabel = connection.Name.Length > 0
                    ? Loc.T("claude_api_local_named", connection.Name) : Loc.T("claude_api_local_unmatched");
        }
        catch { }
        AddOption("", localLabel);
        var profiles = _claudeApiProfiles.Load();
        foreach (var profile in profiles) AddOption(profile.Id, profile.Name);
        if (_claudeApiProfileId.Length > 0 && !profiles.Any(profile => profile.Id == _claudeApiProfileId))
            AddOption(_claudeApiProfileId, Loc.T("claude_api_missing_short"), enabled: false);
    }

    private void SelectClaudeApiProfile(string id)
    {
        if (id == _claudeApiProfileId) return;
        _claudeApiProfileId = id;
        UpdateClaudeQuotaUi(ClaudeQuotaSnapshot.Loading());
        NotifyLayoutChanged();
        _ = RefreshQuotaAsync(QuotaProvider.Claude);
    }

    private async void OnClaudeApiAddClick(object sender, RoutedEventArgs e)
    {
        if (_claudeApiDialogOpen || Content?.XamlRoot == null) return;
        _claudeApiDialogOpen = true;
        QuotaFlyout.Hide();
        try
        {
            var name = new TextBox { Header = Loc.T("claude_api_name"), MaxLength = 40 };
            var address = new TextBox { Header = Loc.T("claude_api_address"), PlaceholderText = "https://…" };
            try { address.Text = _claudeApiProfiles.Resolve(_claudeApiProfileId)?.BaseUrl ?? ""; } catch { }
            var key = new PasswordBox { Header = "API Key", PasswordRevealMode = PasswordRevealMode.Peek };
            var auth = new ComboBox { Header = Loc.T("claude_api_auth") };
            auth.Items.Add(new ComboBoxItem { Content = "Bearer (ANTHROPIC_AUTH_TOKEN)" });
            auth.Items.Add(new ComboBoxItem { Content = "x-api-key (ANTHROPIC_API_KEY)" });
            auth.SelectedIndex = 0;
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 460 };
            panel.Children.Add(name); panel.Children.Add(address); panel.Children.Add(key); panel.Children.Add(auth);
            panel.Children.Add(new TextBlock { Text = Loc.T("claude_api_store_note"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            panel.Children.Add(error);
            var dialog = new ContentDialog { Title = Loc.T("claude_api_add"), Content = panel,
                PrimaryButtonText = Loc.T("quota_account_save"), CloseButtonText = Loc.T("cancel"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            ClaudeApiProfile? added = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { added = _claudeApiProfiles.Add(name.Text, address.Text, key.Password, auth.SelectedIndex == 1 ? "api-key" : "bearer"); }
                catch { error.Text = Loc.T("claude_api_save_failed"); args.Cancel = true; }
            };
            await dialog.ShowAsync();
            key.Password = "";
            if (added != null)
            {
                SelectClaudeApiProfile(added.Id);
                _splitHost?.RefreshClaudeTabTitles();
            }
        }
        finally { _claudeApiDialogOpen = false; }
    }

    private void AddClaudeApiRows(ClaudeApiUsage api)
    {
        void Row(string label, string value) => QuotaWindowPanel.Children.Add(new TextBlock
            { Text = Loc.T(label) + "  " + value, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (api.Balance is decimal balance) Row("claude_api_balance", ClaudeApiUsage.Money(balance, api.Unit));
        if (api.KeyLimit is decimal limit && api.KeyUsed is decimal used)
            Row("claude_api_key_remaining", ClaudeApiUsage.Money(Math.Max(0, limit - used), api.Unit));
        else if (api.Balance == null && api.Remaining is decimal remaining)
            Row("claude_api_remaining", ClaudeApiUsage.Money(remaining, api.Unit));
        if (api.Today.Cost is decimal today) Row("claude_api_today_cost", ClaudeApiUsage.Money(today, api.Unit));
        if (api.Total.Cost is decimal total) Row("claude_api_total_cost", ClaudeApiUsage.Money(total, api.Unit));
        if (api.Today.Tokens is long todayTokens) Row("claude_api_today_tokens", todayTokens.ToString("N0"));
        if (api.Total.Tokens is long totalTokens) Row("claude_api_total_tokens", totalTokens.ToString("N0"));
        if (api.Total.Requests is long requests) Row("claude_api_requests", requests.ToString("N0"));
    }
}
