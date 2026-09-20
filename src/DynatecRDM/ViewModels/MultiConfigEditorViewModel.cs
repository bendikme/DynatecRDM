using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Services;
using PlacementKind = DynatecRDM.Models.WindowPlacementMode;
using ScreenModeKind = DynatecRDM.Models.ScreenMode;

namespace DynatecRDM.ViewModels;

/// <summary>An entry of the group combo; <see cref="Id"/> is null for "no group".</summary>
public sealed class MultiConfigEditorGroupOption
{
    public MultiConfigEditorGroupOption(Guid? id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid? Id { get; }
    public string Name { get; }
}

/// <summary>
/// An entry of the per-item credential combo. <see cref="Id"/> is <see cref="Guid.Empty"/> for
/// "use the connection's own": a selector cannot select an item whose value is null, so the
/// inherit entry needs a real value of its own.
/// </summary>
public sealed class MultiConfigEditorCredentialOption
{
    public MultiConfigEditorCredentialOption(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; }
    public string Name { get; }
}

/// <summary>One colour choice in the header; <see cref="Hex"/> is null for "no colour".</summary>
public sealed class MultiConfigEditorColorSwatch : ObservableObject
{
    private bool _isSelected;

    public MultiConfigEditorColorSwatch(string? hex, string name)
    {
        Hex = hex;
        Name = name;
    }

    public string? Hex { get; }
    public string Name { get; }
    public bool HasColor => Hex is not null;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A connection offered by the "Add connection" picker.</summary>
public sealed class MultiConfigEditorPickerEntry
{
    public MultiConfigEditorPickerEntry(Guid id, string name, string host, string? colorHex, string groupName)
    {
        Id = id;
        Name = name;
        Host = host;
        ColorHex = colorHex;
        GroupName = groupName;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Host { get; }
    public string? ColorHex { get; }
    public string GroupName { get; }
}

/// <summary>A small labelled tag drawn inside a monitor rectangle on the layout map.</summary>
public sealed class MultiConfigEditorMapChip
{
    public MultiConfigEditorMapChip(string name, string? colorHex, bool isItemEnabled, bool isSelected)
    {
        Name = name;
        ColorHex = colorHex;
        IsItemEnabled = isItemEnabled;
        IsSelected = isSelected;
    }

    public string Name { get; }
    public string? ColorHex { get; }

    /// <summary>Named so it cannot be confused with UIElement.IsEnabled in a data trigger.</summary>
    public bool IsItemEnabled { get; }

    public bool IsSelected { get; }
}

/// <summary>One physical display drawn on the layout map, with the items that land on it.</summary>
public sealed class MultiConfigEditorMonitorCell : ObservableObject
{
    public MultiConfigEditorMonitorCell(
        int index, string title, string detail, bool isPrimary,
        double x, double y, double width, double height)
    {
        Index = index;
        Title = title;
        Detail = detail;
        IsPrimary = isPrimary;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Chips = new ObservableCollection<MultiConfigEditorMapChip>();
    }

    public int Index { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsPrimary { get; }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public ObservableCollection<MultiConfigEditorMapChip> Chips { get; }

    public bool IsEmpty => Chips.Count == 0;

    /// <summary>Raised by the editor once every chip has been added.</summary>
    public void ChipsFilled() => OnPropertyChanged(nameof(IsEmpty));
}

/// <summary>An entry of the screen-mode combo.</summary>
public sealed class MultiConfigEditorScreenModeOption
{
    public MultiConfigEditorScreenModeOption(ScreenModeKind value, string name)
    {
        Value = value;
        Name = name;
    }

    public ScreenModeKind Value { get; }
    public string Name { get; }
}

/// <summary>An entry of the placement combo.</summary>
public sealed class MultiConfigEditorPlacementOption
{
    public MultiConfigEditorPlacementOption(PlacementKind value, string name)
    {
        Value = value;
        Name = name;
    }

    public PlacementKind Value { get; }
    public string Name { get; }
}

/// <summary>
/// Edits a <see cref="MultiConfig"/>: a named set of connections launched together, each with
/// its own screen placement layered on top of the connection's own display settings.
///
/// The work is done on a clone, so nothing reaches the store until Save or Launch now.
/// </summary>
public sealed class MultiConfigEditorViewModel : ObservableObject
{
    private const int MaxDelayMs = 600000;

    private static readonly MultiConfigEditorScreenModeOption[] ScreenModes =
    {
        new(ScreenModeKind.Fullscreen, "Full screen"),
        new(ScreenModeKind.Windowed, "Windowed"),
    };

    private static readonly MultiConfigEditorPlacementOption[] Placements =
    {
        new(PlacementKind.Default, "Leave it to Remote Desktop"),
        new(PlacementKind.SpecificMonitorFullscreen, "Full screen on one monitor"),
        new(PlacementKind.SpecificMonitorMaximized, "Maximized on one monitor"),
        new(PlacementKind.SpanAllMonitors, "Span every monitor"),
        new(PlacementKind.SelectedMonitors, "Use the selected monitors"),
        new(PlacementKind.CustomRectangle, "Exact rectangle"),
    };

    private static readonly int[] ColorDepths = { 8, 15, 16, 24, 32 };
    private static readonly int[] ScaleFactors = { 100, 125, 150, 175, 200, 250, 300, 400, 500 };

    private readonly AppServices _services;
    private readonly Dictionary<Guid, RdpConnection> _connectionsById = new();
    private readonly List<RdpConnection> _connections = new();
    private readonly Dictionary<Guid, string> _groupNames = new();
    private readonly Dictionary<int, MultiConfigEditorMonitorCell> _cellsByIndex = new();

    /// <summary>Layout the monitor rectangles were last built for; empty means "none yet".</summary>
    private string _mapSignature = string.Empty;

    private IReadOnlyList<MonitorInfo> _monitorSnapshot = Array.Empty<MonitorInfo>();
    private IReadOnlyList<int> _mstscIds = Array.Empty<int>();

    private string _name;
    private string _description;
    private MultiConfigEditorGroupOption? _selectedGroup;
    private MultiConfigItemViewModel? _selectedItem;
    private string _pickerSearch = string.Empty;
    private MultiConfigEditorPickerEntry? _pickerSelection;
    private bool _isPickerOpen;
    private bool _isBusy;
    private bool _loaded;
    private bool _detached;
    private bool _refreshPending;
    private bool _settingGroup;
    private string? _errorText;
    private string? _warningText;
    private string? _infoText;
    private string? _mapEmptyText;

    public MultiConfigEditorViewModel(AppServices services, MultiConfig? existing, Guid? defaultGroupId)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        IsNew = existing is null;
        Result = existing?.Clone() ?? new MultiConfig();
        if (IsNew && defaultGroupId.HasValue) Result.GroupId = defaultGroupId;

        _name = Result.Name;
        _description = Result.Description ?? string.Empty;

        Items = new ObservableCollection<MultiConfigItemViewModel>();
        Groups = new ObservableCollection<MultiConfigEditorGroupOption>();
        CredentialOptions = new ObservableCollection<MultiConfigEditorCredentialOption>();
        Monitors = new ObservableCollection<MonitorInfo>();
        MonitorCells = new ObservableCollection<MultiConfigEditorMonitorCell>();
        UnplacedItems = new ObservableCollection<MultiConfigItemViewModel>();
        PickerResults = new ObservableCollection<MultiConfigEditorPickerEntry>();

        ColorSwatches = new[]
        {
            new MultiConfigEditorColorSwatch(null, "No colour"),
            new MultiConfigEditorColorSwatch("#2A94FF", "Blue"),
            new MultiConfigEditorColorSwatch("#3DD68C", "Green"),
            new MultiConfigEditorColorSwatch("#F5A524", "Amber"),
            new MultiConfigEditorColorSwatch("#F45B5B", "Red"),
            new MultiConfigEditorColorSwatch("#A97BFF", "Purple"),
            new MultiConfigEditorColorSwatch("#29C7C7", "Teal"),
            new MultiConfigEditorColorSwatch("#FF7FB0", "Pink"),
            new MultiConfigEditorColorSwatch("#8A94A6", "Grey"),
        };
        SyncSwatches();

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !HasError && !IsBusy);
        LaunchCommand = new AsyncRelayCommand(LaunchNowAsync, () => !HasError && !IsBusy);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
        OpenPickerCommand = new RelayCommand(OpenPicker);
        ClosePickerCommand = new RelayCommand(() => IsPickerOpen = false);
        AddConnectionCommand = new RelayCommand(p => AddConnection(p as MultiConfigEditorPickerEntry));
        AddSelectedCommand = new RelayCommand(
            () => AddConnection(PickerSelection),
            () => PickerSelection is not null);
        RemoveCommand = new RelayCommand(RemoveSelected, () => SelectedItem is not null);
        DuplicateCommand = new RelayCommand(DuplicateSelected, () => SelectedItem is not null);
        MoveUpCommand = new RelayCommand(p => Move(p, -1), p => CanMove(p, -1));
        MoveDownCommand = new RelayCommand(p => Move(p, 1), p => CanMove(p, 1));
        AssignMonitorCommand = new RelayCommand(AssignMonitor, _ => SelectedItem is not null);
        ClearOverridesCommand = new RelayCommand(
            () => { SelectedItem?.ClearOverrides(); ScheduleRefresh(); },
            () => SelectedItem is not null);
        SetColorCommand = new RelayCommand(p => SetColor(p as MultiConfigEditorColorSwatch));

        _services.Monitors.MonitorsChanged += OnMonitorsChanged;
        RefreshMonitors();
        Validate();
    }

    /// <summary>The multi-config being edited. It is a clone until Save writes it through.</summary>
    public MultiConfig Result { get; }

    /// <summary>Raised with true when the editor saved, false when the user cancelled.</summary>
    public event EventHandler<bool>? RequestClose;

    public bool IsNew { get; }

    public bool IsSaved { get; private set; }

    public string HeaderText => IsNew ? "New multi-config" : "Edit multi-config";

    // ------------------------------------------------------------------ collections

    public ObservableCollection<MultiConfigItemViewModel> Items { get; }
    public ObservableCollection<MultiConfigEditorGroupOption> Groups { get; }
    public ObservableCollection<MultiConfigEditorCredentialOption> CredentialOptions { get; }
    public ObservableCollection<MonitorInfo> Monitors { get; }
    public ObservableCollection<MultiConfigEditorMonitorCell> MonitorCells { get; }
    public ObservableCollection<MultiConfigItemViewModel> UnplacedItems { get; }
    public ObservableCollection<MultiConfigEditorPickerEntry> PickerResults { get; }
    public IReadOnlyList<MultiConfigEditorColorSwatch> ColorSwatches { get; }

    public IReadOnlyList<MultiConfigEditorScreenModeOption> ScreenModeOptions => ScreenModes;
    public IReadOnlyList<MultiConfigEditorPlacementOption> PlacementOptions => Placements;
    public IReadOnlyList<int> ColorDepthOptions => ColorDepths;
    public IReadOnlyList<int> ScaleFactorOptions => ScaleFactors;

    // ------------------------------------------------------------------ commands

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenPickerCommand { get; }
    public RelayCommand ClosePickerCommand { get; }
    public RelayCommand AddConnectionCommand { get; }
    public RelayCommand AddSelectedCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand AssignMonitorCommand { get; }
    public RelayCommand ClearOverridesCommand { get; }
    public RelayCommand SetColorCommand { get; }

    // ------------------------------------------------------------------ header fields

    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value ?? string.Empty)) return;
            Result.Name = _name;
            Validate();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (!SetProperty(ref _description, value ?? string.Empty)) return;
            Result.Description = string.IsNullOrWhiteSpace(_description) ? null : _description;
        }
    }

    public MultiConfigEditorGroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            if (_settingGroup) return;
            Result.GroupId = value?.Id;
        }
    }

    public string? Color
    {
        get => Result.Color;
        set
        {
            if (string.Equals(Result.Color, value, StringComparison.OrdinalIgnoreCase)) return;
            Result.Color = value;
            OnPropertyChanged();
            SyncSwatches();
        }
    }

    public bool Favorite
    {
        get => Result.Favorite;
        set
        {
            if (Result.Favorite == value) return;
            Result.Favorite = value;
            OnPropertyChanged();
        }
    }

    public bool Sequential
    {
        get => Result.Sequential;
        set
        {
            if (Result.Sequential == value) return;
            Result.Sequential = value;
            OnPropertyChanged();
            Validate();
        }
    }

    public int InitialDelayMs
    {
        get => Result.InitialDelayMs;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxDelayMs);
            if (Result.InitialDelayMs == clamped) return;
            Result.InitialDelayMs = clamped;
            OnPropertyChanged();
        }
    }

    public bool CloseTogether
    {
        get => Result.CloseTogether;
        set
        {
            if (Result.CloseTogether == value) return;
            Result.CloseTogether = value;
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------------ selection and state

    public MultiConfigItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            RaiseCommandStates();
            RecomputeMap();
        }
    }

    public bool HasSelection => _selectedItem is not null;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommandStates();
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (!SetProperty(ref _errorText, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public string? WarningText
    {
        get => _warningText;
        private set
        {
            if (!SetProperty(ref _warningText, value)) return;
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    public string? InfoText
    {
        get => _infoText;
        private set => SetProperty(ref _infoText, value);
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool HasWarning => !string.IsNullOrEmpty(_warningText);

    public string? MapEmptyText
    {
        get => _mapEmptyText;
        private set => SetProperty(ref _mapEmptyText, value);
    }

    /// <summary>Logical size of the layout map, in device-independent pixels.</summary>
    public double MapCanvasWidth => 560;

    public double MapCanvasHeight => 196;

    // ------------------------------------------------------------------ picker

    public bool IsPickerOpen
    {
        get => _isPickerOpen;
        set => SetProperty(ref _isPickerOpen, value);
    }

    public string PickerSearch
    {
        get => _pickerSearch;
        set
        {
            if (!SetProperty(ref _pickerSearch, value ?? string.Empty)) return;
            RefreshPicker();
        }
    }

    /// <summary>Highlighted row in the picker; Add and a double click act on it.</summary>
    public MultiConfigEditorPickerEntry? PickerSelection
    {
        get => _pickerSelection;
        set
        {
            if (!SetProperty(ref _pickerSelection, value)) return;
            AddSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public string? PickerEmptyText { get; private set; }

    // ------------------------------------------------------------------ loading

    /// <summary>Reads connections, groups and credentials off the UI thread and builds the items.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        IsBusy = true;

        // Captured before the combo is populated, because filling its items can clear a selection.
        var desiredGroup = Result.GroupId;

        try
        {
            var connections = await _services.Store.GetConnectionsAsync().ConfigureAwait(true);
            var groups = await _services.Store.GetGroupsAsync().ConfigureAwait(true);
            var credentials = await _services.Store.GetCredentialSetsAsync().ConfigureAwait(true);

            _connections.Clear();
            _connectionsById.Clear();
            foreach (var connection in connections)
            {
                _connections.Add(connection);
                _connectionsById[connection.Id] = connection;
            }
            _connections.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            _groupNames.Clear();
            Groups.Clear();
            Groups.Add(new MultiConfigEditorGroupOption(null, "No group"));
            foreach (var group in groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _groupNames[group.Id] = group.Name;
                Groups.Add(new MultiConfigEditorGroupOption(group.Id, group.Name));
            }

            CredentialOptions.Clear();
            CredentialOptions.Add(
                new MultiConfigEditorCredentialOption(Guid.Empty, "Use the connection's own credential"));

            var knownCredentials = new HashSet<Guid> { Guid.Empty };
            foreach (var credential in credentials.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                knownCredentials.Add(credential.Id);
                var label = string.IsNullOrWhiteSpace(credential.QualifiedUsername)
                    ? credential.Name
                    : $"{credential.Name} ({credential.QualifiedUsername})";
                CredentialOptions.Add(new MultiConfigEditorCredentialOption(credential.Id, label));
            }

            // An item may still point at a credential set that has since been deleted. Without an
            // entry of its own the combo would have nothing to select, blank itself and quietly
            // push the override away; giving it a row keeps the problem visible instead.
            foreach (var model in Result.Items)
            {
                var id = model.CredentialSetIdOverride;
                if (id is null || !knownCredentials.Add(id.Value)) continue;
                CredentialOptions.Add(
                    new MultiConfigEditorCredentialOption(id.Value, "Credential set that no longer exists"));
            }

            BuildItems();

            _settingGroup = true;
            try
            {
                MultiConfigEditorGroupOption? match = null;
                foreach (var option in Groups)
                {
                    if (option.Id != desiredGroup) continue;
                    match = option;
                    break;
                }
                SelectedGroup = match ?? Groups[0];
                Result.GroupId = SelectedGroup?.Id;
            }
            finally
            {
                _settingGroup = false;
            }

            RefreshPicker();
            RecomputeMap();
            Validate();
        }
        catch (Exception ex)
        {
            AppLog.Error("The multi-config editor could not load its data.", ex);
            ErrorText = "The saved connections could not be read: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildItems()
    {
        foreach (var existing in Items) existing.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();

        foreach (var model in Result.Items.OrderBy(static i => i.Order))
        {
            _connectionsById.TryGetValue(model.ConnectionId, out var connection);
            var item = new MultiConfigItemViewModel(model, connection);
            item.SetMonitors(_monitorSnapshot, _mstscIds);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        Renumber();
        SelectedItem = Items.Count > 0 ? Items[0] : null;
        RaiseCommandStates();
    }

    /// <summary>Drops the monitor subscription; called when the window closes.</summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        _services.Monitors.MonitorsChanged -= OnMonitorsChanged;
        foreach (var item in Items) item.PropertyChanged -= OnItemPropertyChanged;
    }

    // ------------------------------------------------------------------ items

    private void OpenPicker()
    {
        PickerSearch = string.Empty;
        RefreshPicker();
        IsPickerOpen = true;
    }

    private void RefreshPicker()
    {
        PickerResults.Clear();

        var term = _pickerSearch.Trim();
        foreach (var connection in _connections)
        {
            if (term.Length > 0
                && connection.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0
                && connection.Host.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0)
            {
                continue;
            }

            PickerResults.Add(new MultiConfigEditorPickerEntry(
                connection.Id,
                connection.Name,
                connection.FullAddress,
                connection.Color,
                GroupNameFor(connection.GroupId)));
        }

        PickerEmptyText = PickerResults.Count > 0
            ? null
            : _connections.Count == 0
                ? "There are no saved connections yet."
                : "No connection matches that search.";
        OnPropertyChanged(nameof(PickerEmptyText));
    }

    private string GroupNameFor(Guid? groupId) =>
        groupId.HasValue && _groupNames.TryGetValue(groupId.Value, out var name) ? name : "No group";

    private void AddConnection(MultiConfigEditorPickerEntry? entry)
    {
        if (entry is null) return;

        _connectionsById.TryGetValue(entry.Id, out var connection);

        var model = new MultiConfigItem
        {
            ConnectionId = entry.Id,
            Order = Items.Count,
        };

        var item = new MultiConfigItemViewModel(model, connection);
        item.SetMonitors(_monitorSnapshot, _mstscIds);
        item.PropertyChanged += OnItemPropertyChanged;
        Items.Add(item);

        Renumber();
        SelectedItem = item;
        IsPickerOpen = false;
        PickerSelection = null;
        PickerSearch = string.Empty;

        RaiseCommandStates();
        RecomputeMap();
        Validate();
    }

    private void RemoveSelected()
    {
        var item = SelectedItem;
        if (item is null) return;

        var index = Items.IndexOf(item);
        if (index < 0) return;

        item.PropertyChanged -= OnItemPropertyChanged;
        Items.RemoveAt(index);
        Renumber();

        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];

        RaiseCommandStates();
        RecomputeMap();
        Validate();
    }

    private void DuplicateSelected()
    {
        var item = SelectedItem;
        if (item is null) return;

        var clone = item.Model.Clone();
        clone.Id = Guid.NewGuid();
        clone.Order = Items.Count;

        var copy = new MultiConfigItemViewModel(clone, item.Connection);
        copy.SetMonitors(_monitorSnapshot, _mstscIds);
        copy.PropertyChanged += OnItemPropertyChanged;

        var index = Items.IndexOf(item);
        if (index < 0 || index + 1 >= Items.Count) Items.Add(copy);
        else Items.Insert(index + 1, copy);

        Renumber();
        SelectedItem = copy;

        RaiseCommandStates();
        RecomputeMap();
        Validate();
    }

    private bool CanMove(object? parameter, int delta)
    {
        var item = parameter as MultiConfigItemViewModel ?? SelectedItem;
        if (item is null) return false;

        var index = Items.IndexOf(item);
        if (index < 0) return false;

        var target = index + delta;
        return target >= 0 && target < Items.Count;
    }

    private void Move(object? parameter, int delta)
    {
        var item = parameter as MultiConfigItemViewModel ?? SelectedItem;
        if (item is null) return;

        var index = Items.IndexOf(item);
        if (index < 0) return;

        var target = index + delta;
        if (target < 0 || target >= Items.Count) return;

        Items.Move(index, target);
        Renumber();
        SelectedItem = item;

        RaiseCommandStates();
        RecomputeMap();
    }

    /// <summary>Items keep a stable order; every change rewrites it as 0..n-1.</summary>
    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++) Items[i].SetOrder(i);
    }

    private void AssignMonitor(object? parameter)
    {
        var item = SelectedItem;
        if (item is null) return;

        int index;
        switch (parameter)
        {
            case int direct:
                index = direct;
                break;
            case MultiConfigEditorMonitorCell cell:
                index = cell.Index;
                break;
            case string text when int.TryParse(
                text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                index = parsed;
                break;
            default:
                return;
        }

        item.AssignToMonitor(index);
        RecomputeMap();
        Validate();
    }

    private void SetColor(MultiConfigEditorColorSwatch? swatch)
    {
        if (swatch is null) return;

        // "this." because System.Drawing.Color is in scope through the implicit usings that
        // UseWindowsForms brings in; the property is what is meant here.
        this.Color = swatch.Hex;
    }

    private void SyncSwatches()
    {
        foreach (var swatch in ColorSwatches)
            swatch.IsSelected = string.Equals(swatch.Hex, Result.Color, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ live refresh

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleRefresh();

    /// <summary>
    /// Coalesces the bursts of change notifications a single edit produces into one map and
    /// validation pass, at background priority so typing never waits for it.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_refreshPending || _detached) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            // No dispatcher to queue on (design time, or shutting down): do it here.
            try
            {
                RecomputeMap();
                Validate();
            }
            catch (Exception ex)
            {
                AppLog.Error("Refreshing the multi-config editor failed.", ex);
            }
            return;
        }

        _refreshPending = true;
        dispatcher.BeginInvoke(
            new Action(() =>
            {
                _refreshPending = false;
                if (_detached) return;
                try
                {
                    RecomputeMap();
                    Validate();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Refreshing the multi-config editor failed.", ex);
                }
            }),
            DispatcherPriority.Background);
    }

    private void OnMonitorsChanged(object? sender, EventArgs e)
    {
        try
        {
            // The monitor service normally marshals this already, but it falls back to the
            // raising thread when it has no dispatcher of its own - and everything below
            // touches collections that are bound to the UI.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                if (dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(RefreshMonitorsSafely), DispatcherPriority.Background);
                return;
            }

            RefreshMonitors();
        }
        catch (Exception ex)
        {
            AppLog.Error("The multi-config editor could not read the new display layout.", ex);
        }
    }

    private void RefreshMonitorsSafely()
    {
        if (_detached) return;
        try
        {
            RefreshMonitors();
        }
        catch (Exception ex)
        {
            AppLog.Error("The multi-config editor could not read the new display layout.", ex);
        }
    }

    private void RefreshMonitors()
    {
        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = _services.Monitors.GetMonitors();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The display layout could not be read.", ex);
            monitors = Array.Empty<MonitorInfo>();
        }

        _monitorSnapshot = monitors;
        _mstscIds = ResolveMstscIds(monitors);

        Monitors.Clear();
        foreach (var monitor in monitors) Monitors.Add(monitor);

        foreach (var item in Items) item.SetMonitors(_monitorSnapshot, _mstscIds);

        RecomputeMap();
    }

    private IReadOnlyList<int> ResolveMstscIds(IReadOnlyList<MonitorInfo> monitors)
    {
        var indexes = new int[monitors.Count];
        for (var i = 0; i < indexes.Length; i++) indexes[i] = monitors[i].Index;

        try
        {
            var ids = _services.Monitors.ToMstscIds(indexes);
            if (ids.Count >= monitors.Count) return ids;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The mstsc monitor ids could not be resolved.", ex);
        }

        var fallback = new int[monitors.Count];
        for (var i = 0; i < fallback.Length; i++) fallback[i] = i + 1;
        return fallback;
    }

    // ------------------------------------------------------------------ layout map

    /// <summary>
    /// Rebuilds the chips on the layout map. The monitor rectangles themselves are only
    /// rebuilt when the display layout actually changed: they are buttons the user clicks,
    /// and recreating them under the mouse on every keystroke would both flicker and lose
    /// the hover and focus state.
    /// </summary>
    private void RecomputeMap()
    {
        UnplacedItems.Clear();

        var monitors = _monitorSnapshot;
        if (monitors.Count == 0)
        {
            MonitorCells.Clear();
            _cellsByIndex.Clear();
            _mapSignature = string.Empty;
            MapEmptyText = "No displays were detected.";
            foreach (var item in Items) UnplacedItems.Add(item);
            return;
        }

        MapEmptyText = null;

        var signature = MapSignature(monitors);
        if (string.Equals(_mapSignature, signature, StringComparison.Ordinal))
        {
            foreach (var cell in MonitorCells) cell.Chips.Clear();
        }
        else
        {
            _mapSignature = signature;
            BuildMonitorCells(monitors);
        }

        foreach (var item in Items)
        {
            var targets = item.MapMonitors;
            var placed = false;

            foreach (var index in targets)
            {
                if (!_cellsByIndex.TryGetValue(index, out var cell)) continue;
                cell.Chips.Add(new MultiConfigEditorMapChip(
                    item.DisplayName, item.ColorHex, item.Enabled, ReferenceEquals(item, _selectedItem)));
                placed = true;
            }

            if (!placed) UnplacedItems.Add(item);
        }

        foreach (var cell in MonitorCells) cell.ChipsFilled();
    }

    /// <summary>Identifies a display layout, so an unchanged one can be left alone.</summary>
    private string MapSignature(IReadOnlyList<MonitorInfo> monitors)
    {
        var builder = new StringBuilder(monitors.Count * 24);
        builder.Append(MapCanvasWidth.ToString(CultureInfo.InvariantCulture)).Append('x')
               .Append(MapCanvasHeight.ToString(CultureInfo.InvariantCulture));

        foreach (var monitor in monitors)
        {
            builder.Append('|').Append(monitor.Index)
                   .Append(':').Append(monitor.Left)
                   .Append(',').Append(monitor.Top)
                   .Append(',').Append(monitor.Width)
                   .Append(',').Append(monitor.Height)
                   .Append(',').Append(monitor.IsPrimary ? '1' : '0')
                   .Append(',').Append(monitor.ResolutionText);
        }

        return builder.ToString();
    }

    private void BuildMonitorCells(IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorCells.Clear();
        _cellsByIndex.Clear();

        int minLeft = int.MaxValue, minTop = int.MaxValue, maxRight = int.MinValue, maxBottom = int.MinValue;
        foreach (var monitor in monitors)
        {
            if (monitor.Left < minLeft) minLeft = monitor.Left;
            if (monitor.Top < minTop) minTop = monitor.Top;
            if (monitor.Right > maxRight) maxRight = monitor.Right;
            if (monitor.Bottom > maxBottom) maxBottom = monitor.Bottom;
        }

        double virtualWidth = Math.Max(1, maxRight - minLeft);
        double virtualHeight = Math.Max(1, maxBottom - minTop);

        const double gutter = 6;
        var scale = Math.Min(
            (MapCanvasWidth - (gutter * 2)) / virtualWidth,
            (MapCanvasHeight - (gutter * 2)) / virtualHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 0.05;

        var offsetX = (MapCanvasWidth - (virtualWidth * scale)) / 2;
        var offsetY = (MapCanvasHeight - (virtualHeight * scale)) / 2;

        foreach (var monitor in monitors)
        {
            var cell = new MultiConfigEditorMonitorCell(
                monitor.Index,
                (monitor.Index + 1).ToString(CultureInfo.InvariantCulture),
                monitor.IsPrimary ? monitor.ResolutionText + "  primary" : monitor.ResolutionText,
                monitor.IsPrimary,
                offsetX + ((monitor.Left - minLeft) * scale),
                offsetY + ((monitor.Top - minTop) * scale),
                Math.Max(56, (monitor.Width * scale) - 3),
                Math.Max(44, (monitor.Height * scale) - 3));

            _cellsByIndex[monitor.Index] = cell;
            MonitorCells.Add(cell);
        }
    }

    // ------------------------------------------------------------------ validation

    private void Validate()
    {
        var enabled = 0;
        var missing = 0;

        foreach (var item in Items)
        {
            if (item.IsMissing) missing++;
            if (item.Enabled) enabled++;
        }

        string? error = null;

        if (string.IsNullOrWhiteSpace(_name))
        {
            error = "Give this multi-config a name.";
        }
        else if (Items.Count == 0)
        {
            error = "Add at least one connection.";
        }
        else if (missing > 0)
        {
            error = missing == 1
                ? "One item points at a connection that no longer exists. Remove it or pick another connection."
                : $"{missing} items point at connections that no longer exist.";
        }
        else if (enabled == 0)
        {
            error = "At least one item has to be enabled.";
        }

        string? warning = null;
        if (error is null)
        {
            var owners = new Dictionary<int, string>();
            var clashes = new List<string>();

            foreach (var item in Items)
            {
                if (!item.Enabled) continue;

                var monitor = item.FullscreenMonitor;
                if (monitor is null) continue;

                if (owners.TryGetValue(monitor.Value, out var first))
                    clashes.Add($"monitor {monitor.Value + 1} ({first} and {item.DisplayName})");
                else
                    owners[monitor.Value] = item.DisplayName;
            }

            if (clashes.Count > 0)
            {
                warning = "Two sessions will take over the same screen - "
                          + string.Join("; ", clashes)
                          + ". That is usually a mistake, but it is allowed.";
            }
        }

        ErrorText = error;
        WarningText = warning;
        InfoText = error is null
            ? $"{enabled} of {Items.Count} {(Items.Count == 1 ? "item" : "items")} will start"
              + (Result.Sequential ? ", one after another." : ", all at once.")
            : null;

        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        LaunchCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        AssignMonitorCommand.RaiseCanExecuteChanged();
        ClearOverridesCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ save and launch

    private async Task<bool> SaveCoreAsync()
    {
        Validate();
        if (HasError) return false;

        Renumber();

        Result.Name = _name.Trim();
        Result.Description = string.IsNullOrWhiteSpace(_description) ? null : _description.Trim();
        // Only trust the combo once it has been populated, so a failed load cannot wipe the group.
        if (Groups.Count > 0) Result.GroupId = _selectedGroup?.Id;
        Result.ModifiedUtc = DateTime.UtcNow;

        var items = new List<MultiConfigItem>(Items.Count);
        foreach (var item in Items) items.Add(item.Model);
        Result.Items = items;

        IsBusy = true;
        try
        {
            await _services.Store.UpsertMultiConfigAsync(Result).ConfigureAwait(true);
            IsSaved = true;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Saving the multi-config '{Result.Name}' failed.", ex);
            ErrorText = "The multi-config could not be saved: " + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (await SaveCoreAsync().ConfigureAwait(true)) RequestClose?.Invoke(this, true);
    }

    private async Task LaunchNowAsync()
    {
        if (!await SaveCoreAsync().ConfigureAwait(true)) return;

        // Launching opens processes and reads the database, so it never runs on the UI thread,
        // and it outlives this window on purpose.
        var snapshot = Result.Clone();
        var sessions = _services.Sessions;

        _ = Task.Run(async () =>
        {
            try
            {
                await sessions.LaunchMultiAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Launching the multi-config '{snapshot.Name}' failed.", ex);
            }
        });

        RequestClose?.Invoke(this, true);
    }
}
