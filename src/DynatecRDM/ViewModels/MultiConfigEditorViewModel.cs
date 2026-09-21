using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;

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

    /// <summary>The picker's second line: the address and the group.</summary>
    public string HostAndGroup => UiLanguage.Format(Strings.Multi_Picker_HostAndGroup, Host, GroupName);
}

/// <summary>
/// Edits a <see cref="MultiConfig"/>: a named set of connections launched together, each with
/// its own screen placement layered on top of the connection's own display settings.
///
/// One map shows every item where it will open. The selected item's display editor draws the
/// monitors and, for a window at a chosen position, the rectangle that is dragged; every item is
/// drawn over them as a <see cref="MapShape"/> whose name selects it.
///
/// The work is done on a clone, so nothing reaches the store until Save or Launch now.
/// </summary>
public sealed class MultiConfigEditorViewModel : ObservableObject
{
    private const int MaxDelayMs = 600000;

    private readonly AppServices _services;
    private readonly Dictionary<Guid, RdpConnection> _connectionsById = new();
    private readonly List<RdpConnection> _connections = new();
    private readonly Dictionary<Guid, string> _groupNames = new();
    private readonly Dictionary<MultiConfigItemViewModel, MapShape> _shapes = new();

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
        MapShapes = new ObservableCollection<MapShape>();
        PickerResults = new ObservableCollection<MultiConfigEditorPickerEntry>();

        ColorSwatches = new[]
        {
            new MultiConfigEditorColorSwatch(null, Strings.Multi_Colour_None),
            new MultiConfigEditorColorSwatch("#2A94FF", Strings.Multi_Colour_Blue),
            new MultiConfigEditorColorSwatch("#3DD68C", Strings.Multi_Colour_Green),
            new MultiConfigEditorColorSwatch("#F5A524", Strings.Multi_Colour_Amber),
            new MultiConfigEditorColorSwatch("#F45B5B", Strings.Multi_Colour_Red),
            new MultiConfigEditorColorSwatch("#A97BFF", Strings.Multi_Colour_Purple),
            new MultiConfigEditorColorSwatch("#29C7C7", Strings.Multi_Colour_Teal),
            new MultiConfigEditorColorSwatch("#FF7FB0", Strings.Multi_Colour_Pink),
            new MultiConfigEditorColorSwatch("#8A94A6", Strings.Multi_Colour_Grey),
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
        SelectItemCommand = new RelayCommand(p =>
        {
            if (p is MultiConfigItemViewModel item) SelectedItem = item;
        });
        ClearOverridesCommand = new RelayCommand(
            () => SelectedItem?.ClearOverrides(),
            () => SelectedItem is not null);
        SetColorCommand = new RelayCommand(p => SetColor(p as MultiConfigEditorColorSwatch));

        Validate();
    }

    /// <summary>The multi-config being edited. It is a clone until Save writes it through.</summary>
    public MultiConfig Result { get; }

    /// <summary>Raised with true when the editor saved, false when the user cancelled.</summary>
    public event EventHandler<bool>? RequestClose;

    public bool IsNew { get; }

    public bool IsSaved { get; private set; }

    public string HeaderText => IsNew ? Strings.Multi_Header_New : Strings.Multi_Header_Edit;

    // ------------------------------------------------------------------ collections

    public ObservableCollection<MultiConfigItemViewModel> Items { get; }
    public ObservableCollection<MultiConfigEditorGroupOption> Groups { get; }
    public ObservableCollection<MultiConfigEditorCredentialOption> CredentialOptions { get; }
    public ObservableCollection<MultiConfigEditorPickerEntry> PickerResults { get; }
    public IReadOnlyList<MultiConfigEditorColorSwatch> ColorSwatches { get; }

    /// <summary>Every item of the set, drawn where it will open, over the selected item's map.</summary>
    public ObservableCollection<MapShape> MapShapes { get; }

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
    public RelayCommand SelectItemCommand { get; }
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
            var previous = _selectedItem;
            if (!SetProperty(ref _selectedItem, value)) return;

            if (previous is not null)
            {
                previous.Display.MapChanged -= OnMapChanged;
                previous.IsSelected = false;
            }
            if (value is not null)
            {
                value.IsSelected = true;
                value.Display.MapChanged += OnMapChanged;
                value.Display.EnsureMonitorMap();
            }

            Raise(nameof(HasSelection), nameof(SelectedDisplay));
            RaiseCommandStates();
            RebuildShapes();
        }
    }

    public bool HasSelection => _selectedItem is not null;

    /// <summary>The selected item's display settings: the map and the form both edit these.</summary>
    public DisplayEditorViewModel? SelectedDisplay => _selectedItem?.Display;

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
            Groups.Add(new MultiConfigEditorGroupOption(null, Strings.Multi_NoGroup));
            foreach (var group in groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _groupNames[group.Id] = group.Name;
                Groups.Add(new MultiConfigEditorGroupOption(group.Id, group.Name));
            }

            CredentialOptions.Clear();
            CredentialOptions.Add(
                new MultiConfigEditorCredentialOption(Guid.Empty, Strings.Multi_Credential_UseConnections));

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
                    new MultiConfigEditorCredentialOption(id.Value, Strings.Multi_Credential_Deleted));
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
            RebuildShapes();
            Validate();
        }
        catch (Exception ex)
        {
            AppLog.Error("The multi-config editor could not load its data.", ex);
            ErrorText = UiLanguage.Format(Strings.Multi_Error_Load, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildItems()
    {
        foreach (var existing in Items) existing.Dispose();
        Items.Clear();

        foreach (var model in Result.Items.OrderBy(static i => i.Order))
        {
            _connectionsById.TryGetValue(model.ConnectionId, out var connection);
            Items.Add(CreateItem(model, connection));
        }

        Renumber();
        SelectedItem = Items.Count > 0 ? Items[0] : null;
        RaiseCommandStates();
    }

    /// <summary>Every item loads its map up front, so selecting one never shows an empty map first.</summary>
    private MultiConfigItemViewModel CreateItem(MultiConfigItem model, RdpConnection? connection)
    {
        var item = new MultiConfigItemViewModel(_services, model, connection, ScheduleRefresh);
        item.Display.EnsureMonitorMap();
        return item;
    }

    /// <summary>Drops the monitor subscriptions; called when the window closes.</summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        if (_selectedItem is not null) _selectedItem.Display.MapChanged -= OnMapChanged;
        foreach (var item in Items) item.Dispose();
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
                ? Strings.Multi_Picker_NoConnections
                : Strings.Multi_Picker_NoMatch;
        OnPropertyChanged(nameof(PickerEmptyText));
    }

    private string GroupNameFor(Guid? groupId) =>
        groupId.HasValue && _groupNames.TryGetValue(groupId.Value, out var name) ? name : Strings.Multi_NoGroup;

    private void AddConnection(MultiConfigEditorPickerEntry? entry)
    {
        if (entry is null) return;

        _connectionsById.TryGetValue(entry.Id, out var connection);

        var model = new MultiConfigItem
        {
            ConnectionId = entry.Id,
            Order = Items.Count,
        };

        var item = CreateItem(model, connection);
        Items.Add(item);

        Renumber();
        SelectedItem = item;
        IsPickerOpen = false;
        PickerSelection = null;
        PickerSearch = string.Empty;

        RaiseCommandStates();
        RebuildShapes();
        Validate();
    }

    private void RemoveSelected()
    {
        var item = SelectedItem;
        if (item is null) return;

        var index = Items.IndexOf(item);
        if (index < 0) return;

        Items.RemoveAt(index);
        Renumber();

        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        item.Dispose();

        RaiseCommandStates();
        RebuildShapes();
        Validate();
    }

    private void DuplicateSelected()
    {
        var item = SelectedItem;
        if (item is null) return;

        var clone = item.Model.Clone();
        clone.Id = Guid.NewGuid();
        clone.Order = Items.Count;

        var copy = CreateItem(clone, item.Connection);

        var index = Items.IndexOf(item);
        if (index < 0 || index + 1 >= Items.Count) Items.Add(copy);
        else Items.Insert(index + 1, copy);

        Renumber();
        SelectedItem = copy;

        RaiseCommandStates();
        RebuildShapes();
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
        RebuildShapes();
    }

    /// <summary>Items keep a stable order; every change rewrites it as 0..n-1.</summary>
    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++) Items[i].SetOrder(i);
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

    /// <summary>
    /// Coalesces the bursts of change notifications a single edit produces into one map and
    /// validation pass, at background priority so typing and dragging never wait for it.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_refreshPending || _detached) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            // No dispatcher to queue on (design time, or shutting down): do it here.
            Refresh();
            return;
        }

        _refreshPending = true;
        dispatcher.BeginInvoke(
            new Action(() =>
            {
                _refreshPending = false;
                if (!_detached) Refresh();
            }),
            DispatcherPriority.Background);
    }

    private void Refresh()
    {
        try
        {
            RebuildShapes();
            Validate();
        }
        catch (Exception ex)
        {
            AppLog.Error("Refreshing the multi-config editor failed.", ex);
        }
    }

    /// <summary>The selected item's map was laid out again - new size, new monitors - so the shapes move with it.</summary>
    private void OnMapChanged(object? sender, EventArgs e)
    {
        try
        {
            RebuildShapes();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Redrawing the multi-config map failed.", ex);
        }
    }

    // ------------------------------------------------------------------ map

    /// <summary>
    /// Draws every item where it will open, in the selected item's map coordinates. Shapes are kept
    /// and moved rather than rebuilt, so a chip under the pointer does not flicker while another
    /// item is dragged. The selected item's own window is the editable rectangle, so it is not
    /// drawn twice.
    /// </summary>
    private void RebuildShapes()
    {
        var map = _selectedItem?.Display;
        if (map is null || !map.HasMonitors)
        {
            MapShapes.Clear();
            _shapes.Clear();
            return;
        }

        var wanted = new List<MapShape>(Items.Count);
        var corners = new List<System.Windows.Point>(Items.Count);

        foreach (var item in Items)
        {
            var selected = ReferenceEquals(item, _selectedItem);
            if (selected && map.ShowCustomRect) continue;
            if (item.Footprint is not { } footprint) continue;

            if (!_shapes.TryGetValue(item, out var shape))
            {
                shape = new MapShape(item, item.ColorHex);
                _shapes[item] = shape;
            }

            var area = map.ToMap(footprint);
            shape.X = area.X;
            shape.Y = area.Y;
            shape.W = area.Width;
            shape.H = area.Height;
            shape.Number = item.Position.ToString(CultureInfo.InvariantCulture);
            shape.Label = item.DisplayName;
            shape.IsSelected = selected;
            shape.IsDimmed = !item.Enabled;

            // Shapes that start at the same corner - two sessions on one monitor - stack their chips.
            var corner = new System.Windows.Point(area.X, area.Y);
            var below = 0;
            foreach (var other in corners)
                if (Math.Abs(other.X - corner.X) < 14 && Math.Abs(other.Y - corner.Y) < 14) below++;
            corners.Add(corner);
            shape.ChipOffset = below * 24;

            wanted.Add(shape);
        }

        foreach (var gone in _shapes.Keys.Where(k => !wanted.Any(w => ReferenceEquals(w.Item, k))).ToList())
            _shapes.Remove(gone);

        for (var i = MapShapes.Count - 1; i >= 0; i--)
            if (!wanted.Contains(MapShapes[i])) MapShapes.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            var at = MapShapes.IndexOf(wanted[i]);
            if (at < 0) MapShapes.Insert(i, wanted[i]);
            else if (at != i) MapShapes.Move(at, i);
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
            error = Strings.Multi_Error_NoName;
        }
        else if (Items.Count == 0)
        {
            error = Strings.Multi_Error_NoItems;
        }
        else if (missing > 0)
        {
            error = UiLanguage.Plural(missing, Strings.Multi_Error_Missing_One, Strings.Multi_Error_Missing_Many);
        }
        else if (enabled == 0)
        {
            error = Strings.Multi_Error_NoneEnabled;
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
                    clashes.Add(UiLanguage.Format(
                        Strings.Multi_Warning_SameScreen_Clash, monitor.Value + 1, first, item.DisplayName));
                else
                    owners[monitor.Value] = item.DisplayName;
            }

            if (clashes.Count > 0)
            {
                warning = UiLanguage.Format(Strings.Multi_Warning_SameScreen, string.Join("; ", clashes));
            }
        }

        ErrorText = error;
        WarningText = warning;
        InfoText = error is null
            ? UiLanguage.Format(
                Items.Count == 1
                    ? (Result.Sequential ? Strings.Multi_Info_Sequential_One : Strings.Multi_Info_Together_One)
                    : (Result.Sequential ? Strings.Multi_Info_Sequential_Many : Strings.Multi_Info_Together_Many),
                enabled, Items.Count)
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
            ErrorText = UiLanguage.Format(Strings.Multi_Error_Save, ex.Message);
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
