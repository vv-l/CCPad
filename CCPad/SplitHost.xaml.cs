using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CCPad.Controls;
using CCPad.Settings;
using CCPad.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CCPad
{
    public sealed partial class SplitHost : UserControl
    {
        private SplitNode _root = null!;
        private PaneNode _activePane = null!;
        private List<ProjectEntry> _projects = new();

        /// <summary>Fired whenever layout or tab state changes (for autosave).</summary>
        public event Action? LayoutChanged;

        public SplitHost(List<ProjectEntry> projects)
        {
            InitializeComponent();
            _projects = projects;

            var panel = CreateTabPanel();
            var node = new PaneNode { Panel = panel };
            _root = node;
            _activePane = node;

            Rebuild();
        }

        public PaneNode ActivePane => _activePane;

        /// <summary>The TerminalPane in the currently-active tab panel, if any.</summary>
        public TerminalPane? ActiveTerminal => _activePane?.Panel?.CurrentPane;

        /// <summary>Raised when the active pane changes (so chrome like the staging toggle can refresh).</summary>
        public event Action? ActivePaneChanged;

        /// <summary>Raised when any pane toggles staging via the Alt+` hotkey, so the
        /// host toolbar button can re-sync.</summary>
        public event Action? StagingChanged;

        // ── Initialization ──────────────────────────────────────────────

        public async Task InitializeFirstTab(string? projectName = null, string? workingDir = null)
        {
            await _activePane.Panel.AddFirstTab(projectName, workingDir);
        }

        public void UpdateProjects(List<ProjectEntry> projects)
        {
            _projects = projects;
            ForEachPanel(_root, p => p.UpdateProjects(projects));
        }

        // ── Split / Close / Navigate ────────────────────────────────────

        public async Task SplitPanel(TabPanel source, SplitOrientation orientation)
        {
            var sourceNode = FindPanelNode(_root, source);
            if (sourceNode == null) return;

            var newPanel = CreateTabPanel();
            var newNode = new PaneNode { Panel = newPanel };

            var container = new SplitContainerNode
            {
                Orientation = orientation,
                First = sourceNode,
                Second = newNode,
                SplitRatio = 0.5
            };

            ReplaceNode(sourceNode, container);
            sourceNode.Parent = container;
            newNode.Parent = container;

            Rebuild();

            await newPanel.AddFirstTab(null, source.DefaultWorkingDir);
            SetActivePane(newNode);
            newPanel.FocusCurrentTab();
        }

        public void ClosePanel(TabPanel source)
        {
            var node = FindPanelNode(_root, source);
            if (node == null) return;

            if (_root == node)
            {
                // Last panel — don't close
                return;
            }

            var parent = node.Parent as SplitContainerNode;
            if (parent == null) return;

            SplitNode sibling = parent.First == node ? parent.Second : parent.First;
            ReplaceNode(parent, sibling);

            node.Panel.DisposeAll();

            var newActive = FindFirstLeaf(sibling);
            _activePane = newActive;
            Rebuild();
            newActive.Panel.FocusCurrentTab();
        }

        public void NavigateFocus(Direction direction)
        {
            var target = FindAdjacentPane(_activePane, direction);
            if (target != null)
            {
                SetActivePane(target);
                target.Panel.FocusCurrentTab();
            }
        }

        public void FocusActive() => _activePane.Panel.FocusCurrentTab();

        /// <summary>Apply the same right-edge reserve to every panel's tab strip.</summary>
        public void SetWorkspaceReserve(double width)
            => ForEachPanel(_root, p => p.SetWorkspaceReserve(width));

        /// <summary>Widest project button across all panels (they share one localized label).</summary>
        public double MaxProjectButtonWidth()
        {
            double max = 0;
            ForEachPanel(_root, p => max = Math.Max(max, p.ProjectButtonDesiredWidth()));
            return max;
        }

        // ── TabPanel factory ────────────────────────────────────────────

        private TabPanel CreateTabPanel()
        {
            var panel = new TabPanel(_projects);
            panel.SplitRequested += async (src, orient) => await SplitPanel(src, orient);
            panel.CloseRequested += ClosePanel;
            panel.Focused += src =>
            {
                var node = FindPanelNode(_root, src);
                if (node != null) SetActivePane(node);
            };
            panel.NavigateRequested += (src, dir) => NavigateFocus(dir);
            panel.TabsChanged += () => LayoutChanged?.Invoke();
            panel.StagingChanged += () => StagingChanged?.Invoke();
            return panel;
        }

        // ── Tree helpers ────────────────────────────────────────────────

        private void ReplaceNode(SplitNode oldNode, SplitNode newNode)
        {
            if (oldNode == _root)
            {
                _root = newNode;
                newNode.Parent = null;
            }
            else if (oldNode.Parent is SplitContainerNode p)
            {
                if (p.First == oldNode) p.First = newNode;
                else p.Second = newNode;
                newNode.Parent = p;
            }
        }

        private static PaneNode FindFirstLeaf(SplitNode node)
        {
            while (node is SplitContainerNode sc)
                node = sc.First;
            return (PaneNode)node;
        }

        private static PaneNode? FindPanelNode(SplitNode node, TabPanel panel)
        {
            if (node is PaneNode pn && pn.Panel == panel) return pn;
            if (node is SplitContainerNode sc)
                return FindPanelNode(sc.First, panel) ?? FindPanelNode(sc.Second, panel);
            return null;
        }

        private static PaneNode? FindAdjacentPane(PaneNode current, Direction direction)
        {
            var targetOrientation = direction is Direction.Left or Direction.Right
                ? SplitOrientation.Vertical
                : SplitOrientation.Horizontal;
            bool forward = direction is Direction.Right or Direction.Down;

            SplitNode node = current;
            while (node.Parent is SplitContainerNode parent)
            {
                if (parent.Orientation == targetOrientation)
                {
                    bool isFirst = parent.First == node;
                    if ((forward && isFirst) || (!forward && !isFirst))
                    {
                        var target = forward ? parent.Second : parent.First;
                        return DescendToEdge(target, direction);
                    }
                }
                node = parent;
            }
            return null;
        }

        private static PaneNode DescendToEdge(SplitNode node, Direction comingFrom)
        {
            while (node is SplitContainerNode sc)
            {
                bool pickFirst = comingFrom is Direction.Right or Direction.Down;
                node = pickFirst ? sc.First : sc.Second;
            }
            return (PaneNode)node;
        }

        private void SetActivePane(PaneNode node)
        {
            _activePane = node;
            ActivePaneChanged?.Invoke();
        }

        private static void ForEachPanel(SplitNode node, Action<TabPanel> action)
        {
            if (node is PaneNode pn) action(pn.Panel);
            else if (node is SplitContainerNode sc)
            {
                ForEachPanel(sc.First, action);
                ForEachPanel(sc.Second, action);
            }
        }

        // ── Visual tree building ────────────────────────────────────────

        private void Rebuild()
        {
            DetachPanels(_root);
            RootGrid.Children.Clear();
            var element = BuildLayout(_root);
            RootGrid.Children.Add(element);

            // After the layout pass settles, force all terminals to recalculate
            // their dimensions. Without this, existing panes keep stale sizes
            // after a split because WebView2 doesn't immediately reflect the new
            // XAML container size to the internal browser viewport.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ForEachPanel(_root, p => p.RefitAllTerminals()));

            LayoutChanged?.Invoke();
        }

        private static void DetachPanels(SplitNode node)
        {
            if (node is PaneNode pn)
            {
                if (pn.Panel.Parent is Panel panel)
                    panel.Children.Remove(pn.Panel);
            }
            else if (node is SplitContainerNode sc)
            {
                DetachPanels(sc.First);
                DetachPanels(sc.Second);
            }
        }

        private FrameworkElement BuildLayout(SplitNode node)
        {
            if (node is PaneNode pn)
                return pn.Panel;

            var sc = (SplitContainerNode)node;
            var grid = new Grid();
            var splitter = new GridSplitter(sc.Orientation, sc);

            if (sc.Orientation == SplitOrientation.Vertical)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sc.SplitRatio, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - sc.SplitRatio, GridUnitType.Star) });

                var first = BuildLayout(sc.First);
                Grid.SetColumn(first, 0);
                grid.Children.Add(first);

                Grid.SetColumn(splitter, 1);
                grid.Children.Add(splitter);

                var second = BuildLayout(sc.Second);
                Grid.SetColumn(second, 2);
                grid.Children.Add(second);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(sc.SplitRatio, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0 - sc.SplitRatio, GridUnitType.Star) });

                var first = BuildLayout(sc.First);
                Grid.SetRow(first, 0);
                grid.Children.Add(first);

                Grid.SetRow(splitter, 1);
                grid.Children.Add(splitter);

                var second = BuildLayout(sc.Second);
                Grid.SetRow(second, 2);
                grid.Children.Add(second);
            }

            return grid;
        }

        // ── Workspace snapshot / restore ────────────────────────────────

        public LayoutNode SnapshotLayout()
        {
            return SnapshotNode(_root);
        }

        private static LayoutNode SnapshotNode(SplitNode node)
        {
            if (node is PaneNode pn)
            {
                return new LayoutNode
                {
                    Type = "pane",
                    Tabs = pn.Panel.GetTabStates(),
                    ActiveTabIndex = pn.Panel.ActiveTabIndex
                };
            }

            var sc = (SplitContainerNode)node;
            return new LayoutNode
            {
                Type = "split",
                Orientation = sc.Orientation == SplitOrientation.Horizontal ? "horizontal" : "vertical",
                SplitRatio = sc.SplitRatio,
                First = SnapshotNode(sc.First),
                Second = SnapshotNode(sc.Second)
            };
        }

        /// <summary>
        /// Phase 1: Build the split tree structure without starting any terminals.
        /// The host must be added to the visual tree BEFORE calling InitializeTerminals().
        /// </summary>
        public static SplitHost RestoreFromLayout(LayoutNode layout, List<ProjectEntry> projects)
        {
            var host = new SplitHost(projects, skipInitialPane: true);
            host._root = host.BuildNodeFromLayout(layout, null);
            host._activePane = FindFirstLeaf(host._root);
            host.Rebuild();
            return host;
        }

        /// <summary>
        /// Phase 2: Initialize all terminals. Must be called AFTER the host is in the visual tree
        /// (WebView2 requires Loaded event to fire).
        /// </summary>
        public async Task InitializeTerminals()
        {
            var tasks = new List<Task>();
            CollectInitTasks(_root, tasks);
            await Task.WhenAll(tasks);
        }

        private static void CollectInitTasks(SplitNode node, List<Task> tasks)
        {
            if (node is PaneNode pn)
            {
                tasks.Add(pn.Panel.InitializeRestoredTabs());
            }
            else if (node is SplitContainerNode sc)
            {
                CollectInitTasks(sc.First, tasks);
                CollectInitTasks(sc.Second, tasks);
            }
        }

        private SplitNode BuildNodeFromLayout(LayoutNode layout, SplitContainerNode? parent)
        {
            if (layout.Type == "pane")
            {
                var panel = CreateTabPanel();
                var node = new PaneNode { Panel = panel, Parent = parent };

                // Store tab states for later initialization (phase 2)
                panel.SetPendingRestore(layout.Tabs, layout.ActiveTabIndex);

                return node;
            }

            var orientation = layout.Orientation == "horizontal"
                ? SplitOrientation.Horizontal
                : SplitOrientation.Vertical;

            var container = new SplitContainerNode
            {
                Orientation = orientation,
                SplitRatio = layout.SplitRatio,
                Parent = parent
            };

            container.First = BuildNodeFromLayout(layout.First!, container);
            container.Second = BuildNodeFromLayout(layout.Second!, container);

            return container;
        }

        // Constructor for restore — skips creating the initial pane
        private SplitHost(List<ProjectEntry> projects, bool skipInitialPane)
        {
            InitializeComponent();
            _projects = projects;
        }

        // ── Disposal ────────────────────────────────────────────────────

        public void DisposeAll()
        {
            ForEachPanel(_root, p => p.DisposeAll());
        }
    }
}
