using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>Marshals a tray update onto the UI thread and swallows anything it throws.</summary>
internal static class TrayUi
{
    /// <summary>
    /// Remembered on first use. Application.Current goes null during shutdown, and without a
    /// cached dispatcher a late event from a worker thread would run inline and mutate a bound
    /// collection off the UI thread. Once it is cached, a late post fails loudly instead.
    /// </summary>
    private static Dispatcher? _dispatcher;

    public static void Post(Action action)
    {
        var dispatcher = _dispatcher ??= System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Run(action);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Run(action);
            return;
        }

        try
        {
            _ = dispatcher.InvokeAsync(() => Run(action));
        }
        catch (Exception ex)
        {
            AppLog.Warn("A tray update could not be marshalled to the UI thread.", ex);
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn("A tray update failed.", ex);
        }
    }
}

/// <summary>The shapes a quick-launch row can take. Drives the implicit data templates.</summary>
public enum TrayRowKind
{
    Header,
    Session,
    MultiConfig,
    Connection,
    More,
    Message,
}

/// <summary>One line in the quick-launch list.</summary>
public abstract class TrayRow : ObservableObject
{
    private bool _isDisconnecting;

    public abstract TrayRowKind Kind { get; }

    /// <summary>False for headers and messages: they are skipped by the keyboard and the mouse.</summary>
    public virtual bool IsSelectable => true;

    /// <summary>True when the row stands for at least one live session that a disconnect would end.</summary>
    public virtual bool CanDisconnect => false;

    /// <summary>
    /// Every session behind the row is being closed. Remote Desktop takes a few seconds to go,
    /// and without this the row would look untouched until it did.
    /// </summary>
    public bool IsDisconnecting
    {
        get => _isDisconnecting;
        internal set => SetProperty(ref _isDisconnecting, value);
    }
}

/// <summary>A section title such as RUNNING or CONNECTIONS.</summary>
public sealed class TrayHeaderRow : TrayRow
{
    public TrayHeaderRow(string title) => Title = title;

    public string Title { get; }

    public override TrayRowKind Kind => TrayRowKind.Header;

    public override bool IsSelectable => false;
}

/// <summary>A live session, with its thumbnail, state and uptime.</summary>
public sealed class TraySessionRow : TrayRow, IDisposable
{
    private string _uptimeText = string.Empty;
    private bool _showSnapshot = true;
    private bool _disposed;

    public TraySessionRow(RdpSession session)
    {
        Session = session;
        Session.PropertyChanged += OnSessionPropertyChanged;
        UpdateUptime();
    }

    /// <summary>The live model. State and StateText are bound straight through to it.</summary>
    public RdpSession Session { get; }

    public override TrayRowKind Kind => TrayRowKind.Session;

    public override bool CanDisconnect => Session.IsActive;

    public string Title => Session.DisplayName;

    public string Host => Session.Host;

    /// <summary>
    /// Proxied rather than bound directly: the snapshot file keeps its name and is overwritten in
    /// place, so RdpSession.SnapshotPath stops raising changes after the first capture. Watching
    /// LastSnapshotUtc as well is what makes a refreshed thumbnail actually appear.
    /// </summary>
    public string? SnapshotPath => Session.SnapshotPath;

    public bool ShowSnapshot
    {
        get => _showSnapshot;
        set
        {
            if (SetProperty(ref _showSnapshot, value))
                Raise(nameof(HasSnapshot), nameof(ShowPlaceholder));
        }
    }

    public bool HasSnapshot => _showSnapshot && !string.IsNullOrWhiteSpace(Session.SnapshotPath);

    public bool ShowPlaceholder => _showSnapshot && !HasSnapshot;

    public string UptimeText => _uptimeText;

    /// <summary>Recomputes the uptime caption. Called once a second while the popup is open.</summary>
    public void UpdateUptime()
    {
        var span = Session.Uptime;
        if (span.Ticks < 0) span = TimeSpan.Zero;

        var text =
            span.TotalSeconds < 60 ? UiLanguage.Format(Strings.Tray_Uptime_Seconds, (int)span.TotalSeconds) :
            span.TotalMinutes < 60 ? UiLanguage.Format(Strings.Tray_Uptime_Minutes, (int)span.TotalMinutes) :
            span.TotalHours < 24 ? UiLanguage.Format(Strings.Tray_Uptime_Hours, (int)span.TotalHours, span.Minutes) :
            UiLanguage.Format(Strings.Tray_Uptime_Days, (int)span.TotalDays, span.Hours);

        SetProperty(ref _uptimeText, text, nameof(UptimeText));
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RdpSession.SnapshotPath) or nameof(RdpSession.LastSnapshotUtc)))
            return;

        // The snapshot writer runs on a worker thread, so hop before touching bindings.
        TrayUi.Post(() => Raise(nameof(SnapshotPath), nameof(HasSnapshot), nameof(ShowPlaceholder)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Session.PropertyChanged -= OnSessionPropertyChanged;
    }
}

/// <summary>A multi-config, with its item count and a running badge.</summary>
public sealed class TrayMultiRow : TrayRow
{
    private bool _isRunning;

    public TrayMultiRow(MultiConfig config) => Config = config;

    public MultiConfig Config { get; private set; }

    public override TrayRowKind Kind => TrayRowKind.MultiConfig;

    public string Title => Config.Name;

    public string CountText => UiLanguage.Plural(Config.Items.Count, Strings.Tray_Multi_Count_One, Strings.Tray_Multi_Count_Many);

    public override bool CanDisconnect => IsRunning;

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    internal void Update(MultiConfig config)
    {
        if (ReferenceEquals(Config, config)) return;
        Config = config;
        Raise(nameof(Title), nameof(CountText));
    }
}

/// <summary>A stored connection.</summary>
public sealed class TrayConnectionRow : TrayRow
{
    private bool _isConnected;
    private bool _showSnapshot;
    private string? _snapshotPath;
    private DateTime _snapshotStamp;

    public TrayConnectionRow(RdpConnection connection) => Connection = connection;

    /// <summary>The thumbnail column is on: the row shows a picture or, without one, the host.</summary>
    public bool ShowSnapshot => _showSnapshot;

    /// <summary>The connection's last capture, which outlives its sessions.</summary>
    public string? SnapshotPath => _snapshotPath;

    /// <summary>When that capture was written. The file keeps its name, so this is what makes a newer one load.</summary>
    public DateTime SnapshotStamp => _snapshotStamp;

    public bool HasSnapshot => _showSnapshot && _snapshotPath is not null;

    public bool ShowPlaceholder => _showSnapshot && _snapshotPath is null;

    /// <summary>Whether the host answered when last asked; Unknown while connected or never checked.</summary>
    public Reachability Reach { get; private set; }

    /// <summary>"Responds" or "No answer", beside a connection that is not running.</summary>
    public string ReachLabel { get; private set; } = string.Empty;

    /// <summary>What was checked and when.</summary>
    public string? ReachTooltip { get; private set; }

    internal void SetReach(ReachabilityResult? result)
    {
        var state = IsConnected || result is null ? Reachability.Unknown : result.State;
        var label = state switch
        {
            Reachability.Responds => Strings.Reach_Responds,
            Reachability.NoAnswer => Strings.Reach_NoAnswer,
            _ => string.Empty,
        };
        string? tooltip = null;
        if (state != Reachability.Unknown)
        {
            var when = DynatecRDM.Converters.RelativeTimeConverter.Describe(result!.CheckedUtc);
            tooltip = state == Reachability.Responds
                ? UiLanguage.Format(Strings.Reach_Responds_Detail, result.Port, when)
                : UiLanguage.Format(Strings.Reach_NoAnswer_Detail, result.Port, when) + Environment.NewLine + Strings.Reach_NoAnswer_Hint;
        }

        if (Reach == state && ReachLabel == label && ReachTooltip == tooltip) return;
        Reach = state;
        ReachLabel = label;
        ReachTooltip = tooltip;
        Raise(nameof(Reach), nameof(ReachLabel), nameof(ReachTooltip));
    }

    internal void SetSnapshot(bool show, string? path, DateTime stamp)
    {
        if (_showSnapshot == show && _snapshotPath == path && _snapshotStamp == stamp) return;

        _showSnapshot = show;
        _snapshotPath = path;
        _snapshotStamp = stamp;
        Raise(nameof(ShowSnapshot), nameof(SnapshotPath), nameof(SnapshotStamp), nameof(HasSnapshot), nameof(ShowPlaceholder));
    }

    public RdpConnection Connection { get; private set; }

    public override TrayRowKind Kind => TrayRowKind.Connection;

    public string Title => Connection.Name;

    public string Host => Connection.FullAddress;

    public string? Color => Connection.Color;

    public override bool CanDisconnect => IsConnected;

    /// <summary>True when a session for this connection is live, so a click focuses instead of launching.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    internal void Update(RdpConnection connection)
    {
        if (ReferenceEquals(Connection, connection)) return;
        Connection = connection;
        Raise(nameof(Title), nameof(Host), nameof(Color));
    }
}

/// <summary>The row that opens the main window when the list was capped.</summary>
public sealed class TrayMoreRow : TrayRow
{
    private string _text = Strings.Tray_More;

    public override TrayRowKind Kind => TrayRowKind.More;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }
}

/// <summary>The empty-state line.</summary>
public sealed class TrayMessageRow : TrayRow
{
    private string _text = string.Empty;

    public override TrayRowKind Kind => TrayRowKind.Message;

    public override bool IsSelectable => false;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }
}

/// <summary>
/// Backs the quick-launch popup. The tray icon keeps one of these alive for the life of the
/// application and refreshes it in the background, so opening the popup is a plain Show() with
/// no store call and no disk access on the way in.
/// </summary>
public sealed class TrayMenuViewModel : ObservableObject, IDisposable
{
    private const int MinimumItems = 4;
    private const int HeaderCacheLimit = 64;

    private readonly AppServices _services;
    private readonly IAppShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;

    private readonly Dictionary<Guid, TraySessionRow> _sessionRows = new();
    private readonly Dictionary<Guid, TrayMultiRow> _multiRows = new();
    private readonly Dictionary<Guid, TrayConnectionRow> _connectionRows = new();
    private readonly Dictionary<string, TrayHeaderRow> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _groupNames = new();
    private readonly List<TraySessionRow> _ticking = new();

    /// <summary>Sessions a disconnect has been asked for and not yet finished. UI thread only.</summary>
    private readonly HashSet<Guid> _closing = new();

    private readonly TrayMoreRow _moreRow = new();
    private readonly TrayMessageRow _messageRow = new();

    private IReadOnlyList<RdpConnection> _connections = Array.Empty<RdpConnection>();
    private IReadOnlyList<ConnectionGroup> _groups = Array.Empty<ConnectionGroup>();

    /// <summary>Held while the list is open, so hosts are checked only then.</summary>
    private IDisposable? _reachDemand;

    /// <summary>For the rebuild in progress: whether rows show pictures, and which connections have one.</summary>
    private bool _rowSnapshots;
    private Dictionary<Guid, DateTime>? _lastKnownSnapshots;
    private IReadOnlyList<MultiConfig> _multiConfigs = Array.Empty<MultiConfig>();

    private string _searchText = string.Empty;
    private string _runningSummary = Strings.Tray_RunningSummary_None;
    private TrayRow? _selectedRow;
    private int _loading;
    private bool _disposed;

    public TrayMenuViewModel(AppServices services, IAppShell shell)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        DisconnectCommand = new RelayCommand(p => Disconnect(p as TrayRow));
        OpenManagerCommand = new RelayCommand(OpenManager);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ExitCommand = new RelayCommand(Exit);

        _clock = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clock.Tick += OnClockTick;

        _services.Sessions.SessionStarted += OnSessionChanged;
        _services.Sessions.SessionStateChanged += OnSessionChanged;
        _services.Sessions.SessionEnded += OnSessionEnded;
        _services.SettingsChanged += OnSettingsChanged;
        _services.Reachability.Changed += OnReachabilityChanged;

        Rebuild();
    }

    /// <summary>Every visible line, headers included, in display order.</summary>
    public ObservableCollection<TrayRow> Rows { get; } = new();

    /// <summary>Ends the sessions behind a session, connection or multi-config row.</summary>
    public ICommand DisconnectCommand { get; }

    public ICommand OpenManagerCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand ExitCommand { get; }

    /// <summary>Raised when the popup should dismiss itself.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised after the row list changed, so the popup can re-fit its height.</summary>
    public event EventHandler? RowsChanged;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            Rebuild();
            SelectFirst();
        }
    }

    public TrayRow? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    /// <summary>"3 running" for the popup header.</summary>
    public string RunningSummary
    {
        get => _runningSummary;
        private set => SetProperty(ref _runningSummary, value);
    }

    /// <summary>Reads the store off the UI thread and republishes the rows when it lands.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;

        try
        {
            var connections = await _services.Store.GetConnectionsAsync(ct).ConfigureAwait(false);
            var groups = await _services.Store.GetGroupsAsync(ct).ConfigureAwait(false);
            var multiConfigs = await _services.Store.GetMultiConfigsAsync(ct).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                _connections = connections;
                _groups = groups;
                _multiConfigs = multiConfigs;

                _groupNames.Clear();
                foreach (var group in groups) _groupNames[group.Id] = group.Name;

                Rebuild();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn("The tray menu could not load its data.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    /// <summary>
    /// Called immediately before the popup is shown. Nothing here awaits: the rows are rebuilt from
    /// the cache that is already in memory and the refreshes are kicked off detached.
    /// </summary>
    public void PrepareForShow()
    {
        if (_searchText.Length > 0)
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
        }

        Rebuild();
        SelectFirst();

        if (!_clock.IsEnabled) _clock.Start();

        // Hosts are checked while the list is open, and not otherwise.
        _reachDemand ??= _services.Reachability.Demand();

        Detached(() => _services.Sessions.RefreshSnapshotsAsync(), "Refreshing the tray snapshots failed.");
        Detached(() => LoadAsync(), "Refreshing the tray menu failed.");
    }

    /// <summary>Called after the popup is hidden, so the uptime clock stops costing anything.</summary>
    public void OnHidden()
    {
        _reachDemand?.Dispose();
        _reachDemand = null;

        try
        {
            _clock.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Stopping the tray uptime clock failed.", ex);
        }
    }

    /// <summary>Runs the row: focus, launch, or open the manager.</summary>
    public void Activate(TrayRow? row)
    {
        if (row is null) return;

        try
        {
            switch (row)
            {
                case TraySessionRow session:
                    RequestClose();
                    _services.Sessions.Focus(session.Session.Id);
                    break;

                case TrayMultiRow multi:
                    RequestClose();
                    LaunchMulti(multi.Config);
                    break;

                case TrayConnectionRow connection:
                    RequestClose();
                    LaunchOrFocus(connection.Connection);
                    break;

                case TrayMoreRow:
                    RequestClose();
                    Post(() => _shell.ShowMain());
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("The tray menu could not run that action.", ex);
        }
    }

    public void ActivateSelected() => Activate(SelectedRow);

    /// <summary>Disconnects the highlighted row. False when it has nothing running to end.</summary>
    public bool DisconnectSelected() => Disconnect(SelectedRow);

    /// <summary>Moves the highlight across every section, skipping headers and wrapping round.</summary>
    public void MoveSelection(int delta)
    {
        if (delta == 0 || Rows.Count == 0) return;

        var start = _selectedRow is null ? -1 : Rows.IndexOf(_selectedRow);
        var index = start >= 0 ? start : (delta > 0 ? -1 : Rows.Count);

        for (var step = 0; step < Rows.Count; step++)
        {
            index += delta;
            if (index < 0) index = Rows.Count - 1;
            else if (index >= Rows.Count) index = 0;

            if (Rows[index].IsSelectable)
            {
                SelectedRow = Rows[index];
                return;
            }
        }
    }

    public void SelectFirst()
    {
        foreach (var row in Rows)
        {
            if (!row.IsSelectable) continue;
            SelectedRow = row;
            return;
        }

        SelectedRow = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _services.Sessions.SessionStarted -= OnSessionChanged;
            _services.Sessions.SessionStateChanged -= OnSessionChanged;
            _services.Sessions.SessionEnded -= OnSessionEnded;
            _services.SettingsChanged -= OnSettingsChanged;
            _services.Reachability.Changed -= OnReachabilityChanged;
            _reachDemand?.Dispose();
            _reachDemand = null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Detaching the tray menu from the session manager failed.", ex);
        }

        try
        {
            _clock.Stop();
            _clock.Tick -= OnClockTick;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Stopping the tray uptime clock failed.", ex);
        }

        foreach (var row in _sessionRows.Values) row.Dispose();
        _sessionRows.Clear();
        _connectionRows.Clear();
        _multiRows.Clear();
        _headers.Clear();
        _ticking.Clear();
        _closing.Clear();
        Rows.Clear();
    }

    // ------------------------------------------------------------------ build

    /// <summary>New reachability results; the rows pick them up while the list is open.</summary>
    private void OnReachabilityChanged(object? sender, EventArgs e) =>
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed && _reachDemand is not null) Rebuild();
        }));

    private void Rebuild()
    {
        if (_disposed) return;

        try
        {
            RebuildCore();
        }
        catch (Exception ex)
        {
            AppLog.Error("Building the tray menu failed.", ex);
        }
    }

    private void RebuildCore()
    {
        var settings = _services.Settings ?? new AppSettings();
        var showSnapshots = settings.ShowSnapshotsInTray && settings.EnableSnapshots;
        var maxItems = settings.TrayMenuMaxItems <= 0
            ? int.MaxValue
            : Math.Max(MinimumItems, settings.TrayMenuMaxItems);
        var query = _searchText.Trim();

        var sessions = _services.Sessions.Sessions;
        PruneSessionRows(sessions);

        var rows = new List<TrayRow>(32);
        _ticking.Clear();

        // RUNNING ------------------------------------------------------------
        // "Active" has a live session; "staying" has one that is not being disconnected. A row
        // shows as disconnecting once it is active but nothing of it is staying.
        var running = 0;
        var active = new LiveSet();
        var staying = new LiveSet();
        List<RdpSession>? visibleSessions = null;

        foreach (var session in sessions)
        {
            if (!session.IsActive) continue;
            running++;
            active.Add(session);
            if (!_closing.Contains(session.Id)) staying.Add(session);
            if (MatchesSession(session, query)) (visibleSessions ??= new List<RdpSession>()).Add(session);
        }

        RunningSummary = running == 0
            ? Strings.Tray_RunningSummary_None
            : UiLanguage.Plural(running, Strings.Tray_RunningSummary_One, Strings.Tray_RunningSummary_Many);

        if (visibleSessions is { Count: > 0 })
        {
            rows.Add(Header(Strings.Tray_Header_Running));
            foreach (var session in visibleSessions)
            {
                var row = SessionRow(session);
                row.ShowSnapshot = showSnapshots;
                row.IsDisconnecting = _closing.Contains(session.Id);
                row.UpdateUptime();
                _ticking.Add(row);
                rows.Add(row);
            }
        }

        // Everything below is listed in the manager's order, which dragging there sets.
        var order = LibraryOrder.Positions(_groups, _connections, _multiConfigs);

        // Connections show their last capture, so the list looks the same whether or not they run.
        _rowSnapshots = showSnapshots;
        _lastKnownSnapshots = showSnapshots ? SnapshotService.LastKnownSnapshots() : null;

        // MULTI-CONFIGS -------------------------------------------------------
        List<MultiConfig>? visibleMultis = null;
        foreach (var config in _multiConfigs)
        {
            if (MatchesMulti(config, query)) (visibleMultis ??= new List<MultiConfig>()).Add(config);
        }

        if (visibleMultis is { Count: > 0 })
        {
            visibleMultis.Sort((a, b) => ByPosition(order, a.Id, b.Id));
            rows.Add(Header(Strings.Tray_Header_MultiConfigs));

            var shown = 0;
            foreach (var config in visibleMultis)
            {
                if (shown++ >= maxItems) break;
                var row = MultiRow(config);
                row.IsRunning = active.Multis.Contains(config.Id);
                row.IsDisconnecting = row.IsRunning && !staying.Multis.Contains(config.Id);
                rows.Add(row);
            }
        }

        // CONNECTIONS ---------------------------------------------------------
        List<RdpConnection>? visible = null;
        foreach (var connection in _connections)
        {
            if (MatchesConnection(connection, query)) (visible ??= new List<RdpConnection>()).Add(connection);
        }

        var truncated = 0;
        if (visible is { Count: > 0 })
        {
            visible.Sort((a, b) => ByPosition(order, a.Id, b.Id));

            if (visible.Count > maxItems)
            {
                truncated = visible.Count - maxItems;
                visible.RemoveRange(maxItems, truncated);
            }

            if (settings.ShowGroupsInTray) AppendGrouped(rows, visible, order, active, staying);
            else AppendSection(rows, Strings.Tray_Header_Connections, visible, active, staying);
        }

        if (truncated > 0)
        {
            _moreRow.Text = UiLanguage.Plural(truncated, Strings.Tray_More_Count_One, Strings.Tray_More_Count_Many);
            rows.Add(_moreRow);
        }

        if (rows.Count == 0)
        {
            _messageRow.Text = query.Length > 0
                ? UiLanguage.Format(Strings.Tray_Empty_NoMatch, query)
                : Strings.Tray_Empty_NoConnections;
            rows.Add(_messageRow);
        }

        SyncRows(rows);
        EnsureSelection();
    }

    /// <summary>
    /// One section per group, in the manager's order - groups depth first - and then the
    /// connections at the top level, which the manager also lists after its groups. A connection
    /// whose group no longer exists is at the top level there, so it is here too.
    /// </summary>
    private void AppendGrouped(
        List<TrayRow> rows, List<RdpConnection> connections, Dictionary<Guid, int> order, LiveSet active, LiveSet staying)
    {
        var known = new HashSet<Guid>();
        foreach (var group in _groups) known.Add(group.Id);

        List<RdpConnection>? loose = null;
        Dictionary<Guid, List<RdpConnection>>? byGroup = null;

        foreach (var connection in connections)
        {
            if (connection.GroupId is { } id && known.Contains(id))
            {
                byGroup ??= new Dictionary<Guid, List<RdpConnection>>();
                if (!byGroup.TryGetValue(id, out var bucket)) byGroup[id] = bucket = new List<RdpConnection>();
                bucket.Add(connection);
            }
            else
            {
                (loose ??= new List<RdpConnection>()).Add(connection);
            }
        }

        if (byGroup is not null)
        {
            var ordered = new List<ConnectionGroup>(byGroup.Count);
            foreach (var group in _groups)
            {
                if (byGroup.ContainsKey(group.Id)) ordered.Add(group);
            }

            ordered.Sort((a, b) => ByPosition(order, a.Id, b.Id));
            foreach (var group in ordered)
                AppendSection(rows, SectionTitle(group.Name), byGroup[group.Id], active, staying);
        }

        if (loose is { Count: > 0 }) AppendSection(rows, Strings.Tray_Header_Connections, loose, active, staying);
    }

    /// <summary>Compares two items by their place in the manager's tree.</summary>
    private static int ByPosition(Dictionary<Guid, int> order, Guid a, Guid b) =>
        (order.TryGetValue(a, out var left) ? left : int.MaxValue)
            .CompareTo(order.TryGetValue(b, out var right) ? right : int.MaxValue);

    private void AppendSection(
        List<TrayRow> rows, string header, List<RdpConnection> items, LiveSet active, LiveSet staying)
    {
        if (items.Count == 0) return;

        rows.Add(Header(header));
        foreach (var connection in items)
        {
            var row = ConnectionRow(connection);
            row.IsConnected = active.Connections.Contains(connection.Id);
            row.IsDisconnecting = row.IsConnected && !staying.Connections.Contains(connection.Id);
            row.SetReach(_services.Reachability.Get(connection.Id));

            if (_lastKnownSnapshots is not null && _lastKnownSnapshots.TryGetValue(connection.Id, out var taken))
                row.SetSnapshot(_rowSnapshots, SnapshotService.LastKnownPath(connection.Id), taken);
            else
                row.SetSnapshot(_rowSnapshots, null, default);

            rows.Add(row);
        }
    }

    /// <summary>The connections and multi-configs a set of sessions belongs to.</summary>
    private sealed class LiveSet
    {
        public readonly HashSet<Guid> Connections = new();
        public readonly HashSet<Guid> Multis = new();

        public void Add(RdpSession session)
        {
            Connections.Add(session.ConnectionId);
            if (session.MultiConfigId is { } multi) Multis.Add(multi);
        }
    }

    /// <summary>
    /// Updates the bound collection in place. Rows are cached objects, so a rebuild that changes
    /// nothing raises nothing and the list keeps its containers and its decoded thumbnails.
    /// </summary>
    private void SyncRows(List<TrayRow> target)
    {
        var changed = false;

        while (Rows.Count > target.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
            changed = true;
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count)
            {
                if (ReferenceEquals(Rows[i], target[i])) continue;
                Rows[i] = target[i];
            }
            else
            {
                Rows.Add(target[i]);
            }

            changed = true;
        }

        if (!changed) return;

        try
        {
            RowsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("A tray row listener threw.", ex);
        }
    }

    private void EnsureSelection()
    {
        if (_selectedRow is not null && _selectedRow.IsSelectable && Rows.Contains(_selectedRow)) return;
        SelectFirst();
    }

    // ------------------------------------------------------------------ rows

    private TrayHeaderRow Header(string title)
    {
        if (_headers.TryGetValue(title, out var row)) return row;
        if (_headers.Count >= HeaderCacheLimit) _headers.Clear();

        row = new TrayHeaderRow(title);
        _headers[title] = row;
        return row;
    }

    private TraySessionRow SessionRow(RdpSession session)
    {
        if (_sessionRows.TryGetValue(session.Id, out var row)) return row;

        row = new TraySessionRow(session);
        _sessionRows[session.Id] = row;
        return row;
    }

    private TrayMultiRow MultiRow(MultiConfig config)
    {
        if (_multiRows.TryGetValue(config.Id, out var row))
        {
            row.Update(config);
            return row;
        }

        row = new TrayMultiRow(config);
        _multiRows[config.Id] = row;
        return row;
    }

    private TrayConnectionRow ConnectionRow(RdpConnection connection)
    {
        if (_connectionRows.TryGetValue(connection.Id, out var row))
        {
            row.Update(connection);
            return row;
        }

        row = new TrayConnectionRow(connection);
        _connectionRows[connection.Id] = row;
        return row;
    }

    private void PruneSessionRows(IReadOnlyList<RdpSession> live)
    {
        if (_sessionRows.Count == 0) return;

        var alive = new HashSet<Guid>(live.Count);
        foreach (var session in live) alive.Add(session.Id);

        List<Guid>? dead = null;
        foreach (var pair in _sessionRows)
        {
            if (!alive.Contains(pair.Key)) (dead ??= new List<Guid>()).Add(pair.Key);
        }

        if (dead is null) return;
        foreach (var id in dead)
        {
            if (_sessionRows.Remove(id, out var row)) row.Dispose();
        }
    }

    // ------------------------------------------------------------- filtering

    private static string SectionTitle(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? Strings.Tray_Header_Group : trimmed.ToUpperInvariant();
    }

    private static bool Has(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static bool MatchesSession(RdpSession session, string query) =>
        query.Length == 0 || Has(session.DisplayName, query) || Has(session.Host, query);

    private static bool MatchesMulti(MultiConfig config, string query) =>
        query.Length == 0 || Has(config.Name, query) || Has(config.Description, query);

    private bool MatchesConnection(RdpConnection connection, string query)
    {
        if (query.Length == 0) return true;

        if (Has(connection.Name, query) || Has(connection.Host, query)
            || Has(connection.Description, query) || Has(connection.Tags, query))
        {
            return true;
        }

        return connection.GroupId is { } id
            && _groupNames.TryGetValue(id, out var group)
            && Has(group, query);
    }

    // -------------------------------------------------------------- commands

    private void LaunchOrFocus(RdpConnection connection)
    {
        var existing = _services.Sessions.FindByConnection(connection.Id);
        if (existing is not null)
        {
            _services.Sessions.Focus(existing.Id);
            return;
        }

        Detached(async () =>
        {
            var session = await _services.Sessions.LaunchAsync(connection).ConfigureAwait(false);
            if (session is null)
            {
                var problem = _services.Sessions.LastLaunchProblem;
                Post(() =>
                {
                    // Something missing on this PC gets its full explanation, not a passing balloon.
                    if (problem is not null) _shell.ShowNotice(Strings.Dependency_Title, problem);
                    else _shell.Notify(
                        Strings.Tray_LaunchFailed_Title,
                        UiLanguage.Format(Strings.Tray_LaunchFailed_Message, connection.Name),
                        true);
                });
            }
        }, $"Launching '{connection.Name}' failed.");
    }

    private void LaunchMulti(MultiConfig config)
    {
        if (_services.Sessions.IsMultiConfigRunning(config.Id))
        {
            foreach (var session in _services.Sessions.Sessions)
            {
                if (session.MultiConfigId != config.Id || !session.IsActive) continue;
                _services.Sessions.Focus(session.Id);
                return;
            }

            return;
        }

        Detached(async () =>
        {
            var started = await _services.Sessions.LaunchMultiAsync(config).ConfigureAwait(false);
            if (started.Count == 0)
            {
                Post(() => _shell.Notify(
                    Strings.Tray_MultiNothingStarted_Title,
                    UiLanguage.Format(Strings.Tray_MultiNothingStarted_Message, config.Name),
                    true));
            }
        }, $"Launching '{config.Name}' failed.");
    }

    /// <summary>
    /// Ends every live session behind the row. The popup stays open so several can be ended in a
    /// row, and the rows say "Disconnecting" until Remote Desktop has actually gone.
    /// </summary>
    private bool Disconnect(TrayRow? row)
    {
        if (row is null || !row.CanDisconnect || row.IsDisconnecting) return false;

        Func<RdpSession, bool> belongs;
        string name;
        switch (row)
        {
            case TraySessionRow s:
                var sessionId = s.Session.Id;
                belongs = session => session.Id == sessionId;
                name = s.Session.DisplayName;
                break;

            case TrayConnectionRow c:
                var connectionId = c.Connection.Id;
                belongs = session => session.ConnectionId == connectionId;
                name = c.Connection.Name;
                break;

            case TrayMultiRow m:
                var multiId = m.Config.Id;
                belongs = session => session.MultiConfigId == multiId;
                name = m.Config.Name;
                break;

            default:
                return false;
        }

        var targets = new List<Guid>();
        foreach (var session in _services.Sessions.Sessions)
        {
            if (session.IsActive && !_closing.Contains(session.Id) && belongs(session)) targets.Add(session.Id);
        }

        if (targets.Count == 0) return false;

        foreach (var id in targets) _closing.Add(id);
        Rebuild();

        Detached(async () =>
        {
            try
            {
                var closes = new Task[targets.Count];
                for (var i = 0; i < targets.Count; i++) closes[i] = _services.Sessions.CloseAsync(targets[i]);
                await Task.WhenAll(closes).ConfigureAwait(false);
            }
            finally
            {
                // A close that failed leaves its session running, and the row has to say so again.
                OnUi(() =>
                {
                    foreach (var id in targets) _closing.Remove(id);
                    Rebuild();
                });
            }
        }, $"Disconnecting '{name}' failed.");

        return true;
    }

    private void OpenManager()
    {
        RequestClose();
        Post(() => _shell.ShowMain());
    }

    private void OpenSettings()
    {
        RequestClose();
        Post(() => _shell.ShowSettings());
    }

    private void Exit()
    {
        RequestClose();
        Post(() => _shell.ExitApplication());
    }

    private void RequestClose()
    {
        try
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Dismissing the tray menu failed.", ex);
        }
    }

    // --------------------------------------------------------------- plumbing

    private void OnClockTick(object? sender, EventArgs e)
    {
        try
        {
            for (var i = 0; i < _ticking.Count; i++) _ticking[i].UpdateUptime();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Refreshing the tray uptimes failed.", ex);
        }
    }

    private void OnSessionChanged(object? sender, RdpSession e) => OnUi(Rebuild);

    private void OnSettingsChanged(object? sender, AppSettings e) => OnUi(Rebuild);

    private void OnSessionEnded(object? sender, RdpSession e) => OnUi(() =>
    {
        if (_sessionRows.Remove(e.Id, out var row)) row.Dispose();
        Rebuild();
    });

    private void OnUi(Action action)
    {
        if (_disposed) return;
        TrayUi.Post(action);
    }

    /// <summary>Queues work behind the popup's dismissal so the window is gone before it runs.</summary>
    private void Post(Action action)
    {
        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    AppLog.Error("A tray menu action failed.", ex);
                }
            }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("A tray menu action could not be queued.", ex);
        }
    }

    /// <summary>Fire and forget, off the UI thread, with the failure logged rather than thrown.</summary>
    private static void Detached(Func<Task> work, string failure)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error(failure, ex);
            }
        });
    }
}
