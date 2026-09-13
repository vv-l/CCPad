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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CCPad
{
    public sealed partial class TabPanel : UserControl
    {
        private int _tabCounter;
        private TerminalPane? _prewarmedPane;
        private List<ProjectEntry> _projects;
        private List<RemoteProjectEntry> _remoteProjects;
        private RemoteDeviceDocument _remoteDevices;
        private string? _defaultWorkingDir;
        private List<TabState>? _pendingRestoreTabs;
        private int _pendingRestoreActiveIndex;
        private int _prewarmVersion;
        private bool _allowFrozenPrewarm = true;
        private bool _disposed;

        public string? DefaultWorkingDir => _defaultWorkingDir;

        public event Action<TabPanel, SplitOrientation>? SplitRequested;
        public event Action<TabPanel>? CloseRequested;
        public event Action<TabPanel>? Focused;

        /// <summary>Fires when tabs are added/closed/switched (for autosave).</summary>
        public event Action? TabsChanged;

        private static string ResolveCliMode(string? requested)
            => CliMode.Normalize(requested ?? CliMode.LoadDefault());

        public TabPanel(List<ProjectEntry> projects)
        {
            InitializeComponent();
            _projects = projects;
            _remoteProjects = RemoteProjectConfig.Load();
            _remoteDevices = RemoteDeviceConfig.Load();
            RefreshProjectFlyout();
            RefreshExternalProjectFlyout();
            ApplyLocalizedChrome();

            // TabView consumes pointer events internally. Listen even when handled
            // so blank tab-strip space can behave like a window caption without
            // affecting tabs, the add button, scroll arrows or project controls.
            Tabs.AddHandler(PointerPressedEvent,
                new PointerEventHandler(OnTabStripBackgroundPointerPressed),
                handledEventsToo: true);

            // Handle tab-width dragging at the TabView boundary.  TabViewItem's
            // template captures the pointer again after item-level handlers run,
            // which immediately cancelled the old per-item drag implementation.
            // The TabView is the last control in this routed-event path, so its
            // capture remains authoritative for the rest of the gesture.
            Tabs.AddHandler(PointerMovedEvent,
                new PointerEventHandler(OnTabWidthPointerMoved),
                handledEventsToo: true);
            Tabs.AddHandler(PointerPressedEvent,
                new PointerEventHandler(OnTabWidthPointerPressed),
                handledEventsToo: true);
            Tabs.AddHandler(PointerReleasedEvent,
                new PointerEventHandler(OnTabWidthPointerReleased),
                handledEventsToo: true);
            Tabs.PointerCaptureLost += OnTabWidthPointerCaptureLost;
            Tabs.PointerExited += OnTabWidthPointerExited;

            ApplyTabHeight(TabHeightManager.Height);
            TabHeightManager.Changed += OnSharedTabHeightChanged;
            Loc.LanguageChanged += OnLanguageChanged;
            RemoteProjectConfig.Changed += OnRemoteProjectsChanged;
            RemoteDeviceConfig.Changed += OnRemoteDevicesChanged;
            Unloaded += (_, _) =>
            {
                TabHeightManager.Changed -= OnSharedTabHeightChanged;
                Loc.LanguageChanged -= OnLanguageChanged;
                RemoteProjectConfig.Changed -= OnRemoteProjectsChanged;
                RemoteDeviceConfig.Changed -= OnRemoteDevicesChanged;
            };
        }

        private void OnLanguageChanged()
        {
            try
            {
                RefreshProjectFlyout();
                RefreshExternalProjectFlyout();
                ApplyLocalizedChrome();
            }
            catch { }
        }

        /// <summary>Localize the static tab-strip chrome (project button + resize handle).</summary>
        private void ApplyLocalizedChrome()
        {
            ProjectLabel.Text = Loc.T("btn_project");
            ExternalProjectLabel.Text = Loc.T("btn_external_project");
            ToolTipService.SetToolTip(ProjectButton, Loc.T("tip_project"));
            ToolTipService.SetToolTip(ExternalProjectButton, Loc.T("tip_external_project"));
            ToolTipService.SetToolTip(ResizeHandle, Loc.T("tip_resize_tabs"));
        }

        private void OnRemoteProjectsChanged()
        {
            _remoteProjects = RemoteProjectConfig.Load();
            RefreshExternalProjectFlyout();
        }

        private void OnRemoteDevicesChanged()
        {
            _remoteDevices = RemoteDeviceConfig.Load();
            RefreshExternalProjectFlyout();
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
            var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            ProjectButton.Measure(infinite);
            ExternalProjectButton.Measure(infinite);
            return ProjectButton.DesiredSize.Width + ExternalProjectButton.DesiredSize.Width;
        }

        // ── Tab-strip height sync + resize handle ───────────────────────

        // Approx. vertical padding above the tab row inside TabView's tab strip
        // (window-drag reserve area). Used to place the handle at the strip's bottom edge.
        private const double TabStripTopPadding = 8;

        private void OnTabStripBackgroundPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(Tabs);
            if (!point.Properties.IsLeftButtonPressed ||
                point.Position.Y < 0 ||
                point.Position.Y >= TabStripTopPadding + TabHeightManager.Height - 3)
                return;

            // Only the strip's genuine background is draggable. Any interactive
            // descendant keeps its normal behavior, including tab headers/close,
            // add/scroll buttons and both project menus.
            for (DependencyObject? node = e.OriginalSource as DependencyObject;
                 node != null && !ReferenceEquals(node, Tabs);
                 node = VisualTreeHelper.GetParent(node))
            {
                if (node is TabViewItem or ButtonBase or ScrollBar)
                    return;
            }

            e.Handled = true;
            App.BeginMainWindowDrag();
        }

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

        // ── Per-tab width grip (rightmost strip of each tab) ────────────

        // Hot zone measured from the tab's RIGHT edge, independent of header
        // content — a crowded title + tag badge can't crowd it out.
        private const double WidthGripZone = 12;
        private const double MinTabWidth = 72;
        private const double MaxTabWidth = 640;

        private bool _widthDragging;
        private TabViewItem? _widthDragItem;
        private TabViewItem? _widthHoverItem;
        private double _widthDragStartX;
        private double _widthDragStartWidth;

        /// <summary>Explicit user width of a tab for persistence; 0 = auto (SizeToContent).</summary>
        private static double CustomWidthOf(TabViewItem item)
            => double.IsNaN(item.Width) ? 0 : item.Width;

        /// <summary>Give a tab an explicit user width. Min/MaxWidth are pinned too:
        /// TabView's layout writes the TabViewItemMaxWidth theme resource (240px)
        /// onto every item, which would otherwise clamp a wider drag.</summary>
        private static void ApplyCustomWidth(TabViewItem item, double w)
        {
            w = Math.Clamp(w, MinTabWidth, MaxTabWidth);
            item.MinWidth = w;
            item.MaxWidth = w;
            item.Width = w;
        }

        /// <summary>Back to auto (SizeToContent) width; TabView re-applies its own
        /// Min/MaxWidth on its next layout pass.</summary>
        private static void ResetTabWidth(TabViewItem item)
        {
            item.Width = double.NaN;
            item.ClearValue(FrameworkElement.MinWidthProperty);
            item.ClearValue(FrameworkElement.MaxWidthProperty);
        }

        private static bool InWidthGripZone(TabViewItem item, Windows.Foundation.Point pos)
            => pos.X >= item.ActualWidth - WidthGripZone;

        /// <summary>Master switch for tab reorder/tear-out. Item-level flags
        /// (CanDrag, Handled, capture) don't stop the gesture — TabView's inner
        /// list drives it from CanReorderTabs/CanDragTabs, so toggle those.</summary>
        private void SetTabStripDragEnabled(bool on)
        {
            Tabs.CanReorderTabs = on;
            Tabs.CanDragTabs = on;
        }

        private static TabViewItem? TabItemFromSource(object? source)
        {
            for (DependencyObject? node = source as DependencyObject;
                 node != null;
                 node = VisualTreeHelper.GetParent(node))
            {
                if (node is TabViewItem item)
                    return item;
            }
            return null;
        }

        private void SetWidthGripHover(TabViewItem? item)
        {
            if (ReferenceEquals(_widthHoverItem, item)) return;
            _widthHoverItem = item;
            SetTabStripDragEnabled(item == null);
            ProtectedCursor = item == null
                ? null
                : InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }

        private void OnTabWidthPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_widthDragging)
            {
                if (_widthDragItem == null) return;
                double dx = e.GetCurrentPoint(Tabs).Position.X - _widthDragStartX;
                ApplyCustomWidth(_widthDragItem, _widthDragStartWidth + dx);
                e.Handled = true;
                return;
            }

            var item = TabItemFromSource(e.OriginalSource);
            if (item == null ||
                !InWidthGripZone(item, e.GetCurrentPoint(item).Position))
                item = null;
            SetWidthGripHover(item);
        }

        private void OnTabWidthPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_widthDragging) return;
            var point = e.GetCurrentPoint(Tabs);
            var item = TabItemFromSource(e.OriginalSource);
            if (!point.Properties.IsLeftButtonPressed || item == null ||
                !InWidthGripZone(item, e.GetCurrentPoint(item).Position))
                return;

            _widthDragging = true;
            _widthDragItem = item;
            _widthDragStartX = point.Position.X;
            _widthDragStartWidth = double.IsNaN(item.Width) ? item.ActualWidth : item.Width;
            SetWidthGripHover(item);
            Tabs.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnTabWidthPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_widthDragging) return;
            FinishTabWidthDrag();
            Tabs.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void OnTabWidthPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_widthDragging)
                FinishTabWidthDrag();
        }

        private void OnTabWidthPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_widthDragging)
                SetWidthGripHover(null);
        }

        private void FinishTabWidthDrag()
        {
            _widthDragging = false;
            _widthDragItem = null;
            SetWidthGripHover(null);
            TabsChanged?.Invoke();
        }

        /// <summary>Horizontal-drag width grip on the tab's right edge: drag sets an
        /// explicit width overriding SizeToContent; double-click restores auto.</summary>
        private void AttachWidthGrip(TabViewItem item)
        {
            // TabView rewrites item Min/MaxWidth from theme resources during its
            // layout passes; while a custom width is active, immediately pin them back.
            item.RegisterPropertyChangedCallback(FrameworkElement.MaxWidthProperty, (_, _) =>
            {
                if (!double.IsNaN(item.Width) && item.MaxWidth != item.Width)
                    item.MaxWidth = item.Width;
            });
            item.RegisterPropertyChangedCallback(FrameworkElement.MinWidthProperty, (_, _) =>
            {
                if (!double.IsNaN(item.Width) && item.MinWidth != item.Width)
                    item.MinWidth = item.Width;
            });

            // DragStarting cancellation is a backstop for the native TabView drag
            // recognizer if it races with our TabView-level pointer handler.
            item.DragStarting += (_, e) =>
            {
                if (_widthDragging || ReferenceEquals(_widthHoverItem, item))
                    e.Cancel = true;
            };

            item.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler((_, e) =>
            {
                if (!InWidthGripZone(item, e.GetPosition(item))) return;
                ResetTabWidth(item);
                e.Handled = true;
            }), handledEventsToo: true);
        }

        public void UpdateProjects(List<ProjectEntry> projects)
        {
            _projects = projects;
            RefreshProjectFlyout();
        }

        // ── Public API ──────────────────────────────────────────────────

        public async Task AddFirstTab(string? projectName = null, string? workingDir = null,
            string? cliMode = null, string? resumeSessionId = null, string? tag = null,
            string? remoteProfileId = null, string? remoteWorkingDir = null)
        {
            if (workingDir != null)
                _defaultWorkingDir = workingDir;
            await AddNewTab(projectName, workingDir, cliMode, resumeSessionId, tag,
                remoteProfileId, remoteWorkingDir);
        }

        public void FocusCurrentTab()
        {
            if (Tabs.SelectedItem is TabViewItem item && CtxOf(item)?.Pane is TerminalPane pane)
                pane.FocusTerminal();
        }

        /// <summary>The TerminalPane shown in the currently-selected tab, or null.</summary>
        public TerminalPane? CurrentPane => Tabs.SelectedItem is TabViewItem item
            ? CtxOf(item)?.Pane
            : null;

        public void RefitAllTerminals()
        {
            foreach (var tab in Tabs.TabItems)
            {
                if (tab is TabViewItem tvi && CtxOf(tvi)?.Pane is TerminalPane pane)
                    pane.Refit();
            }
        }

        // ── Tab management ──────────────────────────────────────────────

        private async Task AddNewTab(string? projectName = null, string? workingDir = null,
            string? cliMode = null, string? resumeSessionId = null, string? tag = null,
            string? remoteProfileId = null, string? remoteWorkingDir = null,
            bool remoteResumePicker = false)
        {
            _tabCounter++;
            string mode = ResolveCliMode(cliMode);
            // A Linux project path is carried separately in remoteWorkingDir;
            // never hand either it or a panel's local default to CreateProcess.
            if (mode == CliMode.CodexRemote)
                workingDir = null;

            var (pane, prewarmed) = await AcquirePaneAsync();
            if (mode == CliMode.CodexRemote)
            {
                pane.RemoteProfileId = string.IsNullOrEmpty(remoteProfileId)
                    ? RemoteDeviceConfig.Load().SelectedDeviceId
                    : remoteProfileId;
                pane.RemoteWorkingDir = remoteWorkingDir;
            }

            // Remote Codex cannot call this machine's loopback notify endpoint.
            // v1 intentionally leaves its green/amber hook state degraded; the
            // process-exit path still turns the tab red when ssh disconnects.
            string extra = mode switch
            {
                CliMode.Codex => CliNotify.PrepareCodexNotify(pane.PaneId),
                CliMode.CodexRemote => "",
                _ => CliNotify.PrepareClaudeHooks(pane.PaneId),
            };
            pane.CompletionHooksActive = extra.Length > 0;
            var (cmd, resumed) = await BuildLaunchCommandAsync(
                mode, extra, resumeSessionId, pane, remoteWorkingDir, remoteResumePicker);

            var item = CreateTabItem(projectName, workingDir, pane, mode, tag);

            Tabs.TabItems.Add(item);
            Tabs.SelectedItem = item;

            if (prewarmed)
            {
                pane.LaunchSession(cmd, workingDir, focusOnReady: true, cliMode: mode);
                // May be a pane re-homed from another panel (shared warm pool) —
                // force a recompose so it can't sit on its default background.
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => pane.NudgeRepaint());
            }
            else
            {
                await pane.InitializeAsync(cmd, workingDir, focusOnReady: true, cliMode: mode);
                // A fresh WebView2's first composited frame can race with a
                // concurrent split-tree layout pass (SplitHost.Rebuild()),
                // leaving the pane visually stuck at its initial paint even
                // though the page is live and CLI output has been written —
                // same class of issue NudgeRepaint fixes for re-homed panes.
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => pane.NudgeRepaint());
            }

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
        /// The returned <c>Resumed</c> flag reports whether the command actually
        /// resumes — callers surface a notice when a requested resume silently fell
        /// back to a fresh session (the saved conversation file no longer exists).
        /// The session-exists checks walk the CLI's session directories on disk, so
        /// they run on the thread pool to keep the UI thread free.
        /// </summary>
        private static async Task<(string Cmd, bool Resumed)> BuildLaunchCommandAsync(
            string mode, string extra, string? resumeSessionId, TerminalPane pane,
            string? remoteWorkingDir = null, bool remoteResumePicker = false,
            bool forkCodexSession = false)
        {
            if (mode == CliMode.CodexRemote)
            {
                // Every remote tab owns a PRIVATE tmux session; its NAME is what
                // SessionId stores (never a local conversation UUID), so the
                // existing freeze/snapshot/restore plumbing reattaches it for
                // free. An old snapshot's empty id just means a fresh session.
                string deviceId = pane.RemoteProfileId ?? RemoteDeviceConfig.Load().SelectedDeviceId;
                string name = string.IsNullOrEmpty(resumeSessionId)
                    ? RemoteSessions.NewSessionName(deviceId)
                    : resumeSessionId!;
                pane.SessionId = name;
                RemoteSessions.EnsureSweeperInBackground(deviceId);
                string cmd = remoteResumePicker
                    ? CliMode.BuildRemoteResumePickerCommand(name, remoteWorkingDir, deviceId)
                    : CliMode.BuildRemoteCommand(name, remoteWorkingDir, deviceId);
                return (cmd, resumeSessionId != null);
            }

            if (mode == CliMode.Codex)
            {
                if (resumeSessionId != null &&
                    await Task.Run(() => CliSessions.CodexSessionExists(resumeSessionId)))
                {
                    // A fork receives a new ID from Codex's notify event. Do not
                    // seed the pane with the source ID or a snapshot taken during
                    // startup could later try to resume the shared source thread.
                    if (!forkCodexSession)
                        pane.SessionId = resumeSessionId;
                    return (forkCodexSession
                        ? CliMode.BuildForkCommand(resumeSessionId, extra)
                        : CliMode.BuildResumeCommand(mode, resumeSessionId, extra), true);
                }
                return (CliMode.BuildCommand(mode, extra), false);
            }

            if (resumeSessionId != null &&
                await Task.Run(() => CliSessions.ClaudeSessionExists(resumeSessionId)))
            {
                pane.SessionId = resumeSessionId;
                return (CliMode.BuildResumeCommand(mode, resumeSessionId, extra), true);
            }

            var sessionId = Guid.NewGuid().ToString();
            pane.SessionId = sessionId;
            return (CliMode.BuildCommand(mode, $"--session-id {sessionId}" + (extra.Length > 0 ? " " + extra : "")), false);
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
        // One TabCtx lives on every TabViewItem.Tag. TabViewItem.Content remains
        // ContentHost for the whole tab lifetime; its child is the live pane or
        // frozen placeholder. The ctx tracks which, and owns the header widgets
        // that survive visual swaps (status dot, tag badge, menu items).

        private sealed class TabCtx
        {
            public Grid ContentHost { get; } = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12))
            };
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

        // Tag non-Claude CLI tabs so mixed panes are visually distinguishable.
        private static string HeaderFor(string baseHeader, string cliMode)
            => cliMode == CliMode.Claude
                ? baseHeader
                : $"{baseHeader} · {CliMode.DisplayName(cliMode)}";

        private TabViewItem CreateTabItem(string? projectName, string? workingDir, TerminalPane pane, string cliMode, string? tag = null)
        {
            string baseHeader = projectName
                ?? (workingDir != null ? System.IO.Path.GetFileName(workingDir) : null)
                ?? $"Terminal {_tabCounter}";
            var (item, tabCtx) = BuildTabItem(baseHeader, cliMode, tag);
            AttachPane(item, tabCtx, pane);
            SetTabVisual(tabCtx, pane);
            return item;
        }

        /// <summary>Chrome shared by live and frozen tabs: header (status dot +
        /// title + tag badge), context menu, and the TabCtx stored on item.Tag.
        /// The TabViewItem.Content is a stable Grid for the lifetime of the tab.
        /// Callers replace only that Grid's child; swapping Content itself can leave
        /// WinUI's selected-content presenter displaying the old visual indefinitely.</summary>
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
            // Grid instead of StackPanel so the title column can compress with an
            // ellipsis when the user drags the tab narrower than its content.
            var headerPanel = new Grid();
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                   // dot
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                   // tag badge
            headerPanel.Children.Add(dot);
            var title = new TextBlock
            {
                Text = HeaderFor(baseHeader, cliMode),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(title, 1);
            headerPanel.Children.Add(title);

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
            Grid.SetColumn(tagBadge, 2);
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
                Tag = tabCtx,
                Content = tabCtx.ContentHost
            };

            AttachWidthGrip(item);

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

        /// <summary>
        /// Replace the visual inside the tab's permanent host.  Keeping the
        /// TabViewItem.Content identity stable avoids a WinUI TabView cache bug:
        /// the model and CLI had thawed successfully, but the selected presenter
        /// continued showing the old "restoring" placeholder until app restart.
        /// </summary>
        private static void SetTabVisual(TabCtx ctx, UIElement visual)
        {
            ctx.ContentHost.Children.Clear();
            ctx.ContentHost.Children.Add(visual);
            ctx.ContentHost.InvalidateMeasure();
            ctx.ContentHost.InvalidateArrange();
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
            if (CtxOf(item)?.Pane is TerminalPane pane)
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
                var ctx = CtxOf(t);
                KillRemoteSession(ctx);
                if (ctx?.Pane is TerminalPane pane)
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
                var ctx = CtxOf(t);
                KillRemoteSession(ctx);
                if (ctx?.Pane is TerminalPane pane)
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
            var ctx = CtxOf(item);
            KillRemoteSession(ctx);
            if (ctx?.Pane is TerminalPane pane)
                pane.Dispose();
            Tabs.TabItems.Remove(item);
            TabsChanged?.Invoke();
        }

        /// <summary>The private tmux session name a tab owns, live or frozen;
        /// null for local tabs and for legacy remote tabs restored with an
        /// empty id.</summary>
        private static string? RemoteNameOf(TabCtx? ctx)
        {
            string? name = ctx?.Pane is TerminalPane p && p.CliMode == CliMode.CodexRemote
                ? p.SessionId
                : ctx?.FrozenState is { CliMode: CliMode.CodexRemote } fs ? fs.SessionId
                : null;
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>Closing a tab for good ends its private remote session.
        /// Freeze and cross-panel migration detach instead and never come
        /// through here; window close keeps sessions alive so the snapshot can
        /// reattach them. Fire-and-forget: a kill lost to a network hiccup is
        /// caught by the remote cron sweeper.</summary>
        private static void KillRemoteSession(TabCtx? ctx)
        {
            if (RemoteNameOf(ctx) is string name)
                RemoteSessions.KillSessionFireAndForget(name,
                    ctx?.Pane?.RemoteProfileId ?? ctx?.FrozenState?.RemoteProfileId);
        }

        /// <summary>Kill every tab's private remote session — used when the
        /// whole panel is closed for good (NOT on window close, where the
        /// archived snapshot may be restored later and must find its sessions
        /// alive).</summary>
        public void KillAllRemoteSessions()
        {
            foreach (var t in Tabs.TabItems)
                if (t is TabViewItem tvi)
                    KillRemoteSession(CtxOf(tvi));
        }

        /// <summary>Add the remote tmux session names owned by this panel's
        /// tabs (live or frozen) to <paramref name="into"/>.</summary>
        internal void CollectRemoteSessionNames(ISet<string> into)
        {
            foreach (var t in Tabs.TabItems)
                if (t is TabViewItem tvi && RemoteNameOf(CtxOf(tvi)) is string name)
                    into.Add(name);
        }

        private async void OnAddTab(TabView sender, object args) => await AddNewTab(null, _defaultWorkingDir);

        private void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
            => CloseTab(args.Tab);

        private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TabsChanged?.Invoke();
            if (Tabs.SelectedItem is TabViewItem item && CtxOf(item)?.Pane is TerminalPane pane)
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
                var dir = pane.CliMode == CliMode.CodexRemote
                    ? null
                    : pane.WorkingDir ?? _defaultWorkingDir;
                var state = new TabState
                {
                    Name = ctx.HeaderBase,
                    WorkingDir = string.IsNullOrEmpty(dir) ? "" : dir,
                    RemoteProfileId = pane.RemoteProfileId ?? "",
                    RemoteWorkingDir = pane.RemoteWorkingDir ?? "",
                    CliMode = string.IsNullOrEmpty(pane.CliMode) ? ctx.Mode : pane.CliMode,
                    // Exclude IDs owned by every other tab in the window, so two
                    // same-cwd tabs can't freeze onto the same conversation.
                    SessionId = ResolveSessionId(pane, dir, ClaimedFor(item)),
                    Tag = ctx.TagValue,
                    Frozen = true,
                    CustomWidth = CustomWidthOf(item)
                };

                // Screenshot before teardown so the placeholder shows the last frame.
                var shot = await pane.CaptureSnapshotAsync();

                DetachPaneHooks(ctx);
                ctx.FrozenState = state;
                ctx.FrozenShot = shot;
                SetTabVisual(ctx, BuildFrozenPlaceholder(item, ctx, state, shot));
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
            TerminalPane? acquired = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string mode = string.IsNullOrEmpty(state.CliMode) ? ResolveCliMode(null) : state.CliMode;
                string? dir = mode == CliMode.CodexRemote
                    ? null
                    : string.IsNullOrEmpty(state.WorkingDir) ? _defaultWorkingDir : state.WorkingDir;
                string? session = string.IsNullOrEmpty(state.SessionId) ? null : state.SessionId;

                var (pane, prewarmed) = await AcquirePaneAsync();
                acquired = pane;
                pane.RemoteProfileId = string.IsNullOrEmpty(state.RemoteProfileId)
                    ? RemoteDeviceConfig.Load().SelectedDeviceId
                    : state.RemoteProfileId;
                pane.RemoteWorkingDir = string.IsNullOrEmpty(state.RemoteWorkingDir)
                    ? null : state.RemoteWorkingDir;
                long tAcquire = watch.ElapsedMilliseconds;
                string extra = mode switch
                {
                    CliMode.Codex => CliNotify.PrepareCodexNotify(pane.PaneId),
                    CliMode.CodexRemote => "",
                    _ => CliNotify.PrepareClaudeHooks(pane.PaneId),
                };
                pane.CompletionHooksActive = extra.Length > 0;
                var (cmd, resumed) = await BuildLaunchCommandAsync(
                    mode, extra, session, pane, pane.RemoteWorkingDir,
                    forkCodexSession: state.ForkOnThaw);
                long tCmd = watch.ElapsedMilliseconds;

                // Don't steal focus when thawing a background tab (batch unfreeze).
                bool focus = ReferenceEquals(Tabs.SelectedItem, item);

                if (prewarmed && pane.IsReady)
                {
                    // Warm renderer: the page is already live, swap in immediately.
                    ctx.Mode = mode;
                    AttachPane(item, ctx, pane);
                    SetTabVisual(ctx, pane);
                    ctx.Dot.Fill = StatusBrush(PaneStatus.Waiting);
                    pane.LaunchSession(cmd, dir, focusOnReady: focus, cliMode: mode);
                    // The warm pane may have been re-homed from another panel's
                    // PrewarmHost — recompose after the layout pass or it can
                    // sit on its default background despite being alive.
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => pane.NudgeRepaint());
                }
                else
                {
                    // Cold renderer: boot the page invisibly inside PrewarmHost so
                    // the frozen placeholder (screenshot + 正在恢复) stays on screen
                    // until the terminal is genuinely ready — never a white pane.
                    // The CLI starts in parallel inside InitializeAsync.
                    PrewarmHost.Children.Add(pane);
                    try
                    {
                        await pane.InitializeAsync(cmd, dir, focusOnReady: false, cliMode: mode);
                    }
                    finally
                    {
                        PrewarmHost.Children.Remove(pane);
                    }
                    if (!pane.IsReady)
                        throw new InvalidOperationException(Loc.T("pane_err_cold"));

                    ctx.Mode = mode;
                    AttachPane(item, ctx, pane);
                    SetTabVisual(ctx, pane);
                    ctx.Dot.Fill = StatusBrush(PaneStatus.Waiting);
                    // Booted hidden in PrewarmHost, now re-homed into the tab —
                    // recompose after the layout pass.
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => pane.NudgeRepaint(focusAfter: focus));
                }
                App.LogDiag($"thaw pane={pane.PaneId} warm={prewarmed && pane.IsReady} " +
                            $"acquire={tAcquire}ms cmd={tCmd - tAcquire}ms init={watch.ElapsedMilliseconds - tCmd}ms " +
                            $"total={watch.ElapsedMilliseconds}ms mode={mode} resumed={resumed}");

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
                App.LogDiag($"thaw FAILED after {watch.ElapsedMilliseconds}ms: {ex.Message}");
                // Rebuild the placeholder so the tab stays frozen and retryable
                // instead of stranding a white unclickable pane. The acquired pane
                // may or may not have been attached yet — dispose it either way
                // (this also kills any CLI it already spawned).
                if (ctx.Pane != null)
                    DetachPaneHooks(ctx);
                acquired?.Dispose();
                ctx.Pane = null;
                ctx.FrozenState = state;
                ctx.FrozenShot = shot;
                SetTabVisual(ctx, BuildFrozenPlaceholder(item, ctx, state, shot));
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
            string shownDir = !string.IsNullOrEmpty(state.RemoteWorkingDir)
                ? state.RemoteWorkingDir
                : state.WorkingDir;
            if (!string.IsNullOrEmpty(shownDir))
            {
                overlay.Children.Add(new TextBlock
                {
                    Text = shownDir,
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
                RemoteProfileId = state.RemoteProfileId,
                RemoteWorkingDir = state.RemoteWorkingDir,
                CliMode = mode,
                SessionId = state.SessionId,
                Tag = ctx.TagValue,
                Frozen = true,
                ForkOnThaw = state.ForkOnThaw
            };
            SetTabVisual(ctx, BuildFrozenPlaceholder(item, ctx, ctx.FrozenState, null));
            ctx.Dot.Fill = FrozenBrush;
            if (state.CustomWidth > 0)
                ApplyCustomWidth(item, state.CustomWidth);
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
            if (!_allowFrozenPrewarm) return false;
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

        /// <summary>
        /// Frozen-template windows should remain genuinely lightweight while all
        /// tabs are frozen. Skip even the single app-wide warm WebView2 and pay
        /// the cold-start cost only after the user chooses a tab to thaw.
        /// </summary>
        internal void DisableFrozenPrewarm()
        {
            _allowFrozenPrewarm = false;
            ReleaseFrozenPrewarm();
            DropPrewarm();
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
            ctx.ContentHost.Children.Clear();
            source.Tabs.TabItems.Remove(item);

            _tabCounter++;
            var (newItem, newCtx) = BuildTabItem(ctx.HeaderBase, ctx.Mode, ctx.TagValue);
            // Carry a user-dragged width across the migration (0 = auto, leave as-is).
            if (CustomWidthOf(item) > 0)
                ApplyCustomWidth(newItem, CustomWidthOf(item));
            if (pane != null)
            {
                AttachPane(newItem, newCtx, pane);
                SetTabVisual(newCtx, pane);
                newCtx.Dot.Fill = StatusBrush(pane.Status);
            }
            else if (frozen != null)
            {
                newCtx.FrozenState = frozen;
                newCtx.FrozenShot = shot;
                SetTabVisual(newCtx, BuildFrozenPlaceholder(newItem, newCtx, frozen, shot));
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

            // A re-parented WebView2 keeps stale viewport metrics AND can keep
            // painting its default background until a visibility change —
            // recompose after the layout pass settles.
            if (pane != null)
            {
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => pane.NudgeRepaint(focusAfter: true));
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
            string localDefault = currentDefault == CliMode.Claude
                ? CliMode.Claude
                : CliMode.Codex;

            // ── Default CLI selector ──
            var defaultItem = new MenuFlyoutSubItem
            {
                Text = Loc.T("proj_default", CliMode.DisplayName(localDefault)),
                Icon = new FontIcon { Glyph = "\uE713" }, // settings gear
            };
            foreach (string mode in new[] { CliMode.Claude, CliMode.Codex })
            {
                string selectedMode = mode;
                var choice = new ToggleMenuFlyoutItem
                {
                    Text = CliMode.DisplayName(selectedMode),
                    IsChecked = localDefault == selectedMode,
                };
                choice.Click += (_, _) =>
                {
                    CliMode.SaveDefault(selectedMode);
                    RefreshProjectFlyout();
                };
                defaultItem.Items.Add(choice);
            }
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
                item.Click += async (_, _) =>
                    await AddNewTab(entry.Name, entry.Path, localDefault);

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

        // ── Remote project config ──────────────────────────────────────

        private void RefreshExternalProjectFlyout()
        {
            ExternalProjectFlyout.Items.Clear();
            _remoteDevices = RemoteDeviceConfig.Load();
            var current = _remoteDevices.Devices.FirstOrDefault(d => d.Id == _remoteDevices.SelectedDeviceId)
                ?? _remoteDevices.Devices.FirstOrDefault();

            if (current != null)
            {
                var selector = new MenuFlyoutSubItem
                {
                    Text = Loc.T("remote_profile_current", current.Name),
                    Icon = new FontIcon { Glyph = "\uE968" }
                };
                foreach (var device in _remoteDevices.Devices)
                {
                    var selected = device;
                    var choice = new ToggleMenuFlyoutItem
                    {
                        Text = $"{device.Name} · {device.User}@{device.Host}",
                        IsChecked = device.Id == current.Id
                    };
                    choice.Click += (_, _) =>
                    {
                        _remoteDevices.SelectedDeviceId = selected.Id;
                        RemoteDeviceConfig.Save(_remoteDevices);
                    };
                    selector.Items.Add(choice);
                }
                ExternalProjectFlyout.Items.Add(selector);
            }
            else
            {
                ExternalProjectFlyout.Items.Add(new MenuFlyoutItem
                {
                    Text = Loc.T("remote_device_none"),
                    Icon = new FontIcon { Glyph = "\uE783" },
                    IsEnabled = false
                });
            }
            ExternalProjectFlyout.Items.Add(new MenuFlyoutSeparator());

            var newRemote = new MenuFlyoutItem
            {
                Text = Loc.T("remote_new_tab"),
                Icon = new FontIcon { Glyph = "\uE756" },
                IsEnabled = current != null
            };
            newRemote.Click += async (_, _) =>
                await AddNewTab(cliMode: CliMode.CodexRemote,
                    remoteProfileId: current!.Id,
                    remoteWorkingDir: current.DefaultWorkingDir);
            ExternalProjectFlyout.Items.Add(newRemote);

            var recoverRemote = new MenuFlyoutItem
            {
                Text = Loc.T("rs_recover_menu"),
                Icon = new FontIcon { Glyph = "" },
                IsEnabled = current != null
            };
            recoverRemote.Click += async (_, _) =>
                await AddNewTab(cliMode: CliMode.CodexRemote,
                    remoteProfileId: current!.Id,
                    remoteWorkingDir: current.DefaultWorkingDir,
                    remoteResumePicker: true);
            ExternalProjectFlyout.Items.Add(recoverRemote);

            if (_remoteProjects.Count > 0)
                ExternalProjectFlyout.Items.Add(new MenuFlyoutSeparator());

            foreach (var project in _remoteProjects)
            {
                var entry = project;
                var item = new MenuFlyoutItem
                {
                    Text = entry.Name,
                    Icon = new FontIcon { Glyph = "\uE968" }
                };
                ToolTipService.SetToolTip(item, entry.RemotePath);
                item.Click += async (_, _) => await OpenRemoteProjectAsync(entry);
                item.IsEnabled = _remoteDevices.Devices.Any(d => d.Id == entry.ProfileId);

                var open = new MenuFlyoutItem
                {
                    Text = Loc.T("remote_project_open"),
                    Icon = new FontIcon { Glyph = "\uE756" }
                };
                open.Click += async (_, _) => await OpenRemoteProjectAsync(entry);

                var edit = new MenuFlyoutItem
                {
                    Text = Loc.T("remote_project_edit"),
                    Icon = new FontIcon { Glyph = "\uE70F" }
                };
                edit.Click += async (_, _) => await ShowRemoteProjectDialogAsync(entry);

                var test = new MenuFlyoutItem
                {
                    Text = Loc.T("remote_project_test"),
                    Icon = new FontIcon { Glyph = "\uE9D9" }
                };
                test.Click += async (_, _) => await TestRemoteProjectAsync(entry);

                var remove = new MenuFlyoutItem
                {
                    Text = Loc.T("proj_remove", entry.Name),
                    Icon = new FontIcon { Glyph = "\uE74D" }
                };
                remove.Click += (_, _) =>
                {
                    _remoteProjects.Remove(entry);
                    RemoteProjectConfig.Save(_remoteProjects);
                };

                var context = new MenuFlyout();
                context.Items.Add(open);
                context.Items.Add(new MenuFlyoutSeparator());
                context.Items.Add(edit);
                context.Items.Add(test);
                context.Items.Add(new MenuFlyoutSeparator());
                context.Items.Add(remove);
                item.ContextFlyout = context;
                ExternalProjectFlyout.Items.Add(item);
            }

            ExternalProjectFlyout.Items.Add(new MenuFlyoutSeparator());
            var add = new MenuFlyoutItem
            {
                Text = Loc.T("remote_project_add"),
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            add.Click += async (_, _) =>
            {
                if (_remoteDevices.Devices.Count == 0)
                    await ShowRemoteDeviceDialogAsync();
                if (RemoteDeviceConfig.Load().Devices.Count > 0)
                    await ShowRemoteProjectDialogAsync();
            };
            ExternalProjectFlyout.Items.Add(add);

            var copyOnboarding = new MenuFlyoutItem
            {
                Text = Loc.T("remote_ai_copy"),
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            ToolTipService.SetToolTip(copyOnboarding, Loc.T("remote_ai_copy_tip"));
            copyOnboarding.Click += async (_, _) => await CopyExternalOnboardingPromptAsync();
            ExternalProjectFlyout.Items.Add(copyOnboarding);

            ExternalProjectFlyout.Items.Add(new MenuFlyoutSeparator());
            if (current != null)
            {
                var manage = new MenuFlyoutSubItem
                {
                    Text = Loc.T("remote_device_manage"),
                    Icon = new FontIcon { Glyph = "\uE713" }
                };
                var testDevice = new MenuFlyoutItem { Text = Loc.T("remote_device_test"), Icon = new FontIcon { Glyph = "\uE9D9" } };
                testDevice.Click += async (_, _) => await TestRemoteDeviceAsync(current);
                var editDevice = new MenuFlyoutItem { Text = Loc.T("remote_device_edit"), Icon = new FontIcon { Glyph = "\uE70F" } };
                editDevice.Click += async (_, _) => await ShowRemoteDeviceDialogAsync(current);
                var removeDevice = new MenuFlyoutItem { Text = Loc.T("remote_device_remove"), Icon = new FontIcon { Glyph = "\uE74D" } };
                removeDevice.Click += async (_, _) => await RemoveRemoteDeviceAsync(current);
                manage.Items.Add(testDevice);
                manage.Items.Add(editDevice);
                manage.Items.Add(new MenuFlyoutSeparator());
                manage.Items.Add(removeDevice);
                ExternalProjectFlyout.Items.Add(manage);
            }
            var addDevice = new MenuFlyoutItem
            {
                Text = Loc.T("remote_device_add"),
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            addDevice.Click += async (_, _) => await ShowRemoteDeviceDialogAsync();
            ExternalProjectFlyout.Items.Add(addDevice);
        }

        private async Task CopyExternalOnboardingPromptAsync()
        {
            const string skillPath = @"D:\CC Pad\.agents\skills\ccpad-onboard-linux-device\SKILL.md";
            string prompt =
                "请使用 $ccpad-onboard-linux-device 帮我将一台 Linux SSH 设备接入 CC Pad。\r\n" +
                $"Skill 文件：{skillPath}\r\n\r\n" +
                "接入方式：优先使用快速自动配置；如果不能自动写入，再返回半手动配置清单。\r\n" +
                "IP/主机：<请填写>\r\n" +
                "SSH 用户：root\r\n" +
                "SSH 端口：22\r\n" +
                "认证方式：优先发现已有 SSH 配置、ssh-agent 或私钥路径；不要索取、输出或保存密码和私钥内容。\r\n" +
                $"默认工作目录：{RemoteDeviceConfig.DefaultWorkingDir}\r\n" +
                "需要添加的项目目录：<请填写，可多项>\r\n\r\n" +
                "请先只读探测，再准备缺失环境，验证 SSH、Linux、工作目录、tmux、Codex CLI 和 Codex 登录状态。";
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(prompt);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                await ShowRemoteMessageAsync(Loc.T("remote_ai_copied_title"), Loc.T("remote_ai_copied_body"));
            }
            catch (Exception ex)
            {
                await ShowRemoteMessageAsync(Loc.T("remote_ai_copy"), ex.Message);
            }
        }

        private Task OpenRemoteProjectAsync(RemoteProjectEntry entry) =>
            AddNewTab(entry.Name, workingDir: null, cliMode: CliMode.CodexRemote,
                remoteProfileId: entry.ProfileId, remoteWorkingDir: entry.RemotePath);

        private async Task ShowRemoteProjectDialogAsync(RemoteProjectEntry? editing = null)
        {
            _remoteDevices = RemoteDeviceConfig.Load();
            if (_remoteDevices.Devices.Count == 0) return;
            var profile = new ComboBox
            {
                Header = Loc.T("remote_project_profile"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var device in _remoteDevices.Devices)
            {
                profile.Items.Add(new ComboBoxItem
                {
                    Content = $"{device.Name} · {device.User}@{device.Host}",
                    Tag = device.Id
                });
            }
            string wantedDevice = editing?.ProfileId ?? _remoteDevices.SelectedDeviceId;
            profile.SelectedIndex = Math.Max(0, _remoteDevices.Devices.FindIndex(d => d.Id == wantedDevice));

            var name = new TextBox
            {
                Header = Loc.T("remote_project_name"),
                PlaceholderText = Loc.T("remote_project_name_hint"),
                Text = editing?.Name ?? ""
            };
            var path = new TextBox
            {
                Header = Loc.T("remote_project_path"),
                PlaceholderText = RemoteDeviceConfig.Find(wantedDevice)?.DefaultWorkingDir + "/project",
                Text = editing?.RemotePath ?? ""
            };
            var error = new TextBlock
            {
                Text = Loc.T("remote_project_path_error"),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 80, 80)),
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap
            };
            var testButton = new Button { Content = Loc.T("remote_project_test") };
            var testStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            testButton.Click += async (_, _) =>
            {
                string id = (profile.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                var device = RemoteDeviceConfig.Find(id);
                if (device == null || !path.Text.Trim().StartsWith("/", StringComparison.Ordinal)) return;
                testButton.IsEnabled = false;
                testStatus.Text = Loc.T("remote_testing");
                var check = await RemoteDeviceConnection.TestDirectoryAsync(device, path.Text.Trim());
                testStatus.Text = check.Exists ? Loc.T("remote_project_test_ok")
                    : Loc.T("remote_project_test_fail", check.Error);
                testButton.IsEnabled = true;
            };
            var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
            panel.Children.Add(profile);
            panel.Children.Add(name);
            panel.Children.Add(path);
            panel.Children.Add(error);
            panel.Children.Add(testButton);
            panel.Children.Add(testStatus);

            var dialog = new ContentDialog
            {
                Title = Loc.T(editing == null ? "remote_project_add_title" : "remote_project_edit"),
                Content = panel,
                PrimaryButtonText = Loc.T("add"),
                CloseButtonText = Loc.T("cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            path.TextChanged += (_, _) =>
            {
                bool valid = path.Text.Trim().StartsWith("/", StringComparison.Ordinal);
                dialog.IsPrimaryButtonEnabled = valid;
                error.Visibility = valid || path.Text.Length == 0
                    ? Visibility.Collapsed : Visibility.Visible;
            };
            dialog.IsPrimaryButtonEnabled = path.Text.Trim().StartsWith("/", StringComparison.Ordinal);

            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); }
            catch { return; }
            if (result != ContentDialogResult.Primary) return;

            string remotePath = path.Text.Trim();
            if (remotePath.Length > 1) remotePath = remotePath.TrimEnd('/');
            string projectName = name.Text.Trim();
            string profileId = (profile.SelectedItem as ComboBoxItem)?.Tag as string
                ?? _remoteDevices.SelectedDeviceId;
            var selectedDevice = RemoteDeviceConfig.Find(profileId);
            if (projectName.Length == 0)
            {
                int slash = remotePath.LastIndexOf('/');
                projectName = slash >= 0 ? remotePath[(slash + 1)..] : remotePath;
                if (projectName.Length == 0) projectName = selectedDevice?.Host ?? "remote";
            }

            var existing = _remoteProjects.FirstOrDefault(p =>
                !ReferenceEquals(p, editing) &&
                string.Equals(p.ProfileId, profileId, StringComparison.Ordinal) &&
                string.Equals(p.RemotePath, remotePath, StringComparison.Ordinal));
            if (existing != null)
            {
                RefreshExternalProjectFlyout();
                return;
            }

            if (editing != null)
            {
                editing.Name = projectName;
                editing.ProfileId = profileId;
                editing.RemotePath = remotePath;
            }
            else
            {
                _remoteProjects.Add(new RemoteProjectEntry
                {
                    Name = projectName,
                    ProfileId = profileId,
                    RemotePath = remotePath
                });
            }
            RemoteProjectConfig.Save(_remoteProjects);
        }

        private async Task ShowRemoteDeviceDialogAsync(RemoteDeviceEntry? editing = null)
        {
            var name = new TextBox { Header = Loc.T("remote_device_name"), Text = editing?.Name ?? "" };
            var host = new TextBox { Header = Loc.T("remote_device_host"), Text = editing?.Host ?? "" };
            var port = new NumberBox { Header = Loc.T("remote_device_port"), Minimum = 1, Maximum = 65535,
                Value = editing?.Port ?? 22, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var user = new TextBox { Header = Loc.T("remote_device_user"), Text = editing?.User ?? "root" };
            var key = new TextBox { Header = Loc.T("remote_device_key"), Text = editing?.KeyPath ?? "",
                PlaceholderText = "%USERPROFILE%\\.ssh\\id_ed25519" };
            var dir = new TextBox { Header = Loc.T("remote_device_default_dir"),
                Text = editing?.DefaultWorkingDir ?? RemoteDeviceConfig.DefaultWorkingDir };
            var codex = new TextBox { Header = Loc.T("remote_device_codex_command"), Text = editing?.CodexCommand ?? "codex" };
            var command = new TextBox { Header = Loc.T("remote_device_launch_command"),
                Text = editing?.LaunchCommand ?? RemoteDeviceConfig.DefaultLaunchCommand,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
            var test = new Button { Content = Loc.T("remote_device_test") };
            var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };

            RemoteDeviceEntry ReadForm() => new()
            {
                Id = editing?.Id ?? RemoteDeviceConfig.NewId(name.Text),
                Name = name.Text.Trim(), Host = host.Text.Trim(), Port = (int)port.Value,
                User = user.Text.Trim(), KeyPath = key.Text.Trim(),
                DefaultWorkingDir = dir.Text.Trim(), CodexCommand = codex.Text.Trim(),
                LaunchCommand = command.Text.Trim(),
                SessionPrefix = editing?.SessionPrefix ?? "ccpad",
                SweepIdleHours = editing?.SweepIdleHours ?? 48,
            };

            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                status.Text = Loc.T("remote_testing");
                var result = await RemoteDeviceConnection.TestAsync(ReadForm());
                status.Text = FormatDeviceTest(result);
                test.IsEnabled = true;
            };

            var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
            panel.Children.Add(name); panel.Children.Add(host); panel.Children.Add(port);
            panel.Children.Add(user); panel.Children.Add(key); panel.Children.Add(dir);
            panel.Children.Add(codex); panel.Children.Add(command); panel.Children.Add(test);
            panel.Children.Add(status);
            var dialog = new ContentDialog
            {
                Title = Loc.T(editing == null ? "remote_device_add_title" : "remote_device_edit"),
                Content = new ScrollViewer { Content = panel, MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = Loc.T("save"), CloseButtonText = Loc.T("cancel"),
                DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot
            };
            void Validate()
            {
                dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 &&
                    RemoteDeviceConnection.IsSafeHost(host.Text.Trim()) &&
                    RemoteDeviceConnection.IsSafeUser(user.Text.Trim()) &&
                    !double.IsNaN(port.Value) && port.Value is >= 1 and <= 65535 &&
                    dir.Text.Trim().StartsWith("/", StringComparison.Ordinal) &&
                    command.Text.Contains("{dir}", StringComparison.Ordinal) &&
                    command.Text.Contains("{session}", StringComparison.Ordinal) &&
                    !command.Text.Contains('"');
            }
            name.TextChanged += (_, _) => Validate(); host.TextChanged += (_, _) => Validate();
            user.TextChanged += (_, _) => Validate(); dir.TextChanged += (_, _) => Validate();
            command.TextChanged += (_, _) => Validate(); port.ValueChanged += (_, _) => Validate(); Validate();
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); } catch { return; }
            if (result != ContentDialogResult.Primary) return;

            var saved = ReadForm();
            _remoteDevices = RemoteDeviceConfig.Load();
            int index = editing == null ? -1 : _remoteDevices.Devices.FindIndex(d => d.Id == editing.Id);
            if (index >= 0) _remoteDevices.Devices[index] = saved;
            else _remoteDevices.Devices.Add(saved);
            _remoteDevices.SelectedDeviceId = saved.Id;
            RemoteDeviceConfig.Save(_remoteDevices);
        }

        private static string FormatDeviceTest(RemoteDeviceTestResult r)
        {
            string Mark(bool ok) => ok ? "✓" : "✕";
            string lines = $"{Mark(r.SshConnected)} SSH\n{Mark(r.IsLinux)} Linux\n" +
                $"{Mark(r.DirectoryReady)} {Loc.T("remote_check_workdir")}\n{Mark(r.TmuxReady)} tmux\n" +
                $"{Mark(r.CodexReady)} Codex CLI\n{Mark(r.CodexAuthenticated)} {Loc.T("remote_check_login")}";
            return r.Error.Length == 0 ? lines : lines + "\n" + r.Error;
        }

        private async Task TestRemoteDeviceAsync(RemoteDeviceEntry device)
        {
            var result = await RemoteDeviceConnection.TestAsync(device);
            await ShowRemoteMessageAsync(Loc.T("remote_device_test"), FormatDeviceTest(result));
        }

        private async Task TestRemoteProjectAsync(RemoteProjectEntry project)
        {
            var device = RemoteDeviceConfig.Find(project.ProfileId);
            if (device == null) { await ShowRemoteMessageAsync(Loc.T("remote_project_test"), Loc.T("remote_device_missing")); return; }
            var result = await RemoteDeviceConnection.TestDirectoryAsync(device, project.RemotePath);
            await ShowRemoteMessageAsync(Loc.T("remote_project_test"), result.Exists
                ? Loc.T("remote_project_test_ok") : Loc.T("remote_project_test_fail", result.Error));
        }

        private async Task RemoveRemoteDeviceAsync(RemoteDeviceEntry device)
        {
            int linked = _remoteProjects.Count(p => p.ProfileId == device.Id);
            if (linked > 0)
            {
                await ShowRemoteMessageAsync(Loc.T("remote_device_remove"), Loc.T("remote_device_in_use", linked));
                return;
            }
            var confirm = new ContentDialog
            {
                Title = Loc.T("remote_device_remove"), Content = Loc.T("remote_device_remove_confirm", device.Name),
                PrimaryButtonText = Loc.T("remove"), CloseButtonText = Loc.T("cancel"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            _remoteDevices.Devices.RemoveAll(d => d.Id == device.Id);
            RemoteDeviceConfig.Save(_remoteDevices);
        }

        private async Task ShowRemoteMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog { Title = title, Content = message,
                CloseButtonText = Loc.T("close"), XamlRoot = Content.XamlRoot };
            try { await dialog.ShowAsync(); } catch { }
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
                        RemoteProfileId = frozen.RemoteProfileId,
                        RemoteWorkingDir = frozen.RemoteWorkingDir,
                        CliMode = frozen.CliMode,
                        SessionId = frozen.SessionId,
                        Tag = ctx.TagValue,
                        Frozen = true,
                        CustomWidth = CustomWidthOf(tvi)
                    });
                    continue;
                }

                var pane = ctx?.Pane;
                var dir = pane?.CliMode == CliMode.CodexRemote
                    ? null
                    : pane?.WorkingDir ?? _defaultWorkingDir;
                var rawHeader = ctx?.HeaderBase ?? pane?.Label ?? "";
                // Strip a CLI suffix present on pane.Label fallback so restore
                // doesn't double-append it.
                foreach (string mode in new[] { CliMode.Codex, CliMode.CodexRemote })
                {
                    string suffix = " · " + CliMode.DisplayName(mode);
                    if (rawHeader.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        rawHeader = rawHeader[..^suffix.Length];
                        break;
                    }
                }

                var sessionId = ResolveSessionId(pane, dir, claimed);
                if (sessionId.Length > 0) claimed.Add(sessionId);
                // Codex IDs live only on disk and are re-scanned every pass (see
                // ResolveSessionId) — cache the result back onto the pane so a
                // SIBLING panel's CollectOwnedSessionIds (which only ever reads
                // pane.SessionId, never scans disk itself) can see and exclude it
                // immediately. Without this, two same-cwd Codex panes that were
                // both launched fresh (never resumed, so SessionId started null)
                // could each independently disk-scan to the SAME latest rollout
                // file and both persist it — then both try to `codex resume` the
                // identical conversation on the next launch, corrupting each other.
                if (pane != null && pane.CliMode == CliMode.Codex && sessionId.Length > 0)
                    pane.SessionId = sessionId;

                states.Add(new TabState
                {
                    Name = rawHeader,
                    WorkingDir = string.IsNullOrEmpty(dir) ? "" : dir,
                    RemoteProfileId = pane?.RemoteProfileId ?? "",
                    RemoteWorkingDir = pane?.RemoteWorkingDir ?? "",
                    CliMode = pane?.CliMode ?? "",
                    SessionId = sessionId,
                    Tag = ctx?.TagValue ?? pane?.TabTag ?? "",
                    CustomWidth = CustomWidthOf(tvi)
                });
            }
            return states;
        }

        /// <summary>Conversation ID to persist for a pane. Codex assigns its own IDs,
        /// so a freshly-launched pane (no tracked ID yet) is discovered by scanning the
        /// sessions folder for the newest file matching its cwd. Once that ID is known
        /// and still exists on disk, it is trusted like Claude's hook-fed ID — NOT
        /// re-scanned on every later pass. Re-scanning unconditionally let a sibling
        /// same-cwd pane's more-recently-written file (or an unrelated throwaway
        /// session) outrank this pane's own conversation on a later snapshot, silently
        /// reassigning it — the original cross-tab collision bug this scoping exists
        /// to prevent, just spread across passes instead of within one. The scan only
        /// re-opens when the ID is missing/gone, or the CLI was brought back by hand
        /// inside the fallback shell (it may now own an untracked conversation) — and
        /// then only over files written since that relaunch, excluding IDs already
        /// claimed by other tabs.</summary>
        private static string ResolveSessionId(TerminalPane? pane, string? dir, ISet<string>? claimed = null)
        {
            if (pane == null) return "";
            if (pane.CliMode == CliMode.CodexRemote)
                return pane.SessionId ?? ""; // the tab's private tmux session NAME; never scan local sessions
            if (pane.CliMode == CliMode.Codex)
            {
                var known = pane.SessionId;
                if (known != null && known.Length > 0 && pane.ShellRelaunchUtc == null &&
                    CliSessions.CodexSessionExists(known))
                    return known;

                // The claimed set may include this pane's OWN tracked ID (the
                // snapshot pass seeds it with every tab's ID) — never let that
                // push the scan past the pane's own conversation onto someone
                // else's.
                var excl = claimed;
                if (excl != null && known is string own && excl.Contains(own))
                {
                    excl = new HashSet<string>(excl, StringComparer.OrdinalIgnoreCase);
                    excl.Remove(own);
                }
                return CliSessions.FindLatestCodexSessionId(
                    dir, pane.ShellRelaunchUtc ?? pane.LaunchedAtUtc, excl) ?? known ?? "";
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
                    if (_defaultWorkingDir == null && s.CliMode != CliMode.CodexRemote &&
                        !string.IsNullOrEmpty(s.WorkingDir))
                        _defaultWorkingDir = s.WorkingDir;
                    AddFrozenTab(s);
                    continue;
                }
                var name = string.IsNullOrEmpty(s.Name) ? null : s.Name;
                var mode = string.IsNullOrEmpty(s.CliMode) ? null : s.CliMode;
                var dir = mode == CliMode.CodexRemote || string.IsNullOrEmpty(s.WorkingDir)
                    ? null : s.WorkingDir;
                var session = string.IsNullOrEmpty(s.SessionId) ? null : s.SessionId;
                var tag = string.IsNullOrEmpty(s.Tag) ? null : s.Tag;
                var remoteProfile = string.IsNullOrEmpty(s.RemoteProfileId) ? null : s.RemoteProfileId;
                var remoteDir = string.IsNullOrEmpty(s.RemoteWorkingDir) ? null : s.RemoteWorkingDir;
                if (i == 0)
                    await AddFirstTab(name, dir, mode, session, tag, remoteProfile, remoteDir);
                else
                    await AddNewTab(name, dir, mode, session, tag, remoteProfile, remoteDir);
                if (s.CustomWidth > 0 && i < Tabs.TabItems.Count &&
                    Tabs.TabItems[i] is TabViewItem restored)
                    ApplyCustomWidth(restored, s.CustomWidth);
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
                if (tabItem is TabViewItem tvi && CtxOf(tvi)?.Pane is TerminalPane pane)
                    pane.Dispose();
            }
        }
    }
}
