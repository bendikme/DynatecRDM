using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;
using DynatecRDM.Views;
using Microsoft.Win32;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Drives the main shell: the connection library on the left, the detail panel on the right
/// and the live-session strip along the bottom.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Which subset of the library the tree shows.</summary>
    public enum LibraryFilter
    {
        All = 0,
        Favorites = 1,
        Connected = 2,
    }

    /// <summary>One resolved entry of the selected multi-config, as the detail panel shows it.</summary>
    public sealed class MultiItemRow
    {
        public string Order { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string Target { get; init; } = string.Empty;
        public string StateText { get; init; } = string.Empty;
        public bool Enabled { get; init; }
    }

    private const int MaxDepth = 64;

    /// <summary>Flicking back and forth to the window must not re-photograph every session each time.</summary>
    private const int SnapshotRefreshMinimumMs = 5_000;

    private readonly AppServices _services;
    private readonly IAppShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _clockTimer;

    private readonly Dictionary<Guid, TreeNodeViewModel> _index = new();
    private readonly List<TreeNodeViewModel> _flat = new();
    private readonly Dictionary<Guid, ConnectionGroup> _groupsById = new();
    private readonly Dictionary<Guid, CredentialSet> _credentialsById = new();
    private readonly Dictionary<Guid, RdpConnection> _connectionsById = new();

    private readonly List<RelayCommand> _syncCommands = new();
    private readonly List<AsyncRelayCommand> _asyncCommands = new();

    private System.Windows.Window? _window;
    private Guid? _pendingSelection;
    private bool _loadedOnce;
    private bool _detached;
    private bool _showSnapshots;
    private long _lastSnapshotRefreshTicks = -SnapshotRefreshMinimumMs;

    private int _connectionCount;
    private int _groupCount;
    private int _multiConfigCount;

    private ObservableCollection<TreeNodeViewModel> _nodes = new();
    private TreeNodeViewModel? _selectedNode;
    private string _searchText = string.Empty;
    private string _activeTerm = string.Empty;
    private LibraryFilter _filter = LibraryFilter.All;
    private bool _isLoading;
    private DateTime _clock = DateTime.UtcNow;
    private string _treeSummary = string.Empty;
    private bool _showNoResults;

    private string _detailName = string.Empty;
    private string _detailKind = string.Empty;
    private string _detailAddress = string.Empty;
    private string _detailBreadcrumb = string.Empty;
    private string _detailCredential = string.Empty;
    private string _detailDisplay = string.Empty;
    private string _detailTags = string.Empty;
    private string _detailDescription = string.Empty;
    private string _detailLaunchCount = string.Empty;
    private string _detailGroupSummary = string.Empty;
    private string _multiSummary = string.Empty;
    private string? _detailColor;
    private DateTime? _detailLastConnectedUtc;
    private bool _detailFavorite;
    private bool _detailRunning;

    public MainViewModel(AppServices services, IAppShell shell)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _searchTimer.Tick += OnSearchTick;

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += OnClockTick;

        NewConnectionCommand = Async(_ => NewConnectionAsync());
        NewMultiConfigCommand = Async(_ => NewMultiConfigAsync());
        NewGroupCommand = Async(_ => NewGroupAsync());
        EditSelectedCommand = Async(_ => EditSelectedAsync(), _ => _selectedNode is not null);
        RenameSelectedCommand = Async(_ => RenameSelectedAsync(), _ => _selectedNode is not null);
        DuplicateSelectedCommand = Async(_ => DuplicateSelectedAsync(), _ => _selectedNode is not null);
        DeleteSelectedCommand = Async(_ => DeleteSelectedAsync(), _ => _selectedNode is not null);
        ExportSelectedCommand = Async(_ => ExportSelectedAsync(), _ => _selectedNode is { IsConnection: true });
        ConnectSelectedCommand = Async(_ => ConnectSelectedAsync(), _ => _selectedNode is { IsConnection: true });
        ToggleFavoriteCommand = Async(_ => ToggleFavoriteAsync(), _ => _selectedNode is { IsGroup: false });
        LaunchMultiCommand = Async(_ => LaunchMultiAsync(), _ => _selectedNode is { IsMultiConfig: true });
        CloseMultiCommand = Async(_ => CloseMultiAsync(), _ => _selectedNode is { IsMultiConfig: true, IsRunning: true });
        ActivateNodeCommand = Async(ActivateAsync);
        ImportRdpFileCommand = Async(_ => ImportRdpFileAsync());
        RefreshCommand = Async(_ => LoadAsync());

        FocusSessionCommand = Sync(FocusSession);
        ReconnectSessionCommand = Async(ReconnectSessionAsync);
        CloseSessionCommand = Async(CloseSessionAsync);

        OpenCredentialsCommand = Async(_ => OpenCredentialsAsync());
        OpenSettingsCommand = Sync(_ => SafeShell(() => _shell.ShowSettings()));
        OpenTransferCommand = Sync(_ => SafeShell(() => _shell.ShowTransfer()));
        FocusSearchCommand = Sync(_ => RaiseFocusSearch());
        ClearSearchCommand = Sync(_ => SearchText = string.Empty);
        ClearHistoryCommand = Async(_ => ClearHistoryAsync(), _ => CanClearHistory);

        _services.Sessions.SessionStarted += OnSessionStarted;
        _services.Sessions.SessionStateChanged += OnSessionStateChanged;
        _services.Sessions.SessionEnded += OnSessionEnded;

        _showSnapshots = _services.Settings?.EnableSnapshots ?? true;
        _services.SettingsChanged += OnSettingsChanged;

        UpdateTreeSummary();
        RefreshDetail();
    }

    // ------------------------------------------------------------------- state

    /// <summary>Root rows of the library tree.</summary>
    public ObservableCollection<TreeNodeViewModel> Nodes
    {
        get => _nodes;
        private set => SetProperty(ref _nodes, value);
    }

    /// <summary>Live sessions. Mutated only on the dispatcher.</summary>
    public ObservableCollection<RdpSession> Sessions { get; } = new();

    /// <summary>Session cards carry a thumbnail of the remote screen while capture is switched on.</summary>
    public bool ShowSnapshots
    {
        get => _showSnapshots;
        private set => SetProperty(ref _showSnapshots, value);
    }

    /// <summary>Entries of the selected multi-config.</summary>
    public ObservableCollection<MultiItemRow> MultiItems { get; } = new();

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            RefreshDetail();
            RaiseCommandStates();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    public LibraryFilter Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            ApplyFilter();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) RaiseCommandStates();
        }
    }

    /// <summary>Ticks once a second while sessions run; drives the live uptime labels.</summary>
    public DateTime Clock
    {
        get => _clock;
        private set => SetProperty(ref _clock, value);
    }

    /// <summary>Footer line under the tree, for example "18 connections, 3 groups, 2 running".</summary>
    public string TreeSummary
    {
        get => _treeSummary;
        private set => SetProperty(ref _treeSummary, value);
    }

    public bool ShowNoResults
    {
        get => _showNoResults;
        private set => SetProperty(ref _showNoResults, value);
    }

    public string DetailName { get => _detailName; private set => SetProperty(ref _detailName, value); }
    public string DetailKind { get => _detailKind; private set => SetProperty(ref _detailKind, value); }
    public string DetailAddress { get => _detailAddress; private set => SetProperty(ref _detailAddress, value); }
    public string DetailBreadcrumb { get => _detailBreadcrumb; private set => SetProperty(ref _detailBreadcrumb, value); }
    public string DetailCredential { get => _detailCredential; private set => SetProperty(ref _detailCredential, value); }
    public string DetailDisplay { get => _detailDisplay; private set => SetProperty(ref _detailDisplay, value); }
    public string DetailTags { get => _detailTags; private set => SetProperty(ref _detailTags, value); }
    public string DetailDescription { get => _detailDescription; private set => SetProperty(ref _detailDescription, value); }
    public string DetailLaunchCount { get => _detailLaunchCount; private set => SetProperty(ref _detailLaunchCount, value); }
    public string DetailGroupSummary { get => _detailGroupSummary; private set => SetProperty(ref _detailGroupSummary, value); }
    public string MultiSummary { get => _multiSummary; private set => SetProperty(ref _multiSummary, value); }
    public string? DetailColor { get => _detailColor; private set => SetProperty(ref _detailColor, value); }
    public DateTime? DetailLastConnectedUtc { get => _detailLastConnectedUtc; private set => SetProperty(ref _detailLastConnectedUtc, value); }
    public bool DetailFavorite { get => _detailFavorite; private set => SetProperty(ref _detailFavorite, value); }
    public bool DetailRunning { get => _detailRunning; private set => SetProperty(ref _detailRunning, value); }

    public bool ShowConnectionDetail => _selectedNode is { IsConnection: true };
    public bool ShowMultiDetail => _selectedNode is { IsMultiConfig: true };
    public bool ShowGroupDetail => _selectedNode is { IsGroup: true };
    public bool ShowEmptyState => _selectedNode is null;

    /// <summary>Raised when Ctrl+F asks the view to put the caret in the search box.</summary>
    public event EventHandler? FocusSearchRequested;

    // ---------------------------------------------------------------- commands

    public ICommand NewConnectionCommand { get; }
    public ICommand NewMultiConfigCommand { get; }
    public ICommand NewGroupCommand { get; }
    public ICommand EditSelectedCommand { get; }
    public ICommand RenameSelectedCommand { get; }
    public ICommand DuplicateSelectedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ExportSelectedCommand { get; }
    public ICommand ConnectSelectedCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand LaunchMultiCommand { get; }
    public ICommand CloseMultiCommand { get; }
    public ICommand ActivateNodeCommand { get; }
    public ICommand ImportRdpFileCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand FocusSessionCommand { get; }
    public ICommand ReconnectSessionCommand { get; }
    public ICommand CloseSessionCommand { get; }
    public ICommand OpenCredentialsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenTransferCommand { get; }
    public ICommand FocusSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }

    private AsyncRelayCommand Async(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(execute, canExecute);
        _asyncCommands.Add(command);
        return command;
    }

    private RelayCommand Sync(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        var command = new RelayCommand(execute, canExecute);
        _syncCommands.Add(command);
        return command;
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(ShowConnectionDetail));
        OnPropertyChanged(nameof(ShowMultiDetail));
        OnPropertyChanged(nameof(ShowGroupDetail));
        OnPropertyChanged(nameof(ShowEmptyState));

        foreach (var command in _asyncCommands) command.RaiseCanExecuteChanged();
        foreach (var command in _syncCommands) command.RaiseCanExecuteChanged();
    }

    private void RaiseFocusSearch()
    {
        try
        {
            FocusSearchRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not move focus to the search box.", ex);
        }
    }

    // ---------------------------------------------------------- window plumbing

    /// <summary>
    /// Called once by the view so dialogs get an owner and the saved rectangle can be
    /// restored before the window is shown.
    /// </summary>
    public void Attach(System.Windows.Window window)
    {
        _window = window;
        ApplyStartupPlacement(window);
        AttachReachability(window);
    }

    /// <summary>
    /// Releases everything that outlives the window: the session-manager subscriptions and the
    /// two dispatcher timers. The session manager and the dispatcher both live for the whole
    /// process, so without this they would keep the view model - and through it the entire
    /// visual tree - alive for good. Called once, from the window's Closed handler.
    /// </summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        try
        {
            _services.Sessions.SessionStarted -= OnSessionStarted;
            _services.Sessions.SessionStateChanged -= OnSessionStateChanged;
            _services.Sessions.SessionEnded -= OnSessionEnded;
            _services.SettingsChanged -= OnSettingsChanged;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not detach the session handlers.", ex);
        }

        try
        {
            _searchTimer.Stop();
            _searchTimer.Tick -= OnSearchTick;
            _clockTimer.Stop();
            _clockTimer.Tick -= OnClockTick;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not stop the main window timers.", ex);
        }

        foreach (var node in _flat)
        {
            node.SelectionChanged = null;
            node.ActivateCommand = null;
        }

        DetachHistory();
        DetachSnapshots();
        DetachReachability();
        _window = null;
    }

    /// <summary>Writes the window rectangle back to the settings as the window closes.</summary>
    public void SaveWindowPlacement()
    {
        if (_window is null) return;

        try
        {
            var settings = _services.Settings;
            settings.MainWindowMaximized = _window.WindowState == WindowState.Maximized;

            var bounds = _window.RestoreBounds;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                settings.MainWindowLeft = bounds.Left;
                settings.MainWindowTop = bounds.Top;
                settings.MainWindowWidth = bounds.Width;
                settings.MainWindowHeight = bounds.Height;
            }

            var snapshot = settings.Clone();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _services.Store.SaveSettingsAsync(snapshot).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not persist the window placement.", ex);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not capture the window placement.", ex);
        }
    }

    private void ApplyStartupPlacement(System.Windows.Window window)
    {
        try
        {
            var settings = _services.Settings;

            var width = Sane(settings.MainWindowWidth, 1280, 900);
            var height = Sane(settings.MainWindowHeight, 800, 560);
            window.Width = width;
            window.Height = height;

            var left = settings.MainWindowLeft;
            var top = settings.MainWindowTop;

            if (IsFinite(left) && IsFinite(top) && IsOnAVisibleMonitor(left, top, width, height))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = left;
                window.Top = top;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            if (settings.MainWindowMaximized) window.WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not restore the window placement.", ex);
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Sane(double value, double fallback, double minimum) =>
        !IsFinite(value) || value < minimum ? fallback : value;

    /// <summary>True when a worthwhile slice of the saved rectangle still lands on a display.</summary>
    private bool IsOnAVisibleMonitor(double left, double top, double width, double height)
    {
        try
        {
            var monitors = _services.Monitors.GetMonitors();
            if (monitors.Count == 0) return false;

            var scale = 1.0;
            foreach (var monitor in monitors)
            {
                if (!monitor.IsPrimary) continue;
                if (monitor.ScaleFactor > 0.1) scale = monitor.ScaleFactor;
                break;
            }

            var right = left + width;
            var bottom = top + height;

            foreach (var monitor in monitors)
            {
                var monitorLeft = monitor.WorkLeft / scale;
                var monitorTop = monitor.WorkTop / scale;
                var monitorRight = monitorLeft + (monitor.WorkWidth / scale);
                var monitorBottom = monitorTop + (monitor.WorkHeight / scale);

                var overlapWidth = Math.Min(right, monitorRight) - Math.Max(left, monitorLeft);
                var overlapHeight = Math.Min(bottom, monitorBottom) - Math.Max(top, monitorTop);

                if (overlapWidth >= 160 && overlapHeight >= 80
                    && top >= monitorTop - 4 && top <= monitorBottom - 32)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not validate the saved window rectangle.", ex);
            return false;
        }
    }

    // -------------------------------------------------------------------- load

    /// <summary>Reads the library and rebuilds the tree. Everything heavy runs off the UI thread.</summary>
    public async Task LoadAsync()
    {
        if (_detached || IsLoading) return;
        IsLoading = true;

        var expanded = CaptureExpansion();
        var selectedId = _selectedNode?.Id ?? _pendingSelection;
        var preferCaptured = _loadedOnce;

        try
        {
            var snapshot = await Task.Run(async () =>
            {
                var groups = await _services.Store.GetGroupsAsync().ConfigureAwait(false);
                var connections = await _services.Store.GetConnectionsAsync().ConfigureAwait(false);
                var multiConfigs = await _services.Store.GetMultiConfigsAsync().ConfigureAwait(false);
                var credentials = await _services.Store.GetCredentialSetsAsync().ConfigureAwait(false);
                return Build(groups, connections, multiConfigs, credentials, expanded, preferCaptured);
            }).ConfigureAwait(true);

            Adopt(snapshot);
            ApplyLastKnownSnapshots();
            ApplyReachability();
            _loadedOnce = true;

            if (selectedId is { } id) SelectById(id);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not load the connection library.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_LoadLibrary, ex.Message), true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private HashSet<Guid>? CaptureExpansion()
    {
        if (_flat.Count == 0) return null;

        var expanded = new HashSet<Guid>();
        foreach (var node in _flat)
        {
            if (node.IsGroup && node.IsExpanded) expanded.Add(node.Id);
        }
        return expanded;
    }

    /// <summary>The result of one background rebuild, handed to the UI thread in a single piece.</summary>
    private sealed class LibrarySnapshot
    {
        public ObservableCollection<TreeNodeViewModel> Roots { get; init; } = new();
        public List<TreeNodeViewModel> Flat { get; init; } = new();
        public Dictionary<Guid, TreeNodeViewModel> Index { get; init; } = new();
        public Dictionary<Guid, ConnectionGroup> Groups { get; init; } = new();
        public Dictionary<Guid, CredentialSet> Credentials { get; init; } = new();
        public Dictionary<Guid, RdpConnection> Connections { get; init; } = new();
        public int ConnectionCount { get; init; }
        public int GroupCount { get; init; }
        public int MultiConfigCount { get; init; }
    }

    private static LibrarySnapshot Build(
        IReadOnlyList<ConnectionGroup> groups,
        IReadOnlyList<RdpConnection> connections,
        IReadOnlyList<MultiConfig> multiConfigs,
        IReadOnlyList<CredentialSet> credentials,
        HashSet<Guid>? expanded,
        bool preferCapturedExpansion)
    {
        var groupsById = new Dictionary<Guid, ConnectionGroup>(groups.Count);
        var groupNodes = new Dictionary<Guid, TreeNodeViewModel>(groups.Count);
        var index = new Dictionary<Guid, TreeNodeViewModel>(groups.Count + connections.Count + multiConfigs.Count);
        var flat = new List<TreeNodeViewModel>(groups.Count + connections.Count + multiConfigs.Count);

        foreach (var group in groups)
        {
            groupsById[group.Id] = group;

            var node = new TreeNodeViewModel(TreeNodeKind.Group, group.Id, group)
            {
                Name = string.IsNullOrWhiteSpace(group.Name) ? Strings.Main_UntitledGroup : group.Name,
                Color = group.Color,
                SortOrder = group.SortOrder,
                IsExpanded = preferCapturedExpansion && expanded is not null
                    ? expanded.Contains(group.Id)
                    : group.IsExpanded,
                SearchText = Haystack(group.Name, group.Description, null, null),
            };

            groupNodes[group.Id] = node;
            index[group.Id] = node;
            flat.Add(node);
        }

        var roots = new List<TreeNodeViewModel>();

        foreach (var group in groups)
        {
            var node = groupNodes[group.Id];
            if (group.ParentId is { } parentId
                && groupNodes.TryGetValue(parentId, out var parent)
                && !ReferenceEquals(parent, node))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        BreakCycles(roots, groupNodes);

        var connectionsById = new Dictionary<Guid, RdpConnection>(connections.Count);
        var leaves = new List<TreeNodeViewModel>(connections.Count + multiConfigs.Count);

        foreach (var connection in connections)
        {
            connectionsById[connection.Id] = connection;

            var node = new TreeNodeViewModel(TreeNodeKind.Connection, connection.Id, connection)
            {
                Name = string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name,
                Subtitle = connection.FullAddress,
                Color = connection.Color,
                Favorite = connection.Favorite,
                SortOrder = connection.SortOrder,
                SearchText = Haystack(connection.Name, connection.Description, connection.Host, connection.Tags),
            };

            index[connection.Id] = node;
            flat.Add(node);
            leaves.Add(node);
        }

        foreach (var config in multiConfigs)
        {
            var enabled = 0;
            foreach (var item in config.Items)
            {
                if (item.Enabled) enabled++;
            }

            var node = new TreeNodeViewModel(TreeNodeKind.MultiConfig, config.Id, config)
            {
                Name = string.IsNullOrWhiteSpace(config.Name) ? Strings.Main_UntitledMultiConfig : config.Name,
                Subtitle = UiLanguage.Plural(enabled, Strings.Main_Count_Connections_One, Strings.Main_Count_Connections_Many),
                Color = config.Color,
                Favorite = config.Favorite,
                SortOrder = config.SortOrder,
                SearchText = Haystack(config.Name, config.Description, null, null),
            };

            index[config.Id] = node;
            flat.Add(node);
            leaves.Add(node);
        }

        foreach (var node in leaves)
        {
            var groupId = node.AsConnection?.GroupId ?? node.AsMultiConfig?.GroupId;
            if (groupId is { } id && groupNodes.TryGetValue(id, out var parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        SortNodes(roots);
        foreach (var node in flat)
        {
            if (node.Children.Count > 1) SortChildren(node);
        }

        var credentialsById = new Dictionary<Guid, CredentialSet>(credentials.Count);
        foreach (var credential in credentials) credentialsById[credential.Id] = credential;

        return new LibrarySnapshot
        {
            Roots = new ObservableCollection<TreeNodeViewModel>(roots),
            Flat = flat,
            Index = index,
            Groups = groupsById,
            Credentials = credentialsById,
            Connections = connectionsById,
            ConnectionCount = connections.Count,
            GroupCount = groups.Count,
            MultiConfigCount = multiConfigs.Count,
        };
    }

    /// <summary>Re-roots any group whose parent chain loops back on itself.</summary>
    private static void BreakCycles(List<TreeNodeViewModel> roots, Dictionary<Guid, TreeNodeViewModel> groupNodes)
    {
        foreach (var pair in groupNodes)
        {
            var node = pair.Value;
            var walker = node.Parent;
            var depth = 0;
            var looped = false;

            while (walker is not null && depth < MaxDepth)
            {
                if (ReferenceEquals(walker, node))
                {
                    looped = true;
                    break;
                }

                walker = walker.Parent;
                depth++;
            }

            if (!looped && depth < MaxDepth) continue;

            node.Parent?.Children.Remove(node);
            node.Parent = null;
            roots.Add(node);
        }
    }

    private static void SortChildren(TreeNodeViewModel node)
    {
        var ordered = new List<TreeNodeViewModel>(node.Children);
        SortNodes(ordered);

        node.Children.Clear();
        foreach (var child in ordered) node.Children.Add(child);
    }

    private static void SortNodes(List<TreeNodeViewModel> nodes) =>
        nodes.Sort(static (a, b) => LibraryOrder.Compare(a.IsGroup, a.SortOrder, a.Name, b.IsGroup, b.SortOrder, b.Name));

    private static string Haystack(string? name, string? description, string? host, string? tags)
    {
        var builder = new StringBuilder(64);
        Append(builder, name);
        Append(builder, description);
        Append(builder, host);
        Append(builder, tags);
        return builder.ToString();

        static void Append(StringBuilder builder, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(value.ToLowerInvariant());
        }
    }

    private void Adopt(LibrarySnapshot snapshot)
    {
        foreach (var node in _flat)
        {
            node.SelectionChanged = null;
            node.ActivateCommand = null;
        }

        _flat.Clear();
        _index.Clear();
        _groupsById.Clear();
        _credentialsById.Clear();
        _connectionsById.Clear();

        foreach (var node in snapshot.Flat)
        {
            node.SelectionChanged = OnNodeSelectionChanged;
            node.ActivateCommand = ActivateNodeCommand;
            _flat.Add(node);
        }

        foreach (var pair in snapshot.Index) _index[pair.Key] = pair.Value;
        foreach (var pair in snapshot.Groups) _groupsById[pair.Key] = pair.Value;
        foreach (var pair in snapshot.Credentials) _credentialsById[pair.Key] = pair.Value;
        foreach (var pair in snapshot.Connections) _connectionsById[pair.Key] = pair.Value;

        _connectionCount = snapshot.ConnectionCount;
        _groupCount = snapshot.GroupCount;
        _multiConfigCount = snapshot.MultiConfigCount;

        _selectedNode = null;
        OnPropertyChanged(nameof(SelectedNode));

        Nodes = snapshot.Roots;

        SyncSessions();
        RefreshRunningFlags();
        ApplyFilter();
        UpdateTreeSummary();
        RefreshDetail();
        RaiseCommandStates();
    }

    // --------------------------------------------------------------- selection

    /// <summary>Selects a connection, multi-config or group by its identifier.</summary>
    public void SelectById(Guid id)
    {
        if (!_index.TryGetValue(id, out var node))
        {
            _pendingSelection = id;
            return;
        }

        _pendingSelection = null;

        var previous = _selectedNode;
        if (previous is not null && !ReferenceEquals(previous, node)) previous.IsSelected = false;

        node.ExpandAncestors();
        node.IsVisible = true;
        node.IsSelected = true;
        SelectedNode = node;
    }

    /// <summary>Selects nothing, which brings back the overview in the detail pane.</summary>
    public void ClearSelection()
    {
        _pendingSelection = null;
        if (_selectedNode is { } node) node.IsSelected = false;
        SelectedNode = null;
    }

    private void OnNodeSelectionChanged(TreeNodeViewModel node, bool selected)
    {
        if (selected) SelectedNode = node;
        else if (ReferenceEquals(_selectedNode, node)) SelectedNode = null;
    }

    // --------------------------------------------------------------- filtering

    private void OnSearchTick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        try
        {
            _activeTerm = _searchText.Trim().ToLowerInvariant();

            var any = false;
            foreach (var node in Nodes) any |= FilterNode(node);

            // An empty library already has its own message, so this one must not double up.
            ShowNoResults = !any
                && Nodes.Count > 0
                && (_activeTerm.Length > 0 || _filter != LibraryFilter.All);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Filtering the library failed.", ex);
        }
    }

    private bool FilterNode(TreeNodeViewModel node)
    {
        var childMatched = false;
        foreach (var child in node.Children) childMatched |= FilterNode(child);

        bool visible;
        if (node.IsGroup)
        {
            visible = childMatched || (_activeTerm.Length == 0 && _filter == LibraryFilter.All);
            if (childMatched && _activeTerm.Length > 0) node.IsExpanded = true;
        }
        else
        {
            visible = MatchesFilter(node) && MatchesTerm(node);
        }

        node.IsVisible = visible;
        return visible;
    }

    private bool MatchesTerm(TreeNodeViewModel node) =>
        _activeTerm.Length == 0 || node.SearchText.Contains(_activeTerm, StringComparison.Ordinal);

    private bool MatchesFilter(TreeNodeViewModel node) => _filter switch
    {
        LibraryFilter.Favorites => node.Favorite,
        LibraryFilter.Connected => node.IsRunning,
        _ => true,
    };

    private void UpdateTreeSummary()
    {
        var builder = new StringBuilder(72);
        builder.Append(UiLanguage.Plural(_connectionCount, Strings.Main_Count_Connections_One, Strings.Main_Count_Connections_Many));
        builder.Append(", ").Append(UiLanguage.Plural(_groupCount, Strings.Main_Count_Groups_One, Strings.Main_Count_Groups_Many));

        if (_multiConfigCount > 0)
            builder.Append(", ").Append(UiLanguage.Plural(_multiConfigCount, Strings.Main_Count_MultiConfigs_One, Strings.Main_Count_MultiConfigs_Many));

        builder.Append(", ").Append(UiLanguage.Plural(Sessions.Count, Strings.Main_Count_Running_One, Strings.Main_Count_Running_Many));

        TreeSummary = builder.ToString();
    }

    // ------------------------------------------------------------- detail pane

    private void RefreshDetail()
    {
        OnPropertyChanged(nameof(ShowConnectionDetail));
        OnPropertyChanged(nameof(ShowMultiDetail));
        OnPropertyChanged(nameof(ShowGroupDetail));
        OnPropertyChanged(nameof(ShowEmptyState));

        var node = _selectedNode;
        RefreshHistory(node);
        RefreshDetailSnapshot(node);
        RefreshDetailReach(node, checkIfStale: true);
        if (node is null)
        {
            ClearDetail();
            return;
        }

        DetailName = node.Name;
        DetailColor = node.Color;
        DetailFavorite = node.Favorite;
        DetailRunning = node.IsRunning;

        if (node.Kind == TreeNodeKind.Connection && node.AsConnection is { } connection)
        {
            DetailKind = Strings.Main_Kind_Connection;
            DetailAddress = connection.FullAddress;
            DetailBreadcrumb = DescribeGroupPath(connection.GroupId);
            DetailCredential = DescribeCredential(connection);
            DetailDisplay = DescribeDisplay(connection.Display);
            DetailTags = string.IsNullOrWhiteSpace(connection.Tags) ? string.Empty : connection.Tags.Trim();
            DetailDescription = connection.Description ?? string.Empty;
            DetailLastConnectedUtc = connection.LastConnectedUtc;
            DetailLaunchCount = UiLanguage.Plural(
                connection.LaunchCount, Strings.Main_Detail_Launched_One, Strings.Main_Detail_Launched_Many);
            DetailGroupSummary = string.Empty;
            MultiSummary = string.Empty;
            MultiItems.Clear();
            return;
        }

        if (node.Kind == TreeNodeKind.MultiConfig && node.AsMultiConfig is { } config)
        {
            DetailKind = Strings.Main_Kind_MultiConfig;
            DetailAddress = string.Empty;
            DetailBreadcrumb = DescribeGroupPath(config.GroupId);
            DetailCredential = string.Empty;
            DetailDisplay = string.Empty;
            DetailTags = string.Empty;
            DetailDescription = config.Description ?? string.Empty;
            DetailLastConnectedUtc = config.LastLaunchedUtc;
            DetailLaunchCount = UiLanguage.Plural(
                config.LaunchCount, Strings.Main_Detail_Launched_One, Strings.Main_Detail_Launched_Many);
            DetailGroupSummary = string.Empty;
            MultiSummary = DescribeMultiConfig(config);
            FillMultiItems(config);
            return;
        }

        if (node.Kind == TreeNodeKind.Group && node.AsGroup is { } group)
        {
            DetailKind = Strings.Main_Kind_Group;
            DetailAddress = string.Empty;
            DetailBreadcrumb = DescribeGroupPath(group.ParentId);
            DetailCredential = string.Empty;
            DetailDisplay = string.Empty;
            DetailTags = string.Empty;
            DetailDescription = group.Description ?? string.Empty;
            DetailLastConnectedUtc = null;
            DetailLaunchCount = string.Empty;
            DetailGroupSummary = DescribeGroupContents(node);
            MultiSummary = string.Empty;
            MultiItems.Clear();
            return;
        }

        ClearDetail();
    }

    private void ClearDetail()
    {
        DetailName = string.Empty;
        DetailKind = string.Empty;
        DetailAddress = string.Empty;
        DetailBreadcrumb = string.Empty;
        DetailCredential = string.Empty;
        DetailDisplay = string.Empty;
        DetailTags = string.Empty;
        DetailDescription = string.Empty;
        DetailLaunchCount = string.Empty;
        DetailGroupSummary = string.Empty;
        MultiSummary = string.Empty;
        DetailColor = null;
        DetailLastConnectedUtc = null;
        DetailFavorite = false;
        DetailRunning = false;
        MultiItems.Clear();
    }

    private void FillMultiItems(MultiConfig config)
    {
        MultiItems.Clear();

        var ordered = new List<MultiConfigItem>(config.Items);
        ordered.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        var position = 1;
        foreach (var item in ordered)
        {
            _connectionsById.TryGetValue(item.ConnectionId, out var connection);

            var name = !string.IsNullOrWhiteSpace(item.DisplayNameOverride)
                ? item.DisplayNameOverride!.Trim()
                : connection?.Name ?? Strings.Main_MultiItem_MissingConnection;

            var display = item.Display.ApplyTo(connection?.Display ?? new DisplaySettings());

            var state = string.Empty;
            if (connection is not null && _services.Sessions.FindByConnection(connection.Id) is { } session)
                state = session.StateText;

            MultiItems.Add(new MultiItemRow
            {
                Order = position.ToString(CultureInfo.InvariantCulture),
                Name = name,
                Address = connection?.FullAddress ?? Strings.Main_MultiItem_NotFound,
                Target = DescribeDisplay(display),
                StateText = state,
                Enabled = item.Enabled,
            });

            position++;
        }
    }

    private string DescribeGroupPath(Guid? groupId)
    {
        if (groupId is null) return Strings.Main_Breadcrumb_Library;

        var parts = new List<string>(8);
        var walker = groupId;
        var depth = 0;

        while (walker is { } id && _groupsById.TryGetValue(id, out var group) && depth++ < MaxDepth)
        {
            parts.Add(string.IsNullOrWhiteSpace(group.Name) ? Strings.Main_UntitledGroup : group.Name);
            walker = group.ParentId;
        }

        parts.Reverse();
        parts.Insert(0, Strings.Main_Breadcrumb_Library);
        return string.Join("  /  ", parts);
    }

    private string DescribeCredential(RdpConnection connection)
    {
        if (connection.CredentialSetId is { } id && _credentialsById.TryGetValue(id, out var credential))
        {
            var label = string.IsNullOrWhiteSpace(credential.Name) ? credential.QualifiedUsername : credential.Name;
            return $"{label}  ({credential.QualifiedUsername})";
        }

        return connection.CredentialDelivery == CredentialDelivery.Prompt
            ? Strings.Main_Credential_Prompt
            : Strings.Main_Credential_None;
    }

    private static string DescribeMultiConfig(MultiConfig config)
    {
        var enabled = 0;
        foreach (var item in config.Items)
        {
            if (item.Enabled) enabled++;
        }

        var format = (config.Sequential, config.CloseTogether) switch
        {
            (true, true) => Strings.Main_MultiSummary_Sequential_CloseTogether,
            (true, false) => Strings.Main_MultiSummary_Sequential,
            (false, true) => Strings.Main_MultiSummary_Together_CloseTogether,
            (false, false) => Strings.Main_MultiSummary_Together,
        };
        var count = UiLanguage.Plural(enabled, Strings.Main_Count_Connections_One, Strings.Main_Count_Connections_Many);
        return UiLanguage.Format(format, count);
    }

    private static string DescribeGroupContents(TreeNodeViewModel node)
    {
        var connections = 0;
        var groups = 0;
        var sets = 0;

        foreach (var child in node.SelfAndDescendants())
        {
            if (ReferenceEquals(child, node)) continue;

            if (child.Kind == TreeNodeKind.Connection) connections++;
            else if (child.Kind == TreeNodeKind.MultiConfig) sets++;
            else groups++;
        }

        var builder = new StringBuilder(64);
        builder.Append(UiLanguage.Plural(connections, Strings.Main_Count_Connections_One, Strings.Main_Count_Connections_Many));
        if (sets > 0) builder.Append(", ").Append(UiLanguage.Plural(sets, Strings.Main_Count_MultiConfigs_One, Strings.Main_Count_MultiConfigs_Many));
        if (groups > 0) builder.Append(", ").Append(UiLanguage.Plural(groups, Strings.Main_Count_SubGroups_One, Strings.Main_Count_SubGroups_Many));
        return builder.ToString();
    }

    /// <summary>Turns display settings into the plain sentence shown in the detail panel.</summary>
    public static string DescribeDisplay(DisplaySettings? display)
    {
        if (display is null) return string.Empty;

        if (display.Placement == WindowPlacementMode.SpecificMonitorFullscreen)
            return UiLanguage.Format(Strings.Main_Display_MonitorFullscreen, display.TargetMonitorIndex + 1);

        if (display.Placement == WindowPlacementMode.SpecificMonitorMaximized)
            return UiLanguage.Format(Strings.Main_Display_MonitorMaximized, display.TargetMonitorIndex + 1);

        if (display.Placement == WindowPlacementMode.SpanAllMonitors)
            return Strings.Main_Display_SpanAll;

        if (display.Placement == WindowPlacementMode.SelectedMonitors)
        {
            return display.SelectedMonitors.Count > 0
                ? UiLanguage.Format(
                    Strings.Main_Display_MonitorList,
                    string.Join(", ", display.SelectedMonitors.Select(static i => i + 1)))
                : Strings.Main_Display_SelectedMonitors;
        }

        if (display.Placement == WindowPlacementMode.CustomRectangle)
        {
            return UiLanguage.Format(
                Strings.Main_Display_CustomRectangle,
                display.CustomWidth, display.CustomHeight, display.CustomLeft, display.CustomTop);
        }

        if (display.UseAllMonitors) return Strings.Main_Display_AllMonitors;

        return display.ScreenMode == ScreenMode.Fullscreen
            ? Strings.Main_Display_Fullscreen
            : UiLanguage.Format(Strings.Main_Display_Windowed, display.DesktopWidth, display.DesktopHeight);
    }

    // ---------------------------------------------------------------- sessions

    private void OnSessionStarted(object? sender, RdpSession session) =>
        OnUi(() =>
        {
            AddSession(session);
            RefreshRunningFlags();
        });

    private void OnSessionStateChanged(object? sender, RdpSession session) =>
        OnUi(() =>
        {
            if (session.IsActive) AddSession(session);
            else RemoveSession(session);
            RefreshRunningFlags();
        });

    private void OnSessionEnded(object? sender, RdpSession session) =>
        OnUi(() =>
        {
            RemoveSession(session);
            RefreshRunningFlags();
        });

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        OnUi(() => ShowSnapshots = settings.EnableSnapshots);

    /// <summary>
    /// Called when the window comes to the front. The watchdog only re-photographs sessions every
    /// half minute or so, which is stale by the time the user switches back here to look.
    /// </summary>
    public void RefreshSnapshotsIfStale()
    {
        if (!ShowSnapshots || Sessions.Count == 0) return;

        var now = Environment.TickCount64;
        if (now - _lastSnapshotRefreshTicks < SnapshotRefreshMinimumMs) return;
        _lastSnapshotRefreshTicks = now;

        _ = Task.Run(async () =>
        {
            try
            {
                await _services.Sessions.RefreshSnapshotsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Refreshing the session thumbnails failed.", ex);
            }
        });
    }

    private void AddSession(RdpSession session)
    {
        foreach (var existing in Sessions)
        {
            if (existing.Id == session.Id) return;
        }

        Sessions.Add(session);
        AfterSessionsChanged();
    }

    private void RemoveSession(RdpSession session)
    {
        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            if (Sessions[i].Id != session.Id) continue;
            Sessions.RemoveAt(i);
            AfterSessionsChanged();
            return;
        }
    }

    /// <summary>Rebuilds the strip from the session manager; used after a reload.</summary>
    private void SyncSessions()
    {
        Sessions.Clear();
        foreach (var session in _services.Sessions.Sessions)
        {
            if (session.IsActive) Sessions.Add(session);
        }
        AfterSessionsChanged();
    }

    private void AfterSessionsChanged()
    {
        UpdateTreeSummary();

        if (Sessions.Count > 0)
        {
            Clock = DateTime.UtcNow;
            if (!_clockTimer.IsEnabled) _clockTimer.Start();
        }
        else if (_clockTimer.IsEnabled)
        {
            _clockTimer.Stop();
        }
    }

    private void OnClockTick(object? sender, EventArgs e) => Clock = DateTime.UtcNow;

    private void RefreshRunningFlags()
    {
        try
        {
            foreach (var root in Nodes) UpdateRunning(root);

            if (_selectedNode is { } node)
            {
                DetailRunning = node.IsRunning;
                if (node.IsMultiConfig && node.AsMultiConfig is { } config) FillMultiItems(config);
            }

            if (_filter == LibraryFilter.Connected) ApplyFilter();

            UpdateTreeSummary();
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not refresh the running state of the library.", ex);
        }
    }

    private bool UpdateRunning(TreeNodeViewModel node)
    {
        var running = node.Kind switch
        {
            TreeNodeKind.Connection => _services.Sessions.FindByConnection(node.Id) is not null,
            TreeNodeKind.MultiConfig => _services.Sessions.IsMultiConfigRunning(node.Id),
            _ => false,
        };

        foreach (var child in node.Children) running |= UpdateRunning(child);

        node.IsRunning = running;
        return running;
    }

    private void FocusSession(object? parameter)
    {
        if (parameter is not RdpSession session) return;

        try
        {
            _services.Sessions.Focus(session.Id);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not focus '{session.DisplayName}'.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_Focus, session.DisplayName), true);
        }
    }

    private async Task ReconnectSessionAsync(object? parameter)
    {
        if (parameter is not RdpSession session) return;

        try
        {
            await _services.Sessions.ReconnectAsync(session.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not reconnect '{session.DisplayName}'.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_Reconnect, session.DisplayName, ex.Message), true);
        }
    }

    private async Task CloseSessionAsync(object? parameter)
    {
        if (parameter is not RdpSession session) return;

        if (_services.Settings.ConfirmSessionClose
            && !Confirm(
                Strings.Main_CloseSession_Title,
                UiLanguage.Format(Strings.Main_CloseSession_Message, session.DisplayName),
                Strings.Main_Session_Close))
        {
            return;
        }

        try
        {
            await _services.Sessions.CloseAsync(session.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not close '{session.DisplayName}'.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_CloseSession, session.DisplayName, ex.Message), true);
        }
    }

    // ---------------------------------------------------------------- commands

    private Task ActivateAsync(object? parameter)
    {
        var node = parameter as TreeNodeViewModel ?? _selectedNode;
        if (node is null) return Task.CompletedTask;

        if (!ReferenceEquals(node, _selectedNode)) SelectById(node.Id);

        if (node.IsConnection) return ConnectSelectedAsync();
        if (node.IsMultiConfig) return LaunchMultiAsync();

        node.IsExpanded = !node.IsExpanded;
        return Task.CompletedTask;
    }

    private async Task ConnectSelectedAsync()
    {
        if (_selectedNode?.AsConnection is not { } connection) return;

        try
        {
            var session = await _services.Sessions.LaunchAsync(connection).ConfigureAwait(true);
            if (session is null)
            {
                // Something missing on this PC gets its full explanation, not "could not be started".
                if (_services.Sessions.LastLaunchProblem is { } problem)
                    _shell.ShowNotice(Strings.Dependency_Title, problem);
                else
                    Notify(UiLanguage.Format(Strings.Main_Error_StartNoSession, connection.Name), true);
                return;
            }

            AddSession(session);
            RefreshRunningFlags();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not launch '{connection.Name}'.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_Start, connection.Name, ex.Message), true);
        }
    }

    private async Task LaunchMultiAsync()
    {
        if (_selectedNode?.AsMultiConfig is not { } config) return;

        try
        {
            var sessions = await _services.Sessions.LaunchMultiAsync(config).ConfigureAwait(true);
            foreach (var session in sessions) AddSession(session);
            RefreshRunningFlags();

            if (sessions.Count == 0) Notify(UiLanguage.Format(Strings.Main_Multi_NothingToLaunch, config.Name), true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not launch '{config.Name}'.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_Start, config.Name, ex.Message), true);
        }
    }

    private async Task CloseMultiAsync()
    {
        if (_selectedNode?.AsMultiConfig is not { } config) return;

        if (_services.Settings.ConfirmSessionClose
            && !Confirm(
                Strings.Main_CloseMulti_Title,
                UiLanguage.Format(Strings.Main_CloseMulti_Message, config.Name),
                Strings.Main_Session_Close))
        {
            return;
        }

        try
        {
            await _services.Sessions.CloseMultiAsync(config.Id).ConfigureAwait(true);
            RefreshRunningFlags();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not close '{config.Name}'.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_CloseMulti, config.Name, ex.Message), true);
        }
    }

    private async Task NewConnectionAsync()
    {
        var editor = new ConnectionEditorViewModel(_services, null, CurrentGroupId());
        if (!ShowDialog(new ConnectionEditorWindow(editor))) return;

        var connection = editor.Result;
        connection.SortOrder = NextLeafPosition(connection.GroupId);
        if (!await RunStoreAsync(
                "save the connection", Strings.Main_Error_SaveConnection,
                () => _services.Store.UpsertConnectionAsync(connection)))
            return;

        await LoadAsync().ConfigureAwait(true);
        SelectById(connection.Id);
    }

    private async Task NewMultiConfigAsync()
    {
        var editor = new MultiConfigEditorViewModel(_services, null, CurrentGroupId());
        if (!ShowDialog(new MultiConfigEditorWindow(editor))) return;

        var config = editor.Result;
        config.SortOrder = NextLeafPosition(config.GroupId);
        if (!await RunStoreAsync(
                "save the multi-config", Strings.Main_Error_SaveMultiConfig,
                () => _services.Store.UpsertMultiConfigAsync(config)))
            return;

        await LoadAsync().ConfigureAwait(true);
        SelectById(config.Id);
    }

    private async Task NewGroupAsync()
    {
        var name = Prompt(Strings.Main_NewGroup, Strings.Main_NewGroup_Prompt, Strings.Main_NewGroup_DefaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var parentId = CurrentGroupId();
        var group = new ConnectionGroup
        {
            Name = name.Trim(),
            ParentId = parentId,
            SortOrder = NextGroupPosition(parentId),
        };

        if (!await RunStoreAsync(
                "create the group", Strings.Main_Error_CreateGroup,
                () => _services.Store.UpsertGroupAsync(group)))
            return;

        await LoadAsync().ConfigureAwait(true);
        SelectById(group.Id);
    }

    private async Task EditSelectedAsync()
    {
        var node = _selectedNode;
        if (node is null) return;

        if (node.IsConnection && node.AsConnection is { } connection)
        {
            var editor = new ConnectionEditorViewModel(_services, connection.Clone(), connection.GroupId);
            if (!ShowDialog(new ConnectionEditorWindow(editor))) return;

            var edited = editor.Result;
            if (!await RunStoreAsync(
                    "save the connection", Strings.Main_Error_SaveConnection,
                    () => _services.Store.UpsertConnectionAsync(edited)))
                return;

            await LoadAsync().ConfigureAwait(true);
            SelectById(edited.Id);
            return;
        }

        if (node.IsMultiConfig && node.AsMultiConfig is { } config)
        {
            var editor = new MultiConfigEditorViewModel(_services, config.Clone(), config.GroupId);
            if (!ShowDialog(new MultiConfigEditorWindow(editor))) return;

            var edited = editor.Result;
            if (!await RunStoreAsync(
                    "save the multi-config", Strings.Main_Error_SaveMultiConfig,
                    () => _services.Store.UpsertMultiConfigAsync(edited)))
                return;

            await LoadAsync().ConfigureAwait(true);
            SelectById(edited.Id);
            return;
        }

        await RenameSelectedAsync().ConfigureAwait(true);
    }

    private async Task RenameSelectedAsync()
    {
        var node = _selectedNode;
        if (node is null) return;

        var name = Prompt(Strings.Main_Rename, Strings.Main_Rename_Prompt, node.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        var trimmed = name.Trim();
        if (string.Equals(trimmed, node.Name, StringComparison.Ordinal)) return;

        var now = DateTime.UtcNow;
        bool saved;

        if (node.IsGroup && node.AsGroup is { } group)
        {
            var edited = group.Clone();
            edited.Name = trimmed;
            edited.ModifiedUtc = now;
            saved = await RunStoreAsync(
                "rename the group", Strings.Main_Error_RenameGroup,
                () => _services.Store.UpsertGroupAsync(edited));
        }
        else if (node.IsConnection && node.AsConnection is { } connection)
        {
            var edited = connection.Clone();
            edited.Name = trimmed;
            edited.ModifiedUtc = now;
            saved = await RunStoreAsync(
                "rename the connection", Strings.Main_Error_RenameConnection,
                () => _services.Store.UpsertConnectionAsync(edited));
        }
        else if (node.IsMultiConfig && node.AsMultiConfig is { } config)
        {
            var edited = config.Clone();
            edited.Name = trimmed;
            edited.ModifiedUtc = now;
            saved = await RunStoreAsync(
                "rename the multi-config", Strings.Main_Error_RenameMultiConfig,
                () => _services.Store.UpsertMultiConfigAsync(edited));
        }
        else
        {
            return;
        }

        if (!saved) return;

        var id = node.Id;
        await LoadAsync().ConfigureAwait(true);
        SelectById(id);
    }

    private async Task DuplicateSelectedAsync()
    {
        var node = _selectedNode;
        if (node is null) return;

        var now = DateTime.UtcNow;
        Guid copyId;
        bool saved;

        if (node.IsConnection && node.AsConnection is { } connection)
        {
            var copy = connection.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = UiLanguage.Format(Strings.Main_CopyName, connection.Name);
            copy.CreatedUtc = now;
            copy.ModifiedUtc = now;
            copy.LastConnectedUtc = null;
            copy.LaunchCount = 0;
            copyId = copy.Id;
            saved = await RunStoreAsync(
                "duplicate the connection", Strings.Main_Error_DuplicateConnection,
                () => _services.Store.UpsertConnectionAsync(copy));
        }
        else if (node.IsMultiConfig && node.AsMultiConfig is { } config)
        {
            var copy = config.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = UiLanguage.Format(Strings.Main_CopyName, config.Name);
            copy.CreatedUtc = now;
            copy.ModifiedUtc = now;
            copy.LastLaunchedUtc = null;
            copy.LaunchCount = 0;
            foreach (var item in copy.Items) item.Id = Guid.NewGuid();
            copyId = copy.Id;
            saved = await RunStoreAsync(
                "duplicate the multi-config", Strings.Main_Error_DuplicateMultiConfig,
                () => _services.Store.UpsertMultiConfigAsync(copy));
        }
        else if (node.IsGroup && node.AsGroup is { } group)
        {
            var copy = group.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = UiLanguage.Format(Strings.Main_CopyName, group.Name);
            copy.CreatedUtc = now;
            copy.ModifiedUtc = now;
            copyId = copy.Id;
            saved = await RunStoreAsync(
                "duplicate the group", Strings.Main_Error_DuplicateGroup,
                () => _services.Store.UpsertGroupAsync(copy));
        }
        else
        {
            return;
        }

        if (!saved) return;

        await LoadAsync().ConfigureAwait(true);
        SelectById(copyId);
    }

    private async Task DeleteSelectedAsync()
    {
        var node = _selectedNode;
        if (node is null) return;

        string title;
        string message;
        string what;
        string failure;

        if (node.IsConnection)
        {
            title = Strings.Main_DeleteConnection_Title;
            message = UiLanguage.Format(Strings.Main_DeleteConnection_Message, node.Name);
            what = "delete the connection";
            failure = Strings.Main_Error_DeleteConnection;
        }
        else if (node.IsMultiConfig)
        {
            title = Strings.Main_DeleteMultiConfig_Title;
            message = UiLanguage.Format(Strings.Main_DeleteMultiConfig_Message, node.Name);
            what = "delete the multi-config";
            failure = Strings.Main_Error_DeleteMultiConfig;
        }
        else
        {
            title = Strings.Main_DeleteGroup_Title;
            message = UiLanguage.Format(Strings.Main_DeleteGroup_Message, node.Name);
            what = "delete the group";
            failure = Strings.Main_Error_DeleteGroup;
        }

        if (!Confirm(title, message)) return;

        var id = node.Id;
        bool deleted;

        if (node.IsConnection)
            deleted = await RunStoreAsync(what, failure, () => _services.Store.DeleteConnectionAsync(id));
        else if (node.IsMultiConfig)
            deleted = await RunStoreAsync(what, failure, () => _services.Store.DeleteMultiConfigAsync(id));
        else
            deleted = await RunStoreAsync(what, failure, () => _services.Store.DeleteGroupAsync(id));

        if (!deleted) return;
        if (node.IsConnection) SnapshotService.ForgetConnection(id);

        _pendingSelection = null;
        _selectedNode = null;
        OnPropertyChanged(nameof(SelectedNode));

        await LoadAsync().ConfigureAwait(true);
        RefreshDetail();
        RaiseCommandStates();
    }

    private async Task ToggleFavoriteAsync()
    {
        var node = _selectedNode;
        if (node is null) return;

        var now = DateTime.UtcNow;
        bool saved;

        if (node.IsConnection && node.AsConnection is { } connection)
        {
            var edited = connection.Clone();
            edited.Favorite = !edited.Favorite;
            edited.ModifiedUtc = now;
            saved = await RunStoreAsync(
                "update the connection", Strings.Main_Error_UpdateConnection,
                () => _services.Store.UpsertConnectionAsync(edited));
        }
        else if (node.IsMultiConfig && node.AsMultiConfig is { } config)
        {
            var edited = config.Clone();
            edited.Favorite = !edited.Favorite;
            edited.ModifiedUtc = now;
            saved = await RunStoreAsync(
                "update the multi-config", Strings.Main_Error_UpdateMultiConfig,
                () => _services.Store.UpsertMultiConfigAsync(edited));
        }
        else
        {
            return;
        }

        if (!saved) return;

        var id = node.Id;
        await LoadAsync().ConfigureAwait(true);
        SelectById(id);
    }

    private async Task ExportSelectedAsync()
    {
        if (_selectedNode?.AsConnection is not { } connection) return;

        var dialog = new SaveFileDialog
        {
            Title = Strings.Main_Export_Title,
            Filter = Strings.Main_FileDialog_RdpFilter,
            FileName = SafeFileName(connection.Name) + ".rdp",
            DefaultExt = ".rdp",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (!ShowFileDialog(dialog)) return;

        var path = dialog.FileName;
        var context = new RdpBuildContext
        {
            Display = connection.Display,
            Credential = connection.CredentialSetId is { } id && _credentialsById.TryGetValue(id, out var credential)
                ? credential
                : null,
            EmbedPassword = false,
            Monitors = SafeMonitors(),
        };

        try
        {
            await Task.Run(() => _services.RdpBuilder.WriteToFile(connection, context, path)).ConfigureAwait(true);
            Notify(UiLanguage.Format(Strings.Main_Export_Done, connection.Name, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not export '{connection.Name}'.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_Export, connection.Name, ex.Message), true);
        }
    }

    private async Task ImportRdpFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Main_Import_Title,
            Filter = Strings.Main_FileDialog_RdpFilter,
            CheckFileExists = true,
            Multiselect = true,
        };

        if (!ShowFileDialog(dialog)) return;

        var files = dialog.FileNames;
        if (files.Length == 0) return;

        var groupId = CurrentGroupId();
        Guid? lastId = null;

        try
        {
            lastId = await Task.Run(async () =>
            {
                Guid? last = null;
                foreach (var file in files)
                {
                    var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    var connection = _services.RdpBuilder.Parse(text, Path.GetFileNameWithoutExtension(file));
                    connection.GroupId = groupId;
                    await _services.Store.UpsertConnectionAsync(connection).ConfigureAwait(false);
                    last = connection.Id;
                }
                return last;
            }).ConfigureAwait(true);

            Notify(UiLanguage.Plural(files.Length, Strings.Main_Import_Done_One, Strings.Main_Import_Done_Many));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not import the Remote Desktop files.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_Import, ex.Message), true);
        }

        await LoadAsync().ConfigureAwait(true);
        if (lastId is { } id) SelectById(id);
    }

    private async Task OpenCredentialsAsync()
    {
        SafeShell(() => _shell.ShowCredentials());
        await LoadAsync().ConfigureAwait(true);
    }

    // ----------------------------------------------------------------- helpers

    private Guid? CurrentGroupId()
    {
        var node = _selectedNode;
        if (node is null) return null;
        return node.IsGroup ? node.Id : node.Parent?.Id;
    }

    private IReadOnlyList<MonitorInfo> SafeMonitors()
    {
        try
        {
            return _services.Monitors.GetMonitors();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not read the monitor layout.", ex);
            return Array.Empty<MonitorInfo>();
        }
    }

    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Strings.Main_Export_DefaultFileName;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var character in name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? Strings.Main_Export_DefaultFileName : result;
    }

    /// <summary>
    /// Runs a store call off the UI thread. <paramref name="what"/> is the English phrase for the
    /// log; <paramref name="failure"/> is the notification text, with {0} for the error message.
    /// </summary>
    private async Task<bool> RunStoreAsync(string what, string failure, Func<Task> action)
    {
        try
        {
            await Task.Run(action).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not {what}.", ex);
            Notify(UiLanguage.Format(failure, ex.Message), true);
            return false;
        }
    }

    private System.Windows.Window? Owner => _window is { IsVisible: true } ? _window : null;

    private bool ShowDialog(System.Windows.Window dialog)
    {
        try
        {
            var owner = Owner;
            dialog.Owner = owner;
            if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            AppLog.Error("A dialog could not be opened.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_OpenWindow, ex.Message), true);
            return false;
        }
    }

    private bool ShowFileDialog(FileDialog dialog)
    {
        try
        {
            var owner = Owner;
            var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return result == true;
        }
        catch (Exception ex)
        {
            AppLog.Error("The file dialog could not be opened.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_OpenFileDialog, ex.Message), true);
            return false;
        }
    }

    private bool Confirm(string title, string message, string? confirmText = null, bool destructive = true) =>
        ShowDialog(new ConfirmDialog(title, message, confirmText ?? Strings.Common_Delete, destructive));

    private string? Prompt(string title, string prompt, string? initial)
    {
        var dialog = new InputDialog(title, prompt, initial);
        return ShowDialog(dialog) ? dialog.Value : null;
    }

    private void Notify(string message, bool isError = false)
    {
        try
        {
            _shell.Notify(AppIdentity.Name, message, isError);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The notification could not be shown.", ex);
        }
    }

    private void SafeShell(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Error("A shell command failed.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_OpenWindow, ex.Message), true);
        }
    }

    private void OnUi(Action action)
    {
        try
        {
            if (_dispatcher.CheckAccess())
            {
                SafeRun(action);
                return;
            }

            _dispatcher.InvokeAsync(() => SafeRun(action));
        }
        catch (Exception ex)
        {
            AppLog.Warn("A UI update could not be scheduled.", ex);
        }
    }

    private static void SafeRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Error("A UI update failed.", ex);
        }
    }
}

/// <summary>
/// Session start time plus the view model's one-second clock, rendered as a live uptime.
/// The clock is only there to make the binding re-evaluate on every tick.
/// </summary>
public sealed class MainWindowUptimeConverter : IMultiValueConverter
{
    public static readonly MainWindowUptimeConverter Instance = new();

    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length == 0 || values[0] is not DateTime started) return string.Empty;

        var ended = values.Length > 1 && values[1] is DateTime finished ? finished : DateTime.UtcNow;
        var span = ended - started;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalHours >= 1) return UiLanguage.Format(Strings.Time_Uptime_Hours, (int)span.TotalHours, span.Minutes);
        if (span.TotalMinutes >= 1) return UiLanguage.Format(Strings.Time_Uptime_Minutes, span.Minutes, span.Seconds);
        return UiLanguage.Format(Strings.Time_Uptime_Seconds, span.Seconds);
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        var result = new object?[targetTypes?.Length ?? 0];
        for (var i = 0; i < result.Length; i++) result[i] = Binding.DoNothing;
        return result;
    }
}
