using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CCPad
{
    /// <summary>One row in the file list — a folder or a file in the current directory.</summary>
    public sealed class FileEntry
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public bool IsDir { get; init; }

        // Segoe Fluent glyphs: folder vs page.
        public string Glyph => IsDir ? "" : "";
        public Brush IconBrush => IsDir
            ? new SolidColorBrush(Color.FromArgb(255, 0xE3, 0xB3, 0x41))   // amber folder
            : new SolidColorBrush(Color.FromArgb(255, 0x9A, 0xA0, 0xA6));  // gray file
    }

    /// <summary>
    /// Simple LOCAL file browser docked on the right. Bound to the active tab's
    /// project working directory; supports breadcrumb path, up, in-folder search,
    /// refresh, and a right-click menu (Open / Delete→recycle / Rename / Copy path).
    /// Pure System.IO — no remote/SFTP, no external dependencies.
    /// </summary>
    public sealed partial class FileManagerPanel : UserControl
    {
        private string? _currentDir;
        private readonly ObservableCollection<FileEntry> _items = new();
        private List<FileEntry> _all = new();   // unfiltered entries of the current dir
        private FileEntry? _ctx;                 // right-clicked entry for the context menu

        public FileManagerPanel()
        {
            InitializeComponent();
            EntryList.ItemsSource = _items;
        }

        /// <summary>Point the browser at a project directory (falls back to the user
        /// profile if the path is empty or missing).</summary>
        public void SetRoot(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Navigate(dir);
        }

        private void Navigate(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                _currentDir = Path.GetFullPath(dir);
                PathBox.Text = _currentDir;
                SearchBox.Text = "";
                LoadEntries();
            }
            catch (Exception ex) { Debug.WriteLine("Navigate: " + ex); }
        }

        private void LoadEntries()
        {
            _all = new List<FileEntry>();
            if (_currentDir != null)
            {
                try
                {
                    var di = new DirectoryInfo(_currentDir);
                    foreach (var d in di.EnumerateDirectories()
                                       .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        _all.Add(new FileEntry { Name = d.Name, FullPath = d.FullName, IsDir = true });
                    foreach (var f in di.EnumerateFiles()
                                       .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        _all.Add(new FileEntry { Name = f.Name, FullPath = f.FullName, IsDir = false });
                }
                catch (Exception ex) { Debug.WriteLine("LoadEntries: " + ex); }
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = SearchBox.Text?.Trim() ?? "";
            _items.Clear();
            foreach (var e in _all)
                if (q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    _items.Add(e);
        }

        // ── Toolbar / search ────────────────────────────────────────────────

        private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadEntries();

        private void OnUpClick(object sender, RoutedEventArgs e)
        {
            if (_currentDir == null) return;
            var parent = Directory.GetParent(_currentDir);
            if (parent != null) Navigate(parent.FullName);
        }

        private void OnPathKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            var p = PathBox.Text?.Trim();
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) Navigate(p);
            else PathBox.Text = _currentDir ?? "";
        }

        // ── List activation ─────────────────────────────────────────────────

        private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntry entry) Activate(entry);
            else if (EntryList.SelectedItem is FileEntry sel) Activate(sel);
        }

        private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter && EntryList.SelectedItem is FileEntry entry)
            { e.Handled = true; Activate(entry); }
            else if (e.Key == Windows.System.VirtualKey.Back)
            { e.Handled = true; OnUpClick(sender, e); }
        }

        private void Activate(FileEntry entry)
        {
            if (entry.IsDir) Navigate(entry.FullPath);
            else OpenFile(entry.FullPath);
        }

        private void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { _ = ShowError("打开失败", ex.Message); }
        }

        // ── Context menu ─────────────────────────────────────────────────────

        private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileEntry entry) return;
            _ctx = entry;
            EntryList.SelectedItem = entry;

            var mf = new MenuFlyout();
            mf.Items.Add(MenuItem("Open", () => Activate(entry)));
            mf.Items.Add(MenuItem("Delete", async () => await DeleteAsync(entry)));
            mf.Items.Add(MenuItem("Rename", async () => await RenameAsync(entry)));
            mf.Items.Add(new MenuFlyoutSeparator());
            mf.Items.Add(MenuItem("Copy file path", () => CopyPath(entry)));
            mf.ShowAt(EntryList, new FlyoutShowOptions { Position = e.GetPosition(EntryList) });
            e.Handled = true;
        }

        private static MenuFlyoutItem MenuItem(string text, Action onClick)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => onClick();
            return item;
        }

        private void CopyPath(FileEntry entry)
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(entry.FullPath);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
            catch (Exception ex) { Debug.WriteLine("CopyPath: " + ex); }
        }

        private async Task DeleteAsync(FileEntry entry)
        {
            var dlg = new ContentDialog
            {
                Title = "删除到回收站",
                Content = $"将 \"{entry.Name}\" 移到回收站？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                if (entry.IsDir)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        entry.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        entry.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                LoadEntries();
            }
            catch (Exception ex) { await ShowError("删除失败", ex.Message); }
        }

        private async Task RenameAsync(FileEntry entry)
        {
            var input = new TextBox { Text = entry.Name };
            input.Loaded += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
            var dlg = new ContentDialog
            {
                Title = "重命名",
                Content = input,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            var newName = input.Text?.Trim();
            if (string.IsNullOrEmpty(newName) || newName == entry.Name) return;
            try
            {
                string dir = Path.GetDirectoryName(entry.FullPath)!;
                string dest = Path.Combine(dir, newName);
                if (entry.IsDir) Directory.Move(entry.FullPath, dest);
                else File.Move(entry.FullPath, dest);
                LoadEntries();
            }
            catch (Exception ex) { await ShowError("重命名失败", ex.Message); }
        }

        private async Task ShowError(string title, string msg)
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = title,
                    Content = msg,
                    CloseButtonText = "好",
                    XamlRoot = XamlRoot
                };
                await dlg.ShowAsync();
            }
            catch { }
        }
    }
}
