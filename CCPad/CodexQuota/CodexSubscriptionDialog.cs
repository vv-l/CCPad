using System;
using System.Threading.Tasks;
using CCPad.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CCPad.CodexQuota
{
    internal static class CodexSubscriptionDialog
    {
        public static async Task<bool> ShowAsync(Window owner, string accountName, string home,
            CodexAccountIdentity identity, CodexSubscriptionStore store)
        {
            if (owner.Content is not FrameworkElement root || root.XamlRoot is null) return false;
            var readable = store.TryLoad(identity.Key, out var saved);
            var panel = new StackPanel { Spacing = 10, MinWidth = 310, MaxWidth = 430 };
            panel.Children.Add(new TextBlock { Text = accountName, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("subscription_help"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
            });
            var paid = DateField("subscription_paid_on", saved.RechargedOn);
            var expires = DateField("subscription_expires_on", saved.ExpiresOn);
            panel.Children.Add(paid);
            panel.Children.Add(expires);
            var note = new TextBox
            {
                Header = Loc.T("subscription_note"), Text = saved.Note, MaxLength = 160,
                PlaceholderText = Loc.T("subscription_optional"),
            };
            panel.Children.Add(note);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                Title = Loc.T("subscription_edit"), Content = panel, XamlRoot = root.XamlRoot,
                PrimaryButtonText = Loc.T("tag_save"), SecondaryButtonText = Loc.T("subscription_clear"),
                CloseButtonText = Loc.T("cancel"), DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = readable, IsSecondaryButtonEnabled = readable,
            };
            if (!readable) error.Text = Loc.T("subscription_storage_error");
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                paid.Date = expires.Date = null;
                note.Text = "";
                error.Text = "";
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var edited = new CodexSubscriptionNote(ToDate(paid.Date), ToDate(expires.Date), note.Text.Trim());
                if (!edited.IsValid)
                {
                    args.Cancel = true;
                    error.Text = Loc.T("subscription_invalid_dates");
                }
                else if (CodexAccountIdentity.Read(home)?.Key != identity.Key)
                {
                    args.Cancel = true;
                    error.Text = Loc.T("subscription_identity_changed");
                }
                else if (!store.TrySave(identity.Key, edited))
                {
                    args.Cancel = true;
                    error.Text = Loc.T("subscription_storage_error");
                }
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static CalendarDatePicker DateField(string label, DateOnly? value) => new()
        {
            Header = Loc.T(label), PlaceholderText = Loc.T("subscription_optional"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}",
            Date = value.HasValue ? new DateTimeOffset(value.Value.ToDateTime(new TimeOnly(12, 0))) : null,
        };

        private static DateOnly? ToDate(DateTimeOffset? value) =>
            value.HasValue ? DateOnly.FromDateTime(value.Value.DateTime) : null;
    }
}
