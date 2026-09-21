using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Edits one set of display settings: full screen or a window, which monitors, the window
/// rectangle on the monitor map, the session size, what happens when the window is resized, and
/// scaling. The connection editor's Display tab is one of these; each item of a multi-config has
/// its own. Every change is written straight into the <see cref="DisplaySettings"/> it was given,
/// and reported through the callback so the owner can validate and preview.
/// </summary>
public sealed class DisplayEditorViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly Action? _changed;

    private DisplaySettings _display;
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private bool _mapRequested;
    private bool _mapReady;
    private bool _monitorsHooked;
    private bool _disposed;

    private double _mapViewportWidth = 560;
    private double _mapViewportHeight = 240;

    public DisplayEditorViewModel(AppServices services, DisplaySettings display, Action? changed = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _changed = changed;
        _display = display ?? throw new ArgumentNullException(nameof(display));

        ResolutionOptions = BuildResolutionOptions();
        ColorDepthOptions = BuildColorDepthOptions();
        DesktopScaleOptions = BuildDesktopScaleOptions();
        DeviceScaleOptions = BuildDeviceScaleOptions();

        SelectMonitorCommand = new RelayCommand(
            p => Guard(() => SelectMonitor(p as MonitorTile), "Selecting a monitor failed."));

        _customLeftText = string.Empty;
        _customTopText = string.Empty;
        _customWidthText = string.Empty;
        _customHeightText = string.Empty;
        _desktopWidthText = string.Empty;
        _desktopHeightText = string.Empty;
        ReadSettings();
    }

    /// <summary>The settings being edited - the same object the editor was given, changed in place.</summary>
    public DisplaySettings Settings => _display;

    /// <summary>Clicking a monitor tile on the map.</summary>
    public RelayCommand SelectMonitorCommand { get; }

    /// <summary>
    /// Raised after the map was laid out again - new size, new monitors, or a layout that moved the
    /// window. Anything drawn over the map in desktop pixels converts again with <see cref="ToMap"/>.
    /// </summary>
    public event EventHandler? MapChanged;

    /// <summary>What the settings amount to on this machine's monitors.</summary>
    public DisplayLayout Layout => CurrentLayout();

    /// <summary>Starts editing another settings object in place of the current one.</summary>
    public void Load(DisplaySettings display)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _chosenMonitorIndex = null;
        ReadSettings();
        RebuildTiles();
        RaiseDisplay();
        Raise(nameof(Resize), nameof(ColorDepth), nameof(DesktopScaleFactor), nameof(DeviceScaleFactor),
            nameof(AlwaysOnTop), nameof(SelectedResolution), nameof(IsCustomResolution));
        PushCustomRectText();
        PushDesktopSizeText();
        UpdatePlacementPreview();
    }

    private void ReadSettings()
    {
        NormalizeLayout();
        _selectedResolution = MatchResolution(_display.DesktopWidth, _display.DesktopHeight);
        _desktopWidthText = _display.DesktopWidth.ToString(CultureInfo.InvariantCulture);
        _desktopHeightText = _display.DesktopHeight.ToString(CultureInfo.InvariantCulture);
        _customLeftText = _display.CustomLeft.ToString(CultureInfo.InvariantCulture);
        _customTopText = _display.CustomTop.ToString(CultureInfo.InvariantCulture);
        _customWidthText = _display.CustomWidth.ToString(CultureInfo.InvariantCulture);
        _customHeightText = _display.CustomHeight.ToString(CultureInfo.InvariantCulture);
        UpdatePlacementPreview();
    }

    /// <summary>The monitor map host reports its size here so the drawing can scale to fit.</summary>
    public void SetMapViewport(double width, double height)
    {
        if (double.IsNaN(width) || double.IsNaN(height)) return;
        if (width < 40 || height < 40) return;
        if (Math.Abs(width - _mapViewportWidth) < 0.5 && Math.Abs(height - _mapViewportHeight) < 0.5) return;

        _mapViewportWidth = width;
        _mapViewportHeight = height;
        RebuildTiles();
    }

    /// <summary>Loads the monitors for the map, once; the Display tab calls it when first shown.</summary>
    public void EnsureMonitorMap()
    {
        if (_mapRequested) return;
        _mapRequested = true;
        _ = LoadMonitorsAsync();
    }

    /// <summary>A rectangle in desktop pixels, in map coordinates - for drawing other things on the map.</summary>
    public System.Windows.Rect ToMap(PixelRect rect) => new(
        _mapOffsetX + (rect.Left - _mapOriginX) * _mapScale,
        _mapOffsetY + (rect.Top - _mapOriginY) * _mapScale,
        Math.Max(2d, rect.Width * _mapScale),
        Math.Max(2d, rect.Height * _mapScale));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_monitorsHooked) return;
        try
        {
            _services.Monitors.MonitorsChanged -= OnMonitorsChanged;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Detaching the monitor listener failed: {ex.Message}");
        }
        _monitorsHooked = false;
    }

    /// <summary>A change the owner should know about: the preview is refreshed here, the rest is theirs.</summary>
    private void Changed()
    {
        if (_disposed) return;
        UpdatePlacementPreview();
        _changed?.Invoke();
    }

    private void SetModel<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        OnPropertyChanged(name);
        Changed();
    }

    private static void Guard(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn(message, ex);
        }
    }

    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int ParseSignedOrZero(string? text) => TryParseInt(text, out var value) ? value : 0;

    // ....................................................................
    // Display
    //
    // The tab offers only combinations that mean something: full screen or a window first, then
    // where. Screen mode, placement and the multimon flag are kept consistent underneath, and what
    // a choice amounts to on this machine is always read back through DisplayLayout - the same
    // resolver the .rdp writer uses - so the tab cannot describe something the launch will not do.
    // ....................................................................

    /// <summary>Where a full-screen session goes.</summary>
    public enum FullScreenLayout
    {
        OneMonitor,
        SelectedMonitors,
        AllMonitors,
    }

    /// <summary>Where a windowed session goes.</summary>
    public enum WindowLayout
    {
        Automatic,
        Maximized,
        Rectangle,
    }

    /// <summary>The smallest window the rectangle can be dragged down to.</summary>
    private const int MinWindowWidth = 320;
    private const int MinWindowHeight = 240;

    /// <summary>How close, in map pixels, a dragged edge has to come to a monitor edge to snap to it.</summary>
    private const double SnapMapPixels = 8;

    /// <summary>A keyboard nudge of the rectangle, in desktop pixels; Ctrl makes it a single pixel.</summary>
    private const int NudgeStep = 10;

    private WindowPlacementMode _lastFullScreenPlacement = WindowPlacementMode.SpecificMonitorFullscreen;
    private WindowPlacementMode _lastWindowPlacement = WindowPlacementMode.Default;

    /// <summary>
    /// The monitor the user last aimed the session at. "Let Windows place it" cannot say which
    /// monitor, so this carries the choice across it to the next layout that can.
    /// </summary>
    private int? _chosenMonitorIndex;

    /// <summary>Full screen or a window - the first of the two choices.</summary>
    public ScreenMode ScreenMode
    {
        get => IsFullScreenPlacement(_display) ? ScreenMode.Fullscreen : ScreenMode.Windowed;
        set
        {
            if (value == ScreenMode) return;
            ApplyPlacement(value == ScreenMode.Fullscreen ? _lastFullScreenPlacement : _lastWindowPlacement);
        }
    }

    public FullScreenLayout FullScreenChoice
    {
        get => _display.Placement switch
        {
            WindowPlacementMode.SelectedMonitors => FullScreenLayout.SelectedMonitors,
            WindowPlacementMode.SpanAllMonitors => FullScreenLayout.AllMonitors,
            _ => FullScreenLayout.OneMonitor,
        };
        set
        {
            if (IsFullScreen && value == FullScreenChoice) return;
            ApplyPlacement(value switch
            {
                FullScreenLayout.SelectedMonitors => WindowPlacementMode.SelectedMonitors,
                FullScreenLayout.AllMonitors => WindowPlacementMode.SpanAllMonitors,
                _ => WindowPlacementMode.SpecificMonitorFullscreen,
            });
        }
    }

    public WindowLayout WindowChoice
    {
        get => _display.Placement switch
        {
            WindowPlacementMode.SpecificMonitorMaximized => WindowLayout.Maximized,
            WindowPlacementMode.CustomRectangle => WindowLayout.Rectangle,
            _ => WindowLayout.Automatic,
        };
        set
        {
            if (IsWindowed && value == WindowChoice) return;
            ApplyPlacement(value switch
            {
                WindowLayout.Maximized => WindowPlacementMode.SpecificMonitorMaximized,
                WindowLayout.Rectangle => WindowPlacementMode.CustomRectangle,
                _ => WindowPlacementMode.Default,
            });
        }
    }

    public bool IsFullScreen => ScreenMode == ScreenMode.Fullscreen;

    public bool IsWindowed => !IsFullScreen;

    public bool IsCustomRectangle => _display.Placement == WindowPlacementMode.CustomRectangle;

    /// <summary>The rectangle can be dragged only once there is a map to drag it on.</summary>
    public bool CanEditRectangle => IsCustomRectangle && HasMonitors;

    /// <summary>
    /// A multimon session keeps its whole layout when it leaves full screen, so the resolution
    /// cannot follow a window there; the tab says so next to the resize choice.
    /// </summary>
    public bool IsMultiMonitorLayout => CurrentLayout().UsesMultimon;

    /// <summary>What clicking the map does in the current layout.</summary>
    public string MapHint => _display.Placement switch
    {
        WindowPlacementMode.SpecificMonitorFullscreen => Strings.Editor_MapHint_FullScreen,
        WindowPlacementMode.SelectedMonitors => Strings.Editor_Monitors_Selected_Hint,
        WindowPlacementMode.SpanAllMonitors => Strings.Editor_MapHint_All,
        WindowPlacementMode.SpecificMonitorMaximized => Strings.Editor_MapHint_Maximized,
        WindowPlacementMode.CustomRectangle => Strings.Editor_MapHint_Rectangle,
        _ => Strings.Editor_MapHint_Automatic,
    };

    public ObservableCollection<MonitorTile> MonitorTiles { get; } = new();

    private string _monitorSummary = Strings.Editor_Monitors_Reading;
    public string MonitorSummary
    {
        get => _monitorSummary;
        private set => SetProperty(ref _monitorSummary, value);
    }

    private bool _hasMonitors;
    public bool HasMonitors
    {
        get => _hasMonitors;
        private set
        {
            if (SetProperty(ref _hasMonitors, value)) OnPropertyChanged(nameof(CanEditRectangle));
        }
    }

    private bool _showCustomRect;
    public bool ShowCustomRect
    {
        get => _showCustomRect;
        private set => SetProperty(ref _showCustomRect, value);
    }

    private double _customRectX;
    public double CustomRectX
    {
        get => _customRectX;
        private set => SetProperty(ref _customRectX, value);
    }

    private double _customRectY;
    public double CustomRectY
    {
        get => _customRectY;
        private set => SetProperty(ref _customRectY, value);
    }

    private double _customRectW = 1;
    public double CustomRectW
    {
        get => _customRectW;
        private set => SetProperty(ref _customRectW, value);
    }

    private double _customRectH = 1;
    public double CustomRectH
    {
        get => _customRectH;
        private set => SetProperty(ref _customRectH, value);
    }

    /// <summary>The size written inside the rectangle on the map.</summary>
    public string CustomRectCaption => UiLanguage.Format(
        Strings.Editor_Size_Format,
        _display.CustomWidth.ToString(CultureInfo.InvariantCulture),
        _display.CustomHeight.ToString(CultureInfo.InvariantCulture));

    private bool _customRectOffScreen;
    /// <summary>True when part of the typed rectangle is off every monitor.</summary>
    public bool CustomRectOffScreen
    {
        get => _customRectOffScreen;
        private set => SetProperty(ref _customRectOffScreen, value);
    }

    private string _customLeftText;
    public string CustomLeftText
    {
        get => _customLeftText;
        set
        {
            if (!SetProperty(ref _customLeftText, value ?? string.Empty)) return;
            _display.CustomLeft = ParseSignedOrZero(_customLeftText);
            OnCustomRectTyped();
        }
    }

    private string _customTopText;
    public string CustomTopText
    {
        get => _customTopText;
        set
        {
            if (!SetProperty(ref _customTopText, value ?? string.Empty)) return;
            _display.CustomTop = ParseSignedOrZero(_customTopText);
            OnCustomRectTyped();
        }
    }

    private string _customWidthText;
    public string CustomWidthText
    {
        get => _customWidthText;
        set
        {
            if (!SetProperty(ref _customWidthText, value ?? string.Empty)) return;
            if (TryParseInt(_customWidthText, out var w) && w > 0) _display.CustomWidth = w;
            OnCustomRectTyped();
        }
    }

    private string _customHeightText;
    public string CustomHeightText
    {
        get => _customHeightText;
        set
        {
            if (!SetProperty(ref _customHeightText, value ?? string.Empty)) return;
            if (TryParseInt(_customHeightText, out var h) && h > 0) _display.CustomHeight = h;
            OnCustomRectTyped();
        }
    }

    /// <summary>
    /// The window size only means something where a window of that size can appear: a window Windows
    /// places, the window a one-monitor full-screen session drops back to, and the size a maximized
    /// window restores to. A rectangle carries its own size, and a multimon session keeps its layout.
    /// </summary>
    public bool ShowWindowSize => CurrentLayout().Kind is DisplayLayoutKind.FullScreen
        or DisplayLayoutKind.Window or DisplayLayoutKind.MaximizedWindow;

    public string WindowSizeLabel => _display.Placement switch
    {
        WindowPlacementMode.SpecificMonitorMaximized => Strings.Editor_WindowSize_Restored,
        WindowPlacementMode.Default => Strings.Editor_WindowSize,
        _ => Strings.Editor_WindowSize_OutsideFullScreen,
    };

    public string WindowSizeHint => _display.Placement switch
    {
        WindowPlacementMode.SpecificMonitorMaximized => Strings.Editor_WindowSize_Maximized_Hint,
        WindowPlacementMode.Default => Strings.Editor_WindowSize_Window_Hint,
        _ => Strings.Editor_WindowSize_FullScreen_Hint,
    };

    public IReadOnlyList<ResolutionOption> ResolutionOptions { get; }

    private ResolutionOption? _selectedResolution;
    public ResolutionOption? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedResolution, value)) return;
            OnPropertyChanged(nameof(IsCustomResolution));

            if (!value.IsCustom)
            {
                _display.DesktopWidth = value.Width;
                _display.DesktopHeight = value.Height;
                PushDesktopSizeText();
            }

            Changed();
        }
    }

    public bool IsCustomResolution => _selectedResolution?.IsCustom == true;

    private string _desktopWidthText;
    public string DesktopWidthText
    {
        get => _desktopWidthText;
        set
        {
            if (!SetProperty(ref _desktopWidthText, value ?? string.Empty)) return;
            if (TryParseInt(_desktopWidthText, out var w) && w > 0) _display.DesktopWidth = w;
            Changed();
        }
    }

    private string _desktopHeightText;
    public string DesktopHeightText
    {
        get => _desktopHeightText;
        set
        {
            if (!SetProperty(ref _desktopHeightText, value ?? string.Empty)) return;
            if (TryParseInt(_desktopHeightText, out var h) && h > 0) _display.DesktopHeight = h;
            Changed();
        }
    }

    /// <summary>Follow the window, scale the picture, or keep the resolution: one choice, never two flags.</summary>
    public ResizeBehavior Resize
    {
        get => DisplayLayout.ResizeOf(_display);
        set
        {
            if (value == Resize) return;
            DisplayLayout.ApplyResize(_display, value);
            OnPropertyChanged();
            Changed();
        }
    }

    public IReadOnlyList<Option> ColorDepthOptions { get; }

    public int ColorDepth
    {
        get => _display.ColorDepth;
        set => SetModel(_display.ColorDepth, value, v => _display.ColorDepth = v);
    }

    public IReadOnlyList<Option> DesktopScaleOptions { get; }

    public int DesktopScaleFactor
    {
        get => _display.DesktopScaleFactor;
        set => SetModel(_display.DesktopScaleFactor, value, v => _display.DesktopScaleFactor = v);
    }

    public IReadOnlyList<Option> DeviceScaleOptions { get; }

    public int DeviceScaleFactor
    {
        get => _display.DeviceScaleFactor;
        set => SetModel(_display.DeviceScaleFactor, value, v => _display.DeviceScaleFactor = v);
    }

    public bool AlwaysOnTop
    {
        get => _display.AlwaysOnTop;
        set => SetModel(_display.AlwaysOnTop, value, v => _display.AlwaysOnTop = v);
    }

    private string _placementPreview = string.Empty;
    public string PlacementPreview
    {
        get => _placementPreview;
        private set => SetProperty(ref _placementPreview, value);
    }


    // ....................................................................
    // Monitor map
    // ....................................................................

    private async Task LoadMonitorsAsync()
    {
        IReadOnlyList<MonitorInfo> monitors = Array.Empty<MonitorInfo>();
        try
        {
            // Enumeration walks the device tree for friendly names, so it stays off the UI thread.
            monitors = await Task.Run(() => _services.Monitors.GetMonitors()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The display editor could not enumerate monitors.", ex);
        }

        if (_disposed) return;

        _monitors = monitors;
        _mapReady = true;

        if (!_monitorsHooked)
        {
            try
            {
                _services.Monitors.MonitorsChanged += OnMonitorsChanged;
                _monitorsHooked = true;
            }
            catch (Exception ex)
            {
                AppLog.Debug_($"Attaching the monitor listener failed: {ex.Message}");
            }
        }

        RebuildTiles();
        RaiseDisplay();
        Changed();
    }

    private void OnMonitorsChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_disposed) return;

            // MonitorService normally marshals this itself, but a refresh raised before its
            // listener exists arrives on a worker thread, and MonitorTiles is bound to the UI.
            if (!_dispatcher.CheckAccess())
            {
                if (!_dispatcher.HasShutdownStarted)
                    _ = _dispatcher.InvokeAsync(() => OnMonitorsChanged(sender, e), DispatcherPriority.Background);
                return;
            }

            _monitors = _services.Monitors.GetMonitors();
            RebuildTiles();
            RaiseDisplay();
            Changed();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Refreshing the monitor map failed.", ex);
        }
    }

    /// <summary>
    /// The monitors to reason about. Before the map has loaded this is the service's cached
    /// snapshot, which costs nothing, so the tab's text is right from the first frame.
    /// </summary>
    public IReadOnlyList<MonitorInfo> KnownMonitors()
    {
        if (_monitors.Count > 0) return _monitors;
        try
        {
            return _services.Monitors.GetMonitors();
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Reading the monitor snapshot failed: {ex.Message}");
            return Array.Empty<MonitorInfo>();
        }
    }

    private DisplayLayout CurrentLayout() => DisplayLayout.Resolve(_display, KnownMonitors());

    private void RebuildTiles()
    {
        if (!_mapReady) return;

        var display = _display;
        MonitorTiles.Clear();

        if (_monitors.Count == 0)
        {
            HasMonitors = false;
            ShowCustomRect = false;
            MonitorSummary = Strings.Editor_Monitors_None;
            return;
        }

        HasMonitors = true;

        var bounds = ScreenGeometry.BoundsOf(_monitors);
        double minX = bounds.Left, minY = bounds.Top, maxX = bounds.Right, maxY = bounds.Bottom;

        // A rectangle stored for a monitor that is no longer there is still drawn, so it can be
        // seen - and dragged back.
        var wantRect = display.Placement == WindowPlacementMode.CustomRectangle
            && display.CustomWidth > 0 && display.CustomHeight > 0;

        if (wantRect)
        {
            minX = Math.Min(minX, display.CustomLeft);
            minY = Math.Min(minY, display.CustomTop);
            maxX = Math.Max(maxX, display.CustomLeft + display.CustomWidth);
            maxY = Math.Max(maxY, display.CustomTop + display.CustomHeight);
        }

        var boundsW = Math.Max(1d, maxX - minX);
        var boundsH = Math.Max(1d, maxY - minY);

        const double pad = 10d;
        var availableW = Math.Max(40d, _mapViewportWidth - pad * 2);
        var availableH = Math.Max(40d, _mapViewportHeight - pad * 2);
        var scale = Math.Min(availableW / boundsW, availableH / boundsH);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 0.05;

        // Kept for the rectangle drag, which works in map pixels and must not rescale mid-drag.
        _mapScale = scale;
        _mapOriginX = minX;
        _mapOriginY = minY;
        _mapOffsetX = pad + (availableW - boundsW * scale) / 2;
        _mapOffsetY = pad + (availableH - boundsH * scale) / 2;

        var layout = CurrentLayout();

        foreach (var m in _monitors)
        {
            var tile = new MonitorTile
            {
                Index = m.Index,
                Title = (m.Index + 1).ToString(CultureInfo.InvariantCulture),
                FriendlyName = m.FriendlyName,
                ResolutionText = m.ResolutionText,
                IsPrimary = m.IsPrimary,
                Description = m.Label
                    + "\n" + UiLanguage.Format(Strings.Editor_Monitor_Tooltip_Position,
                        m.Left.ToString(CultureInfo.InvariantCulture), m.Top.ToString(CultureInfo.InvariantCulture))
                    + "\n" + UiLanguage.Format(Strings.Editor_Monitor_Tooltip_Scale, Math.Round(m.ScaleFactor * 100)),
                X = _mapOffsetX + (m.Left - minX) * scale,
                Y = _mapOffsetY + (m.Top - minY) * scale,
                W = Math.Max(30d, m.Width * scale - 3),
                H = Math.Max(24d, m.Height * scale - 3),
                IsHighlighted = IsTargeted(layout, m),
            };
            MonitorTiles.Add(tile);
        }

        ShowCustomRect = wantRect;
        UpdateCustomRectVisual();
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A rectangle draws itself; every other layout lights up the monitors it lands on.</summary>
    private static bool IsTargeted(DisplayLayout layout, MonitorInfo monitor)
    {
        if (layout.Kind == DisplayLayoutKind.WindowAtRectangle) return false;
        foreach (var m in layout.Monitors)
            if (m.Index == monitor.Index) return true;
        return false;
    }

    private void RefreshHighlights()
    {
        var layout = CurrentLayout();
        foreach (var tile in MonitorTiles)
        {
            var monitor = DisplayLayout.ByIndex(_monitors, tile.Index);
            tile.IsHighlighted = monitor is not null && IsTargeted(layout, monitor);
        }
    }

    private void SelectMonitor(MonitorTile? tile)
    {
        if (tile is null) return;

        var monitors = KnownMonitors();
        var monitor = DisplayLayout.ByIndex(monitors, tile.Index);
        if (monitor is null) return;

        var display = _display;
        switch (display.Placement)
        {
            case WindowPlacementMode.SpecificMonitorFullscreen:
            case WindowPlacementMode.SpecificMonitorMaximized:
                display.TargetMonitorIndex = monitor.Index;
                break;

            case WindowPlacementMode.SelectedMonitors:
            {
                var id = DisplayLayout.MstscIdOf(monitors, monitor);
                if (!display.SelectedMonitors.Remove(id)) display.SelectedMonitors.Add(id);
                OrderSelectedMonitors(monitors);
                break;
            }

            case WindowPlacementMode.CustomRectangle:
            {
                // Clicking another monitor carries the window over to it, keeping its size.
                var work = monitor.WorkArea;
                var rect = CustomRect;
                var moved = new PixelRect(
                    work.Left + (work.Width - rect.Width) / 2,
                    work.Top + (work.Height - rect.Height) / 2,
                    rect.Width,
                    rect.Height);
                SetCustomRect(ScreenGeometry.FitOnScreens(moved, monitors, MinWindowWidth, MinWindowHeight));
                return;
            }

            default:
                return;
        }

        RememberChosenMonitor();
        RefreshHighlights();
        RaiseDisplay();
        Changed();
    }

    /// <summary>
    /// mstsc makes the first id in selectedmonitors the remote session's primary display, so this
    /// machine's primary goes first when it is picked, and the rest follow left to right.
    /// </summary>
    private void OrderSelectedMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        var ids = _display.SelectedMonitors;
        ids.Sort((a, b) =>
        {
            var ma = DisplayLayout.MonitorForMstscId(monitors, a);
            var mb = DisplayLayout.MonitorForMstscId(monitors, b);
            var ka = ma is null ? int.MaxValue : (ma.IsPrimary ? -1 : ma.Index);
            var kb = mb is null ? int.MaxValue : (mb.IsPrimary ? -1 : mb.Index);
            return ka.CompareTo(kb);
        });
    }

    /// <summary>
    /// Full screen or a window is decided by the placement alone - never by re-reading
    /// <see cref="DisplaySettings.ScreenMode"/>, which is the value we are in the middle of setting.
    /// Inside the editor every stored connection has been through <see cref="NormalizeLayout"/>, so
    /// "let Windows place it" (Default) is always the windowed choice by the time it is shown.
    /// </summary>
    private static bool IsFullScreenPlacement(DisplaySettings display) => display.Placement switch
    {
        WindowPlacementMode.SpecificMonitorFullscreen => true,
        WindowPlacementMode.SelectedMonitors => true,
        WindowPlacementMode.SpanAllMonitors => true,
        _ => display.UseAllMonitors,
    };

    /// <summary>
    /// Moves to another layout, carrying the monitor over: a session that was going to monitor 2
    /// stays on monitor 2 whether it becomes full screen, maximized or a rectangle. The screen
    /// mode and the multimon flag are set to match, so the stored settings never contradict.
    /// </summary>
    private void ApplyPlacement(WindowPlacementMode placement)
    {
        var display = _display;
        var monitors = KnownMonitors();
        RememberChosenMonitor();
        var from = (_chosenMonitorIndex is { } chosen ? DisplayLayout.ByIndex(monitors, chosen) : null)
            ?? CurrentLayout().Monitor
            ?? DisplayLayout.PrimaryOf(monitors);

        display.Placement = placement;
        display.UseAllMonitors = placement == WindowPlacementMode.SpanAllMonitors;
        display.ScreenMode = IsFullScreenPlacement(display) ? ScreenMode.Fullscreen : ScreenMode.Windowed;

        switch (placement)
        {
            case WindowPlacementMode.SpecificMonitorFullscreen:
            case WindowPlacementMode.SpecificMonitorMaximized:
                if (from is not null) display.TargetMonitorIndex = from.Index;
                break;

            case WindowPlacementMode.SelectedMonitors:
                if (display.SelectedMonitors.Count == 0 && from is not null)
                    display.SelectedMonitors.Add(DisplayLayout.MstscIdOf(monitors, from));
                break;

            case WindowPlacementMode.CustomRectangle:
                SeedRectangle(from, monitors);
                break;
        }

        if (display.ScreenMode == ScreenMode.Fullscreen) _lastFullScreenPlacement = placement;
        else _lastWindowPlacement = placement;
        RememberChosenMonitor();

        PushCustomRectText();
        RebuildTiles();
        RaiseDisplay();
        Changed();
    }

    /// <summary>Every layout but "let Windows place it" names a monitor; that one is remembered.</summary>
    private void RememberChosenMonitor()
    {
        if (_display.Placement == WindowPlacementMode.Default) return;
        if (CurrentLayout().Monitor is { } monitor) _chosenMonitorIndex = monitor.Index;
    }

    /// <summary>
    /// A rectangle already on the monitor the session was going to is kept. Otherwise one is made
    /// the size the window would have had, in the middle of that monitor.
    /// </summary>
    private void SeedRectangle(MonitorInfo? monitor, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return;

        var display = _display;
        var target = monitor ?? DisplayLayout.PrimaryOf(monitors)!;
        if (ScreenGeometry.IsOnScreens(CustomRect, monitors)
            && ScreenGeometry.MostOverlapping(monitors, CustomRect)?.Index == target.Index)
        {
            return;
        }

        var window = DisplayLayout.Resolve(
            new DisplaySettings
            {
                ScreenMode = ScreenMode.Windowed,
                Placement = WindowPlacementMode.SpecificMonitorMaximized,
                TargetMonitorIndex = target.Index,
                DesktopWidth = display.DesktopWidth,
                DesktopHeight = display.DesktopHeight,
            },
            monitors).WindowRect;

        display.CustomLeft = window.Left;
        display.CustomTop = window.Top;
        display.CustomWidth = window.Width;
        display.CustomHeight = window.Height;
    }

    /// <summary>
    /// Brings stored settings onto the choices the tab offers, without changing what a launch does:
    /// full screen "wherever" is the primary monitor, and a single selected monitor is one monitor.
    /// Only saved if the user saves.
    /// </summary>
    private void NormalizeLayout()
    {
        Normalize(_display, KnownMonitors());

        if (_display.ScreenMode == ScreenMode.Fullscreen) _lastFullScreenPlacement = _display.Placement;
        else _lastWindowPlacement = _display.Placement;
    }

    /// <summary>
    /// What the editor makes of stored settings before showing them. Anyone comparing settings with
    /// what the editor holds normalizes theirs the same way first, so opening something is never
    /// mistaken for changing it.
    /// </summary>
    public static void Normalize(DisplaySettings display, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(display);
        monitors ??= Array.Empty<MonitorInfo>();
        var primary = DisplayLayout.PrimaryOf(monitors);

        switch (display.Placement)
        {
            case WindowPlacementMode.Default when display.UseAllMonitors:
                display.Placement = WindowPlacementMode.SpanAllMonitors;
                break;

            case WindowPlacementMode.Default when display.ScreenMode == ScreenMode.Fullscreen:
                display.Placement = WindowPlacementMode.SpecificMonitorFullscreen;
                display.TargetMonitorIndex = primary?.Index ?? 0;
                break;

            case WindowPlacementMode.SelectedMonitors when monitors.Count > 0:
            {
                var layout = DisplayLayout.Resolve(display, monitors);
                if (!layout.UsesMultimon)
                {
                    display.Placement = WindowPlacementMode.SpecificMonitorFullscreen;
                    display.TargetMonitorIndex = layout.Monitor?.Index ?? primary?.Index ?? 0;
                }
                break;
            }
        }

        display.UseAllMonitors = display.Placement == WindowPlacementMode.SpanAllMonitors;
        display.ScreenMode = IsFullScreenPlacement(display) ? ScreenMode.Fullscreen : ScreenMode.Windowed;
    }

    // ....................................................................
    // The rectangle on the map
    // ....................................................................

    private double _mapScale;
    private double _mapOriginX;
    private double _mapOriginY;
    private double _mapOffsetX;
    private double _mapOffsetY;
    private PixelRect _dragStart;
    private bool _dragging;

    private PixelRect CustomRect => new(
        _display.CustomLeft, _display.CustomTop,
        _display.CustomWidth, _display.CustomHeight);

    /// <summary>Called when the pointer goes down on the rectangle or one of its handles.</summary>
    public void BeginRectangleDrag()
    {
        if (!CanEditRectangle) return;
        _dragStart = CustomRect;
        _dragging = true;
    }

    /// <summary>
    /// The pointer has moved this far, in map pixels, since the drag began. The rectangle follows,
    /// snapping to monitor and taskbar edges, and never leaves the screens.
    /// </summary>
    public void DragRectangle(RectEdges edges, double mapDx, double mapDy)
    {
        if (!_dragging || _mapScale <= 0) return;

        var dx = (int)Math.Round(mapDx / _mapScale);
        var dy = (int)Math.Round(mapDy / _mapScale);
        var snap = (int)Math.Round(SnapMapPixels / _mapScale);

        var next = edges == RectEdges.None
            ? ScreenGeometry.Move(_dragStart, dx, dy, _monitors, snap)
            : ScreenGeometry.Resize(_dragStart, edges, dx, dy, _monitors, MinWindowWidth, MinWindowHeight, snap);

        if (next is { } rect && rect != CustomRect) SetCustomRect(rect, rebuild: false);
    }

    public void EndRectangleDrag()
    {
        if (!_dragging) return;
        _dragging = false;

        // The map may have been drawn wider to show a rectangle that started off the screens.
        RebuildTiles();
    }

    /// <summary>Arrow keys move the rectangle; with Shift they move its right or bottom edge instead.</summary>
    public void NudgeRectangle(int dx, int dy, bool resize, bool fine)
    {
        if (!CanEditRectangle) return;

        var step = fine ? 1 : NudgeStep;
        dx *= step;
        dy *= step;

        var edges = RectEdges.None;
        if (resize)
        {
            if (dx != 0) edges |= RectEdges.Right;
            if (dy != 0) edges |= RectEdges.Bottom;
        }

        var start = CustomRect;
        var next = edges == RectEdges.None
            ? ScreenGeometry.Move(start, dx, dy, _monitors, 0)
            : ScreenGeometry.Resize(start, edges, dx, dy, _monitors, MinWindowWidth, MinWindowHeight, 0);

        if (next is { } rect && rect != start) SetCustomRect(rect);
    }

    /// <summary>A typed rectangle is brought back onto the screens when its field is left.</summary>
    public void CommitCustomRectangle()
    {
        if (!IsCustomRectangle || _monitors.Count == 0) return;

        var rect = CustomRect;
        var fitted = ScreenGeometry.FitOnScreens(
            rect with
            {
                Width = Math.Max(rect.Width, MinWindowWidth),
                Height = Math.Max(rect.Height, MinWindowHeight),
            },
            _monitors, MinWindowWidth, MinWindowHeight);

        if (fitted != rect) SetCustomRect(fitted);
    }

    private void SetCustomRect(PixelRect rect, bool rebuild = true)
    {
        var display = _display;
        display.CustomLeft = rect.Left;
        display.CustomTop = rect.Top;
        display.CustomWidth = rect.Width;
        display.CustomHeight = rect.Height;

        RememberChosenMonitor();
        PushCustomRectText();
        if (rebuild) RebuildTiles();
        else UpdateCustomRectVisual();
        Changed();
    }

    private void OnCustomRectTyped()
    {
        RebuildTiles();
        Changed();
    }

    private void UpdateCustomRectVisual()
    {
        var display = _display;
        CustomRectX = _mapOffsetX + (display.CustomLeft - _mapOriginX) * _mapScale;
        CustomRectY = _mapOffsetY + (display.CustomTop - _mapOriginY) * _mapScale;
        CustomRectW = Math.Max(4d, display.CustomWidth * _mapScale);
        CustomRectH = Math.Max(4d, display.CustomHeight * _mapScale);
        CustomRectOffScreen = IsCustomRectangle && _monitors.Count > 0
            && !ScreenGeometry.IsOnScreens(CustomRect, _monitors);
        OnPropertyChanged(nameof(CustomRectCaption));
    }

    private void PushCustomRectText()
    {
        var display = _display;
        _customLeftText = display.CustomLeft.ToString(CultureInfo.InvariantCulture);
        _customTopText = display.CustomTop.ToString(CultureInfo.InvariantCulture);
        _customWidthText = display.CustomWidth.ToString(CultureInfo.InvariantCulture);
        _customHeightText = display.CustomHeight.ToString(CultureInfo.InvariantCulture);
        Raise(nameof(CustomLeftText), nameof(CustomTopText), nameof(CustomWidthText), nameof(CustomHeightText),
            nameof(CustomRectCaption));
    }

    private void PushDesktopSizeText()
    {
        _desktopWidthText = _display.DesktopWidth.ToString(CultureInfo.InvariantCulture);
        _desktopHeightText = _display.DesktopHeight.ToString(CultureInfo.InvariantCulture);
        Raise(nameof(DesktopWidthText), nameof(DesktopHeightText));
    }

    /// <summary>Everything on the Display tab that is derived from the layout.</summary>
    private void RaiseDisplay() => Raise(
        nameof(ScreenMode), nameof(FullScreenChoice), nameof(WindowChoice),
        nameof(IsFullScreen), nameof(IsWindowed), nameof(IsCustomRectangle), nameof(CanEditRectangle),
        nameof(IsMultiMonitorLayout), nameof(MapHint),
        nameof(ShowWindowSize), nameof(WindowSizeLabel), nameof(WindowSizeHint));

    // ....................................................................
    // Plain-language description of the layout
    // ....................................................................

    private void UpdatePlacementPreview()
    {
        var layout = CurrentLayout();

        var text = DescribeStart(layout) + " " + DescribeSwitch(layout);
        if (_display.AlwaysOnTop) text += " " + Strings.Editor_Outcome_OnTop;

        PlacementPreview = text;
    }

    private string DescribeStart(DisplayLayout layout)
    {
        var monitor = layout.Monitor;
        switch (layout.Kind)
        {
            case DisplayLayoutKind.FullScreen:
                return monitor is null
                    ? Strings.Editor_Outcome_FullScreen_Unknown
                    : UiLanguage.Format(Strings.Editor_Outcome_FullScreen, MonitorNumber(monitor), monitor.FriendlyName, monitor.ResolutionText);

            case DisplayLayoutKind.FullScreenMultiMonitor:
            {
                if (layout.Monitors.Count == 0) return Strings.Editor_Outcome_Across_Unknown;

                if (layout.MstscIds.Count == 0)
                {
                    var area = ScreenGeometry.BoundsOf(layout.Monitors);
                    return UiLanguage.Format(
                        Strings.Editor_Outcome_AcrossAll,
                        layout.Monitors.Count,
                        area.Width.ToString(CultureInfo.InvariantCulture),
                        area.Height.ToString(CultureInfo.InvariantCulture));
                }

                var names = new List<string>(layout.Monitors.Count);
                foreach (var m in layout.Monitors)
                    names.Add(UiLanguage.Format(Strings.Editor_Outcome_Monitor_Named, MonitorNumber(m), m.FriendlyName));
                return UiLanguage.Format(Strings.Editor_Outcome_AcrossSelected, JoinList(names));
            }

            case DisplayLayoutKind.MaximizedWindow:
                return monitor is null
                    ? Strings.Editor_Outcome_Maximized_Unknown
                    : UiLanguage.Format(Strings.Editor_Outcome_Maximized, MonitorNumber(monitor), monitor.FriendlyName, monitor.ResolutionText);

            case DisplayLayoutKind.WindowAtRectangle:
                return UiLanguage.Format(
                    Strings.Editor_Outcome_Rectangle,
                    layout.WindowRect.Left.ToString(CultureInfo.InvariantCulture),
                    layout.WindowRect.Top.ToString(CultureInfo.InvariantCulture),
                    layout.WindowRect.Width.ToString(CultureInfo.InvariantCulture),
                    layout.WindowRect.Height.ToString(CultureInfo.InvariantCulture));

            default:
                return UiLanguage.Format(
                    Strings.Editor_Outcome_Window,
                    layout.DesktopWidth.ToString(CultureInfo.InvariantCulture),
                    layout.DesktopHeight.ToString(CultureInfo.InvariantCulture),
                    monitor is null ? "1" : MonitorNumber(monitor));
        }
    }

    /// <summary>What happens when the window changes size - above all, between full screen and a window.</summary>
    private static string DescribeSwitch(DisplayLayout layout)
    {
        var desktop = (
            layout.DesktopWidth.ToString(CultureInfo.InvariantCulture),
            layout.DesktopHeight.ToString(CultureInfo.InvariantCulture));

        switch (layout.Kind)
        {
            case DisplayLayoutKind.FullScreen:
            {
                var window = (
                    layout.WindowClientWidth.ToString(CultureInfo.InvariantCulture),
                    layout.WindowClientHeight.ToString(CultureInfo.InvariantCulture));
                return layout.Resize switch
                {
                    ResizeBehavior.FollowWindow => UiLanguage.Format(Strings.Editor_Outcome_Switch_Follow, window.Item1, window.Item2),
                    ResizeBehavior.Scale => UiLanguage.Format(Strings.Editor_Outcome_Switch_Scale, window.Item1, window.Item2),
                    _ => UiLanguage.Format(Strings.Editor_Outcome_Switch_Fixed, desktop.Item1, desktop.Item2),
                };
            }

            case DisplayLayoutKind.FullScreenMultiMonitor:
                return layout.Resize == ResizeBehavior.Scale
                    ? Strings.Editor_Outcome_Switch_MultiScaled
                    : Strings.Editor_Outcome_Switch_Multi;

            default:
                return layout.Resize switch
                {
                    ResizeBehavior.FollowWindow => Strings.Editor_Outcome_Resize_Follow,
                    ResizeBehavior.Scale => Strings.Editor_Outcome_Resize_Scale,
                    _ => UiLanguage.Format(Strings.Editor_Outcome_Resize_Fixed, desktop.Item1, desktop.Item2),
                };
        }
    }

    private static string MonitorNumber(MonitorInfo monitor) =>
        (monitor.Index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Joins list items the way the UI language writes a list: "a, b, c" or "a, b og c".</summary>
    internal static string JoinList(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return string.Empty;
        if (items.Count == 1) return items[0];

        var head = string.Join(", ", items.Take(items.Count - 1));
        return UiLanguage.Format(Strings.Editor_List_JoinLast, head, items[^1]);
    }

    private ResolutionOption MatchResolution(int width, int height)
    {
        foreach (var option in ResolutionOptions)
        {
            if (!option.IsCustom && option.Width == width && option.Height == height)
                return option;
        }
        return ResolutionOptions[^1];
    }

    private static IReadOnlyList<ResolutionOption> BuildResolutionOptions() => new[]
    {
        new ResolutionOption("1280 x 720", 1280, 720),
        new ResolutionOption("1366 x 768", 1366, 768),
        new ResolutionOption("1600 x 900", 1600, 900),
        new ResolutionOption("1920 x 1080", 1920, 1080),
        new ResolutionOption("1920 x 1200", 1920, 1200),
        new ResolutionOption("2560 x 1080", 2560, 1080),
        new ResolutionOption("2560 x 1440", 2560, 1440),
        new ResolutionOption("3440 x 1440", 3440, 1440),
        new ResolutionOption("3840 x 2160", 3840, 2160),
        ResolutionOption.Custom(),
    };

    private static IReadOnlyList<Option> BuildColorDepthOptions() => new[]
    {
        new Option(32, Strings.Editor_ColourDepth_32),
        new Option(24, Strings.Editor_ColourDepth_24),
        new Option(16, Strings.Editor_ColourDepth_16),
        new Option(15, Strings.Editor_ColourDepth_15),
    };

    private static IReadOnlyList<Option> BuildDesktopScaleOptions() => new[]
    {
        new Option(100, ScaleLabel(100)),
        new Option(125, ScaleLabel(125)),
        new Option(150, ScaleLabel(150)),
        new Option(175, ScaleLabel(175)),
        new Option(200, ScaleLabel(200)),
        new Option(250, ScaleLabel(250)),
        new Option(300, ScaleLabel(300)),
        new Option(400, ScaleLabel(400)),
        new Option(500, ScaleLabel(500)),
    };

    private static IReadOnlyList<Option> BuildDeviceScaleOptions() => new[]
    {
        new Option(100, ScaleLabel(100)),
        new Option(140, ScaleLabel(140)),
        new Option(180, ScaleLabel(180)),
    };

    /// <summary>A percentage as the UI language writes it: 125% or 125 %.</summary>
    private static string ScaleLabel(int percent) => UiLanguage.Format(Strings.Editor_Scale_Percent, percent);

    /// <summary>One entry of the session-resolution list.</summary>
    public sealed class ResolutionOption
    {
        public ResolutionOption(string label, int width, int height)
        {
            Label = label;
            Width = width;
            Height = height;
        }

        private ResolutionOption(string label)
        {
            Label = label;
            IsCustom = true;
        }

        public static ResolutionOption Custom() => new(Strings.Editor_Resolution_Custom);

        public string Label { get; }
        public int Width { get; }
        public int Height { get; }
        public bool IsCustom { get; }

        public override string ToString() => Label;
    }

    /// <summary>One monitor drawn on the live map, in scaled canvas coordinates.</summary>
    public sealed class MonitorTile : ObservableObject
    {
        public int Index { get; init; }
        public string Title { get; init; } = string.Empty;
        public string FriendlyName { get; init; } = string.Empty;
        public string ResolutionText { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }

        private double _x;
        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        private double _y;
        public double Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        private double _w = 1;
        public double W
        {
            get => _w;
            set => SetProperty(ref _w, value);
        }

        private double _h = 1;
        public double H
        {
            get => _h;
            set => SetProperty(ref _h, value);
        }

        private bool _isHighlighted;
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set => SetProperty(ref _isHighlighted, value);
        }
    }
}
