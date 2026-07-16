using System;
using System.Collections.Generic;
using CCPad.Localization;
using CCPad.Settings;
using CCPad.Web;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CCPad
{
    public sealed partial class MainWindow : Window
    {
        private SplitHost? _splitHost;
        private string? _currentWorkspaceFile;
        private string? _openedTemplateFile;
        private string? _openedTemplateName;

        private ReleaseInfo? _releaseInfo;
        private WebTerminalServer? _webServer;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autosaveTimer;
        private bool _autosaveSubscribed;

        // Close-confirmation state. _closeConfirmed short-circuits the dialog when
        // Close() is re-invoked from the dialog's own confirm path.
        private bool _closeConfirmed;
        private bool _confirmDialogOpen;
        private bool _restoreOnNextLaunch;

        private MenuFlyoutSubItem? _closedSessionsMenu;

        // Memory-pressure guard state (see StartResourceGuard).
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resourceTimer;
        private bool _resourceWarningArmed = true;

        public MainWindow()
        {
            InitializeComponent();
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            // Trigger first-time init from Loaded rather than Activated: at
            // Loaded time the element is in the live visual tree so XamlRoot is
            // guaranteed available. (Activated can fire while Content.XamlRoot
            // is still null, which made ContentDialog.ShowAsync throw E_INVALIDARG.)
            RootGrid.Loaded += OnRootLoaded;
            Closed += OnWindowClosed;
            InitAboutMenu();
            InitFreezeMenu();
            WorkspaceFlyout.Opening += (_, _) => RefreshWorkspaceFlyout();
            ApplyLocalizedChrome();
            Loc.LanguageChanged += OnLanguageChanged;
            RootContainer.ActualThemeChanged += OnActualThemeChanged;
            ThemeManager.PrefChanged += OnThemePrefChanged;
            LastCmdButton.IsChecked = LastCmdBarManager.IsOn;
            LastCmdBarManager.Changed += OnLastCmdBarManagerChanged;
            SessionRecovery.MarkRunning();
        }

        // ── Theme ───────────────────────────────────────────────────────────
        // Chrome (window + tab strip) switches via XAML ThemeDictionaries keyed off
        // RootContainer.RequestedTheme; terminals listen to ThemeManager separately.

        private void ApplyThemePref()
        {
            RootContainer.RequestedTheme = ThemeManager.ToElementTheme(ThemeManager.Pref);
            // ActualTheme resolves synchronously on a loaded element, so panes built
            // right after this read the correct effective (dark/light) value.
            ThemeManager.SetEffective(RootContainer.ActualTheme == ElementTheme.Dark);
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
            => ThemeManager.SetEffective(sender.ActualTheme == ElementTheme.Dark);

        private void OnThemePrefChanged()
        {
            ApplyThemePref();
            InitAboutMenu(); // refresh the theme radio checks
        }

        private bool _initialized;

        private async void OnRootLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            RootGrid.Loaded -= OnRootLoaded;

            // Apply the saved theme before any terminal panes are built below, so
            // their initial xterm styling matches the effective dark/light value.
            ApplyThemePref();

            var app = Application.Current as App;
            var projects = ProjectConfig.Load();
            var startDir = app?.StartupWorkingDir;
            var workspaceFile = app?.StartupWorkspaceFile;

            try
            {
                // Workspace file from command-line takes precedence over recovery.
                if (workspaceFile != null)
                {
                    var ws = WorkspaceConfig.LoadFromFile(workspaceFile);
                    if (ws?.Layout != null)
                    {
                        bool frozenTemplate = WorkspaceConfig.IsTemplateFile(workspaceFile);
                        if (frozenTemplate)
                            WorkspaceConfig.MarkAllTabsFrozen(ws);
                        _splitHost = SplitHost.RestoreFromLayout(ws.Layout, projects);
                        if (frozenTemplate)
                            _splitHost.DisableFrozenPrewarm();
                        RootGrid.Children.Add(_splitHost);
                        RestoreWindowSize(ws);
                        await _splitHost.InitializeTerminals();

                        _currentWorkspaceFile = frozenTemplate ? null : workspaceFile;
                        _openedTemplateFile = frozenTemplate ? workspaceFile : null;
                        _openedTemplateName = frozenTemplate
                            ? (string.IsNullOrWhiteSpace(ws.TemplateName)
                                ? System.IO.Path.GetFileNameWithoutExtension(workspaceFile)
                                : ws.TemplateName)
                            : null;
                        EnterWorkspaceMode();
                        Activated += OnActivated;
                        AttachAutosave();
                        StartResourceGuard();
                        _ = DelayedUpdateCheckAsync();
                        return;
                    }
                }

                // Session restore: sweep dead instances' autosaves into the
                // closed-session history, then take the newest entry flagged for
                // auto-restore — either a crash victim (Task Manager kill etc.)
                // or the last clean close with the restore checkbox ticked.
                // Crashed entries restore silently once; if that silent restore
                // itself never reached a clean close, the crash-loop guard falls
                // back to asking. Everything else stays in the history menu.
                if (workspaceFile == null && startDir == null)
                {
                    SessionRecovery.SweepCrashedSessions();
                    bool recoveryEnabled = AppConfig.Load().SessionRecoveryEnabled;
                    var pending = SessionRecovery.TryConsumePendingRestore(includeCrashed: recoveryEnabled);
                    if (pending?.Entry.Layout != null)
                    {
                        bool restored;
                        if (pending.WasCrashed && SessionRecovery.HasCrashRestoreAttempt())
                        {
                            restored = await TryShowRecoveryDialogAsync(pending.Entry, projects);
                        }
                        else
                        {
                            if (pending.WasCrashed)
                                SessionRecovery.MarkCrashRestoreAttempt();
                            _splitHost = SplitHost.RestoreFromLayout(pending.Entry.Layout, projects);
                            RootGrid.Children.Add(_splitHost);
                            RestoreWindowSize(pending.Entry);
                            await _splitHost.InitializeTerminals();
                            RefreshWorkspaceFlyout();
                            restored = true;
                        }

                        if (restored)
                        {
                            Activated += OnActivated;
                            AttachAutosave();
                            StartResourceGuard();
                            _ = DelayedUpdateCheckAsync();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Workspace/recovery restore failed. Never let this propagate:
                // an unhandled throw here fail-fasts WinUI and, because the
                // running lock is left behind, becomes a launch-time crash loop.
                // Log it and fall through to a clean blank launch instead.
                App.LogStartupError("OnRootLoaded/restore", ex);
                try { RootGrid.Children.Clear(); } catch { }
                _splitHost = null;
                _currentWorkspaceFile = null;
                _openedTemplateFile = null;
                _openedTemplateName = null;
            }

            // Normal launch — fresh terminal, no workspace.
            try
            {
                _splitHost = new SplitHost(projects);
                RootGrid.Children.Add(_splitHost);
                var startName = startDir != null ? System.IO.Path.GetFileName(startDir) : null;
                await _splitHost.InitializeFirstTab(startName, startDir);

                RefreshWorkspaceFlyout();
                Activated += OnActivated;
                AttachAutosave();
                StartResourceGuard();
                _ = DelayedUpdateCheckAsync();
            }
            catch (Exception ex)
            {
                App.LogStartupError("OnRootLoaded/normal-launch", ex);
            }
        }

        private async System.Threading.Tasks.Task<bool> TryShowRecoveryDialogAsync(
            WorkspaceEntry recovered, List<ProjectEntry> projects)
        {
            // ContentDialog.ShowAsync throws E_INVALIDARG without a XamlRoot.
            // If it is somehow still unavailable, skip recovery rather than crash.
            var xamlRoot = Content?.XamlRoot;
            if (xamlRoot == null) return false;

            // Content must be created after the window has a XamlRoot.
            var dontAskAgain = new CheckBox { Content = Loc.T("recovery_dontask") };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("recovery_body"),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("recovery_note"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(dontAskAgain);

            var dlg = new ContentDialog
            {
                Title = Loc.T("recovery_title"),
                Content = panel,
                PrimaryButtonText = Loc.T("recovery_restore"),
                CloseButtonText = Loc.T("recovery_fresh"),
                XamlRoot = xamlRoot
            };

            var result = await dlg.ShowAsync();

            if (dontAskAgain.IsChecked == true)
            {
                var prefs = AppConfig.Load();
                prefs.SessionRecoveryEnabled = false;
                AppConfig.Save(prefs);
            }

            // Declined: nothing to clean up — the pending flag was already
            // consumed, and the entry stays in the closed-session history menu.
            if (result != ContentDialogResult.Primary) return false;

            _splitHost = SplitHost.RestoreFromLayout(recovered.Layout!, projects);
            RootGrid.Children.Add(_splitHost);
            RestoreWindowSize(recovered);
            await _splitHost.InitializeTerminals();
            RefreshWorkspaceFlyout();
            return true;
        }

        private async System.Threading.Tasks.Task DelayedUpdateCheckAsync()
        {
            await System.Threading.Tasks.Task.Delay(2000);
            await CheckForUpdateAsync(silent: true);
        }

        private void OnActivated(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _splitHost?.FocusActive();
                RefreshStageButton();
            }
        }

        // ── Bottom-right toggles (Auto / Staging) ────────────────────────

        /// <summary>Toggle command-staging mode on the currently-active terminal.</summary>
        private void OnStageButtonClick(object sender, RoutedEventArgs e)
        {
            // ToggleButton has already flipped IsChecked; IsChecked is the intent.
            _splitHost?.ActiveTerminal?.SetStaging(StageButton.IsChecked == true);
        }

        /// <summary>Toggle the GLOBAL last-command info bar (same switch as Alt+L in a pane).</summary>
        private void OnLastCmdButtonClick(object sender, RoutedEventArgs e)
        {
            LastCmdBarManager.Set(LastCmdButton.IsChecked == true);
        }

        /// <summary>Keep the toolbar button in sync when the bar is flipped via Alt+L.</summary>
        private void OnLastCmdBarManagerChanged(bool on)
        {
            DispatcherQueue.TryEnqueue(() => LastCmdButton.IsChecked = on);
        }

        /// <summary>Toggle auto-confirm (自动回车) on the currently-active terminal.</summary>
        private void OnAutoButtonClick(object sender, RoutedEventArgs e)
        {
            _splitHost?.ActiveTerminal?.SetAutoConfirm(AutoButton.IsChecked == true);
        }

        /// <summary>Sync the toggle visuals to the active terminal (called on focus / pane switch).</summary>
        private void RefreshStageButton()
        {
            // Set IsChecked programmatically — only Click (not Checked/Unchecked)
            // runs the toggle logic, so this won't feed back into the pane.
            var t = _splitHost?.ActiveTerminal;
            StageButton.IsChecked = t?.StagingOn == true;
            AutoButton.IsChecked = t?.AutoConfirmOn == true;
        }

        // ── File manager panel (right-docked) ────────────────────────────

        private string? _filePanelRootedDir;

        /// <summary>Toggle the right-docked local file browser.</summary>
        private void OnFilesButtonClick(object sender, RoutedEventArgs e)
        {
            if (FilesButton.IsChecked == true)
            {
                FilePanelCol.Width = new GridLength(320);
                FilePanel.Visibility = Visibility.Visible;
                var wd = _splitHost?.ActiveTerminal?.WorkingDir;
                _filePanelRootedDir = wd;
                FilePanel.SetRoot(wd);
            }
            else
            {
                FilePanel.Visibility = Visibility.Collapsed;
                FilePanelCol.Width = new GridLength(0);
            }
        }

        /// <summary>On tab/pane switch, re-root the browser to the active project dir —
        /// only when that dir actually changed, so manual navigation isn't reset every
        /// time focus returns to a terminal in the same project.</summary>
        private void RefreshFilePanelRoot()
        {
            if (FilePanel.Visibility != Visibility.Visible) return;
            var wd = _splitHost?.ActiveTerminal?.WorkingDir;
            if (string.IsNullOrWhiteSpace(wd)) return;
            if (string.Equals(wd, _filePanelRootedDir, StringComparison.OrdinalIgnoreCase)) return;
            _filePanelRootedDir = wd;
            FilePanel.SetRoot(wd);
        }

        // ── Workspace mode ───────────────────────────────────────────────

        /// <summary>
        /// Show the workspace button and update title. Only called when a
        /// .ccpad-workspace file is explicitly opened.
        /// </summary>
        private void EnterWorkspaceMode()
        {
            WorkspaceButton.Visibility = Visibility.Visible;
            UpdateTitle();
            RefreshWorkspaceFlyout();
            ScheduleTopRightAdjust();
        }

        private void RefreshWorkspaceFlyout()
        {
            WorkspaceFlyout.Items.Clear();

            if (_currentWorkspaceFile != null)
            {
                // In workspace mode — save to current file
                var saveItem = new MenuFlyoutItem
                {
                    Text = Loc.T("ws_save"),
                    Icon = new FontIcon { Glyph = "\uE74E" }
                };
                saveItem.Click += (_, _) => SaveWorkspaceToCurrent();
                WorkspaceFlyout.Items.Add(saveItem);
            }

            // Always available
            var saveAsItem = new MenuFlyoutItem
            {
                Text = Loc.T("ws_saveas"),
                Icon = new FontIcon { Glyph = "\uE792" }
            };
            saveAsItem.Click += async (_, _) => await SaveWorkspaceAs();
            WorkspaceFlyout.Items.Add(saveAsItem);

            var saveTemplateItem = new MenuFlyoutItem
            {
                Text = Loc.T("template_save_new"),
                Icon = new FontIcon { Glyph = "\uE74E" }
            };
            saveTemplateItem.Click += (_, _) => SaveNewFrozenTemplate();
            WorkspaceFlyout.Items.Add(saveTemplateItem);

            var templateLibrary = new MenuFlyoutSubItem
            {
                Text = Loc.T("template_library"),
                Icon = new FontIcon { Glyph = "\uE8F1" }
            };
            PopulateFrozenTemplateMenu(templateLibrary);
            WorkspaceFlyout.Items.Add(templateLibrary);

            var openItem = new MenuFlyoutItem
            {
                Text = Loc.T("ws_open"),
                Icon = new FontIcon { Glyph = "\uE8E5" }
            };
            openItem.Click += async (_, _) => await OpenWorkspaceFromFile();
            WorkspaceFlyout.Items.Add(openItem);

            // Current file info
            var displayedFile = _currentWorkspaceFile ?? _openedTemplateFile;
            if (displayedFile != null)
            {
                WorkspaceFlyout.Items.Add(new MenuFlyoutSeparator());
                var infoItem = new MenuFlyoutItem
                {
                    Text = _openedTemplateName ?? System.IO.Path.GetFileName(displayedFile),
                    Icon = new FontIcon { Glyph = "\uE8F1" },
                    IsEnabled = false
                };
                WorkspaceFlyout.Items.Add(infoItem);
            }
        }

        // ── Open workspace ───────────────────────────────────────────────

        private async System.Threading.Tasks.Task OpenWorkspaceFromFile()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeFilter.Add(WorkspaceConfig.FileExtension);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var ws = WorkspaceConfig.LoadFromFile(file.Path);
            if (ws?.Layout == null) return;

            var projects = ProjectConfig.Load();
            if (_splitHost != null)
            {
                _splitHost.DisposeAll();
                RootGrid.Children.Remove(_splitHost);
            }

            _splitHost = SplitHost.RestoreFromLayout(ws.Layout, projects);
            RootGrid.Children.Add(_splitHost);
            RestoreWindowSize(ws);
            await _splitHost.InitializeTerminals();

            if (_autosaveSubscribed)
            {
                SubscribeSplitHostEvents();
                WriteRecoverySnapshot();
            }

            _currentWorkspaceFile = file.Path;
            _openedTemplateFile = null;
            _openedTemplateName = null;
            EnterWorkspaceMode();
        }

        // ── Save workspace ───────────────────────────────────────────────

        private void SaveWorkspaceToCurrent()
        {
            if (_splitHost == null || _currentWorkspaceFile == null) return;
            var snapshot = CreateSnapshot();
            WorkspaceConfig.SaveToFile(_currentWorkspaceFile, snapshot);
        }

        private async System.Threading.Tasks.Task SaveWorkspaceAs()
        {
            if (_splitHost == null) return;

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.SuggestedFileName = System.IO.Path.GetFileName(Environment.CurrentDirectory);
            picker.FileTypeChoices.Add(Loc.T("ws_filetype"), new List<string> { WorkspaceConfig.FileExtension });

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            var snapshot = CreateSnapshot();
            if (WorkspaceConfig.SaveToFile(file.Path, snapshot))
            {
                _currentWorkspaceFile = file.Path;
                _openedTemplateFile = null;
                _openedTemplateName = null;
                EnterWorkspaceMode();
            }
        }

        private void SaveNewFrozenTemplate()
        {
            if (_splitHost == null) return;
            var snapshot = CreateSnapshot();
            FrozenTemplateStore.SaveNew(snapshot, Loc.T("template_default_prefix"));
            RefreshWorkspaceFlyout();
        }

        private void PopulateFrozenTemplateMenu(MenuFlyoutSubItem menu)
        {
            var templates = FrozenTemplateStore.List();
            if (templates.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem
                {
                    Text = Loc.T("template_none"),
                    IsEnabled = false
                });
            }

            foreach (var template in templates)
            {
                var captured = template;
                var itemMenu = new MenuFlyoutSubItem { Text = template.Name };

                var openNew = new MenuFlyoutItem { Text = Loc.T("template_open_new") };
                openNew.Click += (_, _) => LaunchFrozenTemplate(captured.Path);
                itemMenu.Items.Add(openNew);

                var restoreHere = new MenuFlyoutItem { Text = Loc.T("template_restore_current") };
                restoreHere.Click += async (_, _) => await RestoreFrozenTemplateHere(captured);
                itemMenu.Items.Add(restoreHere);
                itemMenu.Items.Add(new MenuFlyoutSeparator());

                var overwrite = new MenuFlyoutItem { Text = Loc.T("template_overwrite") };
                overwrite.Click += async (_, _) => await OverwriteFrozenTemplate(captured);
                itemMenu.Items.Add(overwrite);

                var rename = new MenuFlyoutItem { Text = Loc.T("template_rename") };
                rename.Click += async (_, _) => await RenameFrozenTemplate(captured);
                itemMenu.Items.Add(rename);

                var export = new MenuFlyoutItem { Text = Loc.T("template_export") };
                export.Click += async (_, _) => await ExportFrozenTemplate(captured);
                itemMenu.Items.Add(export);

                var delete = new MenuFlyoutItem { Text = Loc.T("template_delete") };
                delete.Click += async (_, _) => await DeleteFrozenTemplate(captured);
                itemMenu.Items.Add(delete);

                menu.Items.Add(itemMenu);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            var import = new MenuFlyoutItem { Text = Loc.T("template_import") };
            import.Click += async (_, _) => await ImportFrozenTemplate();
            menu.Items.Add(import);
        }

        private void LaunchFrozenTemplate(string path)
        {
            try
            {
                var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "CCPad.exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                    ArgumentList = { path }
                });
            }
            catch (Exception ex)
            {
                App.LogStartupError("OpenFrozenTemplateInNewProcess", ex);
            }
        }

        private async System.Threading.Tasks.Task RestoreFrozenTemplateHere(FrozenTemplateStore.Item template)
        {
            if (template.Entry.Layout == null) return;
            if (!await ConfirmTemplateAction(
                    Loc.T("template_restore_title"),
                    Loc.T("template_restore_body"),
                    Loc.T("recovery_restore")))
                return;

            try
            {
                if (_splitHost != null)
                {
                    try
                    {
                        var current = CreateSnapshot();
                        if (current.Layout != null)
                            SessionRecovery.ArchiveClosed(current);
                    }
                    catch { }
                    _splitHost.DisposeAll();
                    RootGrid.Children.Remove(_splitHost);
                }

                _splitHost = SplitHost.RestoreFromLayout(template.Entry.Layout, ProjectConfig.Load());
                _splitHost.DisableFrozenPrewarm();
                RootGrid.Children.Add(_splitHost);
                RestoreWindowSize(template.Entry);
                await _splitHost.InitializeTerminals();

                _currentWorkspaceFile = null;
                _openedTemplateFile = template.Path;
                _openedTemplateName = template.Name;
                if (_autosaveSubscribed)
                {
                    SubscribeSplitHostEvents();
                    WriteRecoverySnapshot();
                }
                EnterWorkspaceMode();
            }
            catch (Exception ex)
            {
                App.LogStartupError("RestoreFrozenTemplateHere", ex);
            }
        }

        private async System.Threading.Tasks.Task OverwriteFrozenTemplate(FrozenTemplateStore.Item template)
        {
            if (_splitHost == null) return;
            if (!await ConfirmTemplateAction(
                    Loc.T("template_overwrite_title"),
                    Loc.T("template_overwrite_body", template.Name),
                    Loc.T("template_overwrite")))
                return;
            FrozenTemplateStore.Overwrite(template, CreateSnapshot());
            RefreshWorkspaceFlyout();
        }

        private async System.Threading.Tasks.Task RenameFrozenTemplate(FrozenTemplateStore.Item template)
        {
            if (Content?.XamlRoot == null) return;
            var input = new TextBox
            {
                Text = template.Name,
                MinWidth = 320
            };
            input.Loaded += (_, _) =>
            {
                input.Focus(FocusState.Programmatic);
                input.SelectAll();
            };
            var dialog = new ContentDialog
            {
                Title = Loc.T("template_rename"),
                Content = input,
                PrimaryButtonText = Loc.T("template_rename"),
                CloseButtonText = Loc.T("cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    FrozenTemplateStore.Rename(template, input.Text);
                    RefreshWorkspaceFlyout();
                }
            }
            catch (Exception ex) { App.LogStartupError("RenameFrozenTemplate", ex); }
        }

        private async System.Threading.Tasks.Task ExportFrozenTemplate(FrozenTemplateStore.Item template)
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.SuggestedFileName = template.Name;
            picker.FileTypeChoices.Add(
                Loc.T("template_filetype"),
                new List<string> { WorkspaceConfig.TemplateExtension });
            var file = await picker.PickSaveFileAsync();
            if (file != null)
                FrozenTemplateStore.Export(template, file.Path);
        }

        private async System.Threading.Tasks.Task ImportFrozenTemplate()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeFilter.Add(WorkspaceConfig.TemplateExtension);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            FrozenTemplateStore.Import(
                file.Path,
                System.IO.Path.GetFileNameWithoutExtension(file.Path));
            RefreshWorkspaceFlyout();
        }

        private async System.Threading.Tasks.Task DeleteFrozenTemplate(FrozenTemplateStore.Item template)
        {
            if (!await ConfirmTemplateAction(
                    Loc.T("template_delete_title"),
                    Loc.T("template_delete_body", template.Name),
                    Loc.T("template_delete")))
                return;
            if (FrozenTemplateStore.Delete(template) &&
                string.Equals(_openedTemplateFile, template.Path, StringComparison.OrdinalIgnoreCase))
            {
                _openedTemplateFile = null;
                _openedTemplateName = null;
                UpdateTitle();
            }
            RefreshWorkspaceFlyout();
        }

        private async System.Threading.Tasks.Task<bool> ConfirmTemplateAction(
            string title, string body, string primaryText)
        {
            if (Content?.XamlRoot == null) return false;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = primaryText,
                    CloseButtonText = Loc.T("cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot
                };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            catch { return false; }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private WorkspaceEntry CreateSnapshot()
        {
            var appWindow = GetAppWindow();
            var presenter = appWindow?.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            var isMaximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
            return new WorkspaceEntry
            {
                WindowWidth = appWindow?.Size.Width ?? 1200,
                WindowHeight = appWindow?.Size.Height ?? 800,
                WindowX = appWindow?.Position.X ?? -1,
                WindowY = appWindow?.Position.Y ?? -1,
                IsMaximized = isMaximized,
                Layout = _splitHost!.SnapshotLayout()
            };
        }

        private void RestoreWindowSize(WorkspaceEntry ws)
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;

            if (ws.WindowX >= 0 && ws.WindowY >= 0)
                appWindow.Move(new Windows.Graphics.PointInt32(ws.WindowX, ws.WindowY));

            if (ws.WindowWidth > 0 && ws.WindowHeight > 0)
                appWindow.Resize(new SizeInt32(ws.WindowWidth, ws.WindowHeight));

            if (ws.IsMaximized && appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.Maximize();
        }

        private AppWindow? GetAppWindow()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var wid = Win32Interop.GetWindowIdFromWindow(hwnd);
                return AppWindow.GetFromWindowId(wid);
            }
            catch { return null; }
        }

        private void UpdateTitle()
        {
            if (_currentWorkspaceFile != null)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(_currentWorkspaceFile);
                Title = $"{name} — CC Pad";
            }
            else if (_openedTemplateFile != null)
            {
                var name = _openedTemplateName
                    ?? System.IO.Path.GetFileNameWithoutExtension(_openedTemplateFile);
                Title = $"{name} — CC Pad ({Loc.T("template_filetype")})";
            }
            else
            {
                Title = "CC Pad";
            }
        }

        // ── About menu ─────────────────────────────────────────────────────

        private void InitAboutMenu()
        {
            // Rebuildable: cleared and repopulated on every language change.
            AboutFlyout.Items.Clear();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "dev";

            var aboutItem = new MenuFlyoutItem
            {
                Text = $"CC Pad v{versionStr}",
                Icon = new FontIcon { Glyph = "\uE946" },
                IsEnabled = false
            };
            AboutFlyout.Items.Add(aboutItem);

            var updateItem = new MenuFlyoutItem
            {
                Text = Loc.T("menu_check_update"),
                Icon = new FontIcon { Glyph = "\uECC5" }
            };
            updateItem.Click += async (_, _) => await CheckForUpdateAsync(silent: false);
            AboutFlyout.Items.Add(updateItem);

            _remoteMenuItem = new MenuFlyoutItem
            {
                Text = Loc.T("menu_remote"),
                Icon = new FontIcon { Glyph = "\uE774" }
            };
            _remoteMenuItem.Click += (_, _) => OnRemoteTerminalClick();
            AboutFlyout.Items.Add(_remoteMenuItem);

            AboutFlyout.Items.Add(new MenuFlyoutSeparator());

            var recoveryToggle = new ToggleMenuFlyoutItem
            {
                Text = Loc.T("menu_recovery_toggle"),
                Icon = new FontIcon { Glyph = "\uE777" },
                IsChecked = AppConfig.Load().SessionRecoveryEnabled
            };
            recoveryToggle.Click += (s, _) =>
            {
                var prefs = AppConfig.Load();
                prefs.SessionRecoveryEnabled = ((ToggleMenuFlyoutItem)s).IsChecked;
                AppConfig.Save(prefs);
                if (!prefs.SessionRecoveryEnabled)
                    SessionRecovery.DeleteOwnSnapshot();
            };
            AboutFlyout.Items.Add(recoveryToggle);

            // Closed-session history \u2014 repopulated every time the flyout opens.
            _closedSessionsMenu = new MenuFlyoutSubItem
            {
                Text = Loc.T("closed_menu"),
                Icon = new FontIcon { Glyph = "\uE823" } // Recent (clock)
            };
            AboutFlyout.Items.Add(_closedSessionsMenu);
            AboutFlyout.Opening -= OnAboutFlyoutOpening;
            AboutFlyout.Opening += OnAboutFlyoutOpening;

            var clearSessionItem = new MenuFlyoutItem
            {
                Text = Loc.T("menu_clear_recovery"),
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            clearSessionItem.Click += (_, _) => SessionRecovery.ClearAll();
            AboutFlyout.Items.Add(clearSessionItem);

            var confirmCloseToggle = new ToggleMenuFlyoutItem
            {
                Text = Loc.T("menu_confirm_close"),
                Icon = new FontIcon { Glyph = "\uE8BB" }, // ChromeClose
                IsChecked = AppConfig.Load().ConfirmOnClose
            };
            confirmCloseToggle.Click += (s, _) =>
            {
                var prefs = AppConfig.Load();
                prefs.ConfirmOnClose = ((ToggleMenuFlyoutItem)s).IsChecked;
                AppConfig.Save(prefs);
            };
            AboutFlyout.Items.Add(confirmCloseToggle);

            var notifyToggle = new ToggleMenuFlyoutItem
            {
                Text = Loc.T("menu_notify_toggle"),
                Icon = new FontIcon { Glyph = "" }, // bell / ringer
                IsChecked = AppConfig.Load().NotifyToastEnabled
            };
            notifyToggle.Click += (s, _) =>
            {
                var prefs = AppConfig.Load();
                prefs.NotifyToastEnabled = ((ToggleMenuFlyoutItem)s).IsChecked;
                AppConfig.Save(prefs);
            };
            AboutFlyout.Items.Add(notifyToggle);

            // Theme picker (dark = the all-black skin, light = original look, or follow OS)
            var themeItem = new MenuFlyoutSubItem
            {
                Text = Loc.T("menu_theme"),
                Icon = new FontIcon { Glyph = "" } // Personalize
            };
            foreach (var (code, key) in new[]
                     {
                         (ThemeManager.Dark, "theme_dark"),
                         (ThemeManager.Light, "theme_light"),
                         (ThemeManager.System, "theme_system"),
                     })
            {
                var c = code;
                var themeChoice = new ToggleMenuFlyoutItem
                {
                    Text = Loc.T(key),
                    IsChecked = ThemeManager.Pref == c
                };
                themeChoice.Click += (_, _) => ThemeManager.SetPref(c);
                themeItem.Items.Add(themeChoice);
            }
            AboutFlyout.Items.Add(themeItem);

            // Skip-permission-prompts toggle (default on — see CliMode.BuildCommand).
            // Affects newly launched tabs; existing sessions keep their launch mode.
            var bypassToggle = new ToggleMenuFlyoutItem
            {
                Text = Loc.T("menu_bypass_toggle"),
                Icon = new FontIcon { Glyph = "" }, // unlock
                IsChecked = AppConfig.Load().BypassPermissions
            };
            bypassToggle.Click += (s, _) =>
            {
                var prefs = AppConfig.Load();
                prefs.BypassPermissions = ((ToggleMenuFlyoutItem)s).IsChecked;
                AppConfig.Save(prefs);
            };
            AboutFlyout.Items.Add(bypassToggle);

            AboutFlyout.Items.Add(new MenuFlyoutSeparator());

            var languageItem = new MenuFlyoutSubItem
            {
                Text = Loc.T("menu_language"),
                Icon = new FontIcon { Glyph = "" } // LocaleLanguage globe
            };
            foreach (var lang in Loc.PickerOrder)
            {
                var code = lang;
                var langItem = new ToggleMenuFlyoutItem
                {
                    Text = Loc.DisplayName(code),
                    IsChecked = Loc.Lang == code
                };
                langItem.Click += (_, _) => Loc.SetLanguage(code);
                languageItem.Items.Add(langItem);
            }
            AboutFlyout.Items.Add(languageItem);

            AboutFlyout.Items.Add(new MenuFlyoutSeparator());

            var githubItem = new MenuFlyoutItem
            {
                Text = "GitHub",
                Icon = new FontIcon { Glyph = "\uE774" }
            };
            githubItem.Click += async (_, _) =>
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/vv-l/CCPad"));
            };
            AboutFlyout.Items.Add(githubItem);
        }

        // ── Freeze menu ────────────────────────────────────────────────────
        // Manual-first memory relief: freezing closes a tab's WebView2 page and
        // CLI process, leaving a click-to-restore placeholder in the tab. The
        // optional auto-freeze does the same to long-idle tabs, piggybacked on
        // the 30s resource timer.

        private void InitFreezeMenu()
        {
            // Rebuildable: cleared and repopulated on language change / threshold pick.
            FreezeFlyout.Items.Clear();

            var freezeIdle = new MenuFlyoutItem
            {
                Text = Loc.T("freeze_idle_all"),
                Icon = new FontIcon { Glyph = "" }
            };
            freezeIdle.Click += async (_, _) =>
            {
                if (_splitHost != null)
                    await _splitHost.FreezeAllAsync(idleOnly: true);
            };
            FreezeFlyout.Items.Add(freezeIdle);

            var freezeAll = new MenuFlyoutItem
            {
                Text = Loc.T("freeze_all"),
                Icon = new FontIcon { Glyph = "" }
            };
            freezeAll.Click += async (_, _) =>
            {
                if (_splitHost == null) return;
                int working = _splitHost.CountLive(workingOnly: true);
                if (working > 0 && !await ConfirmFreezeAllAsync(working)) return;
                await _splitHost.FreezeAllAsync(idleOnly: false);
            };
            FreezeFlyout.Items.Add(freezeAll);

            var unfreezeAll = new MenuFlyoutItem
            {
                Text = Loc.T("unfreeze_all"),
                Icon = new FontIcon { Glyph = "" } // play / resume
            };
            unfreezeAll.Click += async (_, _) =>
            {
                if (_splitHost != null)
                    await _splitHost.UnfreezeAllAsync();
            };
            FreezeFlyout.Items.Add(unfreezeAll);

            FreezeFlyout.Items.Add(new MenuFlyoutSeparator());

            var autoToggle = new ToggleMenuFlyoutItem
            {
                Text = Loc.T("autofreeze_toggle"),
                Icon = new FontIcon { Glyph = "" }, // Recent (clock)
                IsChecked = AppConfig.Load().AutoFreezeEnabled
            };
            autoToggle.Click += (s, _) =>
            {
                var prefs = AppConfig.Load();
                prefs.AutoFreezeEnabled = ((ToggleMenuFlyoutItem)s).IsChecked;
                AppConfig.Save(prefs);
            };
            FreezeFlyout.Items.Add(autoToggle);

            var delayItem = new MenuFlyoutSubItem
            {
                Text = Loc.T("autofreeze_delay"),
                Icon = new FontIcon { Glyph = "" } // Stopwatch
            };
            foreach (var (minutes, key) in new[]
                     {
                         (30, "dur_30m"),
                         (60, "dur_1h"),
                         (120, "dur_2h"),
                         (240, "dur_4h"),
                     })
            {
                var m = minutes;
                var choice = new ToggleMenuFlyoutItem
                {
                    Text = Loc.T(key),
                    IsChecked = AppConfig.Load().AutoFreezeMinutes == m
                };
                choice.Click += (_, _) =>
                {
                    var prefs = AppConfig.Load();
                    prefs.AutoFreezeMinutes = m;
                    AppConfig.Save(prefs);
                    InitFreezeMenu(); // refresh the radio checks
                };
                delayItem.Items.Add(choice);
            }
            FreezeFlyout.Items.Add(delayItem);
        }

        private async System.Threading.Tasks.Task<bool> ConfirmFreezeAllAsync(int workingCount)
        {
            if (Content?.XamlRoot == null) return true;
            try
            {
                var dlg = new ContentDialog
                {
                    Title = Loc.T("freeze_working_title"),
                    Content = Loc.T("freeze_all_working_body", workingCount),
                    PrimaryButtonText = Loc.T("freeze_confirm"),
                    CloseButtonText = Loc.T("cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                return await dlg.ShowAsync() == ContentDialogResult.Primary;
            }
            catch { return false; }
        }

        // Reentrancy guard: an auto-freeze pass can outlive one timer tick
        // (screenshots + teardown per tab), so ticks that land mid-pass skip.
        private bool _autoFreezeBusy;

        private async void RunAutoFreezeCheck()
        {
            if (_autoFreezeBusy || _splitHost == null) return;
            var prefs = AppConfig.Load();
            if (!prefs.AutoFreezeEnabled) return;
            _autoFreezeBusy = true;
            try
            {
                int minutes = Math.Max(5, prefs.AutoFreezeMinutes);
                await _splitHost.AutoFreezeIdleAsync(TimeSpan.FromMinutes(minutes));
            }
            catch { }
            finally { _autoFreezeBusy = false; }
        }

        /// <summary>
        /// Lay out the two top-right overlay buttons (Workspace + the per-panel
        /// Projects button) by measured size instead of hard-coded margins, so long
        /// localized labels (e.g. "Espacio de trabajo") don't overlap or clip. Posted
        /// low-priority so it runs after labels/layout have settled.
        /// </summary>
        private void ScheduleTopRightAdjust()
            => DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AdjustTopRightLayout);

        private void AdjustTopRightLayout()
        {
            try
            {
                bool wsVisible = WorkspaceButton.Visibility == Visibility.Visible;

                WorkspaceButton.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                double wsWidth = wsVisible ? WorkspaceButton.DesiredSize.Width : 0;

                double projWidth = _splitHost?.MaxProjectButtonWidth() ?? 64;
                const double footerPad = 8, gap = 8;

                // Park the workspace overlay just left of the project button.
                WorkspaceButton.Margin = new Thickness(0, 7, projWidth + footerPad + gap, 0);

                // Reserve matching blank space at the right of every tab strip.
                _splitHost?.SetWorkspaceReserve(wsVisible ? wsWidth + gap : 0);
            }
            catch { }
        }

        /// <summary>Apply localized tooltips/labels to the static XAML chrome.</summary>
        private void ApplyLocalizedChrome()
        {
            ToolTipService.SetToolTip(UpdateButton, Loc.T("tip_check_update"));
            ToolTipService.SetToolTip(AboutButton, Loc.T("tip_about"));
            AboutButtonLabel.Text = Loc.T("btn_about");
            ToolTipService.SetToolTip(FreezeButton, Loc.T("tip_freeze"));
            FreezeButtonLabel.Text = Loc.T("btn_freeze");
            ToolTipService.SetToolTip(WorkspaceButton, Loc.T("tip_workspace"));
            WorkspaceLabel.Text = Loc.T("btn_workspace");
            ToolTipService.SetToolTip(LastCmdButton, Loc.T("tip_lastcmd"));
            LastCmdButtonLabel.Text = Loc.T("btn_lastcmd");
            ToolTipService.SetToolTip(FilesButton, Loc.T("tip_files"));
            FilesButtonLabel.Text = Loc.T("btn_files");
            ToolTipService.SetToolTip(AutoButton, Loc.T("tip_auto"));
            AutoButtonLabel.Text = Loc.T("btn_auto");
            ToolTipService.SetToolTip(StageButton, Loc.T("tip_stage"));
            StageButtonLabel.Text = Loc.T("btn_stage");
        }

        /// <summary>Live language switch: rebuild menus + chrome in the new language.</summary>
        private void OnLanguageChanged()
        {
            try
            {
                InitAboutMenu();
                InitFreezeMenu();
                UpdateRemoteMenuItem(_webServer?.IsRunning == true);
                RefreshWorkspaceFlyout();
                ApplyLocalizedChrome();
                App.ReRegisterContextMenu();
                ScheduleTopRightAdjust();
            }
            catch { }
        }

        // ── Update check ────────────────────────────────────────────────

        private async System.Threading.Tasks.Task CheckForUpdateAsync(bool silent)
        {
            var info = await UpdateChecker.CheckAsync();
            if (info != null)
            {
                _releaseInfo = info;
                DispatcherQueue.TryEnqueue(() => UpdateButton.Visibility = Visibility.Visible);

                if (!silent)
                    DispatcherQueue.TryEnqueue(() => ShowUpdateDialog(info));
            }
            else if (!silent)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var dlg = new ContentDialog
                    {
                        Title = Loc.T("menu_check_update"),
                        Content = Loc.T("update_latest", UpdateChecker.GetCurrentVersion()),
                        CloseButtonText = Loc.T("ok"),
                        XamlRoot = Content.XamlRoot
                    };
                    await dlg.ShowAsync();
                });
            }
        }

        private async void OnUpdateButtonClick(object sender, RoutedEventArgs e)
        {
            if (_releaseInfo != null)
                ShowUpdateDialog(_releaseInfo);
            else
                await CheckForUpdateAsync(silent: false);
        }

        private async void ShowUpdateDialog(ReleaseInfo info)
        {
            var hasAsset = info.AssetUrl != null;
            var dlg = new ContentDialog
            {
                Title = Loc.T("update_found_title"),
                Content = Loc.T("update_found_body", info.Version, UpdateChecker.GetCurrentVersion()),
                PrimaryButtonText = hasAsset ? Loc.T("update_download_install") : Loc.T("update_goto_page"),
                CloseButtonText = Loc.T("later"),
                XamlRoot = Content.XamlRoot
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (!hasAsset)
            {
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri(UpdateChecker.ReleasesPageUrl));
                return;
            }

            // Show progress dialog
            var cts = new System.Threading.CancellationTokenSource();
            var progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 300,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var statusText = new TextBlock { Text = Loc.T("update_downloading", info.AssetName) };
            var panel = new StackPanel();
            panel.Children.Add(statusText);
            panel.Children.Add(progressBar);

            var progressDlg = new ContentDialog
            {
                Title = Loc.T("update_downloading_title"),
                Content = panel,
                CloseButtonText = Loc.T("cancel"),
                XamlRoot = Content.XamlRoot
            };
            progressDlg.CloseButtonClick += (_, _) => cts.Cancel();

            // Start download in background, show dialog concurrently
            string? localPath = null;
            Exception? downloadError = null;
            var downloadTask = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<int>(pct =>
                        DispatcherQueue.TryEnqueue(() => progressBar.Value = pct));
                    localPath = await UpdateChecker.DownloadAssetAsync(
                        info.AssetUrl!, info.AssetName!, progress, cts.Token);
                }
                catch (Exception ex)
                {
                    downloadError = ex;
                }
            });

            // Close progress dialog automatically when download finishes
            _ = downloadTask.ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(() => progressDlg.Hide()),
                System.Threading.Tasks.TaskScheduler.Default);

            await progressDlg.ShowAsync();
            await downloadTask;

            if (cts.IsCancellationRequested)
                return;

            if (downloadError != null || localPath == null)
            {
                var errDlg = new ContentDialog
                {
                    Title = Loc.T("update_failed_title"),
                    Content = Loc.T("update_failed_body"),
                    PrimaryButtonText = Loc.T("update_goto_page"),
                    CloseButtonText = Loc.T("close"),
                    XamlRoot = Content.XamlRoot
                };
                if (await errDlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    await Windows.System.Launcher.LaunchUriAsync(
                        new Uri(UpdateChecker.ReleasesPageUrl));
                }
                return;
            }

            // Launch installer and exit
            UpdateChecker.LaunchInstaller(localPath);
            Application.Current.Exit();
        }

        // ── Web server ────────────────────────────────────────────────────

        private MenuFlyoutItem? _remoteMenuItem;

        private async void OnRemoteTerminalClick()
        {
            if (_webServer?.IsRunning == true)
            {
                await ShowWebServerInfoDialog();
                return;
            }

            // Collect LAN addresses
            var addresses = WebTerminalServer.GetLanAddresses();
            if (addresses.Count == 0) addresses.Add("localhost");

            var tokenCheck = new CheckBox { Content = Loc.T("remote_token_enable"), IsChecked = false };

            var addressCombo = new ComboBox { Width = 300 };
            foreach (var addr in addresses) addressCombo.Items.Add(addr);
            addressCombo.SelectedIndex = 0;

            var portBox = new NumberBox
            {
                Value = 9220,
                Minimum = 1,
                Maximum = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 150,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var autoIncrementCheck = new CheckBox
            {
                Content = Loc.T("remote_autoincrement"),
                IsChecked = true
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("remote_intro"),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock { Text = Loc.T("remote_listen_addr"), FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)) });
            panel.Children.Add(addressCombo);
            panel.Children.Add(new TextBlock { Text = Loc.T("remote_port"), FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)), Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(portBox);
            panel.Children.Add(autoIncrementCheck);
            panel.Children.Add(tokenCheck);

            var dlg = new ContentDialog
            {
                Title = Loc.T("remote_start_title"),
                Content = panel,
                PrimaryButtonText = Loc.T("remote_start"),
                CloseButtonText = Loc.T("cancel"),
                XamlRoot = Content.XamlRoot
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var port = (int)portBox.Value;
            var autoIncrement = autoIncrementCheck.IsChecked == true;

            _webServer ??= new WebTerminalServer();
            _webServer.UseToken = tokenCheck.IsChecked == true;
            _webServer.Host = addressCombo.SelectedItem?.ToString() ?? "localhost";

            try
            {
                var (success, actualPort, error) = await _webServer.StartAsync(port, autoIncrement);
                if (!success)
                {
                    var errDlg = new ContentDialog
                    {
                        Title = Loc.T("remote_start_failed_title"),
                        Content = Loc.T("remote_start_failed_body", error),
                        CloseButtonText = Loc.T("ok"),
                        XamlRoot = Content.XamlRoot
                    };
                    await errDlg.ShowAsync();
                    return;
                }
                UpdateRemoteMenuItem(true);
                await ShowWebServerStartedDialog();
            }
            catch (Exception ex)
            {
                var errDlg = new ContentDialog
                {
                    Title = Loc.T("remote_start_failed_title"),
                    Content = Loc.T("remote_start_failed_body", ex.Message),
                    CloseButtonText = Loc.T("ok"),
                    XamlRoot = Content.XamlRoot
                };
                await errDlg.ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task ShowWebServerStartedDialog()
        {
            var url = _webServer!.GetAccessUrl();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = Loc.T("remote_started"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            var urlBox = new TextBox
            {
                Text = url,
                IsReadOnly = true,
                FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                FontSize = 13
            };
            panel.Children.Add(urlBox);

            if (_webServer.Token != null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("remote_token_warn"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150))
                });
            }

            var dlg = new ContentDialog
            {
                Title = Loc.T("menu_remote"),
                Content = panel,
                PrimaryButtonText = Loc.T("remote_copy_link"),
                SecondaryButtonText = Loc.T("remote_open_browser"),
                CloseButtonText = Loc.T("ok"),
                XamlRoot = Content.XamlRoot
            };

            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(url);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
        }

        private async System.Threading.Tasks.Task ShowWebServerInfoDialog()
        {
            var url = _webServer!.GetAccessUrl();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("remote_running_status", _webServer.ClientCount),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var urlBox = new TextBox
            {
                Text = url,
                IsReadOnly = true,
                FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                FontSize = 13
            };
            panel.Children.Add(urlBox);

            var dlg = new ContentDialog
            {
                Title = Loc.T("menu_remote"),
                Content = panel,
                PrimaryButtonText = Loc.T("remote_stop"),
                SecondaryButtonText = Loc.T("remote_open_browser"),
                CloseButtonText = Loc.T("ok"),
                XamlRoot = Content.XamlRoot
            };

            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await _webServer.StopAsync();
                _webServer = null;
                UpdateRemoteMenuItem(false);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
        }

        private void UpdateRemoteMenuItem(bool running)
        {
            if (_remoteMenuItem == null) return;
            _remoteMenuItem.Text = running ? Loc.T("menu_remote_running") : Loc.T("menu_remote");
        }

        // ── Auto-save on close (only if in workspace mode) ───────────────

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (!_closeConfirmed)
            {
                var prefs = AppConfig.Load();
                if (prefs.ConfirmOnClose && _splitHost != null && Content?.XamlRoot != null)
                {
                    // Cancel this close and ask first; the dialog re-enters Close()
                    // with _closeConfirmed set when the user confirms.
                    args.Handled = true;
                    if (!_confirmDialogOpen)
                        _ = ConfirmCloseAsync();
                    return;
                }
                // Confirmation disabled — honor the last checkbox choice directly.
                _restoreOnNextLaunch = prefs.RestoreOnClose;
            }

            Loc.LanguageChanged -= OnLanguageChanged;
            ThemeManager.PrefChanged -= OnThemePrefChanged;
            LastCmdBarManager.Changed -= OnLastCmdBarManagerChanged;

            _resourceTimer?.Stop();

            // Snapshot for the closed-session history must be taken while the
            // panes (and their session IDs / working dirs) are still alive.
            WorkspaceEntry? finalSnapshot = null;
            if (_splitHost != null)
            {
                try
                {
                    finalSnapshot = CreateSnapshot();
                    finalSnapshot.RestoreOnLaunch = _restoreOnNextLaunch;
                }
                catch { }
            }

            if (_splitHost != null)
            {
                try
                {
                    // Only auto-save if we're in a workspace
                    if (_currentWorkspaceFile != null)
                    {
                        var snapshot = CreateSnapshot();
                        WorkspaceConfig.SaveToFile(_currentWorkspaceFile, snapshot);
                    }
                }
                catch { }

                _splitHost.DisposeAll();
            }
            _webServer?.Dispose();
            Notify.ToastService.Unregister();

            // Clean exit — every close lands in the restorable history; the
            // checkbox only decides whether the next launch restores it
            // automatically or leaves it in the menu.
            try
            {
                if (finalSnapshot?.Layout != null)
                    SessionRecovery.ArchiveClosed(finalSnapshot);
                SessionRecovery.MarkClosedCleanly();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task ConfirmCloseAsync()
        {
            _confirmDialogOpen = true;
            try
            {
                var xamlRoot = Content?.XamlRoot;
                if (xamlRoot == null)
                {
                    _closeConfirmed = true;
                    Close();
                    return;
                }

                var prefs = AppConfig.Load();
                var restoreBox = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = Loc.T("close_restore_check"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    IsChecked = prefs.RestoreOnClose
                };
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("close_confirm_body"),
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(restoreBox);

                var dlg = new ContentDialog
                {
                    Title = Loc.T("close_confirm_title"),
                    Content = panel,
                    PrimaryButtonText = Loc.T("close_confirm_quit"),
                    CloseButtonText = Loc.T("cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };

                var result = await dlg.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                bool restore = restoreBox.IsChecked == true;
                if (prefs.RestoreOnClose != restore)
                {
                    prefs.RestoreOnClose = restore;
                    AppConfig.Save(prefs);
                }
                _restoreOnNextLaunch = restore;
                _closeConfirmed = true;
                Close();
            }
            catch
            {
                // Dialog failed (e.g. another ContentDialog already open) — never
                // leave the window unclosable.
                _closeConfirmed = true;
                _restoreOnNextLaunch = AppConfig.Load().RestoreOnClose;
                Close();
            }
            finally
            {
                _confirmDialogOpen = false;
            }
        }

        // ── Session-recovery autosave ───────────────────────────────────

        private void AttachAutosave()
        {
            if (_autosaveSubscribed || _splitHost == null) return;
            _autosaveSubscribed = true;

            // Throttle: schedule a single write 2s after the latest change.
            _autosaveTimer = DispatcherQueue.CreateTimer();
            _autosaveTimer.Interval = TimeSpan.FromSeconds(2);
            _autosaveTimer.IsRepeating = false;
            _autosaveTimer.Tick += (_, _) => WriteRecoverySnapshot();

            SubscribeSplitHostEvents();

            // Initial snapshot so a crash before any layout change still recovers.
            WriteRecoverySnapshot();
        }

        /// <summary>Events live on the SplitHost instance, so every code path that
        /// swaps _splitHost (open workspace, restore closed session) must re-run
        /// this or autosave silently stops following the new layout.</summary>
        private void SubscribeSplitHostEvents()
        {
            if (_splitHost == null) return;
            _splitHost.LayoutChanged += ScheduleAutosave;
            _splitHost.LayoutChanged += ScheduleTopRightAdjust; // re-reserve for new split panels
            _splitHost.ActivePaneChanged += RefreshStageButton; // keep the staging toggle in sync
            _splitHost.StagingChanged += RefreshStageButton;    // Alt+` hotkey re-syncs the button
            _splitHost.ActivePaneChanged += RefreshFilePanelRoot; // follow active project dir
        }

        private void ScheduleAutosave()
        {
            if (!AppConfig.Load().SessionRecoveryEnabled) return;
            _autosaveTimer?.Start(); // Start() restarts the countdown if already pending
        }

        private void WriteRecoverySnapshot()
        {
            if (_splitHost == null) return;
            if (!AppConfig.Load().SessionRecoveryEnabled) return;
            try
            {
                var snapshot = CreateSnapshot();
                SessionRecovery.SaveSnapshot(snapshot);
            }
            catch { }
        }

        /// <summary>Called by SplitHost when layout/tabs change.</summary>
        public void NotifyLayoutChanged() => ScheduleAutosave();

        // ── Closed-session history menu ─────────────────────────────────

        private void OnAboutFlyoutOpening(object? sender, object e) => RefreshClosedSessionsMenu();

        private void RefreshClosedSessionsMenu()
        {
            if (_closedSessionsMenu == null) return;
            _closedSessionsMenu.Items.Clear();

            var closed = SessionRecovery.ListClosed();
            if (closed.Count == 0)
            {
                _closedSessionsMenu.Items.Add(new MenuFlyoutItem
                {
                    Text = Loc.T("closed_none"),
                    IsEnabled = false
                });
                return;
            }

            foreach (var item in closed)
            {
                var captured = item;
                var entryItem = new MenuFlyoutItem
                {
                    Text = $"{item.ClosedAt:MM-dd HH:mm}  {DescribeLayout(item.Entry.Layout)}"
                };
                entryItem.Click += async (_, _) => await RestoreClosedSessionAsync(captured);
                _closedSessionsMenu.Items.Add(entryItem);
            }
        }

        /// <summary>Short human label for a saved layout: its first few tab names.</summary>
        private static string DescribeLayout(LayoutNode? node)
        {
            var names = new List<string>();
            CollectTabNames(node, names);
            if (names.Count == 0) return Loc.T("closed_unnamed");
            var text = string.Join(", ", names.GetRange(0, Math.Min(3, names.Count)));
            if (names.Count > 3) text += $" (+{names.Count - 3})";
            return text;
        }

        private static void CollectTabNames(LayoutNode? node, List<string> into)
        {
            if (node == null) return;
            if (node.Tabs != null)
                foreach (var t in node.Tabs)
                    if (!string.IsNullOrWhiteSpace(t.Name)) into.Add(t.Name);
            CollectTabNames(node.First, into);
            CollectTabNames(node.Second, into);
        }

        private async System.Threading.Tasks.Task RestoreClosedSessionAsync(SessionRecovery.ClosedEntry item)
        {
            var ws = item.Entry;
            if (ws.Layout == null) return;
            var projects = ProjectConfig.Load();

            try
            {
                if (_splitHost != null)
                {
                    var xamlRoot = Content?.XamlRoot;
                    if (xamlRoot != null)
                    {
                        var dlg = new ContentDialog
                        {
                            Title = Loc.T("closed_restore_title"),
                            Content = new TextBlock
                            {
                                Text = Loc.T("closed_restore_body"),
                                TextWrapping = TextWrapping.Wrap
                            },
                            PrimaryButtonText = Loc.T("recovery_restore"),
                            CloseButtonText = Loc.T("cancel"),
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = xamlRoot
                        };
                        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
                    }

                    // The replaced layout becomes a history entry itself, so this
                    // action is always undoable from the same menu.
                    try
                    {
                        var current = CreateSnapshot();
                        if (current.Layout != null)
                            SessionRecovery.ArchiveClosed(current);
                    }
                    catch { }

                    _splitHost.DisposeAll();
                    RootGrid.Children.Remove(_splitHost);
                }

                _splitHost = SplitHost.RestoreFromLayout(ws.Layout, projects);
                RootGrid.Children.Add(_splitHost);
                RestoreWindowSize(ws);
                await _splitHost.InitializeTerminals();

                if (_autosaveSubscribed)
                {
                    SubscribeSplitHostEvents();
                    WriteRecoverySnapshot();
                }
                RefreshWorkspaceFlyout();
            }
            catch (Exception ex)
            {
                App.LogStartupError("RestoreClosedSession", ex);
            }
        }

        // ── Memory-pressure guard ───────────────────────────────────────
        // Many parallel CC Pad windows (each with its WebView2 renderer chain and
        // CLI node processes) can exhaust RAM until every window — and its close
        // dialog — freezes. Warn via InfoBar before that point: once at launch
        // when this window joins an already-crowded/loaded system, and again at
        // runtime when load crosses the red line.

        private void StartResourceGuard()
        {
            if (_resourceTimer != null) return;
            _resourceTimer = DispatcherQueue.CreateTimer();
            _resourceTimer.Interval = TimeSpan.FromSeconds(30);
            _resourceTimer.IsRepeating = true;
            _resourceTimer.Tick += (_, _) =>
            {
                CheckResourcePressure(atLaunch: false);
                RunAutoFreezeCheck();
            };
            _resourceTimer.Start();

            // Launch check is delayed so this window's own WebView2/CLI spawn-up
            // is already counted in the reading it warns about.
            var initial = DispatcherQueue.CreateTimer();
            initial.Interval = TimeSpan.FromSeconds(5);
            initial.IsRepeating = false;
            initial.Tick += (_, _) => CheckResourcePressure(atLaunch: true);
            initial.Start();
        }

        private void CheckResourcePressure(bool atLaunch)
        {
            try
            {
                var snapshot = ResourceGuard.CaptureSnapshot();
                if (!snapshot.IsValid) return; // both probes failed — stay quiet

                bool physicalLaunchPressure =
                    snapshot.PhysicalLoadPercent >= ResourceGuard.WarnPhysicalLoadAtLaunch;
                bool physicalRuntimePressure =
                    snapshot.PhysicalLoadPercent >= ResourceGuard.WarnPhysicalLoadRuntime;
                bool commitLaunchPressure = snapshot.CommitLoadPercent > 0 &&
                    (snapshot.CommitLoadPercent >= ResourceGuard.WarnCommitLoadAtLaunch ||
                     snapshot.CommitAvailableGb < ResourceGuard.WarnCommitAvailableGb);
                bool commitRuntimePressure = snapshot.CommitLoadPercent > 0 &&
                    (snapshot.CommitLoadPercent >= ResourceGuard.WarnCommitLoadRuntime ||
                     snapshot.CommitAvailableGb < ResourceGuard.WarnCommitAvailableGb);

                if (atLaunch)
                {
                    int instances = ResourceGuard.CountInstances();
                    if (instances >= ResourceGuard.WarnInstanceCount ||
                        physicalLaunchPressure || commitLaunchPressure)
                    {
                        ResourceInfoBar.Title = Loc.T("mem_warn_title");
                        ResourceInfoBar.Message = Loc.T(
                            "mem_warn_launch_v2",
                            instances,
                            snapshot.PhysicalLoadPercent,
                            snapshot.PhysicalAvailableGb.ToString("0.0"),
                            snapshot.CommitLoadPercent.ToString("0.0"),
                            snapshot.CommitAvailableGb.ToString("0.0"));
                        ResourceInfoBar.IsOpen = true;
                        if (physicalRuntimePressure || commitRuntimePressure)
                            _resourceWarningArmed = false;
                    }
                    return;
                }

                if (physicalRuntimePressure || commitRuntimePressure)
                {
                    // One warning per pressure episode: re-arm only after the
                    // load has clearly come back down.
                    if (!_resourceWarningArmed) return;
                    _resourceWarningArmed = false;
                    ResourceInfoBar.Title = Loc.T("mem_warn_title");
                    ResourceInfoBar.Message = Loc.T(
                        "mem_warn_runtime_v2",
                        snapshot.PhysicalLoadPercent,
                        snapshot.PhysicalAvailableGb.ToString("0.0"),
                        snapshot.CommitLoadPercent.ToString("0.0"),
                        snapshot.CommitAvailableGb.ToString("0.0"));
                    ResourceInfoBar.IsOpen = true;
                }
                else if (snapshot.PhysicalLoadPercent < ResourceGuard.RearmBelowLoad &&
                         (snapshot.CommitLoadPercent <= 0 ||
                          (snapshot.CommitLoadPercent < ResourceGuard.RearmBelowLoad &&
                           snapshot.CommitAvailableGb >= ResourceGuard.RearmCommitAvailableGb)))
                {
                    _resourceWarningArmed = true;
                }
            }
            catch { }
        }
    }
}
