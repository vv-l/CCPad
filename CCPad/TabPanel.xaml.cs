using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CCPad.Localization;
using CCPad.Settings;
using CCPad.Terminal;
using CCPad.Web;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CCPad
{
    public sealed partial class TabPanel : UserControl
    {
        private int _tabCounter;
        private TerminalPane? _prewarmedPane;
        private List<ProjectEntry> _projects;
        private string? _defaultWorkingDir;
        private List<TabState>? _pendingRestoreTabs;
        private int _pendingRestoreActiveIndex;
        private int _prewarmVersion;
        private bool _disposed;

        public string? DefaultWorkingDir => _defaultWorkingDir;

        public event Action<TabPanel, SplitOrientation>? SplitRequested;
        public event Action<TabPanel>? CloseRequested;
        public event Action<TabPanel>? Focused;

        /// <summary>Fires when tabs are added/closed/switched (for autosave).</summary>
        public event Action? TabsChanged;

        private static string ResolveCliMode(string? requested)
            => requested ?? CliMode.LoadDefault();

        public TabPanel(List<ProjectEntry> projects)
        {
            InitializeComponent();
            _projects = projects;
            RefreshProjectFlyout();
            ApplyLocalizedChrome();

            ApplyTabHeight(TabHeightManager.Height);
            TabHeightManager.Changed += OnSharedTabHeightChanged;
            Loc.LanguageChanged += OnLanguageChanged;
            Unloaded += (_, _) =>
            {
                TabHeightManager.Changed -= OnSharedTabHeightChanged;
                Loc.LanguageChanged -= OnLanguageChanged;
            };
        }

        private void OnLanguageChanged()
        {
            try
            {
                RefreshProjectFlyout();
                ApplyLocalizedChrome();
            }
            catch { }
        }

        /// <summary>Localize the static tab-strip chrome (project button + resize handle).</summary>
        private void ApplyLocalizedChrome()
        {
            ProjectLabel.Text = Loc.T("btn_project");
            ToolTipService.SetToolTip(ProjectButton, Loc.T("tip_project"));
            ToolTipService.SetToolTip(ResizeHandle, Loc.T("tip_resize_tabs"));
        }

        /// <summary>Blank space reserved at the right of the tab strip for the global
        /// WorkspaceButton overlay. Driven by MainWindow so it tracks the button's
        /// actual (language-dependent) width.</summary>
        public void SetWorkspaceReserve(double width)
        {
            WorkspaceReserve.Width = width < 0 ? 0 : width;
        }

        /// <summary>Natural width of the project button (for laying out the overlay beside it).</summary>
        public double ProjectButtonDesiredWidth()
        {
            ProjectButton.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return ProjectButton.DesiredSize.Width;
        }

        // ── Tab-strip height sync + resize handle ───────────────────────

        // Approx. vertical padding above the tab row inside TabView's tab strip
        // (window-drag reserve area). Used to place the handle at the strip's bottom edge.
        private const double TabStripTopPadding = 8;

        private bool _dragging;
        private double _dragStartY;
        private double _dragStartHeight;
        private static readonly SolidColorBrush HandleHoverBrush =
            new(Windows.UI.Color.FromArgb(80, 76, 194, 255));
        private static readonly SolidColorBrush HandleTransparentBrush =
            new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        private void OnSharedTabHeightChanged(double h) => ApplyTabHeight(h);

        private void ApplyTabHeight(double h)
        {
            foreach (var item in Tabs.TabItems)
                if (item is TabViewItem tvi) tvi.Height = h;

            // Position the handle so its vertical center sits on the boundary
            // between tab strip and content.
            double topMargin = h + TabStripTopPadding - ResizeHandle.Height / 2;
            ResizeHandle.Margin = new Thickness(0, topMargin, 0, 0);
        }

        private void OnHandlePointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ResizeHandle.Background = HandleHoverBrush;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        }

        private void OnHandlePointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging) return;
            ResizeHandle.Background = HandleTransparentBrush;
            ProtectedCursor = null;
        }

        private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragging = true;
            _dragStartY = e.GetCurrentPoint(this).Position.Y;
            _dragStartHeight = TabHeightManager.Height;
            ResizeHandle.CapturePointer(e.Pointer);
        }

        private void OnHandlePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            var dy = e.GetCurrentPoint(this).Position.Y - _dragStartY;
            TabHeightManager.Height = _dragStartHeight + dy;
        }

        private void OnHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ResizeHandle.ReleasePointerCapture(e.Pointer);
            ResizeHandle.Background = HandleTransparentBrush;
            ProtectedCursor = null;
            TabHeightManager.Persist();
        }

        private void OnHandleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            TabHeightManager.Height = TabHeightManager.DefaultHeight;
            TabHeightManager.Persist();
        }

        public void UpdateProjects(List<ProjectEntry> projects)
        {
            _projects = projects;
            RefreshProjectFlyout();
        }

        // ── Public API ──────────────────────────────────────────────────

        public async Task AddFirstTab(string? projectName = null, string? workingDir = null, string? cliMode = null, string? resumeSessionId = null, string? tag = null)
        {
            if (workingDir != null)
                _defaultWorkingDir = workingDir;
            await AddNewTab(projectName, workingDir, cliMode, resumeSessionId, tag);
        }

        public void FocusCurrentTab()
        {
            if (Tabs.SelectedItem is TabViewItem item && item.Content is TerminalPane pane)
                pane.FocusTerminal();
        }

        /// <summary>The TerminalPane shown in the currently-selected tab, or null.</summary>
        public TerminalPane? CurrentPane => (Tabs.SelectedItem as TabViewItem)?.Content as TerminalPane;

        public void RefitAllTerminals()
        {
            foreach (var tab in Tabs.TabItems)
            {
                if (tab is TabViewItem tvi && tvi.Content is TerminalPane pane)
                    pane.Refit();
            }
        }

        // ── Tab management ──────────────────────────────────────────────

        private async Task AddNewTab(string? projectName = null, string? workingDir = null, string? cliMode = null, string? resumeSessionId = null, string? tag = null)
        {
            _tabCounter++;
            string mode = ResolveCliMode(cliMode);

            var (pane, prewarmed) = await AcquirePaneAsync();

            // Inject per-pane CLI notifications (no-op if the local listener
            // can't start). Claude uses --settings hooks; Codex uses a -c notify
            // override routed through CCPad.exe --notify. Needs pane.PaneId, so
            // the command is built after the pane is acquired.
            string extra = mode == CliMode.Codex
                ? CliNotify.PrepareCodexNotify(pane.PaneId)
                : CliNotify.PrepareClaudeHooks(pane.PaneId);
            string cmd = BuildLaunchCommand(mode, extra, resumeSessionId, pane, out bool resumed);

            var item = CreateTabItem(projectName, workingDir, pane, mode, tag);

            Tabs.TabItems.Add(item);
            Tabs.SelectedItem = item;

            if (prewarmed)
                pane.LaunchSession(cmd, workingDir, focusOnReady: true, cliMode: mode);
            else
                await pane.InitializeAsync(cmd, workingDir, focusOnReady: true, cliMode: mode);

            // A restore that asked for a conversation which no longer exists must
            // not LOOK like a successful resume.
            if (resumeSessionId != null && !resumed)
                pane.ShowNotice(Loc.T("resume_session_missing", resumeSessionId));

            PrewarmNextPane();
            TabsChanged?.Invoke();
        }

        /// <summary>
        /// Resolve the CLI command for a tab, resuming a saved conversation when a
        /// still-existing session ID was provided. Fresh Claude tabs get a UUID of our
        /// own via --session-id, so the snapshot always knows the conversation to
        /// resume; Codex assigns its own ID which is harvested at snapshot time.
        /// <paramref name="resumed"/> reports whether the command actually resumes —
        /// callers surface a notice when a requested resume silently fell back to a
        /// fresh session (the saved conversation file no longer exists).
        /// </summary>
        private static string BuildLaunchCommand(string mode, string extra, string? resumeSessionId, TerminalPane pane, out bool resumed)
        {
            resumed = false;
            if (mode == CliMode.Codex)
            {
                if (resumeSessionId != null && CliSessions.CodexSessionExists(resumeSessionId))
                {
                    pane.SessionId = resumeSessionId;
                    resumed = true;
                    return CliMode.BuildResumeCommand(mode, resumeSessionId, extra);
                }
                return CliMode.BuildCommand(mode, extra);
            }

            if (resumeSessionId != null && CliSessions.ClaudeSessionExists(resumeSessionId))
            {
                pane.SessionId = resumeSessionId;
                resumed = true;
                return CliMode.BuildResumeCommand(mode, resumeSessionId, extra);
            }

            var sessionId = Guid.NewGuid().ToString();
            pane.SessionId = sessionId;
            return CliMode.BuildCommand(mode, $"--session-id {sessionId}" + (extra.Length > 0 ? " " + extra : ""));
        }

        /// <summary>Subscribe every pane→panel event, remembering each handler so
        /// ctx.UnhookPane can detach them all — required when a tab migrates to
        /// another panel (the handlers close over THIS panel and its tab item).</summary>
        private void HookPane(TabViewItem item, TabCtx ctx, TerminalPane pane)
        {
            var unhook = new List<Action>();

            Action newTab = async () => await AddNewTab(null, _defaultWorkingDir);
            pane.NewTabRequested += newTab;
            unhook.Add(() => pane.NewTabRequested -= newTab);

            Action closeTab = CloseCurrentTab;
            pane.CloseTabRequested += closeTab;
            unhook.Add(() => pane.CloseTabRequested -= closeTab);

            Action splitH = () => SplitRequested?.Invoke(this, SplitOrientation.Horizontal);
            pane.SplitHorizontalRequested += splitH;
            unhook.Add(() => pane.SplitHorizontalRequested -= splitH);

            Action splitV = () => SplitRequested?.Invoke(this, SplitOrientation.Vertical);
            pane.SplitVerticalRequested += splitV;
            unhook.Add(() => pane.SplitVerticalRequested -= splitV);

            Action<string> navigate = dir =>
            {
                var direction = dir switch
                {
                    "left" => Direction.Left,
                    "right" => Direction.Right,
                    "up" => Direction.Up,
                    "down" => Direction.Down,
                    _ => (Direction?)null
                };
                if (direction.HasValue)
                    NavigateRequested?.Invoke(this, direction.Value);
            };
            pane.NavigateRequested += navigate;
            unhook.Add(() => pane.NavigateRequested -= navigate);

            Action closePane = () => CloseRequested?.Invoke(this);
            pane.ClosePaneRequested += closePane;
            unhook.Add(() => pane.ClosePaneRequested -= closePane);

            Action staging = () => StagingChanged?.Invoke();
            pane.StagingChanged += staging;
            unhook.Add(() => pane.StagingChanged -= staging);

            Action focused = () => Focused?.Invoke(this);
            pane.PaneFocused += focused;
            unhook.Add(() => pane.PaneFocused -= focused);

            Action<TerminalPane> status = p => OnPaneStatusChanged(p, item, ctx.Dot);
            pane.StatusChanged += status;
            unhook.Add(() => pane.StatusChanged -= status);

            Action reveal = () => RevealTab(item);
            pane.RevealRequested += reveal;
            unhook.Add(() => pane.RevealRequested -= reveal);

            ctx.UnhookPane = () =>
            {
                foreach (var u in unhook) u();
                ctx.UnhookPane = null;
            };
        }

        /// <summary>Unhook the pane's panel-bound event handlers and forget it.
        /// The pane itself stays alive — callers dispose or re-home it.</summary>
        private static void DetachPaneHooks(TabCtx ctx)
        {
            ctx.UnhookPane?.Invoke();
            ctx.Pane = null;
        }

        public event Action<TabPanel, Direction>? NavigateRequested;
        /// <summary>Bubbled up when a pane toggles staging via the Alt+` hotkey.</summary>
        public event Action? StagingChanged;

        /// <summary>Set by the host: returns the session IDs owned by every tab in
        /// the whole window except the given one. Keeps the disk-scan fallback in
        /// ResolveSessionId from handing one conversation to two tabs — even when
        /// they live in different split panels (the bug that produced duplicate
        /// frozen-tab session IDs).</summary>
        internal Func<TabViewItem?, ISet<string>>? ClaimedSessionIds { get; set; }

        /// <summary>Add the session IDs owned by this panel's tabs (frozen snapshot
        /// or live pane) to <paramref name="into"/>, skipping <paramref name="except"/>.</summary>
        internal void CollectOwnedSessionIds(ISet<string> into, TabViewItem? except)
        {
            foreach (var t in Tabs.TabItems)
            {
                if (t is not TabViewItem tvi || ReferenceEquals(tvi, except)) continue;
                var c = CtxOf(tvi);
                var id = c?.FrozenState?.SessionId ?? c?.Pane?.SessionId;
                if (!string.IsNullOrEmpty(id)) into.Add(id!);
            }
        }

        private ISet<string> ClaimedFor(TabViewItem? except)
        {
            if (ClaimedSessionIds != null) return ClaimedSessionIds(except);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectOwnedSessionIds(set, except);
            return set;
        }

        /// <summary>
        /// Acquire a pane for a new/thawed tab, preferring the prewarmed one.
        /// The prewarmed page may have sat hidden for hours, and Chromium
        /// reclaims invisible renderers under memory pressure — mounting a
        /// dead one gives a permanent white pane. Probe it; if it doesn't
        /// answer, fall back to building a fresh pane the slow way.
        /// </summary>
        private async Task<(TerminalPane Pane, bool Prewarmed)> AcquirePaneAsync()
        {
            // This panel is (about to be) live again — give up its claim on the
            // one warm renderer reserved for fully-frozen panels.
            ReleaseFrozenPrewarm();
            TerminalPane? pane = _prewarmedPane;
            if (pane != null)
            {
                _prewarmedPane = null;
                PrewarmHost.Children.Remove(pane);
            }
            else
            {
                // No warm pane of our own — borrow the app-wide one reserved by
                // the fully-frozen holder panel. Panes re-home across panels in
                // the same window, so a thaw anywhere can start warm instead of
                // paying the WebView2 cold start.
                pane = StealSharedWarmPane();
            }
            bool prewarmed = pane != null;
            if (pane == null)
            {
                pane = new TerminalPane();
            }
            else if (!await pane.PingAsync(1000))
            {
                pane.Dispose();
                pane = new TerminalPane();
                prewarmed = false;
            }
            return (pane, prewarmed);
        }

        /// <summary>Take the warm pane stashed by the app-wide frozen-prewarm
        /// holder, restocking it so the next thaw is warm too. Null when nobody
        /// holds one.</summary>
        private static TerminalPane? StealSharedWarmPane()
        {
            var holder = s_frozenPrewarmHolder;
            var pane = holder?._prewarmedPane;
            if (holder == null || pane == null || holder._disposed) return null;
            holder._prewarmedPane = null;
            holder.PrewarmHost.Children.Remove(pane);
            holder.PrewarmNextPane();
            return pane;
        }

        // ── Per-tab context ─────────────────────────────────────────────
        // One TabCtx lives on every TabViewItem.Tag. Content is the live
        // TerminalPane while running, or the frozen placeholder while frozen;
        // the ctx tracks which, and owns the header widgets that must survive
        // the pane being swapped out (status dot, tag badge, menu items).

        private sealed class TabCtx
        {
            public TerminalPane? Pane;          // null while frozen
            public TabState? FrozenState;       // null while live
            public Microsoft.UI.Xaml.Media.ImageSource? FrozenShot; // last screenshot, survives moves/thaw failures
            public Action? UnhookPane;          // unsubscribes every pane→panel event (set by HookPane)
            public bool Busy;                   // freeze/unfreeze in flight
            public string HeaderBase = "";
            public string Mode = "";
            public string TagValue = "";        // single source of truth for the tab tag
            public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!;
            public Border TagBadge = null!;
            public TextBlock TagText = null!;
            public TextBlock? FrozenHint;       // placeholder status line, set while frozen
            public MenuFlyoutItem FreezeItem = null!;
            public MenuFlyoutItem UnfreezeItem = null!;
        }

        private static TabCtx? CtxOf(TabViewItem item) => item.Tag as TabCtx;

        // Tag non-default CLI tabs so mixed Claude/Codex panes are visually distinguishable.
        private static string HeaderFor(string baseHeader, string cliMode)
            => cliMode == CliMode.Codex ? $"{baseHeader} · Codex" : baseHeader;

        private TabViewItem CreateTabItem(string? projectName, string? workingDir, TerminalPane pane, string cliMode, string? tag = null)
        {
            string baseHeader = projectName
                ?? (workingDir != null ? System.IO.Path.GetFileName(workingDir) : null)
                ?? $"Terminal {_tabCounter}";
            var (item, tabCtx) = BuildTabItem(baseHeader, cliMode, tag);
            AttachPane(item, tabCtx, pane);
            item.Content = pane;
            return item;
        }

        /// <summary>Chrome shared by live and frozen tabs: header (status dot +
        /// title + tag badge), context menu, and the TabCtx stored on item.Tag.
        /// The caller sets Content (a pane via AttachPane, or a frozen placeholder).</summary>
        private (TabViewItem Item, TabCtx Ctx) BuildTabItem(string baseHeader, string cliMode, string? tag)
        {
            var tabCtx = new TabCtx { HeaderBase = baseHeader, Mode = cliMode };

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = StatusBrush(PaneStatus.Waiting)
            };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(dot);
            headerPanel.Children.Add(new TextBlock
            {
                Text = HeaderFor(baseHeader, cliMode),
                VerticalAlignment = VerticalAlignment.Center
            });

            // User tag badge: a small pill after the title. Ellipsized past MaxWidth;
            // the full text is available via tooltip (set in ApplyTag).
            var tagText = new TextBlock
            {
                FontSize = 11,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var tagBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 0, 6, 1),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = TagBadgeBackground,
                BorderBrush = TagBadgeBorder,
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed,
                Child = tagText
            };
            headerPanel.Children.Add(tagBadge);

            tabCtx.Dot = dot;
            tabCtx.TagBadge = tagBadge;
            tabCtx.TagText = tagText;
            ApplyTag(tabCtx, tag);

            var item = new TabViewItem
            {
                Header = headerPanel,
                IsClosable = true,
                Height = TabHeightManager.Height,
                Tag = tabCtx
            };

            var ctx = new MenuFlyout();

            var setTag = new MenuFlyoutItem
            {
                Text = Loc.T("tab_tag_set"),
                Icon = new FontIcon { Glyph = "" }
            };
            setTag.Click += async (_, _) => await EditTagAsync(tabCtx);

            var freezeTab = new MenuFlyoutItem
            {
                Text = Loc.T("tab_freeze"),
                Icon = new FontIcon { Glyph = "" }
            };
            freezeTab.Click += async (_, _) => await FreezeTabAsync(item, confirmIfWorking: true);
            tabCtx.FreezeItem = freezeTab;

            var unfreezeTab = new MenuFlyoutItem
            {
                Text = Loc.T("tab_unfreeze"),
                Icon = new FontIcon { Glyph = "" }
            };
            unfreezeTab.Click += async (_, _) => await UnfreezeTabAsync(item);
            tabCtx.UnfreezeItem = unfreezeTab;

            var splitRight = new MenuFlyoutItem
            {
                Text = Loc.T("tab_split_right"),
                Icon = new FontIcon { Glyph = "\uEA61" },
                KeyboardAcceleratorTextOverride = "Alt+Shift+="
            };
            splitRight.Click += (_, _) => SplitRequested?.Invoke(this, SplitOrientation.Vertical);

            var splitDown = new MenuFlyoutItem
            {
                Text = Loc.T("tab_split_down"),
                Icon = new FontIcon { Glyph = "\uE745" },
                KeyboardAcceleratorTextOverride = "Alt+Shift+-"
            };
            splitDown.Click += (_, _) => SplitRequested?.Invoke(this, SplitOrientation.Horizontal);

            var closeTab = new MenuFlyoutItem
            {
                Text = Loc.T("tab_close"),
                Icon = new FontIcon { Glyph = "\uE711" },
                KeyboardAcceleratorTextOverride = "Ctrl+W"
            };
            closeTab.Click += (_, _) => CloseTab(item);

            var closeOthers = new MenuFlyoutItem
            {
                Text = Loc.T("tab_close_others"),
                Icon = new FontIcon { Glyph = "\uE89B" },
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+W"
            };
            closeOthers.Click += (_, _) => CloseOtherTabs(item);

            var closeLeft = new MenuFlyoutItem
            {
                Text = Loc.T("tab_close_left"),
                Icon = new FontIcon { Glyph = "\uE746" }
            };
            closeLeft.Click += (_, _) => CloseTabsToSide(item, left: true);

            var closeRight = new MenuFlyoutItem
            {
                Text = Loc.T("tab_close_right"),
                Icon = new FontIcon { Glyph = "\uEA61" }
            };
            closeRight.Click += (_, _) => CloseTabsToSide(item, left: false);

            ctx.Items.Add(setTag);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(freezeTab);
            ctx.Items.Add(unfreezeTab);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(splitRight);
            ctx.Items.Add(splitDown);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(closeTab);
            ctx.Items.Add(closeOthers);
            //ctx.Items.Add(closeLeft);
            //ctx.Items.Add(closeRight);

            // Freeze/unfreeze are mutually exclusive — show whichever applies now.
            ctx.Opening += (_, _) =>
            {
                tabCtx.FreezeItem.Visibility = tabCtx.Pane != null ? Visibility.Visible : Visibility.Collapsed;
                tabCtx.UnfreezeItem.Visibility = tabCtx.FrozenState != null ? Visibility.Visible : Visibility.Collapsed;
            };

            item.ContextFlyout = ctx;
            return (item, tabCtx);
        }

        /// <summary>Bind a live pane to a tab: label/tag mirror the ctx, and the
        /// status dot + toast reveal follow the pane. Called at creation and on thaw.</summary>
        private void AttachPane(TabViewItem item, TabCtx ctx, TerminalPane pane)
        {
            ctx.Pane = pane;
            ctx.FrozenState = null;
            ctx.FrozenHint = null;
            ctx.FrozenShot = null;
            pane.Label = HeaderFor(ctx.HeaderBase, ctx.Mode);
            pane.TabTag = ctx.TagValue;
            HookPane(item, ctx, pane);
        }

        // ── Tab tag badge ───────────────────────────────────────────────

        // Same blue family as the resize-handle hover; translucent so the
        // inherited header foreground stays readable in both themes.
        private static readonly SolidColorBrush TagBadgeBackground =
            new(Windows.UI.Color.FromArgb(46, 76, 194, 255));
        private static readonly SolidColorBrush TagBadgeBorder =
            new(Windows.UI.Color.FromArgb(102, 76, 194, 255));

        private static void ApplyTag(TabCtx ctx, string? tag)
        {
            ctx.TagValue = tag?.Trim() ?? "";
            // Propagate to whichever backing store is current, so both live
            // snapshots (pane.TabTag) and frozen ones (FrozenState.Tag) persist it.
            if (ctx.Pane != null) ctx.Pane.TabTag = ctx.TagValue;
            if (ctx.FrozenState != null) ctx.FrozenState.Tag = ctx.TagValue;
            ctx.TagText.Text = ctx.TagValue;
            bool has = ctx.TagValue.Length > 0;
            ctx.TagBadge.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            // Hovering shows the full tag (the badge itself ellipsizes past MaxWidth).
            ToolTipService.SetToolTip(ctx.TagBadge, has ? ctx.TagValue : null);
        }

        private bool _tagDialogOpen;

        private async Task EditTagAsync(TabCtx ctx)
        {
            if (_tagDialogOpen || XamlRoot == null) return;
            _tagDialogOpen = true;
            try
            {
                var box = new TextBox
                {
                    Text = ctx.TagValue,
                    PlaceholderText = Loc.T("tag_placeholder"),
                    MaxLength = 100
                };
                box.SelectionStart = box.Text.Length;

                var dlg = new ContentDialog
                {
                    Title = Loc.T("tag_dialog_title"),
                    Content = box,
                    PrimaryButtonText = Loc.T("tag_save"),
                    SecondaryButtonText = Loc.T("tag_clear"),
                    CloseButtonText = Loc.T("cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);

                var result = await dlg.ShowAsync();
                if (result == ContentDialogResult.Primary)
                    ApplyTag(ctx, box.Text);
                else if (result == ContentDialogResult.Secondary)
                    ApplyTag(ctx, "");
                else
                    return;
                TabsChanged?.Invoke();
            }
            catch { }
            finally { _tagDialogOpen = false; }
        }

        // ── Tab status light ────────────────────────────────────────────

        private static readonly SolidColorBrush WorkingBrush =
            new(Windows.UI.Color.FromArgb(255, 63, 185, 80));   // green  #3FB950
        private static readonly SolidColorBrush WaitingBrush =
            new(Windows.UI.Color.FromArgb(255, 227, 179, 65));  // amber  #E3B341
        private static readonly SolidColorBrush DisconnectedBrush =
            new(Windows.UI.Color.FromArgb(255, 248, 81, 73));   // red    #F85149

        private static SolidColorBrush StatusBrush(PaneStatus status) => status switch
        {
            PaneStatus.Waiting => WaitingBrush,
            PaneStatus.Disconnected => DisconnectedBrush,
            _ => WorkingBrush,
        };

        private void OnPaneStatusChanged(TerminalPane pane, TabViewItem item, Microsoft.UI.Xaml.Shapes.Ellipse dot)
        {
            dot.Fill = StatusBrush(pane.Status);

            // Toast only when a tab needs you and you're not already looking at it.
            if (pane.Status == PaneStatus.Waiting && !IsTabActivelyVisible(item))
                Notify.ToastService.ShowWaiting(pane.PaneId, pane.Label);
        }

        private bool IsTabActivelyVisible(TabViewItem item)
            => App.IsMainWindowForeground() && ReferenceEquals(Tabs.SelectedItem, item);

        private void RevealTab(TabViewItem item)
        {
            App.ActivateMainWindow();
            Tabs.SelectedItem = item;
            if (item.Content is TerminalPane pane)
                pane.FocusTerminal();
        }

        private void CloseCurrentTab()
        {
            if (Tabs.SelectedItem is TabViewItem item)
                CloseTab(item);
        }

        private void CloseOtherTabs(TabViewItem keep)
        {
            var toClose = Tabs.TabItems.Cast<TabViewItem>().Where(t => t != keep).ToList();
            foreach (var t in toClose)
            {
                if (t.Content is TerminalPane pane)
                    pane.Dispose();
                Tabs.TabItems.Remove(t);
            }
        }

        private void CloseTabsToSide(TabViewItem anchor, bool left)
        {
            int idx = Tabs.TabItems.IndexOf(anchor);
            var toClose = Tabs.TabItems.Cast<TabViewItem>()
                .Where((t, i) => left ? i < idx : i > idx).ToList();
            foreach (var t in toClose)
            {
                if (t.Content is TerminalPane pane)
                    pane.Dispose();
                Tabs.TabItems.Remove(t);
            }
        }

        private void CloseTab(TabViewItem item)
        {
            if (Tabs.TabItems.Count <= 1)
            {
                // Last tab — close the whole panel
                CloseRequested?.Invoke(this);
                return;
            }
            if (item.Content is TerminalPane pane)
                pane.Dispose();
            Tabs.TabItems.Remove(item);
            TabsChanged?.Invoke();
        }

        private async void OnAddTab(TabView sender, object args) => await AddNewTab(null, _defaultWorkingDir);

        private void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
            => CloseTab(args.Tab);

        private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TabsChanged?.Invoke();
            if (Tabs.SelectedItem is TabViewItem item && item.Content is TerminalPane pane)
            {
                // 等待 TabView 完成所有指针事件处理（PointerReleased）后再聚焦终端
                await Task.Delay(50);
                pane.FocusTerminal();
            }
        }

        // ── Freeze / thaw ───────────────────────────────────────────────
        // Freezing tears down the whole TerminalPane (WebView2 renderer AND the
        // CLI's node process — the latter is the bigger memory consumer) and
        // leaves a lightweight placeholder in the tab. Thawing builds a fresh
        // pane through the normal resume pipeline (claude --resume / codex resume).

        private static readonly SolidColorBrush FrozenBrush =
            new(Windows.UI.Color.FromArgb(255, 76, 194, 255));  // blue  #4CC2FF
        private static readonly SolidColorBrush FrozenTextBrush =
            new(Windows.UI.Color.FromArgb(255, 220, 220, 220));

        /// <summary>Live (unfrozen) tabs; workingOnly counts only panes whose CLI
        /// is currently running a command.</summary>
        public int CountLive(bool workingOnly = false)
        {
            int n = 0;
            foreach (var t in Tabs.TabItems)
                if (t is TabViewItem tvi && CtxOf(tvi)?.Pane is TerminalPane p &&
                    (!workingOnly || p.Status == PaneStatus.Working))
                    n++;
            return n;
        }

        public int CountFrozen()
        {
            int n = 0;
            foreach (var t in Tabs.TabItems)
                if (t is TabViewItem tvi && CtxOf(tvi)?.FrozenState != null)
                    n++;
            return n;
        }

        /// <summary>Freeze one tab: persist its conversation + capture a screenshot,
        /// then dispose the pane. The tab stays in the strip as a placeholder.</summary>
        public async Task<bool> FreezeTabAsync(TabViewItem item, bool confirmIfWorking)
        {
            var ctx = CtxOf(item);
            if (ctx == null || ctx.Busy || ctx.Pane is not TerminalPane pane) return false;

            if (confirmIfWorking && pane.Status == PaneStatus.Working &&
                !await ConfirmFreezeWorkingAsync(1))
                return false;

            ctx.Busy = true;
            try
            {
                var dir = pane.WorkingDir ?? _defaultWorkingDir;
                var state = new TabState
                {
                    Name = ctx.HeaderBase,
                    WorkingDir = string.IsNullOrEmpty(dir) ? "" : dir,
                    CliMode = string.IsNullOrEmpty(pane.CliMode) ? ctx.Mode : pane.CliMode,
                    // Exclude IDs owned by every other tab in the window, so two
                    // same-cwd tabs can't freeze onto the same conversation.
                    SessionId = ResolveSessionId(pane, dir, ClaimedFor(item)),
                    Tag = ctx.TagValue,
                    Frozen = true
                };

                // Screenshot before teardown so the placeholder shows the last frame.
                var shot = await pane.CaptureSnapshotAsync();

                DetachPaneHooks(ctx);
                ctx.FrozenState = state;
                ctx.FrozenShot = shot;
                item.Content = BuildFrozenPlaceholder(item, ctx, state, shot);
                ctx.Dot.Fill = FrozenBrush;
                pane.Dispose();

                // A fully-frozen panel keeps at most ONE warm renderer per app
                // (first panel wins) so the next thaw skips the WebView2 cold
                // start; every other fully-frozen panel drops to zero processes.
                if (CountLive() == 0)
                {
                    if (ClaimFrozenPrewarm())
                    {
                        if (_prewarmedPane == null)
                            PrewarmNextPane();
                    }
                    else
                    {
                        DropPrewarm();
                    }
                }

                TabsChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                // Freeze failed mid-flight — the tab stays live (worst case the
                // pane is already half torn down and shows its own error overlay).
                // Log it: a silent failure here reads as "freeze did nothing".
                App.LogStartupError("FreezeTab", ex);
                return false;
            }
            finally { ctx.Busy = false; }
        }

        /// <summary>Thaw a frozen tab: fresh pane through the normal resume pipeline.</summary>
        public async Task UnfreezeTabAsync(TabViewItem item)
        {
            var ctx = CtxOf(item);
            if (ctx == null || ctx.Busy || ctx.FrozenState is not TabState state) return;

            var shot = ctx.FrozenShot;
            ctx.Busy = true;
            if (ctx.FrozenHint != null) ctx.FrozenHint.Text = Loc.T("frozen_restoring");
            try
            {
                string mode = string.IsNullOrEmpty(state.CliMode) ? ResolveCliMode(null) : state.CliMode;
                string? dir = string.IsNullOrEmpty(state.WorkingDir) ? _defaultWorkingDir : state.WorkingDir;
                string? session = string.IsNullOrEmpty(state.SessionId) ? null : state.SessionId;

                var (pane, prewarmed) = await AcquirePaneAsync();
                string extra = mode == CliMode.Codex
                    ? CliNotify.PrepareCodexNotify(pane.PaneId)
                    : CliNotify.PrepareClaudeHooks(pane.PaneId);
                string cmd = BuildLaunchCommand(mode, extra, session, pane, out bool resumed);

                ctx.Mode = mode;
                AttachPane(item, ctx, pane);
                item.Content = pane;
                ctx.Dot.Fill = StatusBrush(PaneStatus.Waiting);

                // Don't steal focus when thawing a background tab (batch unfreeze).
                bool focus = ReferenceEquals(Tabs.SelectedItem, item);
                if (prewarmed)
                    pane.LaunchSession(cmd, dir, focusOnReady: focus, cliMode: mode);
                else
                    await pane.InitializeAsync(cmd, dir, focusOnReady: focus, cliMode: mode);

                // The frozen conversation is gone from disk — a thaw must not
                // LOOK like a successful resume.
                if (session != null && !resumed)
                    pane.ShowNotice(Loc.T("resume_session_missing", session));

                PrewarmNextPane();
                TabsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                App.LogStartupError("UnfreezeTab", ex);
                // If the failure hit after AttachPane the tab is showing a dead,
                // never-initialized pane and the frozen state has been cleared —
                // rebuild the placeholder so the tab stays frozen and retryable
                // instead of stranding a white unclickable pane.
                if (item.Content is TerminalPane dead)
                {
                    DetachPaneHooks(ctx);
                    dead.Dispose();
                }
                ctx.Pane = null;
                ctx.FrozenState = state;
                ctx.FrozenShot = shot;
                item.Content = BuildFrozenPlaceholder(item, ctx, state, shot);
                ctx.Dot.Fill = FrozenBrush;
                if (ctx.FrozenHint != null)
                    ctx.FrozenHint.Text = Loc.T("frozen_thaw_failed");
            }
            finally { ctx.Busy = false; }
        }

        /// <summary>Placeholder shown in place of a frozen pane: the last screenshot
        /// dimmed under a snowflake + hint. Click anywhere to thaw.</summary>
        private Grid BuildFrozenPlaceholder(TabViewItem item, TabCtx ctx, TabState state, ImageSource? shot)
        {
            var root = new Grid
            {
                // Fixed dark backdrop (not theme-dependent) so the dimmed
                // screenshot and light text always read correctly.
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12))
            };
            if (shot != null)
            {
                root.Children.Add(new Image
                {
                    Source = shot,
                    Stretch = Stretch.Uniform,
                    Opacity = 0.35,
                    VerticalAlignment = VerticalAlignment.Top
                });
            }

            var hint = new TextBlock
            {
                Text = Loc.T("frozen_hint"),
                FontSize = 13,
                Opacity = 0.75,
                Foreground = FrozenTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ctx.FrozenHint = hint;

            var overlay = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 10
            };
            overlay.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 40,
                Foreground = FrozenBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            overlay.Children.Add(new TextBlock
            {
                Text = Loc.T("frozen_title"),
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = FrozenTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            overlay.Children.Add(hint);
            if (!string.IsNullOrEmpty(state.WorkingDir))
            {
                overlay.Children.Add(new TextBlock
                {
                    Text = state.WorkingDir,
                    FontSize = 12,
                    Opacity = 0.5,
                    Foreground = FrozenTextBrush,
                    MaxWidth = 520,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            root.Children.Add(overlay);

            root.Tapped += async (_, _) => await UnfreezeTabAsync(item);
            return root;
        }

        /// <summary>Restore path: recreate a tab in the frozen state without starting
        /// WebView2 or the CLI. No screenshot survives a restart — icon-only placeholder.</summary>
        public void AddFrozenTab(TabState state)
        {
            _tabCounter++;
            string baseHeader = string.IsNullOrEmpty(state.Name) ? $"Terminal {_tabCounter}" : state.Name;
            string mode = string.IsNullOrEmpty(state.CliMode) ? ResolveCliMode(null) : state.CliMode;
            var (item, ctx) = BuildTabItem(baseHeader, mode, state.Tag);
            ctx.FrozenState = new TabState
            {
                Name = baseHeader,
                WorkingDir = state.WorkingDir,
                CliMode = mode,
                SessionId = state.SessionId,
                Tag = ctx.TagValue,
                Frozen = true
            };
            item.Content = BuildFrozenPlaceholder(item, ctx, ctx.FrozenState, null);
            ctx.Dot.Fill = FrozenBrush;
            Tabs.TabItems.Add(item);
        }

        /// <summary>Freeze every live tab. idleOnly skips Working panes; when false
        /// the caller has already confirmed freezing working panes.</summary>
        public async Task FreezeAllAsync(bool idleOnly)
        {
            foreach (var t in Tabs.TabItems.OfType<TabViewItem>().ToList())
            {
                var ctx = CtxOf(t);
                if (ctx?.Pane is not TerminalPane pane) continue;
                if (idleOnly && pane.Status == PaneStatus.Working) continue;
                await FreezeTabAsync(t, confirmIfWorking: false);
            }
        }

        public async Task UnfreezeAllAsync()
        {
            foreach (var t in Tabs.TabItems.OfType<TabViewItem>().ToList())
                if (CtxOf(t)?.FrozenState != null)
                    await UnfreezeTabAsync(t);
        }

        /// <summary>Auto-freeze pass: freeze panes idle past the threshold. Never
        /// touches Working panes or the tab the user is currently looking at.</summary>
        public async Task AutoFreezeIdleAsync(TimeSpan idleFor)
        {
            foreach (var t in Tabs.TabItems.OfType<TabViewItem>().ToList())
            {
                var ctx = CtxOf(t);
                if (ctx?.Pane is not TerminalPane pane) continue;
                if (pane.Status == PaneStatus.Working) continue;
                if (IsTabActivelyVisible(t)) continue;
                if (DateTime.UtcNow - pane.LastActivityUtc < idleFor) continue;
                await FreezeTabAsync(t, confirmIfWorking: false);
            }
        }

        // At most one fully-frozen panel per app keeps its hidden prewarmed
        // renderer alive (~100 MB) so the next thaw starts warm instead of paying
        // the full WebView2 cold boot; the rest drop to zero processes.
        private static TabPanel? s_frozenPrewarmHolder;

        private bool ClaimFrozenPrewarm()
        {
            if (s_frozenPrewarmHolder != null && s_frozenPrewarmHolder != this)
                return false;
            s_frozenPrewarmHolder = this;
            return true;
        }

        private void ReleaseFrozenPrewarm()
        {
            if (s_frozenPrewarmHolder == this)
                s_frozenPrewarmHolder = null;
        }

        /// <summary>Drop the hidden prewarmed pane so a fully-frozen panel holds
        /// zero renderer processes.</summary>
        private void DropPrewarm()
        {
            _prewarmVersion++;
            _prewarmedPane = null;
            foreach (var child in PrewarmHost.Children.OfType<TerminalPane>().ToList())
                child.Dispose();
            PrewarmHost.Children.Clear();
        }

        // ── Cross-panel tab drag ────────────────────────────────────────
        // Tabs can be dragged between split panels in the same window. The live
        // TerminalPane (WebView2 + CLI) is re-homed without a restart; only the
        // tab chrome is rebuilt, because header widgets and menu handlers close
        // over their owning panel. WebView2 cannot re-parent across windows, so
        // drops are same-XamlRoot only. The payload travels in statics (same
        // process, single drag at a time) — the DataPackage just carries a marker.

        private const string TabDragMarker = "ccpad/tab";
        private static TabPanel? s_dragSourcePanel;
        private static TabViewItem? s_dragItem;

        private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
        {
            var ctx = CtxOf(args.Tab);
            if (ctx == null || ctx.Busy)
            {
                args.Cancel = true;
                return;
            }
            s_dragSourcePanel = this;
            s_dragItem = args.Tab;
            args.Data.Properties.Add(TabDragMarker, true);
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        }

        private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
        {
            s_dragSourcePanel = null;
            s_dragItem = null;
        }

        private bool IsForeignTabDrag(DragEventArgs e)
            => s_dragSourcePanel != null && s_dragItem != null &&
               !ReferenceEquals(s_dragSourcePanel, this) &&
               s_dragSourcePanel.XamlRoot == XamlRoot &&
               e.DataView.Properties.ContainsKey(TabDragMarker);

        private void OnTabStripDragOver(object sender, DragEventArgs e)
        {
            if (IsForeignTabDrag(e))
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        }

        private void OnTabStripDrop(object sender, DragEventArgs e)
        {
            if (!IsForeignTabDrag(e)) return;

            // Insert before the first tab whose right edge the pointer hasn't passed.
            int index = Tabs.TabItems.Count;
            for (int i = 0; i < Tabs.TabItems.Count; i++)
            {
                if (Tabs.TabItems[i] is TabViewItem tvi &&
                    e.GetPosition(tvi).X - tvi.ActualWidth < 0)
                {
                    index = i;
                    break;
                }
            }
            MoveTabHere(s_dragSourcePanel!, s_dragItem!, index);
        }

        /// <summary>Migrate a tab (live pane or frozen placeholder) from another
        /// panel into this one at <paramref name="index"/>.</summary>
        private void MoveTabHere(TabPanel source, TabViewItem item, int index)
        {
            var ctx = CtxOf(item);
            if (ctx == null || ctx.Busy) return;

            var pane = ctx.Pane;
            var frozen = ctx.FrozenState;
            var shot = ctx.FrozenShot;

            // Keep the source selection sane before pulling the tab out.
            if (ReferenceEquals(source.Tabs.SelectedItem, item) && source.Tabs.TabItems.Count > 1)
            {
                int i = source.Tabs.TabItems.IndexOf(item);
                source.Tabs.SelectedItem = source.Tabs.TabItems[i == 0 ? 1 : i - 1];
            }

            DetachPaneHooks(ctx);
            item.Content = null;
            source.Tabs.TabItems.Remove(item);

            _tabCounter++;
            var (newItem, newCtx) = BuildTabItem(ctx.HeaderBase, ctx.Mode, ctx.TagValue);
            if (pane != null)
            {
                AttachPane(newItem, newCtx, pane);
                newItem.Content = pane;
                newCtx.Dot.Fill = StatusBrush(pane.Status);
            }
            else if (frozen != null)
            {
                newCtx.FrozenState = frozen;
                newCtx.FrozenShot = shot;
                newItem.Content = BuildFrozenPlaceholder(newItem, newCtx, frozen, shot);
                newCtx.Dot.Fill = FrozenBrush;
            }

            index = Math.Clamp(index, 0, Tabs.TabItems.Count);
            Tabs.TabItems.Insert(index, newItem);
            Tabs.SelectedItem = newItem;

            // Prewarm claims follow live counts, same rules as freeze/thaw.
            if (pane != null)
                ReleaseFrozenPrewarm();
            if (source.Tabs.TabItems.Count == 0)
            {
                // The move emptied the source panel — close it like its last tab closed.
                source.CloseRequested?.Invoke(source);
            }
            else if (source.CountLive() == 0)
            {
                if (source.ClaimFrozenPrewarm())
                {
                    if (source._prewarmedPane == null)
                        source.PrewarmNextPane();
                }
                else
                {
                    source.DropPrewarm();
                }
            }

            // WebView2 keeps stale viewport metrics across a re-parent — refit
            // after the layout pass settles (same trick as SplitHost.Rebuild).
            if (pane != null)
            {
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        pane.Refit();
                        pane.FocusTerminal();
                    });
            }

            source.TabsChanged?.Invoke();
            TabsChanged?.Invoke();
        }

        private async Task<bool> ConfirmFreezeWorkingAsync(int count)
        {
            if (XamlRoot == null) return true;
            try
            {
                var dlg = new ContentDialog
                {
                    Title = Loc.T("freeze_working_title"),
                    Content = count > 1
                        ? Loc.T("freeze_all_working_body", count)
                        : Loc.T("freeze_working_body"),
                    PrimaryButtonText = Loc.T("freeze_confirm"),
                    CloseButtonText = Loc.T("cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                return await dlg.ShowAsync() == ContentDialogResult.Primary;
            }
            catch { return false; }
        }

        // ── Pre-warm ────────────────────────────────────────────────────

        private async void PrewarmNextPane()
        {
            if (_disposed) return;

            var version = ++_prewarmVersion;

            _prewarmedPane = null;
            foreach (var child in PrewarmHost.Children.OfType<TerminalPane>().ToList())
                child.Dispose();
            PrewarmHost.Children.Clear();

            var pane = new TerminalPane();
            PrewarmHost.Children.Add(pane);
            try
            {
                await pane.PrewarmAsync();
                if (_disposed || version != _prewarmVersion)
                {
                    PrewarmHost.Children.Remove(pane);
                    pane.Dispose();
                    return;
                }
                _prewarmedPane = pane;
            }
            catch
            {
                PrewarmHost.Children.Remove(pane);
                pane.Dispose();
                if (version == _prewarmVersion)
                    _prewarmedPane = null;
            }
        }

        // ── Project config ──────────────────────────────────────────────

        private void RefreshProjectFlyout()
        {
            ProjectFlyout.Items.Clear();

            string currentDefault = CliMode.LoadDefault();

            // ── Default CLI toggle ──
            var defaultItem = new ToggleMenuFlyoutItem
            {
                Text = Loc.T("proj_default", CliMode.DisplayName(currentDefault)),
                Icon = new FontIcon { Glyph = "\uE713" }, // settings gear
                IsChecked = currentDefault == CliMode.Codex
            };
            defaultItem.Click += (_, _) =>
            {
                string next = currentDefault == CliMode.Codex ? CliMode.Claude : CliMode.Codex;
                CliMode.SaveDefault(next);
                RefreshProjectFlyout();
            };
            ProjectFlyout.Items.Add(defaultItem);

            ProjectFlyout.Items.Add(new MenuFlyoutSeparator());

            // ── Quick-launch new tab in each CLI ──
            var newClaude = new MenuFlyoutItem
            {
                Text = Loc.T("proj_new_claude"),
                Icon = new FontIcon { Glyph = "\uE756" }
            };
            newClaude.Click += async (_, _) => await AddNewTab(null, _defaultWorkingDir, CliMode.Claude);
            ProjectFlyout.Items.Add(newClaude);

            var newCodex = new MenuFlyoutItem
            {
                Text = Loc.T("proj_new_codex"),
                Icon = new FontIcon { Glyph = "\uE756" }
            };
            newCodex.Click += async (_, _) => await AddNewTab(null, _defaultWorkingDir, CliMode.Codex);
            ProjectFlyout.Items.Add(newCodex);

            if (_projects.Count > 0)
                ProjectFlyout.Items.Add(new MenuFlyoutSeparator());

            // ── Project entries: click = default CLI; submenu = explicit choice + remove ──
            foreach (var proj in _projects)
            {
                var entry = proj;
                var item = new MenuFlyoutItem
                {
                    Text = entry.Name,
                    Icon = new FontIcon { Glyph = "\uE8B7" }
                };
                item.Click += async (_, _) => await AddNewTab(entry.Name, entry.Path);

                var openClaude = new MenuFlyoutItem { Text = Loc.T("proj_open_claude"), Icon = new FontIcon { Glyph = "\uE756" } };
                openClaude.Click += async (_, _) => await AddNewTab(entry.Name, entry.Path, CliMode.Claude);

                var openCodex = new MenuFlyoutItem { Text = Loc.T("proj_open_codex"), Icon = new FontIcon { Glyph = "\uE756" } };
                openCodex.Click += async (_, _) => await AddNewTab(entry.Name, entry.Path, CliMode.Codex);

                var openInExplorer = new MenuFlyoutItem
                {
                    Text = Loc.T("proj_open_dir"),
                    Icon = new FontIcon { Glyph = "\uE838" }
                };
                openInExplorer.Click += (_, _) =>
                {
                    try
                    {
                        if (System.IO.Directory.Exists(entry.Path))
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{entry.Path}\"",
                                UseShellExecute = true
                            });
                        }
                    }
                    catch { }
                };

                var removeItem = new MenuFlyoutItem
                {
                    Text = Loc.T("proj_remove", entry.Name),
                    Icon = new FontIcon { Glyph = "\uE74D" }
                };
                removeItem.Click += (_, _) =>
                {
                    _projects.Remove(entry);
                    ProjectConfig.Save(_projects);
                    RefreshProjectFlyout();
                };

                var subFlyout = new MenuFlyout();
                subFlyout.Items.Add(openClaude);
                subFlyout.Items.Add(openCodex);
                subFlyout.Items.Add(new MenuFlyoutSeparator());
                subFlyout.Items.Add(openInExplorer);
                subFlyout.Items.Add(new MenuFlyoutSeparator());
                subFlyout.Items.Add(removeItem);
                item.ContextFlyout = subFlyout;

                ProjectFlyout.Items.Add(item);
            }

            ProjectFlyout.Items.Add(new MenuFlyoutSeparator());

            var addItem = new MenuFlyoutItem
            {
                Text = Loc.T("proj_add"),
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addItem.Click += async (_, _) =>
            {
                var path = await PickFolderAsync();
                if (path != null)
                {
                    var name = System.IO.Path.GetFileName(path);
                    _projects.Add(new ProjectEntry { Name = name, Path = path });
                    ProjectConfig.Save(_projects);
                    RefreshProjectFlyout();
                    await AddNewTab(name, path);
                }
            };
            ProjectFlyout.Items.Add(addItem);
        }

        // ── Folder picker ─────────────────────────────────────────────

        private async Task<string?> PickFolderAsync()
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
                (Application.Current as App)!.MainWnd);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        // ── Workspace snapshot / restore ─────────────────────────────────

        public int ActiveTabIndex => Tabs.SelectedIndex;

        public List<TabState> GetTabStates()
        {
            var states = new List<TabState>();

            // Session IDs already owned by a tab ANYWHERE in the window (not just
            // this panel — split panels used to slip through), so the fallback
            // scan can't hand one conversation to two same-cwd panes. Grows as
            // this pass resolves further IDs.
            var claimed = ClaimedFor(null);

            foreach (var tabItem in Tabs.TabItems)
            {
                if (tabItem is not TabViewItem tvi) continue;
                var ctx = CtxOf(tvi);

                // Frozen tabs already carry their snapshot — persist it as-is.
                if (ctx?.FrozenState is TabState frozen)
                {
                    states.Add(new TabState
                    {
                        Name = frozen.Name,
                        WorkingDir = frozen.WorkingDir,
                        CliMode = frozen.CliMode,
                        SessionId = frozen.SessionId,
                        Tag = ctx.TagValue,
                        Frozen = true
                    });
                    continue;
                }

                var pane = tvi.Content as TerminalPane;
                var dir = pane?.WorkingDir ?? _defaultWorkingDir;
                var rawHeader = ctx?.HeaderBase ?? pane?.Label ?? "";
                // Strip the " · Codex" suffix (present on pane.Label fallback) so
                // restore doesn't double-append it.
                const string codexSuffix = " · Codex";
                if (rawHeader.EndsWith(codexSuffix))
                    rawHeader = rawHeader[..^codexSuffix.Length];

                var sessionId = ResolveSessionId(pane, dir, claimed);
                if (sessionId.Length > 0) claimed.Add(sessionId);

                states.Add(new TabState
                {
                    Name = rawHeader,
                    WorkingDir = string.IsNullOrEmpty(dir) ? "" : dir,
                    CliMode = pane?.CliMode ?? "",
                    SessionId = sessionId,
                    Tag = ctx?.TagValue ?? pane?.TabTag ?? ""
                });
            }
            return states;
        }

        /// <summary>Conversation ID to persist for a pane. Codex IDs live only on disk,
        /// so scan the sessions folder each time (a fresh in-pane conversation may have
        /// replaced the one we resumed). Claude IDs are tracked on the pane (assigned at
        /// launch, updated live from hook callbacks), and that hook-fed ID is treated as
        /// authoritative: scanning the cwd for "the newest conversation" can steal a
        /// session written by an UNRELATED claude running in the same directory (bots,
        /// plain terminals — this machine runs several). The scan therefore only runs
        /// when the pane could genuinely own an untracked conversation: it was launched
        /// without hook instrumentation, or a CLI was brought back by hand inside the
        /// fallback shell — and then only over files written since that relaunch,
        /// excluding IDs already claimed by other tabs.</summary>
        private static string ResolveSessionId(TerminalPane? pane, string? dir, ISet<string>? claimed = null)
        {
            if (pane == null) return "";
            if (pane.CliMode == CliMode.Codex)
            {
                // The claimed set may include this pane's OWN tracked ID (the
                // snapshot pass seeds it with every tab's ID) — never let that
                // push the scan past the pane's own conversation onto someone
                // else's.
                var excl = claimed;
                if (excl != null && pane.SessionId is string own && excl.Contains(own))
                {
                    excl = new HashSet<string>(excl, StringComparer.OrdinalIgnoreCase);
                    excl.Remove(own);
                }
                return CliSessions.FindLatestCodexSessionId(dir, pane.LaunchedAtUtc, excl)
                    ?? pane.SessionId ?? "";
            }
            var id = pane.SessionId ?? "";
            if (id.Length > 0 && CliSessions.ClaudeSessionExists(id)) return id;
            bool hooked = pane.Command.Contains("--settings", StringComparison.Ordinal);
            if (id.Length > 0 && hooked && pane.ShellRelaunchUtc == null)
                return id;   // hook-tracked, just an empty conversation — don't scan
            return CliSessions.FindLatestClaudeSessionId(
                dir, pane.ShellRelaunchUtc ?? pane.LaunchedAtUtc, claimed) ?? id;
        }

        /// <summary>
        /// Store tab states for deferred initialization (used by workspace restore).
        /// Tabs are NOT created yet — call InitializeRestoredTabs() after the control is in the visual tree.
        /// </summary>
        public void SetPendingRestore(List<TabState>? tabs, int activeIndex)
        {
            _pendingRestoreTabs = tabs;
            _pendingRestoreActiveIndex = activeIndex;
        }

        /// <summary>
        /// Actually create the tabs from pending restore data. Must be called after the
        /// TabPanel is in the visual tree (WebView2 needs Loaded event).
        /// </summary>
        public async Task InitializeRestoredTabs()
        {
            if (_pendingRestoreTabs != null && _pendingRestoreTabs.Count > 0)
            {
                await RestoreFromStates(_pendingRestoreTabs, _pendingRestoreActiveIndex);
            }
            else
            {
                await AddFirstTab();
            }
            _pendingRestoreTabs = null;

            // A panel restored to all-frozen tabs never went through AddNewTab, so
            // no prewarmed pane exists and the first thaw would pay the full
            // WebView2 cold start. Same one-warm-renderer-per-app rule as freezing.
            if (CountLive() == 0 && CountFrozen() > 0 && ClaimFrozenPrewarm())
                PrewarmNextPane();
        }

        public async Task RestoreFromStates(List<TabState> states, int activeIndex)
        {
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (s.Frozen)
                {
                    // Recreate as a placeholder — no WebView2/CLI until thawed.
                    if (_defaultWorkingDir == null && !string.IsNullOrEmpty(s.WorkingDir))
                        _defaultWorkingDir = s.WorkingDir;
                    AddFrozenTab(s);
                    continue;
                }
                var name = string.IsNullOrEmpty(s.Name) ? null : s.Name;
                var dir = string.IsNullOrEmpty(s.WorkingDir) ? null : s.WorkingDir;
                var mode = string.IsNullOrEmpty(s.CliMode) ? null : s.CliMode;
                var session = string.IsNullOrEmpty(s.SessionId) ? null : s.SessionId;
                var tag = string.IsNullOrEmpty(s.Tag) ? null : s.Tag;
                if (i == 0)
                    await AddFirstTab(name, dir, mode, session, tag);
                else
                    await AddNewTab(name, dir, mode, session, tag);
            }
            if (activeIndex >= 0 && activeIndex < Tabs.TabItems.Count)
                Tabs.SelectedIndex = activeIndex;
            else if (Tabs.SelectedItem == null && Tabs.TabItems.Count > 0)
                Tabs.SelectedIndex = 0;
        }

        // ── Disposal ────────────────────────────────────────────────────

        public void DisposeAll()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseFrozenPrewarm();
            _prewarmVersion++;
            _prewarmedPane?.Dispose();
            _prewarmedPane = null;
            foreach (var child in PrewarmHost.Children.OfType<TerminalPane>().ToList())
                child.Dispose();
            PrewarmHost.Children.Clear();
            foreach (var tabItem in Tabs.TabItems)
            {
                if (tabItem is TabViewItem tvi && tvi.Content is TerminalPane pane)
                    pane.Dispose();
            }
        }
    }
}
