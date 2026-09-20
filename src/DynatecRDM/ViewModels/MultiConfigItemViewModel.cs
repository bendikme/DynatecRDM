using System.Collections.ObjectModel;
using System.Globalization;
using DynatecRDM.Models;
using PlacementKind = DynatecRDM.Models.WindowPlacementMode;
using ScreenModeKind = DynatecRDM.Models.ScreenMode;

namespace DynatecRDM.ViewModels;

/// <summary>
/// One monitor check box inside the "Selected monitors" field. The stored value is the mstsc
/// monitor id (what the .rdp file carries), not our own list position, so both are kept.
/// </summary>
public sealed class MultiConfigItemMonitorToggle : ObservableObject
{
    private readonly Action<MultiConfigItemMonitorToggle>? _changed;
    private bool _isSelected;

    public MultiConfigItemMonitorToggle(
        int index, int mstscId, string label, bool isSelected, Action<MultiConfigItemMonitorToggle>? changed)
    {
        Index = index;
        MstscId = mstscId;
        Label = label;
        _isSelected = isSelected;
        _changed = changed;
    }

    /// <summary>Our own zero-based monitor index.</summary>
    public int Index { get; }

    /// <summary>The id mstsc uses for this monitor.</summary>
    public int MstscId { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) _changed?.Invoke(this);
        }
    }
}

/// <summary>
/// One connection inside the multi-config being edited: the <see cref="MultiConfigItem"/>
/// together with the <see cref="RdpConnection"/> it points at.
///
/// Every display field follows the same shape: an <c>Inherit*</c> flag that mirrors
/// "the override is null", and a value property that reads the override when there is one and
/// the connection's own setting when there is not. The editor disables the value control while
/// the inherit flag is set, so the inherited value stays visible but read-only.
/// </summary>
public sealed class MultiConfigItemViewModel : ObservableObject
{
    private static readonly int[] NoMonitors = Array.Empty<int>();

    private readonly MultiConfigItem _model;

    /// <summary>Stand-in defaults used while the connection is missing or still loading.</summary>
    private readonly DisplaySettings _fallback = new();

    private RdpConnection? _connection;
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private IReadOnlyList<int> _mstscIds = Array.Empty<int>();
    private string _displayNameText;
    private bool _rebuilding;

    public MultiConfigItemViewModel(MultiConfigItem model, RdpConnection? connection)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _connection = connection;
        _displayNameText = model.DisplayNameOverride ?? string.Empty;
        MonitorToggles = new ObservableCollection<MultiConfigItemMonitorToggle>();
    }

    /// <summary>The item this view model edits in place.</summary>
    public MultiConfigItem Model => _model;

    public Guid ConnectionId => _model.ConnectionId;

    public ObservableCollection<MultiConfigItemMonitorToggle> MonitorToggles { get; }

    private DisplayOverride Over => _model.Display;

    private DisplaySettings Baseline => _connection?.Display ?? _fallback;

    // ------------------------------------------------------------------ identity

    public RdpConnection? Connection
    {
        get => _connection;
        set
        {
            _connection = value;
            RebuildMonitorToggles();
            Raise(
                nameof(Connection), nameof(IsMissing), nameof(DisplayName), nameof(Host),
                nameof(ColorHex), nameof(ConnectionName), nameof(AutoReconnect),
                nameof(ScreenMode), nameof(Placement), nameof(TargetMonitorIndex),
                nameof(UseAllMonitors), nameof(DesktopWidth), nameof(DesktopHeight),
                nameof(ColorDepth), nameof(SmartSizing), nameof(DynamicResolution),
                nameof(DesktopScaleFactor), nameof(CustomLeft), nameof(CustomTop),
                nameof(CustomWidth), nameof(CustomHeight), nameof(AlwaysOnTop));
            Touch();
        }
    }

    public bool IsMissing => _connection is null;

    public string ConnectionName => _connection?.Name ?? "Missing connection";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(_model.DisplayNameOverride) ? ConnectionName : _model.DisplayNameOverride!;

    public string Host => _connection?.FullAddress ?? "This connection no longer exists";

    public string? ColorHex => _connection?.Color;

    public int Order => _model.Order;

    /// <summary>One-based position shown in the list.</summary>
    public int Position => _model.Order + 1;

    /// <summary>Rewrites the stored order; the list renumbers 0..n-1 after every change.</summary>
    public void SetOrder(int order)
    {
        if (_model.Order == order) return;
        _model.Order = order;
        Raise(nameof(Order), nameof(Position));
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value) return;
            _model.Enabled = value;
            Raise(nameof(Enabled));
            Touch();
        }
    }

    /// <summary>Raw text of the display-name box; blank means "use the connection name".</summary>
    public string DisplayNameText
    {
        get => _displayNameText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref _displayNameText, text)) return;
            _model.DisplayNameOverride = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            Raise(nameof(DisplayName));
            Touch();
        }
    }

    /// <summary>Milliseconds to wait after this item before the next one starts.</summary>
    public int DelayMs
    {
        get => _model.DelayMs;
        set
        {
            var clamped = Math.Clamp(value, 0, 600000);
            if (_model.DelayMs == clamped) return;
            _model.DelayMs = clamped;
            Raise(nameof(DelayMs));
        }
    }

    /// <summary>Credential set used instead of the connection's own; null means inherit.</summary>
    public Guid? CredentialSetIdOverride
    {
        get => _model.CredentialSetIdOverride;
        set
        {
            if (_model.CredentialSetIdOverride == value) return;
            _model.CredentialSetIdOverride = value;
            Raise(nameof(CredentialSetIdOverride), nameof(CredentialSelection));
            Touch();
        }
    }

    /// <summary>
    /// What the credential combo binds to. <see cref="Guid.Empty"/> stands for "inherit",
    /// because a selector cannot select an item whose value is null.
    ///
    /// Nullable so that a combo box which momentarily has no matching item cannot push an
    /// unconvertible null at a non-nullable property and leave the binding in error. The
    /// getter never returns null.
    /// </summary>
    public Guid? CredentialSelection
    {
        get => _model.CredentialSetIdOverride ?? Guid.Empty;
        set
        {
            if (value is null) return;
            CredentialSetIdOverride = value.Value == Guid.Empty ? null : value;
        }
    }

    public bool InheritAutoReconnect
    {
        get => _model.AutoReconnectOverride is null;
        set
        {
            if (value == InheritAutoReconnect) return;
            _model.AutoReconnectOverride = value ? null : _connection?.AutoReconnect ?? true;
            Raise(nameof(InheritAutoReconnect), nameof(AutoReconnect));
            Touch();
        }
    }

    public bool AutoReconnect
    {
        get => _model.AutoReconnectOverride ?? _connection?.AutoReconnect ?? true;
        set
        {
            if (_model.AutoReconnectOverride == value) return;
            _model.AutoReconnectOverride = value;
            Raise(nameof(AutoReconnect));
            Touch();
        }
    }

    // ------------------------------------------------------------------ screen mode

    public bool InheritScreenMode
    {
        get => Over.ScreenMode is null;
        set
        {
            if (value == InheritScreenMode) return;
            Over.ScreenMode = value ? null : Baseline.ScreenMode;
            Raise(nameof(InheritScreenMode), nameof(ScreenMode));
            Touch();
        }
    }

    public ScreenModeKind? ScreenMode
    {
        get => Over.ScreenMode ?? Baseline.ScreenMode;
        set
        {
            if (value is null || Over.ScreenMode == value) return;
            Over.ScreenMode = value;
            Raise(nameof(ScreenMode));
            Touch();
        }
    }

    // ------------------------------------------------------------------ placement

    public bool InheritPlacement
    {
        get => Over.Placement is null;
        set
        {
            if (value == InheritPlacement) return;
            Over.Placement = value ? null : Baseline.Placement;
            Raise(nameof(InheritPlacement), nameof(Placement));
            Touch();
        }
    }

    public PlacementKind? Placement
    {
        get => Over.Placement ?? Baseline.Placement;
        set
        {
            if (value is null || Over.Placement == value) return;
            Over.Placement = value;
            Raise(nameof(Placement));
            Touch();
        }
    }

    public bool InheritTargetMonitor
    {
        get => Over.TargetMonitorIndex is null;
        set
        {
            if (value == InheritTargetMonitor) return;
            Over.TargetMonitorIndex = value ? null : Baseline.TargetMonitorIndex;
            Raise(nameof(InheritTargetMonitor), nameof(TargetMonitorIndex));
            Touch();
        }
    }

    /// <summary>
    /// Nullable so a combo box that momentarily has no selection cannot push an unconvertible
    /// null at the model. The getter never returns null.
    /// </summary>
    public int? TargetMonitorIndex
    {
        get => Over.TargetMonitorIndex ?? Baseline.TargetMonitorIndex;
        set
        {
            if (value is null || Over.TargetMonitorIndex == value) return;
            Over.TargetMonitorIndex = value;
            Raise(nameof(TargetMonitorIndex));
            Touch();
        }
    }

    public bool InheritSelectedMonitors
    {
        get => Over.SelectedMonitors is not { Count: > 0 };
        set
        {
            if (value == InheritSelectedMonitors) return;

            if (value)
            {
                Over.SelectedMonitors = null;
            }
            else
            {
                var seed = new List<int>(Baseline.SelectedMonitors);
                if (seed.Count == 0) seed.Add(MstscIdForIndex(TargetMonitorIndex ?? 0));
                Over.SelectedMonitors = seed;
            }

            RebuildMonitorToggles();
            Raise(nameof(InheritSelectedMonitors));
            Touch();
        }
    }

    public bool InheritUseAllMonitors
    {
        get => Over.UseAllMonitors is null;
        set
        {
            if (value == InheritUseAllMonitors) return;
            Over.UseAllMonitors = value ? null : Baseline.UseAllMonitors;
            Raise(nameof(InheritUseAllMonitors), nameof(UseAllMonitors));
            Touch();
        }
    }

    public bool UseAllMonitors
    {
        get => Over.UseAllMonitors ?? Baseline.UseAllMonitors;
        set
        {
            if (Over.UseAllMonitors == value) return;
            Over.UseAllMonitors = value;
            Raise(nameof(UseAllMonitors));
            Touch();
        }
    }

    // ------------------------------------------------------------------ desktop size and quality

    public bool InheritDesktopSize
    {
        get => Over.DesktopWidth is null && Over.DesktopHeight is null;
        set
        {
            if (value == InheritDesktopSize) return;

            if (value)
            {
                Over.DesktopWidth = null;
                Over.DesktopHeight = null;
            }
            else
            {
                Over.DesktopWidth = Baseline.DesktopWidth;
                Over.DesktopHeight = Baseline.DesktopHeight;
            }

            Raise(nameof(InheritDesktopSize), nameof(DesktopWidth), nameof(DesktopHeight));
            Touch();
        }
    }

    public int DesktopWidth
    {
        get => Over.DesktopWidth ?? Baseline.DesktopWidth;
        set
        {
            var clamped = Math.Clamp(value, 0, 32767);
            if (Over.DesktopWidth == clamped) return;
            Over.DesktopWidth = clamped;
            Raise(nameof(DesktopWidth));
            Touch();
        }
    }

    public int DesktopHeight
    {
        get => Over.DesktopHeight ?? Baseline.DesktopHeight;
        set
        {
            var clamped = Math.Clamp(value, 0, 32767);
            if (Over.DesktopHeight == clamped) return;
            Over.DesktopHeight = clamped;
            Raise(nameof(DesktopHeight));
            Touch();
        }
    }

    public bool InheritColorDepth
    {
        get => Over.ColorDepth is null;
        set
        {
            if (value == InheritColorDepth) return;
            Over.ColorDepth = value ? null : Baseline.ColorDepth;
            Raise(nameof(InheritColorDepth), nameof(ColorDepth));
            Touch();
        }
    }

    public int? ColorDepth
    {
        get => Over.ColorDepth ?? Baseline.ColorDepth;
        set
        {
            if (value is null || Over.ColorDepth == value) return;
            Over.ColorDepth = value;
            Raise(nameof(ColorDepth));
            Touch();
        }
    }

    public bool InheritSmartSizing
    {
        get => Over.SmartSizing is null;
        set
        {
            if (value == InheritSmartSizing) return;
            Over.SmartSizing = value ? null : Baseline.SmartSizing;
            Raise(nameof(InheritSmartSizing), nameof(SmartSizing));
            Touch();
        }
    }

    public bool SmartSizing
    {
        get => Over.SmartSizing ?? Baseline.SmartSizing;
        set
        {
            if (Over.SmartSizing == value) return;
            Over.SmartSizing = value;
            Raise(nameof(SmartSizing));
            Touch();
        }
    }

    public bool InheritDynamicResolution
    {
        get => Over.DynamicResolution is null;
        set
        {
            if (value == InheritDynamicResolution) return;
            Over.DynamicResolution = value ? null : Baseline.DynamicResolution;
            Raise(nameof(InheritDynamicResolution), nameof(DynamicResolution));
            Touch();
        }
    }

    public bool DynamicResolution
    {
        get => Over.DynamicResolution ?? Baseline.DynamicResolution;
        set
        {
            if (Over.DynamicResolution == value) return;
            Over.DynamicResolution = value;
            Raise(nameof(DynamicResolution));
            Touch();
        }
    }

    public bool InheritScaleFactor
    {
        get => Over.DesktopScaleFactor is null;
        set
        {
            if (value == InheritScaleFactor) return;
            Over.DesktopScaleFactor = value ? null : Baseline.DesktopScaleFactor;
            Raise(nameof(InheritScaleFactor), nameof(DesktopScaleFactor));
            Touch();
        }
    }

    public int? DesktopScaleFactor
    {
        get => Over.DesktopScaleFactor ?? Baseline.DesktopScaleFactor;
        set
        {
            if (value is null || Over.DesktopScaleFactor == value) return;
            Over.DesktopScaleFactor = value;
            Raise(nameof(DesktopScaleFactor));
            Touch();
        }
    }

    // ------------------------------------------------------------------ custom rectangle

    public bool InheritCustomRect
    {
        get => Over.CustomLeft is null && Over.CustomTop is null
               && Over.CustomWidth is null && Over.CustomHeight is null;
        set
        {
            if (value == InheritCustomRect) return;

            if (value)
            {
                Over.CustomLeft = null;
                Over.CustomTop = null;
                Over.CustomWidth = null;
                Over.CustomHeight = null;
            }
            else
            {
                Over.CustomLeft = Baseline.CustomLeft;
                Over.CustomTop = Baseline.CustomTop;
                Over.CustomWidth = Baseline.CustomWidth;
                Over.CustomHeight = Baseline.CustomHeight;
            }

            Raise(
                nameof(InheritCustomRect), nameof(CustomLeft), nameof(CustomTop),
                nameof(CustomWidth), nameof(CustomHeight));
            Touch();
        }
    }

    public int CustomLeft
    {
        get => Over.CustomLeft ?? Baseline.CustomLeft;
        set
        {
            var clamped = Math.Clamp(value, -32768, 32767);
            if (Over.CustomLeft == clamped) return;
            Over.CustomLeft = clamped;
            Raise(nameof(CustomLeft));
            Touch();
        }
    }

    public int CustomTop
    {
        get => Over.CustomTop ?? Baseline.CustomTop;
        set
        {
            var clamped = Math.Clamp(value, -32768, 32767);
            if (Over.CustomTop == clamped) return;
            Over.CustomTop = clamped;
            Raise(nameof(CustomTop));
            Touch();
        }
    }

    public int CustomWidth
    {
        get => Over.CustomWidth ?? Baseline.CustomWidth;
        set
        {
            var clamped = Math.Clamp(value, 0, 32767);
            if (Over.CustomWidth == clamped) return;
            Over.CustomWidth = clamped;
            Raise(nameof(CustomWidth));
            Touch();
        }
    }

    public int CustomHeight
    {
        get => Over.CustomHeight ?? Baseline.CustomHeight;
        set
        {
            var clamped = Math.Clamp(value, 0, 32767);
            if (Over.CustomHeight == clamped) return;
            Over.CustomHeight = clamped;
            Raise(nameof(CustomHeight));
            Touch();
        }
    }

    public bool InheritAlwaysOnTop
    {
        get => Over.AlwaysOnTop is null;
        set
        {
            if (value == InheritAlwaysOnTop) return;
            Over.AlwaysOnTop = value ? null : Baseline.AlwaysOnTop;
            Raise(nameof(InheritAlwaysOnTop), nameof(AlwaysOnTop));
            Touch();
        }
    }

    public bool AlwaysOnTop
    {
        get => Over.AlwaysOnTop ?? Baseline.AlwaysOnTop;
        set
        {
            if (Over.AlwaysOnTop == value) return;
            Over.AlwaysOnTop = value;
            Raise(nameof(AlwaysOnTop));
            Touch();
        }
    }

    // ------------------------------------------------------------------ derived

    public bool HasOverrides =>
        Over.HasAny || _model.CredentialSetIdOverride.HasValue
        || _model.AutoReconnectOverride.HasValue
        || !string.IsNullOrWhiteSpace(_model.DisplayNameOverride);

    /// <summary>One line describing where this item will land.</summary>
    public string Summary
    {
        get
        {
            if (IsMissing) return "This connection no longer exists";

            var text = (Placement ?? PlacementKind.Default) switch
            {
                PlacementKind.SpecificMonitorFullscreen =>
                    $"Full screen on monitor {(TargetMonitorIndex ?? 0) + 1}",
                PlacementKind.SpecificMonitorMaximized =>
                    $"Maximized on monitor {(TargetMonitorIndex ?? 0) + 1}",
                PlacementKind.SpanAllMonitors => "Spanning every monitor",
                PlacementKind.SelectedMonitors => DescribeSelectedMonitors(),
                PlacementKind.CustomRectangle =>
                    $"Window {CustomWidth} x {CustomHeight} at {CustomLeft}, {CustomTop}",
                _ => (ScreenMode ?? ScreenModeKind.Fullscreen) == ScreenModeKind.Fullscreen
                    ? "Full screen, wherever the connection puts it"
                    : $"Windowed {DesktopWidth} x {DesktopHeight}",
            };

            if (AlwaysOnTop) text += ", always on top";
            return text;
        }
    }

    /// <summary>Monitors this item lands on, for the layout map. Empty means "not placed".</summary>
    public IReadOnlyList<int> MapMonitors
    {
        get
        {
            switch (Placement ?? PlacementKind.Default)
            {
                case PlacementKind.SpecificMonitorFullscreen:
                case PlacementKind.SpecificMonitorMaximized:
                    return new[] { TargetMonitorIndex ?? 0 };

                case PlacementKind.SpanAllMonitors:
                {
                    var all = new int[_monitors.Count];
                    for (var i = 0; i < all.Length; i++) all[i] = _monitors[i].Index;
                    return all;
                }

                case PlacementKind.SelectedMonitors:
                    return EffectiveSelectedIndexes();

                case PlacementKind.CustomRectangle:
                {
                    var index = MonitorContaining(
                        CustomLeft + (CustomWidth / 2), CustomTop + (CustomHeight / 2));
                    return index >= 0 ? new[] { index } : NoMonitors;
                }

                default:
                    return NoMonitors;
            }
        }
    }

    /// <summary>The monitor this item takes over completely, or null when it does not.</summary>
    public int? FullscreenMonitor =>
        (Placement ?? PlacementKind.Default) == PlacementKind.SpecificMonitorFullscreen
            ? TargetMonitorIndex ?? 0
            : null;

    /// <summary>Puts this item full screen on one monitor; used by the layout map.</summary>
    public void AssignToMonitor(int index)
    {
        Over.Placement = PlacementKind.SpecificMonitorFullscreen;
        Over.TargetMonitorIndex = index;
        Raise(
            nameof(InheritPlacement), nameof(Placement),
            nameof(InheritTargetMonitor), nameof(TargetMonitorIndex));
        Touch();
    }

    /// <summary>Drops every override and goes back to the connection's own settings.</summary>
    public void ClearOverrides()
    {
        _model.Display = new DisplayOverride();
        _model.CredentialSetIdOverride = null;
        _model.AutoReconnectOverride = null;
        _model.DisplayNameOverride = null;
        _displayNameText = string.Empty;
        RebuildMonitorToggles();

        Raise(
            nameof(DisplayNameText), nameof(DisplayName),
            nameof(CredentialSetIdOverride), nameof(CredentialSelection),
            nameof(InheritAutoReconnect), nameof(AutoReconnect),
            nameof(InheritScreenMode), nameof(ScreenMode),
            nameof(InheritPlacement), nameof(Placement),
            nameof(InheritTargetMonitor), nameof(TargetMonitorIndex),
            nameof(InheritSelectedMonitors), nameof(InheritUseAllMonitors), nameof(UseAllMonitors),
            nameof(InheritDesktopSize), nameof(DesktopWidth), nameof(DesktopHeight),
            nameof(InheritColorDepth), nameof(ColorDepth),
            nameof(InheritSmartSizing), nameof(SmartSizing),
            nameof(InheritDynamicResolution), nameof(DynamicResolution),
            nameof(InheritScaleFactor), nameof(DesktopScaleFactor),
            nameof(InheritCustomRect), nameof(CustomLeft), nameof(CustomTop),
            nameof(CustomWidth), nameof(CustomHeight),
            nameof(InheritAlwaysOnTop), nameof(AlwaysOnTop));
        Touch();
    }

    /// <summary>Hands the item the current displays; called on load and on MonitorsChanged.</summary>
    public void SetMonitors(IReadOnlyList<MonitorInfo>? monitors, IReadOnlyList<int>? mstscIds)
    {
        _monitors = monitors ?? (IReadOnlyList<MonitorInfo>)Array.Empty<MonitorInfo>();
        _mstscIds = mstscIds ?? (IReadOnlyList<int>)Array.Empty<int>();
        RebuildMonitorToggles();

        // The monitor combo's item list was just rebuilt underneath it, which drops its
        // selection. Re-announcing the value makes it pick the right row again instead of
        // sitting blank while the model still holds the index.
        Raise(nameof(TargetMonitorIndex), nameof(InheritSelectedMonitors));
        Touch();
    }

    // ------------------------------------------------------------------ internals

    private void Touch(params string[] names)
    {
        if (names.Length > 0) Raise(names);
        Raise(nameof(Summary), nameof(MapMonitors), nameof(HasOverrides));
    }

    private void RebuildMonitorToggles()
    {
        _rebuilding = true;
        try
        {
            MonitorToggles.Clear();

            var ids = Over.SelectedMonitors is { Count: > 0 } chosen ? chosen : Baseline.SelectedMonitors;

            foreach (var monitor in _monitors)
            {
                var id = MstscIdForIndex(monitor.Index);
                MonitorToggles.Add(new MultiConfigItemMonitorToggle(
                    monitor.Index,
                    id,
                    (monitor.Index + 1).ToString(CultureInfo.InvariantCulture),
                    ids.Contains(id),
                    OnMonitorToggled));
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void OnMonitorToggled(MultiConfigItemMonitorToggle toggle)
    {
        if (_rebuilding) return;

        var ids = new List<int>(MonitorToggles.Count);
        foreach (var candidate in MonitorToggles)
            if (candidate.IsSelected) ids.Add(candidate.MstscId);

        Over.SelectedMonitors = ids.Count > 0 ? ids : null;
        Raise(nameof(InheritSelectedMonitors));
        Touch();
    }

    private int MstscIdForIndex(int index)
    {
        for (var i = 0; i < _monitors.Count; i++)
        {
            if (_monitors[i].Index != index) continue;
            return i < _mstscIds.Count ? _mstscIds[i] : index + 1;
        }
        return index + 1;
    }

    private int IndexForMstscId(int id)
    {
        for (var i = 0; i < _mstscIds.Count && i < _monitors.Count; i++)
            if (_mstscIds[i] == id) return _monitors[i].Index;

        // No map handed over yet: mstsc numbers displays from one.
        return _mstscIds.Count == 0 && id > 0 ? id - 1 : -1;
    }

    private List<int> EffectiveSelectedIndexes()
    {
        var ids = Over.SelectedMonitors is { Count: > 0 } chosen ? chosen : Baseline.SelectedMonitors;
        var result = new List<int>(ids.Count);

        foreach (var id in ids)
        {
            var index = IndexForMstscId(id);
            if (index >= 0 && !result.Contains(index)) result.Add(index);
        }

        result.Sort();
        return result;
    }

    private string DescribeSelectedMonitors()
    {
        var indexes = EffectiveSelectedIndexes();
        if (indexes.Count == 0) return "Across the monitors the connection selects";

        var labels = new string[indexes.Count];
        for (var i = 0; i < indexes.Count; i++)
            labels[i] = (indexes[i] + 1).ToString(CultureInfo.InvariantCulture);

        var joined = string.Join(", ", labels);
        return indexes.Count == 1 ? $"Full screen on monitor {joined}" : $"Across monitors {joined}";
    }

    private int MonitorContaining(int x, int y)
    {
        foreach (var monitor in _monitors)
        {
            if (x >= monitor.Left && x < monitor.Right && y >= monitor.Top && y < monitor.Bottom)
                return monitor.Index;
        }
        return -1;
    }
}
