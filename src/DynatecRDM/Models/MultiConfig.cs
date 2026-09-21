namespace DynatecRDM.Models;

/// <summary>
/// Per-item overrides applied on top of a stored connection when it is launched as part of
/// a multi-config. Every property is nullable: null means "inherit from the connection".
/// </summary>
public sealed class DisplayOverride
{
    public ScreenMode? ScreenMode { get; set; }
    public bool? UseAllMonitors { get; set; }
    public List<int>? SelectedMonitors { get; set; }
    public int? DesktopWidth { get; set; }
    public int? DesktopHeight { get; set; }
    public int? ColorDepth { get; set; }
    public bool? SmartSizing { get; set; }
    public bool? DynamicResolution { get; set; }
    public int? DesktopScaleFactor { get; set; }
    public int? DeviceScaleFactor { get; set; }
    public WindowPlacementMode? Placement { get; set; }
    public int? TargetMonitorIndex { get; set; }
    public int? CustomLeft { get; set; }
    public int? CustomTop { get; set; }
    public int? CustomWidth { get; set; }
    public int? CustomHeight { get; set; }
    public bool? AlwaysOnTop { get; set; }

    /// <summary>True when at least one value is set.</summary>
    public bool HasAny =>
        ScreenMode.HasValue || UseAllMonitors.HasValue || SelectedMonitors is { Count: > 0 } ||
        DesktopWidth.HasValue || DesktopHeight.HasValue || ColorDepth.HasValue ||
        SmartSizing.HasValue || DynamicResolution.HasValue || DesktopScaleFactor.HasValue ||
        DeviceScaleFactor.HasValue || Placement.HasValue || TargetMonitorIndex.HasValue || CustomLeft.HasValue ||
        CustomTop.HasValue || CustomWidth.HasValue || CustomHeight.HasValue || AlwaysOnTop.HasValue;

    /// <summary>Returns a copy of <paramref name="baseline"/> with every set override applied.</summary>
    public DisplaySettings ApplyTo(DisplaySettings baseline)
    {
        var d = baseline.Clone();
        if (ScreenMode.HasValue) d.ScreenMode = ScreenMode.Value;
        if (UseAllMonitors.HasValue) d.UseAllMonitors = UseAllMonitors.Value;
        if (SelectedMonitors is { Count: > 0 }) d.SelectedMonitors = new List<int>(SelectedMonitors);
        if (DesktopWidth.HasValue) d.DesktopWidth = DesktopWidth.Value;
        if (DesktopHeight.HasValue) d.DesktopHeight = DesktopHeight.Value;
        if (ColorDepth.HasValue) d.ColorDepth = ColorDepth.Value;
        if (SmartSizing.HasValue) d.SmartSizing = SmartSizing.Value;
        if (DynamicResolution.HasValue) d.DynamicResolution = DynamicResolution.Value;
        if (DesktopScaleFactor.HasValue) d.DesktopScaleFactor = DesktopScaleFactor.Value;
        if (DeviceScaleFactor.HasValue) d.DeviceScaleFactor = DeviceScaleFactor.Value;
        if (Placement.HasValue) d.Placement = Placement.Value;
        if (TargetMonitorIndex.HasValue) d.TargetMonitorIndex = TargetMonitorIndex.Value;
        if (CustomLeft.HasValue) d.CustomLeft = CustomLeft.Value;
        if (CustomTop.HasValue) d.CustomTop = CustomTop.Value;
        if (CustomWidth.HasValue) d.CustomWidth = CustomWidth.Value;
        if (CustomHeight.HasValue) d.CustomHeight = CustomHeight.Value;
        if (AlwaysOnTop.HasValue) d.AlwaysOnTop = AlwaysOnTop.Value;
        return d;
    }

    /// <summary>
    /// The override that turns <paramref name="baseline"/> into <paramref name="chosen"/>, holding
    /// only what differs, so everything else keeps following the connection. Fields that only mean
    /// something together are kept together: where the session goes (screen mode, placement,
    /// monitors, rectangle), the session size, and what happens on resize. Otherwise a later change
    /// to the connection could pair its new screen mode with the item's old placement.
    /// </summary>
    public static DisplayOverride Between(DisplaySettings baseline, DisplaySettings chosen)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(chosen);

        var result = new DisplayOverride();

        var layoutDiffers =
            baseline.ScreenMode != chosen.ScreenMode
            || baseline.UseAllMonitors != chosen.UseAllMonitors
            || !baseline.SelectedMonitors.SequenceEqual(chosen.SelectedMonitors)
            || baseline.Placement != chosen.Placement
            || baseline.TargetMonitorIndex != chosen.TargetMonitorIndex
            || baseline.CustomLeft != chosen.CustomLeft
            || baseline.CustomTop != chosen.CustomTop
            || baseline.CustomWidth != chosen.CustomWidth
            || baseline.CustomHeight != chosen.CustomHeight;
        if (layoutDiffers)
        {
            result.ScreenMode = chosen.ScreenMode;
            result.UseAllMonitors = chosen.UseAllMonitors;
            result.SelectedMonitors = new List<int>(chosen.SelectedMonitors);
            result.Placement = chosen.Placement;
            result.TargetMonitorIndex = chosen.TargetMonitorIndex;
            result.CustomLeft = chosen.CustomLeft;
            result.CustomTop = chosen.CustomTop;
            result.CustomWidth = chosen.CustomWidth;
            result.CustomHeight = chosen.CustomHeight;
        }

        if (baseline.DesktopWidth != chosen.DesktopWidth || baseline.DesktopHeight != chosen.DesktopHeight)
        {
            result.DesktopWidth = chosen.DesktopWidth;
            result.DesktopHeight = chosen.DesktopHeight;
        }

        if (baseline.SmartSizing != chosen.SmartSizing || baseline.DynamicResolution != chosen.DynamicResolution)
        {
            result.SmartSizing = chosen.SmartSizing;
            result.DynamicResolution = chosen.DynamicResolution;
        }

        if (baseline.ColorDepth != chosen.ColorDepth) result.ColorDepth = chosen.ColorDepth;
        if (baseline.DesktopScaleFactor != chosen.DesktopScaleFactor) result.DesktopScaleFactor = chosen.DesktopScaleFactor;
        if (baseline.DeviceScaleFactor != chosen.DeviceScaleFactor) result.DeviceScaleFactor = chosen.DeviceScaleFactor;
        if (baseline.AlwaysOnTop != chosen.AlwaysOnTop) result.AlwaysOnTop = chosen.AlwaysOnTop;

        return result;
    }

    public DisplayOverride Clone() => new()
    {
        ScreenMode = ScreenMode,
        UseAllMonitors = UseAllMonitors,
        SelectedMonitors = SelectedMonitors is null ? null : new List<int>(SelectedMonitors),
        DesktopWidth = DesktopWidth,
        DesktopHeight = DesktopHeight,
        ColorDepth = ColorDepth,
        SmartSizing = SmartSizing,
        DynamicResolution = DynamicResolution,
        DesktopScaleFactor = DesktopScaleFactor,
        DeviceScaleFactor = DeviceScaleFactor,
        Placement = Placement,
        TargetMonitorIndex = TargetMonitorIndex,
        CustomLeft = CustomLeft,
        CustomTop = CustomTop,
        CustomWidth = CustomWidth,
        CustomHeight = CustomHeight,
        AlwaysOnTop = AlwaysOnTop,
    };
}

/// <summary>One connection inside a multi-config, with its own screen layout.</summary>
public sealed class MultiConfigItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The stored connection this item launches.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>Screen-layout overrides for this item.</summary>
    public DisplayOverride Display { get; set; } = new();

    /// <summary>Use a different credential set than the connection normally would.</summary>
    public Guid? CredentialSetIdOverride { get; set; }

    /// <summary>Override the auto-reconnect behaviour for this item only.</summary>
    public bool? AutoReconnectOverride { get; set; }

    /// <summary>Extra .rdp lines merged on top of the connection's own.</summary>
    public Dictionary<string, string> CustomPropertyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int Order { get; set; }

    /// <summary>Milliseconds to wait after launching this item before starting the next.</summary>
    public int DelayMs { get; set; } = 400;

    public bool Enabled { get; set; } = true;

    /// <summary>Optional label shown instead of the connection name.</summary>
    public string? DisplayNameOverride { get; set; }

    public MultiConfigItem Clone() => new()
    {
        Id = Id,
        ConnectionId = ConnectionId,
        Display = Display.Clone(),
        CredentialSetIdOverride = CredentialSetIdOverride,
        AutoReconnectOverride = AutoReconnectOverride,
        CustomPropertyOverrides = new Dictionary<string, string>(CustomPropertyOverrides, StringComparer.OrdinalIgnoreCase),
        Order = Order,
        DelayMs = DelayMs,
        Enabled = Enabled,
        DisplayNameOverride = DisplayNameOverride,
    };
}

/// <summary>A named set of connections launched together into a predefined screen layout.</summary>
public sealed class MultiConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? GroupId { get; set; }
    public string? Color { get; set; }

    public List<MultiConfigItem> Items { get; set; } = new();

    /// <summary>Launch items one after another (true) or all at once (false).</summary>
    public bool Sequential { get; set; } = true;

    /// <summary>Milliseconds to wait before the first item starts.</summary>
    public int InitialDelayMs { get; set; }

    /// <summary>Close every session in the set when any one of them is closed.</summary>
    public bool CloseTogether { get; set; }

    public bool Favorite { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLaunchedUtc { get; set; }
    public int LaunchCount { get; set; }

    public MultiConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        GroupId = GroupId,
        Color = Color,
        Items = Items.Select(i => i.Clone()).ToList(),
        Sequential = Sequential,
        InitialDelayMs = InitialDelayMs,
        CloseTogether = CloseTogether,
        Favorite = Favorite,
        SortOrder = SortOrder,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
        LastLaunchedUtc = LastLaunchedUtc,
        LaunchCount = LaunchCount,
    };
}
