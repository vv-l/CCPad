using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CCPad.Localization;
using CCPad.Settings;
using CCPad.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace CCPad;

public sealed partial class CodexObserverWindow : Window
{
    public sealed record AgentRow(string Id, string Label, string Status)
    {
        public override string ToString() => Label;
    }
    public sealed record EventRow(ObservedEvent Event, string Caption, string Summary)
    {
        public override string ToString() => Caption;
    }
    private readonly string _codexHome;
    private readonly string _cwd;
    private readonly Func<string?> _confirmedSession;
    private readonly Func<bool> _paneAlive;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private CodexObservationReader? _reader;
    private string? _sessionId;
    private string? _lastConfirmed;
    private ObservationSnapshot? _snapshot;
    private bool _closed;
    private bool _reading;
    private bool _rendering;
    private string _selectedAgent = "";
    private string? _bindingError;
    private bool? _lastPaneAlive;

    public CodexObserverWindow(string codexHome, string cwd, Func<string?> confirmedSession, Func<bool> paneAlive)
    {
        _codexHome = codexHome;
        _cwd = cwd;
        _confirmedSession = confirmedSession;
        _paneAlive = paneAlive;
        InitializeComponent();
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ObserverRoot.RequestedTheme = ThemeManager.ToElementTheme(ThemeManager.Pref);
        AppWindow.Resize(new SizeInt32(1120, 820));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "claude.ico"));
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        ObserverRoot.Loaded += (_, _) => { _timer.Start(); _ = RefreshAsync(); };
        Loc.LanguageChanged += OnLanguageChanged;
        ThemeManager.PrefChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            Loc.LanguageChanged -= OnLanguageChanged;
            ThemeManager.PrefChanged -= OnThemeChanged;
        };
        Localize();
    }

    private void Localize()
    {
        Title = ObserverTitle.Text = Loc.T("observe_title");
        BindingHelp.Text = Loc.T("observe_binding_help");
        SessionIdBox.PlaceholderText = Loc.T("observe_session_id");
        BindButton.Content = Loc.T("observe_bind");
        SearchBox.PlaceholderText = Loc.T("observe_search");
        FollowButton.Content = Loc.T("observe_follow");
        AgentsHeading.Text = Loc.T("observe_agents");
        TimelineHeading.Text = Loc.T("observe_timeline");
        CoverageText.Text = Loc.T("observe_coverage");
        Render(true);
    }

    private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(() => { if (!_closed) Localize(); });
    private void OnThemeChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed) ObserverRoot.RequestedTheme = ThemeManager.ToElementTheme(ThemeManager.Pref);
    });

    private async Task RefreshAsync()
    {
        if (_closed || _reading) return;
        _reading = true;
        try
        {
            var confirmed = _confirmedSession();
            if (confirmed != _lastConfirmed)
            {
                _lastConfirmed = confirmed;
                // A real CLI switch invalidates the old binding, including a manual one.
                Bind(confirmed);
            }
            if (_reader == null && confirmed != null) Bind(confirmed);
            var reader = _reader;
            if (reader == null) { Render(false); return; }
            var snapshot = await Task.Run(reader.Read);
            if (_closed || !ReferenceEquals(reader, _reader)) return;
            var paneAlive = _paneAlive();
            var changed = _lastPaneAlive != paneAlive || _snapshot == null || !_snapshot.Agents.SequenceEqual(snapshot.Agents) ||
                !_snapshot.Events.SequenceEqual(snapshot.Events);
            _lastPaneAlive = paneAlive;
            _snapshot = snapshot;
            Render(changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _bindingError = Loc.T("observe_read_failed");
            Render(false);
        }
        finally { _reading = false; }
    }

    private void Bind(string? id)
    {
        if (_sessionId == id) return;
        _sessionId = id;
        _reader = id == null ? null : new CodexObservationReader(_codexHome, id);
        _snapshot = null;
        _selectedAgent = "";
        _bindingError = null;
        Render(true);
    }

    private async void OnBindClick(object sender, RoutedEventArgs e)
    {
        BindButton.IsEnabled = false;
        var input = SessionIdBox.Text.Trim();
        var confirmedBefore = _confirmedSession();
        var id = await Task.Run(() => CodexObservationReader.ValidateRoot(_codexHome, input, _cwd));
        if (_closed) return;
        BindButton.IsEnabled = true;
        if (confirmedBefore != _confirmedSession()) return;
        if (id == null || (confirmedBefore != null && confirmedBefore != id)) _bindingError = Loc.T("observe_invalid_id");
        else { Bind(id); _bindingError = null; }
        Render(true);
        _ = RefreshAsync();
    }

    private void Render(bool changed)
    {
        if (_closed) return;
        SessionText.Text = _cwd + "\n" + (_sessionId ?? Loc.T("observe_waiting_id"));
        BindingNotice.IsOpen = _sessionId == null || _bindingError != null || _snapshot?.Error != null;
        BindingNotice.Message = _bindingError ?? (_snapshot?.Error is string error ? Loc.T("observe_" + error) : "");
        var agents = _snapshot?.Agents ?? Array.Empty<ObservedAgent>();
        if (changed)
        {
            _rendering = true;
            var rows = new List<AgentRow> { new("", Loc.T("observe_all"), Loc.T("observe_count", agents.Count)) };
            var seen = new HashSet<string>();
            void Append(string? parent, int depth)
            {
                foreach (var a in agents.Where(a => parent == null ? a.Id == _sessionId : a.ParentId == parent).OrderBy(a => a.CreatedAt))
                {
                    if (!seen.Add(a.Id)) continue;
                    rows.Add(new AgentRow(a.Id, new string(' ', Math.Min(depth, 8) * 2) + (depth == 0 ? Loc.T("observe_brain") : "↳ " + a.Name),
                        State(a.Id == _sessionId && !_paneAlive() ? "unknown" : a.State) + " · " + a.Id[..8] + (a.Model.Length == 0 ? "" : "\n" + a.Model)));
                    Append(a.Id, depth + 1);
                }
            }
            Append(null, 0);
            AgentList.ItemsSource = rows;
            AgentList.SelectedItem = rows.FirstOrDefault(a => a.Id == _selectedAgent) ?? rows[0];
            _rendering = false;
            RenderEvents();
        }
        var selected = agents.FirstOrDefault(a => a.Id == (_selectedAgent.Length == 0 ? _sessionId : _selectedAgent));
        var usage = selected?.InputTokens is long input
            ? Loc.T("observe_usage", input.ToString("N0"), (selected.CachedTokens ?? 0).ToString("N0"), (selected.OutputTokens ?? 0).ToString("N0"))
            : Loc.T("observe_usage_unknown");
        MetricsText.Text = (!_paneAlive() ? Loc.T("observe_pane_closed") + " · " : "") + usage +
            (_snapshot?.CatchingUp == true ? " · " + Loc.T("observe_catching_up") : "") +
            (_snapshot?.HistoryClipped == true ? " · " + Loc.T("observe_clipped") : "") +
            (_snapshot?.UnreadableLines > 0 ? " · " + Loc.T("observe_bad_lines", _snapshot.UnreadableLines) : "");
    }

    private void RenderEvents()
    {
        var selectedId = (TimelineList.SelectedItem as EventRow)?.Event.Id;
        var search = SearchBox.Text.Trim();
        var events = (_snapshot?.Events ?? Array.Empty<ObservedEvent>())
            .Where(e => _selectedAgent.Length == 0 || e.AgentId == _selectedAgent)
            .Where(e => search.Length == 0 || e.Detail.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(e => new EventRow(e,
                $"{e.At.ToLocalTime():HH:mm:ss}  ·  {AgentName(e.AgentId)}  ·  {EventTitle(e)}" +
                (e.State.Length == 0 ? "" : "  ·  " + State(e.State)) +
                (e.Seconds is double seconds ? $"  ·  {seconds:0.0}s" : ""),
                e.Detail.Length == 0 ? EventTitle(e) : e.Detail[..Math.Min(240, e.Detail.Length)].Replace('\r', ' ').Replace('\n', ' ')))
            .ToList();
        _rendering = true;
        TimelineList.ItemsSource = events;
        var selection = FollowButton.IsChecked == true ? events.LastOrDefault() : events.FirstOrDefault(e => e.Event.Id == selectedId);
        TimelineList.SelectedItem = selection;
        _rendering = false;
        if (selection != null && FollowButton.IsChecked == true) TimelineList.ScrollIntoView(selection);
        ShowDetail(selection);
    }

    private string AgentName(string id) => id == _sessionId ? Loc.T("observe_brain") :
        _snapshot?.Agents.FirstOrDefault(a => a.Id == id)?.Name ?? id[..8];
    private static string EventTitle(ObservedEvent e) => e.Kind == "tool" && e.Title != "tool_output" ? e.Title : Loc.T("observe_" + e.Title);
    private static string State(string state) => Loc.T("observe_state_" + state);
    private void ShowDetail(EventRow? row)
    {
        DetailHeading.Text = row?.Caption ?? Loc.T("observe_detail");
        DetailBox.Text = row?.Event.Detail ?? "";
    }
    private void OnAgentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        _selectedAgent = (AgentList.SelectedItem as AgentRow)?.Id ?? "";
        RenderEvents();
        Render(false);
    }
    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = TimelineList.SelectedItem as EventRow;
        if (!_rendering && row != null) FollowButton.IsChecked = false;
        ShowDetail(row);
    }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (TimelineList != null) RenderEvents(); }
    private void OnFollowClick(object sender, RoutedEventArgs e) => RenderEvents();
}
