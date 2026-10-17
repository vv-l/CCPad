using System;
using System.Threading;
using System.Threading.Tasks;
using CCPad.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CCPad.CodexQuota
{
    internal static class CodexQuotaAccountDialog
    {
        public static async Task<CodexQuotaProfile?> ShowAsync(Window owner, CodexQuotaProfileStore store)
        {
            if (owner.Content is not FrameworkElement root || root.XamlRoot is null) return null;

            var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 470 };
            var mode = new ComboBox { Header = Loc.T("quota_account_add_method") };
            mode.Items.Add(new ComboBoxItem
            {
                Content = Loc.T("quota_account_method_login"), Tag = "login",
            });
            mode.Items.Add(new ComboBoxItem
            {
                Content = Loc.T("quota_account_method_existing"), Tag = "existing",
            });
            mode.SelectedIndex = 0;

            var name = new TextBox { Header = Loc.T("quota_account_name"), MaxLength = 40 };
            var path = new TextBox
            {
                Header = Loc.T("quota_account_directory"),
                PlaceholderText = Loc.T("quota_account_auto_directory"),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
            };
            var browse = new Button { Content = Loc.T("quota_account_browse") };
            var login = new Button { Content = Loc.T("quota_account_login_button") };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            var help = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(mode);
            panel.Children.Add(name);
            panel.Children.Add(path);
            panel.Children.Add(browse);
            panel.Children.Add(login);
            panel.Children.Add(help);
            panel.Children.Add(status);
            panel.Children.Add(error);

            var dialog = new ContentDialog
            {
                Title = Loc.T("quota_account_add"), Content = panel,
                PrimaryButtonText = Loc.T("quota_account_save"), CloseButtonText = Loc.T("cancel"),
                DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = true,
                XamlRoot = root.XamlRoot,
            };

            using var loginCts = new CancellationTokenSource();
            bool closed = false;
            Task loginTask = Task.CompletedTask;
            dialog.Closed += (_, _) =>
            {
                closed = true;
                loginCts.Cancel();
            };

            bool loginMode = true;
            bool loginCompleted = false;
            string? pendingHome = null;
            CodexQuotaProfile? added = null;

            bool ValidName() => !string.IsNullOrWhiteSpace(name.Text) &&
                name.Text.Trim().Length <= 40 && !name.Text.Trim().Contains('\n');

            void UpdateEnabled()
            {
                dialog.IsPrimaryButtonEnabled = ValidName() &&
                    (loginMode ? loginCompleted : !string.IsNullOrWhiteSpace(path.Text));
            }

            void ApplyMode()
            {
                loginMode = (mode.SelectedItem as ComboBoxItem)?.Tag as string == "login";
                loginCompleted = false;
                pendingHome = null;
                error.Text = "";
                status.Text = "";
                if (loginMode)
                {
                    path.Text = "";
                    path.IsReadOnly = true;
                    path.PlaceholderText = Loc.T("quota_account_auto_directory");
                    browse.Visibility = Visibility.Collapsed;
                    login.Visibility = Visibility.Visible;
                    help.Text = Loc.T("quota_account_login_help");
                }
                else
                {
                    path.Text = "";
                    path.IsReadOnly = false;
                    path.PlaceholderText = store.DefaultHome;
                    browse.Visibility = Visibility.Visible;
                    login.Visibility = Visibility.Collapsed;
                    help.Text = Loc.T("quota_account_add_help");
                }
                UpdateEnabled();
            }

            name.TextChanged += (_, _) => UpdateEnabled();
            path.TextChanged += (_, _) => UpdateEnabled();
            mode.SelectionChanged += (_, _) => ApplyMode();
            browse.Click += async (_, _) =>
            {
                browse.IsEnabled = false;
                try
                {
                    var picker = new Windows.Storage.Pickers.FolderPicker();
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
                    picker.FileTypeFilter.Add("*");
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder is not null) path.Text = folder.Path;
                }
                catch { error.Text = Loc.T("quota_account_pick_error"); }
                finally { browse.IsEnabled = true; }
            };
            async Task LoginAsync()
            {
                if (closed) return;
                if (!ValidName())
                {
                    error.Text = Loc.T("quota_account_invalid_name");
                    return;
                }

                login.IsEnabled = false;
                mode.IsEnabled = false;
                name.IsEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                error.Text = "";
                try
                {
                    pendingHome = CodexLoginService.CreateProfileHome();
                    path.Text = pendingHome;
                    status.Text = Loc.T("quota_account_login_opening");
                    var result = await CodexLoginService.RunAsync(pendingHome, loginCts.Token);
                    if (closed) return;
                    if (result == CodexLoginOutcome.Completed &&
                        CodexQuotaProfileStore.HasReadableChatGptLogin(pendingHome))
                    {
                        loginCompleted = true;
                        status.Text = Loc.T("quota_account_login_success");
                    }
                    else
                    {
                        loginCompleted = false;
                        status.Text = result switch
                        {
                            CodexLoginOutcome.CliMissing => Loc.T("quota_account_login_missing_cli"),
                            CodexLoginOutcome.Cancelled => Loc.T("quota_account_login_cancelled"),
                            _ => Loc.T("quota_account_login_failed"),
                        };
                        mode.IsEnabled = true;
                        name.IsEnabled = true;
                        login.IsEnabled = true;
                    }
                }
                catch
                {
                    if (closed) return;
                    loginCompleted = false;
                    status.Text = Loc.T("quota_account_login_failed");
                    mode.IsEnabled = name.IsEnabled = login.IsEnabled = true;
                }
                finally
                {
                    if (!closed)
                    {
                        dialog.IsSecondaryButtonEnabled = true;
                        UpdateEnabled();
                    }
                }
            }
            login.Click += (_, _) =>
            {
                if (!closed && login.IsEnabled) loginTask = LoginAsync();
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var home = loginMode ? pendingHome : path.Text;
                if (string.IsNullOrWhiteSpace(home) || (loginMode && !loginCompleted))
                {
                    args.Cancel = true;
                    error.Text = Loc.T("quota_account_login_required");
                    return;
                }

                // Official login uses a new isolated CODEX_HOME on every run.
                // Re-login of an already registered account should refresh that
                // profile instead of leaving the new credentials untracked.
                var result = store.TryAddOrReplace(name.Text, home, out added);
                if (result == CodexQuotaProfileError.None) return;
                args.Cancel = true;
                error.Text = result switch
                {
                    CodexQuotaProfileError.InvalidName => Loc.T("quota_account_invalid_name"),
                    CodexQuotaProfileError.InvalidLogin => Loc.T("quota_account_invalid_login"),
                    CodexQuotaProfileError.Duplicate => Loc.T("quota_account_duplicate"),
                    _ => Loc.T("quota_account_storage_error"),
                };
            };

            ApplyMode();
            try
            {
                return await dialog.ShowAsync() == ContentDialogResult.Primary ? added : null;
            }
            finally
            {
                closed = true;
                loginCts.Cancel();
                // Observe the owned task before disposing its cancellation source.
                // Await yields to the dispatcher while the owned process exits.
                try { await loginTask; } catch { }
            }
        }
    }
}
