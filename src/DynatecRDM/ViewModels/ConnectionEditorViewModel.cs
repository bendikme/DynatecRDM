using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Drives the connection editor. Every .rdp capability is projected onto a flat, bindable
/// surface over a <em>clone</em> of the incoming connection, so cancelling really cancels.
/// The monitor map and the .rdp preview are built lazily the first time their tab is shown,
/// which is what keeps the dialog opening instantly.
/// </summary>
public sealed class ConnectionEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>Tab indexes, in the order the XAML declares them.</summary>
    private const int TabDisplay = 1;
    private const int TabAdvanced = 7;

    private readonly AppServices _services;
    private readonly RdpConnection _model;
    private readonly DispatcherTimer _previewDebounce;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private bool _mapRequested;
    private bool _mapReady;
    private bool _previewRequested;
    private bool _monitorsHooked;
    private bool _populating;
    private bool _disposed;

    private double _mapViewportWidth = 560;
    private double _mapViewportHeight = 240;

    public ConnectionEditorViewModel(AppServices services, RdpConnection? existing, Guid? defaultGroupId)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = Dispatcher.CurrentDispatcher;

        IsNew = existing is null;
        _model = existing?.Clone() ?? CreateDefault(services, defaultGroupId);
        Result = _model;

        Normalize(_model);

        _portText = _model.Port.ToString(CultureInfo.InvariantCulture);
        _desktopWidthText = _model.Display.DesktopWidth.ToString(CultureInfo.InvariantCulture);
        _desktopHeightText = _model.Display.DesktopHeight.ToString(CultureInfo.InvariantCulture);
        _customLeftText = _model.Display.CustomLeft.ToString(CultureInfo.InvariantCulture);
        _customTopText = _model.Display.CustomTop.ToString(CultureInfo.InvariantCulture);
        _customWidthText = _model.Display.CustomWidth.ToString(CultureInfo.InvariantCulture);
        _customHeightText = _model.Display.CustomHeight.ToString(CultureInfo.InvariantCulture);
        _maxReconnectAttemptsText = _model.MaxReconnectAttempts.ToString(CultureInfo.InvariantCulture);
        _reconnectDelayText = _model.ReconnectDelaySeconds.ToString(CultureInfo.InvariantCulture);

        CredentialDeliveryOptions = BuildCredentialDeliveryOptions();
        ColorOptions = BuildColorOptions();
        ResolutionOptions = BuildResolutionOptions();
        ColorDepthOptions = BuildColorDepthOptions();
        DesktopScaleOptions = BuildDesktopScaleOptions();
        DeviceScaleOptions = BuildDeviceScaleOptions();
        QualityOptions = BuildQualityOptions();
        AudioPlaybackOptions = BuildAudioPlaybackOptions();
        AudioCaptureOptions = BuildAudioCaptureOptions();
        VideoPlaybackOptions = BuildVideoPlaybackOptions();
        KeyboardHookOptions = BuildKeyboardHookOptions();
        GatewayUsageOptions = BuildGatewayUsageOptions();
        GatewayCredentialSourceOptions = BuildGatewayCredentialSourceOptions();
        AuthLevelOptions = BuildAuthLevelOptions();
        KnownPropertyNames = RdpFileBuilder.KnownPropertyNames;

        _selectedColorOption = MatchColor(_model.Color);
        _selectedResolution = MatchResolution(_model.Display.DesktopWidth, _model.Display.DesktopHeight);

        foreach (var pair in _model.CustomProperties)
            CustomProperties.Add(new CustomProperty(pair.Key, pair.Value, OnCustomPropertyChanged));

        // RelayCommand does not swallow exceptions the way AsyncRelayCommand does, and a
        // command runs straight off a click, so every synchronous body is guarded here.
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsValid);
        CancelCommand = new RelayCommand(() => Guard(Cancel, "Cancelling the connection editor failed."));
        TestCommand = new AsyncRelayCommand(TestAsync, () => !string.IsNullOrWhiteSpace(_model.Host));
        ManageCredentialsCommand = new RelayCommand(ManageCredentials);
        SelectMonitorCommand = new RelayCommand(
            p => Guard(() => SelectMonitor(p as MonitorTile), "Selecting a monitor failed."));
        AddCustomPropertyCommand = new RelayCommand(
            () => Guard(AddCustomProperty, "Adding a custom property failed."));
        RemoveCustomPropertyCommand = new RelayCommand(
            p => Guard(() => RemoveCustomProperty(p as CustomProperty), "Removing a custom property failed."));
        LanPresetCommand = new RelayCommand(
            () => Guard(() => ApplyPerformancePreset(Preset.Lan), "Applying the LAN preset failed."));
        BalancedPresetCommand = new RelayCommand(
            () => Guard(() => ApplyPerformancePreset(Preset.Balanced), "Applying the balanced preset failed."));
        LowBandwidthPresetCommand = new RelayCommand(
            () => Guard(() => ApplyPerformancePreset(Preset.LowBandwidth), "Applying the low-bandwidth preset failed."));

        _previewDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _previewDebounce.Tick += OnPreviewTick;

        UpdateCredentialDeliveryHelp();
        UpdateWatchdogSummary();
        UpdatePlacementPreview();
        Validate();

        _ = LoadListsAsync();
    }

    /// <summary>The edited connection. Only written to the store when the user saves.</summary>
    public RdpConnection Result { get; private set; }

    /// <summary>Raised when the dialog should close: true when the connection was saved.</summary>
    public event EventHandler<bool>? RequestClose;

    public bool IsNew { get; }

    public string WindowTitle => IsNew ? "New connection" : "Edit connection";

    public string HeaderSubtitle => IsNew
        ? "Every Remote Desktop option, including the ones mstsc keeps hidden."
        : "Editing an existing connection. Changes apply the next time it launches.";

    // ....................................................................
    // Tab selection - the map and the preview are built on first sight
    // ....................................................................

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetProperty(ref _selectedTabIndex, value)) return;
            if (value == TabDisplay) EnsureMonitorMap();
            else if (value == TabAdvanced) EnsurePreview();
        }
    }

    // ....................................................................
    // General
    // ....................................................................

    public string Name
    {
        get => _model.Name;
        set
        {
            if (string.Equals(_model.Name, value, StringComparison.Ordinal)) return;
            _model.Name = value ?? string.Empty;
            OnPropertyChanged();
            Invalidate();
        }
    }

    public string? Description
    {
        get => _model.Description;
        set => SetModel(_model.Description, value, v => _model.Description = v);
    }

    public string Host
    {
        get => _model.Host;
        set
        {
            if (string.Equals(_model.Host, value, StringComparison.Ordinal)) return;
            _model.Host = value ?? string.Empty;
            OnPropertyChanged();
            // A credential without a domain is qualified with this host's machine name.
            OnPropertyChanged(nameof(CredentialSummary));
            ClearTestResult();
            TestCommand.RaiseCanExecuteChanged();
            Invalidate();
        }
    }

    private string _portText;
    public string PortText
    {
        get => _portText;
        set
        {
            if (!SetProperty(ref _portText, value ?? string.Empty)) return;
            if (TryParseInt(_portText, out var port)) _model.Port = port;
            ClearTestResult();
            Invalidate();
        }
    }

    public ObservableCollection<GroupOption> Groups { get; } = new();

    private GroupOption? _selectedGroup;
    public GroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            // A null selection is never a user action: it is the combo box reacting to its
            // items arriving. Ignoring it keeps the model's group intact while the list loads.
            if (_populating) return;
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedGroup, value)) return;
            _model.GroupId = value.Id;
            Invalidate();
        }
    }

    public ObservableCollection<CredentialOption> Credentials { get; } = new();

    private CredentialOption? _selectedCredential;
    public CredentialOption? SelectedCredential
    {
        get => _selectedCredential;
        set
        {
            if (_populating) return;
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedCredential, value)) return;
            _model.CredentialSetId = value.Id;
            OnPropertyChanged(nameof(CredentialSummary));
            Invalidate();
        }
    }

    public string CredentialSummary
    {
        get
        {
            var set = FindCredentialSet(_model.CredentialSetId);
            if (set is null) return "Windows will prompt for a user name and password.";

            // Show the name actually sent, which for a credential with no domain includes this
            // connection's machine name.
            var logon = set.GetLogonName(_model.Host);
            return set.HasPassword
                ? $"Signs in as {logon}."
                : $"Signs in as {logon} - no password stored, Windows will ask.";
        }
    }

    public IReadOnlyList<Option> CredentialDeliveryOptions { get; }

    public CredentialDelivery CredentialDeliveryMode
    {
        get => _model.CredentialDelivery;
        set
        {
            if (_model.CredentialDelivery == value) return;
            _model.CredentialDelivery = value;
            OnPropertyChanged();
            UpdateCredentialDeliveryHelp();
            Invalidate();
        }
    }

    private string _credentialDeliveryHelp = string.Empty;
    public string CredentialDeliveryHelp
    {
        get => _credentialDeliveryHelp;
        private set => SetProperty(ref _credentialDeliveryHelp, value);
    }

    public IReadOnlyList<ColorOption> ColorOptions { get; }

    private ColorOption? _selectedColorOption;
    public ColorOption? SelectedColorOption
    {
        get => _selectedColorOption;
        set
        {
            // Ctrl+click clears a list box selection; the palette always keeps one choice.
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedColorOption, value)) return;
            _model.Color = value.Hex;
            Invalidate();
        }
    }

    public string? Tags
    {
        get => _model.Tags;
        set => SetModel(_model.Tags, value, v => _model.Tags = v);
    }

    public bool Favorite
    {
        get => _model.Favorite;
        set => SetModel(_model.Favorite, value, v => _model.Favorite = v);
    }

    private bool _isTesting;
    public bool IsTesting
    {
        get => _isTesting;
        private set => SetProperty(ref _isTesting, value);
    }

    private string? _testResultText;
    public string? TestResultText
    {
        get => _testResultText;
        private set => SetProperty(ref _testResultText, value);
    }

    private bool _testSucceeded;
    public bool TestSucceeded
    {
        get => _testSucceeded;
        private set => SetProperty(ref _testSucceeded, value);
    }

    // ....................................................................
    // Display
    // ....................................................................

    public ScreenMode ScreenMode
    {
        get => _model.Display.ScreenMode;
        set
        {
            if (_model.Display.ScreenMode == value) return;
            _model.Display.ScreenMode = value;
            OnPropertyChanged();
            Invalidate();
        }
    }

    public WindowPlacementMode Placement
    {
        get => _model.Display.Placement;
        set
        {
            if (_model.Display.Placement == value) return;
            _model.Display.Placement = value;
            _model.Display.UseAllMonitors = value == WindowPlacementMode.SpanAllMonitors;
            OnPropertyChanged();
            Raise(nameof(IsCustomRectangle), nameof(IsMultiMonitorSelection), nameof(UseAllMonitors));
            RefreshResolutionFromMonitor();
            RebuildTiles();
            Invalidate();
        }
    }

    public bool IsCustomRectangle => Placement == WindowPlacementMode.CustomRectangle;

    public bool IsMultiMonitorSelection => Placement == WindowPlacementMode.SelectedMonitors;

    public bool UseAllMonitors => _model.Display.UseAllMonitors;

    public ObservableCollection<MonitorTile> MonitorTiles { get; } = new();

    private string _monitorSummary = "Reading the display layout...";
    public string MonitorSummary
    {
        get => _monitorSummary;
        private set => SetProperty(ref _monitorSummary, value);
    }

    private bool _hasMonitors;
    public bool HasMonitors
    {
        get => _hasMonitors;
        private set => SetProperty(ref _hasMonitors, value);
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

    private string _customLeftText;
    public string CustomLeftText
    {
        get => _customLeftText;
        set
        {
            if (!SetProperty(ref _customLeftText, value ?? string.Empty)) return;
            _model.Display.CustomLeft = ParseSignedOrZero(_customLeftText);
            RebuildTiles();
            Invalidate();
        }
    }

    private string _customTopText;
    public string CustomTopText
    {
        get => _customTopText;
        set
        {
            if (!SetProperty(ref _customTopText, value ?? string.Empty)) return;
            _model.Display.CustomTop = ParseSignedOrZero(_customTopText);
            RebuildTiles();
            Invalidate();
        }
    }

    private string _customWidthText;
    public string CustomWidthText
    {
        get => _customWidthText;
        set
        {
            if (!SetProperty(ref _customWidthText, value ?? string.Empty)) return;
            if (TryParseInt(_customWidthText, out var w) && w > 0) _model.Display.CustomWidth = w;
            RebuildTiles();
            Invalidate();
        }
    }

    private string _customHeightText;
    public string CustomHeightText
    {
        get => _customHeightText;
        set
        {
            if (!SetProperty(ref _customHeightText, value ?? string.Empty)) return;
            if (TryParseInt(_customHeightText, out var h) && h > 0) _model.Display.CustomHeight = h;
            RebuildTiles();
            Invalidate();
        }
    }

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

            if (value.IsMatchMonitor)
            {
                RefreshResolutionFromMonitor();
            }
            else if (!value.IsCustom)
            {
                _model.Display.DesktopWidth = value.Width;
                _model.Display.DesktopHeight = value.Height;
                PushDesktopSizeText();
            }

            Invalidate();
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
            if (TryParseInt(_desktopWidthText, out var w) && w > 0) _model.Display.DesktopWidth = w;
            Invalidate();
        }
    }

    private string _desktopHeightText;
    public string DesktopHeightText
    {
        get => _desktopHeightText;
        set
        {
            if (!SetProperty(ref _desktopHeightText, value ?? string.Empty)) return;
            if (TryParseInt(_desktopHeightText, out var h) && h > 0) _model.Display.DesktopHeight = h;
            Invalidate();
        }
    }

    public IReadOnlyList<Option> ColorDepthOptions { get; }

    public int ColorDepth
    {
        get => _model.Display.ColorDepth;
        set => SetModel(_model.Display.ColorDepth, value, v => _model.Display.ColorDepth = v);
    }

    public bool SmartSizing
    {
        get => _model.Display.SmartSizing;
        set => SetModel(_model.Display.SmartSizing, value, v => _model.Display.SmartSizing = v);
    }

    public bool DynamicResolution
    {
        get => _model.Display.DynamicResolution;
        set => SetModel(_model.Display.DynamicResolution, value, v => _model.Display.DynamicResolution = v);
    }

    public IReadOnlyList<Option> DesktopScaleOptions { get; }

    public int DesktopScaleFactor
    {
        get => _model.Display.DesktopScaleFactor;
        set => SetModel(_model.Display.DesktopScaleFactor, value, v => _model.Display.DesktopScaleFactor = v);
    }

    public IReadOnlyList<Option> DeviceScaleOptions { get; }

    public int DeviceScaleFactor
    {
        get => _model.Display.DeviceScaleFactor;
        set => SetModel(_model.Display.DeviceScaleFactor, value, v => _model.Display.DeviceScaleFactor = v);
    }

    public bool AlwaysOnTop
    {
        get => _model.Display.AlwaysOnTop;
        set => SetModel(_model.Display.AlwaysOnTop, value, v => _model.Display.AlwaysOnTop = v);
    }

    private string _placementPreview = string.Empty;
    public string PlacementPreview
    {
        get => _placementPreview;
        private set => SetProperty(ref _placementPreview, value);
    }

    // ....................................................................
    // Experience
    // ....................................................................

    public IReadOnlyList<Option> QualityOptions { get; }

    public ConnectionQuality Quality
    {
        get => _model.Experience.ConnectionQuality;
        set
        {
            if (_model.Experience.ConnectionQuality == value) return;
            _model.Experience.ConnectionQuality = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NetworkAutoDetectEnabled));
            Invalidate();
        }
    }

    /// <summary>mstsc ignores a fixed connection type while it is still probing the link.</summary>
    public bool NetworkAutoDetectEnabled => Quality == ConnectionQuality.AutoDetect;

    public bool NetworkAutoDetect
    {
        get => _model.Experience.NetworkAutoDetect;
        set => SetModel(_model.Experience.NetworkAutoDetect, value, v => _model.Experience.NetworkAutoDetect = v);
    }

    public bool BandwidthAutoDetect
    {
        get => _model.Experience.BandwidthAutoDetect;
        set => SetModel(_model.Experience.BandwidthAutoDetect, value, v => _model.Experience.BandwidthAutoDetect = v);
    }

    public bool Compression
    {
        get => _model.Experience.Compression;
        set => SetModel(_model.Experience.Compression, value, v => _model.Experience.Compression = v);
    }

    public bool PersistentBitmapCache
    {
        get => _model.Experience.PersistentBitmapCache;
        set => SetModel(_model.Experience.PersistentBitmapCache, value, v => _model.Experience.PersistentBitmapCache = v);
    }

    public bool FontSmoothing
    {
        get => _model.Experience.FontSmoothing;
        set => SetModel(_model.Experience.FontSmoothing, value, v => _model.Experience.FontSmoothing = v);
    }

    public bool DesktopComposition
    {
        get => _model.Experience.DesktopComposition;
        set => SetModel(_model.Experience.DesktopComposition, value, v => _model.Experience.DesktopComposition = v);
    }

    public bool ShowWallpaper
    {
        get => _model.Experience.ShowWallpaper;
        set => SetModel(_model.Experience.ShowWallpaper, value, v => _model.Experience.ShowWallpaper = v);
    }

    public bool FullWindowDrag
    {
        get => _model.Experience.FullWindowDrag;
        set => SetModel(_model.Experience.FullWindowDrag, value, v => _model.Experience.FullWindowDrag = v);
    }

    public bool MenuAnimations
    {
        get => _model.Experience.MenuAnimations;
        set => SetModel(_model.Experience.MenuAnimations, value, v => _model.Experience.MenuAnimations = v);
    }

    public bool VisualStyles
    {
        get => _model.Experience.VisualStyles;
        set => SetModel(_model.Experience.VisualStyles, value, v => _model.Experience.VisualStyles = v);
    }

    public bool CursorShadow
    {
        get => _model.Experience.CursorShadow;
        set => SetModel(_model.Experience.CursorShadow, value, v => _model.Experience.CursorShadow = v);
    }

    public bool MstscAutoReconnection
    {
        get => _model.Experience.AutoReconnection;
        set => SetModel(_model.Experience.AutoReconnection, value, v => _model.Experience.AutoReconnection = v);
    }

    public IReadOnlyList<Option> AudioPlaybackOptions { get; }

    public AudioMode AudioPlayback
    {
        get => _model.Experience.AudioMode;
        set => SetModel(_model.Experience.AudioMode, value, v => _model.Experience.AudioMode = v);
    }

    public IReadOnlyList<Option> AudioCaptureOptions { get; }

    public AudioCaptureMode AudioCapture
    {
        get => _model.Experience.AudioCaptureMode;
        set => SetModel(_model.Experience.AudioCaptureMode, value, v => _model.Experience.AudioCaptureMode = v);
    }

    public IReadOnlyList<Option> VideoPlaybackOptions { get; }

    public VideoPlaybackMode VideoPlayback
    {
        get => _model.Experience.VideoPlaybackMode;
        set => SetModel(_model.Experience.VideoPlaybackMode, value, v => _model.Experience.VideoPlaybackMode = v);
    }

    // ....................................................................
    // Local resources
    // ....................................................................

    public bool RedirectClipboard
    {
        get => _model.Redirection.Clipboard;
        set => SetModel(_model.Redirection.Clipboard, value, v => _model.Redirection.Clipboard = v);
    }

    public bool RedirectPrinters
    {
        get => _model.Redirection.Printers;
        set => SetModel(_model.Redirection.Printers, value, v => _model.Redirection.Printers = v);
    }

    public bool RedirectSmartCards
    {
        get => _model.Redirection.SmartCards;
        set => SetModel(_model.Redirection.SmartCards, value, v => _model.Redirection.SmartCards = v);
    }

    public bool RedirectPorts
    {
        get => _model.Redirection.Ports;
        set => SetModel(_model.Redirection.Ports, value, v => _model.Redirection.Ports = v);
    }

    public bool RedirectPnpDevices
    {
        get => _model.Redirection.PnpDevices;
        set => SetModel(_model.Redirection.PnpDevices, value, v => _model.Redirection.PnpDevices = v);
    }

    public bool RedirectWebAuthn
    {
        get => _model.Redirection.WebAuthn;
        set => SetModel(_model.Redirection.WebAuthn, value, v => _model.Redirection.WebAuthn = v);
    }

    public bool RedirectLocation
    {
        get => _model.Redirection.Location;
        set => SetModel(_model.Redirection.Location, value, v => _model.Redirection.Location = v);
    }

    public bool RedirectDrives
    {
        get => _model.Redirection.RedirectDrives;
        set => SetModel(_model.Redirection.RedirectDrives, value, v => _model.Redirection.RedirectDrives = v);
    }

    public string DriveList
    {
        get => _model.Redirection.DriveList;
        set => SetModel(_model.Redirection.DriveList, value ?? string.Empty, v => _model.Redirection.DriveList = v);
    }

    public bool RedirectCameras
    {
        get => _model.Redirection.RedirectCameras;
        set => SetModel(_model.Redirection.RedirectCameras, value, v => _model.Redirection.RedirectCameras = v);
    }

    public string CameraList
    {
        get => _model.Redirection.CameraList;
        set => SetModel(_model.Redirection.CameraList, value ?? string.Empty, v => _model.Redirection.CameraList = v);
    }

    public IReadOnlyList<Option> KeyboardHookOptions { get; }

    public int KeyboardHook
    {
        get => _model.Redirection.KeyboardHook;
        set => SetModel(_model.Redirection.KeyboardHook, value, v => _model.Redirection.KeyboardHook = v);
    }

    // ....................................................................
    // Gateway
    // ....................................................................

    public IReadOnlyList<Option> GatewayUsageOptions { get; }

    public GatewayUsageMethod GatewayUsage
    {
        get => _model.Gateway.UsageMethod;
        set
        {
            if (_model.Gateway.UsageMethod == value) return;
            _model.Gateway.UsageMethod = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GatewayEnabled));
            Invalidate();
        }
    }

    public bool GatewayEnabled => _model.Gateway.UsageMethod != GatewayUsageMethod.DoNotUse;

    public string? GatewayHost
    {
        get => _model.Gateway.HostName;
        set => SetModel(_model.Gateway.HostName, value, v => _model.Gateway.HostName = v);
    }

    public IReadOnlyList<Option> GatewayCredentialSourceOptions { get; }

    public GatewayCredentialSource GatewayCredentials
    {
        get => _model.Gateway.CredentialSource;
        set => SetModel(_model.Gateway.CredentialSource, value, v => _model.Gateway.CredentialSource = v);
    }

    public bool GatewayBypassLocal
    {
        get => _model.Gateway.BypassForLocalAddresses;
        set => SetModel(_model.Gateway.BypassForLocalAddresses, value, v => _model.Gateway.BypassForLocalAddresses = v);
    }

    public ObservableCollection<CredentialOption> GatewayCredentialOptions { get; } = new();

    private CredentialOption? _selectedGatewayCredential;
    public CredentialOption? SelectedGatewayCredential
    {
        get => _selectedGatewayCredential;
        set
        {
            if (_populating) return;
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedGatewayCredential, value)) return;
            _model.Gateway.CredentialSetId = value.Id;
            Invalidate();
        }
    }

    // ....................................................................
    // Security and program
    // ....................................................................

    public IReadOnlyList<Option> AuthLevelOptions { get; }

    public AuthenticationLevel AuthLevel
    {
        get => _model.Security.AuthenticationLevel;
        set => SetModel(_model.Security.AuthenticationLevel, value, v => _model.Security.AuthenticationLevel = v);
    }

    public bool EnableCredSsp
    {
        get => _model.Security.EnableCredSsp;
        set => SetModel(_model.Security.EnableCredSsp, value, v => _model.Security.EnableCredSsp = v);
    }

    public bool PromptForCredentialsOnce
    {
        get => _model.Security.PromptForCredentialsOnce;
        set => SetModel(_model.Security.PromptForCredentialsOnce, value, v => _model.Security.PromptForCredentialsOnce = v);
    }

    public bool AdministrativeSession
    {
        get => _model.Security.AdministrativeSession;
        set => SetModel(_model.Security.AdministrativeSession, value, v => _model.Security.AdministrativeSession = v);
    }

    public bool PublicMode
    {
        get => _model.Security.PublicMode;
        set => SetModel(_model.Security.PublicMode, value, v => _model.Security.PublicMode = v);
    }

    public string? AlternateShell
    {
        get => _model.Security.AlternateShell;
        set => SetModel(_model.Security.AlternateShell, value, v => _model.Security.AlternateShell = v);
    }

    public string? ShellWorkingDirectory
    {
        get => _model.Security.ShellWorkingDirectory;
        set => SetModel(_model.Security.ShellWorkingDirectory, value, v => _model.Security.ShellWorkingDirectory = v);
    }

    public bool RemoteAppMode
    {
        get => _model.Security.RemoteAppMode;
        set
        {
            if (_model.Security.RemoteAppMode == value) return;
            _model.Security.RemoteAppMode = value;
            OnPropertyChanged();
            Invalidate();
        }
    }

    public string? RemoteApplicationName
    {
        get => _model.Security.RemoteApplicationName;
        set => SetModel(_model.Security.RemoteApplicationName, value, v => _model.Security.RemoteApplicationName = v);
    }

    public string? RemoteApplicationProgram
    {
        get => _model.Security.RemoteApplicationProgram;
        set => SetModel(_model.Security.RemoteApplicationProgram, value, v => _model.Security.RemoteApplicationProgram = v);
    }

    public string? RemoteApplicationCmdLine
    {
        get => _model.Security.RemoteApplicationCmdLine;
        set => SetModel(_model.Security.RemoteApplicationCmdLine, value, v => _model.Security.RemoteApplicationCmdLine = v);
    }

    public string? LoadBalanceInfo
    {
        get => _model.Security.LoadBalanceInfo;
        set => SetModel(_model.Security.LoadBalanceInfo, value, v => _model.Security.LoadBalanceInfo = v);
    }

    // ....................................................................
    // Watchdog
    // ....................................................................

    public bool AutoReconnect
    {
        get => _model.AutoReconnect;
        set
        {
            if (_model.AutoReconnect == value) return;
            _model.AutoReconnect = value;
            OnPropertyChanged();
            UpdateWatchdogSummary();
            Invalidate();
        }
    }

    private string _maxReconnectAttemptsText;
    public string MaxReconnectAttemptsText
    {
        get => _maxReconnectAttemptsText;
        set
        {
            if (!SetProperty(ref _maxReconnectAttemptsText, value ?? string.Empty)) return;
            if (TryParseInt(_maxReconnectAttemptsText, out var n) && n >= 0) _model.MaxReconnectAttempts = n;
            UpdateWatchdogSummary();
            Invalidate();
        }
    }

    private string _reconnectDelayText;
    public string ReconnectDelayText
    {
        get => _reconnectDelayText;
        set
        {
            if (!SetProperty(ref _reconnectDelayText, value ?? string.Empty)) return;
            if (TryParseInt(_reconnectDelayText, out var n) && n >= 0) _model.ReconnectDelaySeconds = n;
            UpdateWatchdogSummary();
            Invalidate();
        }
    }

    private string _watchdogSummary = string.Empty;
    public string WatchdogSummary
    {
        get => _watchdogSummary;
        private set => SetProperty(ref _watchdogSummary, value);
    }

    // ....................................................................
    // Advanced
    // ....................................................................

    public ObservableCollection<CustomProperty> CustomProperties { get; } = new();

    public IReadOnlyList<string> KnownPropertyNames { get; }

    private string _rdpPreview = string.Empty;
    public string RdpPreview
    {
        get => _rdpPreview;
        private set => SetProperty(ref _rdpPreview, value);
    }

    // ....................................................................
    // Validation
    // ....................................................................

    private string? _nameError;
    public string? NameError
    {
        get => _nameError;
        private set => SetProperty(ref _nameError, value);
    }

    private string? _hostError;
    public string? HostError
    {
        get => _hostError;
        private set => SetProperty(ref _hostError, value);
    }

    private string? _portError;
    public string? PortError
    {
        get => _portError;
        private set => SetProperty(ref _portError, value);
    }

    private string? _validationSummary;
    public string? ValidationSummary
    {
        get => _validationSummary;
        private set
        {
            if (!SetProperty(ref _validationSummary, value)) return;
            OnPropertyChanged(nameof(HasValidationSummary));
        }
    }

    public bool HasValidationSummary => !string.IsNullOrEmpty(_validationSummary);

    private bool _isValid = true;
    public bool IsValid
    {
        get => _isValid;
        private set
        {
            if (!SetProperty(ref _isValid, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    // ....................................................................
    // Commands
    // ....................................................................

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public RelayCommand ManageCredentialsCommand { get; }
    public RelayCommand SelectMonitorCommand { get; }
    public RelayCommand AddCustomPropertyCommand { get; }
    public RelayCommand RemoveCustomPropertyCommand { get; }
    public RelayCommand LanPresetCommand { get; }
    public RelayCommand BalancedPresetCommand { get; }
    public RelayCommand LowBandwidthPresetCommand { get; }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _previewDebounce.Stop();
        _previewDebounce.Tick -= OnPreviewTick;

        if (_monitorsHooked)
        {
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

        foreach (var row in CustomProperties) row.Detach();
    }

    // ....................................................................
    // Loading
    // ....................................................................

    private async Task LoadListsAsync()
    {
        IReadOnlyList<ConnectionGroup> groups = Array.Empty<ConnectionGroup>();
        IReadOnlyList<CredentialSet> credentials = Array.Empty<CredentialSet>();

        try
        {
            groups = await _services.Store.GetGroupsAsync().ConfigureAwait(true);
            credentials = await _services.Store.GetCredentialSetsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("The connection editor could not read groups or credentials.", ex);
        }

        if (_disposed) return;

        PopulateGroups(groups);
        PopulateCredentials(credentials);
        OnPropertyChanged(nameof(CredentialSummary));
        Invalidate();
    }

    private void PopulateGroups(IReadOnlyList<ConnectionGroup> groups)
    {
        _populating = true;
        try
        {
            Groups.Clear();
            Groups.Add(new GroupOption(null, "(no group)"));
            foreach (var option in FlattenGroups(groups)) Groups.Add(option);
        }
        finally
        {
            _populating = false;
        }

        _selectedGroup = FindOption(Groups, _model.GroupId) ?? Groups[0];
        _model.GroupId = _selectedGroup.Id;
        OnPropertyChanged(nameof(SelectedGroup));
    }

    /// <summary>Depth-first walk that turns the group tree into an indented flat list.</summary>
    private static List<GroupOption> FlattenGroups(IReadOnlyList<ConnectionGroup> groups)
    {
        var result = new List<GroupOption>(groups.Count);
        if (groups.Count == 0) return result;

        var byParent = new Dictionary<Guid, List<ConnectionGroup>>();
        var roots = new List<ConnectionGroup>();
        var known = new HashSet<Guid>();

        foreach (var g in groups) known.Add(g.Id);

        foreach (var g in groups)
        {
            // A group whose parent is missing is treated as a root so it can never disappear.
            if (g.ParentId is { } parent && known.Contains(parent) && parent != g.Id)
            {
                if (!byParent.TryGetValue(parent, out var bucket))
                {
                    bucket = new List<ConnectionGroup>();
                    byParent[parent] = bucket;
                }
                bucket.Add(g);
            }
            else
            {
                roots.Add(g);
            }
        }

        static int Compare(ConnectionGroup a, ConnectionGroup b)
        {
            var order = a.SortOrder.CompareTo(b.SortOrder);
            return order != 0 ? order : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        roots.Sort(Compare);
        foreach (var bucket in byParent.Values) bucket.Sort(Compare);

        var visited = new HashSet<Guid>();
        var stack = new Stack<(ConnectionGroup Group, int Depth)>();
        for (var i = roots.Count - 1; i >= 0; i--) stack.Push((roots[i], 0));

        while (stack.Count > 0)
        {
            var (group, depth) = stack.Pop();
            if (!visited.Add(group.Id)) continue;

            var indent = depth == 0 ? string.Empty : new string(' ', depth * 4);
            result.Add(new GroupOption(group.Id, indent + group.Name));

            if (!byParent.TryGetValue(group.Id, out var children)) continue;
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }

        return result;
    }

    private List<CredentialSet> _credentialSets = new();

    private void PopulateCredentials(IReadOnlyList<CredentialSet> credentials)
    {
        _credentialSets = credentials
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _populating = true;
        try
        {
            Credentials.Clear();
            GatewayCredentialOptions.Clear();

            Credentials.Add(new CredentialOption(null, "(prompt every time)"));
            GatewayCredentialOptions.Add(new CredentialOption(null, "(use the session credential)"));

            foreach (var c in _credentialSets)
            {
                var label = string.IsNullOrWhiteSpace(c.Name) ? c.QualifiedUsername : $"{c.Name} - {c.QualifiedUsername}";
                Credentials.Add(new CredentialOption(c.Id, label));
                GatewayCredentialOptions.Add(new CredentialOption(c.Id, label));
            }
        }
        finally
        {
            _populating = false;
        }

        _selectedCredential = FindOption(Credentials, _model.CredentialSetId) ?? Credentials[0];
        _model.CredentialSetId = _selectedCredential.Id;
        OnPropertyChanged(nameof(SelectedCredential));

        _selectedGatewayCredential = FindOption(GatewayCredentialOptions, _model.Gateway.CredentialSetId)
            ?? GatewayCredentialOptions[0];
        _model.Gateway.CredentialSetId = _selectedGatewayCredential.Id;
        OnPropertyChanged(nameof(SelectedGatewayCredential));
    }

    private CredentialSet? FindCredentialSet(Guid? id)
    {
        if (id is not { } value) return null;
        foreach (var c in _credentialSets)
        {
            if (c.Id == value) return c;
        }
        return null;
    }

    // ....................................................................
    // Monitor map
    // ....................................................................

    private void EnsureMonitorMap()
    {
        if (_mapRequested) return;
        _mapRequested = true;
        _ = LoadMonitorsAsync();
    }

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
            AppLog.Warn("The connection editor could not enumerate monitors.", ex);
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

        RefreshResolutionFromMonitor();
        RebuildTiles();
        UpdatePlacementPreview();
        Invalidate();
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
            RefreshResolutionFromMonitor();
            RebuildTiles();
            UpdatePlacementPreview();
            Invalidate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Refreshing the monitor map failed.", ex);
        }
    }

    private void RebuildTiles()
    {
        if (!_mapReady) return;

        var display = _model.Display;
        MonitorTiles.Clear();

        if (_monitors.Count == 0)
        {
            HasMonitors = false;
            ShowCustomRect = false;
            MonitorSummary = "No monitors were detected.";
            return;
        }

        HasMonitors = true;
        MonitorSummary = _monitors.Count == 1
            ? "1 monitor detected."
            : $"{_monitors.Count} monitors detected. Click one to target it.";

        double minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var m in _monitors)
        {
            if (m.Left < minX) minX = m.Left;
            if (m.Top < minY) minY = m.Top;
            if (m.Right > maxX) maxX = m.Right;
            if (m.Bottom > maxY) maxY = m.Bottom;
        }

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

        var offsetX = pad + (availableW - boundsW * scale) / 2;
        var offsetY = pad + (availableH - boundsH * scale) / 2;

        foreach (var m in _monitors)
        {
            var tile = new MonitorTile
            {
                Index = m.Index,
                Title = (m.Index + 1).ToString(CultureInfo.InvariantCulture),
                FriendlyName = m.FriendlyName,
                ResolutionText = m.ResolutionText,
                IsPrimary = m.IsPrimary,
                Description = $"{m.Label}\nPosition {m.Left}, {m.Top}\nScale {Math.Round(m.ScaleFactor * 100)}%",
                X = offsetX + (m.Left - minX) * scale,
                Y = offsetY + (m.Top - minY) * scale,
                W = Math.Max(30d, m.Width * scale - 3),
                H = Math.Max(24d, m.Height * scale - 3),
                IsHighlighted = IsTargeted(m.Index),
            };
            MonitorTiles.Add(tile);
        }

        if (wantRect)
        {
            CustomRectX = offsetX + (display.CustomLeft - minX) * scale;
            CustomRectY = offsetY + (display.CustomTop - minY) * scale;
            CustomRectW = Math.Max(4d, display.CustomWidth * scale);
            CustomRectH = Math.Max(4d, display.CustomHeight * scale);
            ShowCustomRect = true;
        }
        else
        {
            ShowCustomRect = false;
        }
    }

    private bool IsTargeted(int index) => _model.Display.Placement switch
    {
        WindowPlacementMode.SpanAllMonitors => true,
        WindowPlacementMode.SelectedMonitors => _model.Display.SelectedMonitors.Contains(index),
        WindowPlacementMode.SpecificMonitorFullscreen => index == _model.Display.TargetMonitorIndex,
        WindowPlacementMode.SpecificMonitorMaximized => index == _model.Display.TargetMonitorIndex,
        _ => false,
    };

    private void SelectMonitor(MonitorTile? tile)
    {
        if (tile is null) return;

        var display = _model.Display;
        if (display.Placement == WindowPlacementMode.SelectedMonitors)
        {
            if (!display.SelectedMonitors.Remove(tile.Index)) display.SelectedMonitors.Add(tile.Index);
            display.SelectedMonitors.Sort();
        }
        else
        {
            display.TargetMonitorIndex = tile.Index;
        }

        foreach (var t in MonitorTiles) t.IsHighlighted = IsTargeted(t.Index);

        RefreshResolutionFromMonitor();
        Invalidate();
    }

    /// <summary>Keeps the requested desktop size in step with the monitor the session targets.</summary>
    private void RefreshResolutionFromMonitor()
    {
        if (_selectedResolution?.IsMatchMonitor != true) return;

        var monitor = CurrentTargetMonitor();
        if (monitor is null) return;

        _model.Display.DesktopWidth = monitor.Width;
        _model.Display.DesktopHeight = monitor.Height;
        PushDesktopSizeText();
    }

    private MonitorInfo? CurrentTargetMonitor()
    {
        if (_monitors.Count == 0) return null;

        var index = _model.Display.TargetMonitorIndex;
        foreach (var m in _monitors)
        {
            if (m.Index == index) return m;
        }

        foreach (var m in _monitors)
        {
            if (m.IsPrimary) return m;
        }

        return _monitors[0];
    }

    private void PushDesktopSizeText()
    {
        _desktopWidthText = _model.Display.DesktopWidth.ToString(CultureInfo.InvariantCulture);
        _desktopHeightText = _model.Display.DesktopHeight.ToString(CultureInfo.InvariantCulture);
        Raise(nameof(DesktopWidthText), nameof(DesktopHeightText));
    }

    // ....................................................................
    // Plain-language placement preview
    // ....................................................................

    private void UpdatePlacementPreview()
    {
        var display = _model.Display;
        var sb = new StringBuilder(96);

        switch (display.Placement)
        {
            case WindowPlacementMode.SpecificMonitorFullscreen:
                sb.Append("Full screen on ").Append(DescribeTargetMonitor());
                break;

            case WindowPlacementMode.SpecificMonitorMaximized:
                sb.Append("Maximized window on ").Append(DescribeTargetMonitor());
                break;

            case WindowPlacementMode.SpanAllMonitors:
                sb.Append(_monitors.Count > 0
                    ? $"Full screen spanning all {_monitors.Count} monitors ({DescribeVirtualDesktop()})"
                    : "Full screen spanning every monitor");
                break;

            case WindowPlacementMode.SelectedMonitors:
                sb.Append(DescribeSelectedMonitors());
                break;

            case WindowPlacementMode.CustomRectangle:
                sb.Append("Window at ")
                  .Append(display.CustomLeft.ToString(CultureInfo.InvariantCulture)).Append(", ")
                  .Append(display.CustomTop.ToString(CultureInfo.InvariantCulture))
                  .Append(" sized ")
                  .Append(display.CustomWidth.ToString(CultureInfo.InvariantCulture)).Append('x')
                  .Append(display.CustomHeight.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                sb.Append(display.ScreenMode == Models.ScreenMode.Fullscreen
                    ? "Full screen wherever Windows opens it"
                    : $"Window of {display.DesktopWidth}x{display.DesktopHeight} wherever Windows opens it");
                break;
        }

        if (display.SmartSizing) sb.Append(", scaled to fit the window");
        else if (display.DynamicResolution) sb.Append(", resizing the session with the window");

        if (display.AlwaysOnTop) sb.Append(", kept above other windows");

        sb.Append('.');

        // The builder resolves the screen mode from the placement, so the radio buttons above
        // can be overruled. Saying so here keeps this panel honest about the real outcome.
        var resolved = ResolveScreenMode(display);
        if (resolved != display.ScreenMode)
        {
            sb.Append(resolved == Models.ScreenMode.Fullscreen
                ? " This placement needs a full-screen session, so the screen mode above is switched for you."
                : " This placement needs a windowed session, so the screen mode above is switched for you.");
        }

        PlacementPreview = sb.ToString();
    }

    /// <summary>
    /// Mirrors the rule in <see cref="RdpFileBuilder"/> so the preview never promises
    /// something the generated .rdp file will not do.
    /// </summary>
    private static Models.ScreenMode ResolveScreenMode(DisplaySettings display) => display.Placement switch
    {
        WindowPlacementMode.SpecificMonitorFullscreen => Models.ScreenMode.Fullscreen,
        WindowPlacementMode.SpanAllMonitors => Models.ScreenMode.Fullscreen,
        WindowPlacementMode.SelectedMonitors => Models.ScreenMode.Fullscreen,
        WindowPlacementMode.SpecificMonitorMaximized => Models.ScreenMode.Windowed,
        WindowPlacementMode.CustomRectangle => Models.ScreenMode.Windowed,
        _ => display.ScreenMode == Models.ScreenMode.Windowed && !display.UseAllMonitors
            ? Models.ScreenMode.Windowed
            : Models.ScreenMode.Fullscreen,
    };

    private string DescribeTargetMonitor()
    {
        var monitor = CurrentTargetMonitor();
        var number = _model.Display.TargetMonitorIndex + 1;
        return monitor is null
            ? $"monitor {number.ToString(CultureInfo.InvariantCulture)}"
            : $"monitor {(monitor.Index + 1).ToString(CultureInfo.InvariantCulture)} ({monitor.FriendlyName}, {monitor.ResolutionText})";
    }

    private string DescribeVirtualDesktop()
    {
        if (_monitors.Count == 0) return "size unknown";

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var m in _monitors)
        {
            if (m.Left < minX) minX = m.Left;
            if (m.Top < minY) minY = m.Top;
            if (m.Right > maxX) maxX = m.Right;
            if (m.Bottom > maxY) maxY = m.Bottom;
        }

        return $"{(maxX - minX).ToString(CultureInfo.InvariantCulture)}x{(maxY - minY).ToString(CultureInfo.InvariantCulture)} in total";
    }

    private string DescribeSelectedMonitors()
    {
        var selected = _model.Display.SelectedMonitors;
        if (selected.Count == 0) return "No monitors picked yet - click the ones to use on the map above";

        var names = new List<string>(selected.Count);
        foreach (var index in selected)
        {
            var match = _monitors.FirstOrDefault(m => m.Index == index);
            names.Add(match is null
                ? $"monitor {(index + 1).ToString(CultureInfo.InvariantCulture)}"
                : $"monitor {(match.Index + 1).ToString(CultureInfo.InvariantCulture)} ({match.FriendlyName})");
        }

        return selected.Count == 1
            ? $"Full screen on {names[0]}"
            : $"Full screen spanning {string.Join(", ", names)}";
    }

    private void UpdateWatchdogSummary()
    {
        if (!_model.AutoReconnect)
        {
            WatchdogSummary = "A dropped session stays closed until you launch it again.";
            return;
        }

        var attempts = _model.MaxReconnectAttempts <= 0
            ? "indefinitely"
            : $"up to {_model.MaxReconnectAttempts.ToString(CultureInfo.InvariantCulture)} times";

        var delay = _model.ReconnectDelaySeconds <= 0
            ? "immediately"
            : $"every {_model.ReconnectDelaySeconds.ToString(CultureInfo.InvariantCulture)} seconds";

        WatchdogSummary =
            $"If this session drops, the manager relaunches it {attempts}, retrying {delay}. " +
            "It stands down as soon as you close the session yourself.";
    }

    private void UpdateCredentialDeliveryHelp() => CredentialDeliveryHelp = _model.CredentialDelivery switch
    {
        CredentialDelivery.WindowsVault =>
            "The login is written to the Windows Credential Vault as TERMSRV/" + HostForHelp() +
            " just before launch. Nothing sensitive reaches the .rdp file.",
        CredentialDelivery.EmbeddedInRdpFile =>
            "The password is encrypted with your Windows account key and embedded in the generated .rdp file. " +
            "The file is deleted after launch when shredding is enabled.",
        CredentialDelivery.Both =>
            "Writes the vault entry and embeds the encrypted password. The most reliable option, and the default.",
        _ => "Nothing is stored anywhere. Windows asks for the password each time the session starts.",
    };

    private string HostForHelp()
    {
        var host = _model.Host;
        return string.IsNullOrWhiteSpace(host) ? "<host>" : host.Trim();
    }

    // ....................................................................
    // Custom properties and the .rdp preview
    // ....................................................................

    private void AddCustomProperty()
    {
        var row = new CustomProperty(string.Empty, string.Empty, OnCustomPropertyChanged);
        CustomProperties.Add(row);
        Invalidate();
    }

    private void RemoveCustomProperty(CustomProperty? row)
    {
        if (row is null) return;
        row.Detach();
        CustomProperties.Remove(row);
        Invalidate();
    }

    private void OnCustomPropertyChanged() => Invalidate();

    /// <summary>Folds the editor rows back into the model, dropping blanks and duplicate keys.</summary>
    private void CommitCustomProperties()
    {
        _model.CustomProperties.Clear();
        foreach (var row in CustomProperties)
        {
            var key = row.Key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            _model.CustomProperties[key] = row.Value?.Trim() ?? string.Empty;
        }
    }

    private void EnsurePreview()
    {
        if (_previewRequested) return;
        _previewRequested = true;
        BuildPreview();
    }

    private void Invalidate()
    {
        if (_disposed) return;

        UpdatePlacementPreview();
        Validate();

        if (!_previewRequested) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        try
        {
            _previewDebounce.Stop();
            BuildPreview();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Rendering the .rdp preview failed.", ex);
        }
    }

    private void BuildPreview()
    {
        try
        {
            CommitCustomProperties();

            var credential = FindCredentialSet(_model.CredentialSetId);
            var context = new RdpBuildContext
            {
                Display = _model.Display,
                Credential = credential,
                // The real blob is produced at launch time; a preview never carries a secret.
                EmbedPassword = false,
                Monitors = _monitors.Count > 0 ? _monitors : null,
            };

            var body = _services.RdpBuilder.Build(_model, context);
            var sb = new StringBuilder(body.Length + 256);

            sb.Append("; Preview of the .rdp file DYNATEC RDM will generate for this connection.").AppendLine();
            sb.Append("; Lines starting with ';' are comments and are not written to the real file.").AppendLine();
            sb.AppendLine();
            sb.Append(body);
            if (body.Length > 0 && !body.EndsWith('\n')) sb.AppendLine();

            var embeds = _model.CredentialDelivery is CredentialDelivery.EmbeddedInRdpFile or CredentialDelivery.Both;
            if (embeds && credential is not null && credential.HasPassword)
            {
                sb.AppendLine();
                sb.Append("; password 51:b:<stored>").AppendLine();
                sb.Append("; The encrypted password blob is generated at launch and never shown here.").AppendLine();
            }
            else if (embeds)
            {
                sb.AppendLine();
                sb.Append("; password 51:b:<stored>").AppendLine();
                sb.Append("; No password is stored for the selected credential, so no blob will be written.").AppendLine();
            }

            RdpPreview = sb.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Building the .rdp preview failed.", ex);
            RdpPreview = "; The preview could not be generated: " + ex.Message;
        }
    }

    // ....................................................................
    // Validation, save and cancel
    // ....................................................................

    private void Validate()
    {
        var problems = new List<string>(3);

        if (string.IsNullOrWhiteSpace(_model.Name))
        {
            NameError = "A name is required.";
            problems.Add("the connection needs a name");
        }
        else
        {
            NameError = null;
        }

        if (string.IsNullOrWhiteSpace(_model.Host))
        {
            HostError = "A host name or IP address is required.";
            problems.Add("the host is empty");
        }
        else
        {
            HostError = null;
        }

        if (!TryParseInt(_portText, out var port))
        {
            PortError = "Enter a number.";
            problems.Add("the port is not a number");
        }
        else if (port is < 1 or > 65535)
        {
            PortError = "Use 1 to 65535.";
            problems.Add("the port must be between 1 and 65535");
        }
        else
        {
            PortError = null;
        }

        IsValid = problems.Count == 0;
        ValidationSummary = problems.Count == 0
            ? null
            : "Cannot save yet: " + string.Join(", ", problems) + ".";
    }

    private async Task SaveAsync()
    {
        Validate();
        if (!IsValid) return;

        CommitCustomProperties();

        _model.Name = _model.Name.Trim();
        _model.Host = _model.Host.Trim();
        _model.Description = NullIfBlank(_model.Description);
        _model.Tags = NullIfBlank(_model.Tags);
        _model.Gateway.HostName = NullIfBlank(_model.Gateway.HostName);
        _model.Security.AlternateShell = NullIfBlank(_model.Security.AlternateShell);
        _model.Security.ShellWorkingDirectory = NullIfBlank(_model.Security.ShellWorkingDirectory);
        _model.Security.RemoteApplicationName = NullIfBlank(_model.Security.RemoteApplicationName);
        _model.Security.RemoteApplicationProgram = NullIfBlank(_model.Security.RemoteApplicationProgram);
        _model.Security.RemoteApplicationCmdLine = NullIfBlank(_model.Security.RemoteApplicationCmdLine);
        _model.Security.LoadBalanceInfo = NullIfBlank(_model.Security.LoadBalanceInfo);

        if (string.IsNullOrWhiteSpace(_model.Redirection.DriveList)) _model.Redirection.DriveList = "*";
        if (string.IsNullOrWhiteSpace(_model.Redirection.CameraList)) _model.Redirection.CameraList = "*";

        _model.ModifiedUtc = DateTime.UtcNow;

        try
        {
            await _services.Store.UpsertConnectionAsync(_model).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Saving the connection '{_model.Name}' failed.", ex);
            ValidationSummary = "The connection could not be saved: " + ex.Message;
            return;
        }

        Result = _model;
        RequestClose?.Invoke(this, true);
    }

    private void Cancel() => RequestClose?.Invoke(this, false);

    private async Task TestAsync()
    {
        var host = _model.Host?.Trim() ?? string.Empty;
        if (host.Length == 0) return;

        var port = TryParseInt(_portText, out var parsed) && parsed is > 0 and <= 65535 ? parsed : 3389;

        IsTesting = true;
        TestSucceeded = false;
        TestResultText = "Testing...";

        var started = DateTime.UtcNow;
        bool reachable;
        try
        {
            reachable = await RdpConnectivity.IsReachableAsync(host, port, 2000).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Testing {host}:{port} failed.", ex);
            reachable = false;
        }

        var elapsed = (int)Math.Max(0, (DateTime.UtcNow - started).TotalMilliseconds);
        IsTesting = false;
        TestSucceeded = reachable;
        TestResultText = reachable
            ? $"{host}:{port.ToString(CultureInfo.InvariantCulture)} answered in {elapsed.ToString(CultureInfo.InvariantCulture)} ms."
            : $"{host}:{port.ToString(CultureInfo.InvariantCulture)} did not answer.";
    }

    private void ClearTestResult()
    {
        if (_testResultText is null) return;
        TestResultText = null;
        TestSucceeded = false;
    }

    private void ManageCredentials()
    {
        try
        {
            var owner = System.Windows.Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive);

            var dialog = new Views.CredentialsWindow(new CredentialsViewModel(_services));
            if (owner is not null && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Error("The credential manager could not be opened from the connection editor.", ex);
            return;
        }

        // Whatever the user did in there, the list may have changed.
        _ = ReloadCredentialsAsync();
    }

    private async Task ReloadCredentialsAsync()
    {
        try
        {
            var credentials = await _services.Store.GetCredentialSetsAsync().ConfigureAwait(true);
            if (_disposed) return;

            PopulateCredentials(credentials);
            OnPropertyChanged(nameof(CredentialSummary));
            Invalidate();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reloading credential sets failed.", ex);
        }
    }

    // ....................................................................
    // Performance presets
    // ....................................................................

    private enum Preset
    {
        Lan,
        Balanced,
        LowBandwidth,
    }

    private void ApplyPerformancePreset(Preset preset)
    {
        var exp = _model.Experience;

        switch (preset)
        {
            case Preset.Lan:
                exp.ConnectionQuality = ConnectionQuality.Lan;
                exp.NetworkAutoDetect = false;
                exp.BandwidthAutoDetect = false;
                exp.Compression = false;
                exp.PersistentBitmapCache = true;
                exp.FontSmoothing = true;
                exp.DesktopComposition = true;
                exp.ShowWallpaper = true;
                exp.FullWindowDrag = true;
                exp.MenuAnimations = true;
                exp.VisualStyles = true;
                exp.CursorShadow = true;
                exp.VideoPlaybackMode = VideoPlaybackMode.MultimediaRedirection;
                break;

            case Preset.LowBandwidth:
                exp.ConnectionQuality = ConnectionQuality.Modem;
                exp.NetworkAutoDetect = false;
                exp.BandwidthAutoDetect = true;
                exp.Compression = true;
                exp.PersistentBitmapCache = true;
                exp.FontSmoothing = false;
                exp.DesktopComposition = false;
                exp.ShowWallpaper = false;
                exp.FullWindowDrag = false;
                exp.MenuAnimations = false;
                exp.VisualStyles = false;
                exp.CursorShadow = false;
                exp.VideoPlaybackMode = VideoPlaybackMode.Legacy;
                break;

            default:
                exp.ConnectionQuality = ConnectionQuality.AutoDetect;
                exp.NetworkAutoDetect = true;
                exp.BandwidthAutoDetect = true;
                exp.Compression = true;
                exp.PersistentBitmapCache = true;
                exp.FontSmoothing = true;
                exp.DesktopComposition = false;
                exp.ShowWallpaper = false;
                exp.FullWindowDrag = true;
                exp.MenuAnimations = false;
                exp.VisualStyles = true;
                exp.CursorShadow = false;
                exp.VideoPlaybackMode = VideoPlaybackMode.MultimediaRedirection;
                break;
        }

        Raise(
            nameof(Quality), nameof(NetworkAutoDetectEnabled), nameof(NetworkAutoDetect),
            nameof(BandwidthAutoDetect), nameof(Compression), nameof(PersistentBitmapCache),
            nameof(FontSmoothing), nameof(DesktopComposition), nameof(ShowWallpaper),
            nameof(FullWindowDrag), nameof(MenuAnimations), nameof(VisualStyles),
            nameof(CursorShadow), nameof(VideoPlayback));

        Invalidate();
    }

    // ....................................................................
    // Helpers
    // ....................................................................

    /// <summary>Runs a command body so a fault is logged instead of escaping into the click.</summary>
    private static void Guard(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Error(message, ex);
        }
    }

    private void SetModel<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        OnPropertyChanged(name);
        Invalidate();
    }

    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int ParseSignedOrZero(string? text) => TryParseInt(text, out var value) ? value : 0;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static GroupOption? FindOption(IEnumerable<GroupOption> options, Guid? id)
    {
        foreach (var option in options)
        {
            if (option.Id == id) return option;
        }
        return null;
    }

    private static CredentialOption? FindOption(IEnumerable<CredentialOption> options, Guid? id)
    {
        foreach (var option in options)
        {
            if (option.Id == id) return option;
        }
        return null;
    }

    private ColorOption MatchColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            foreach (var option in ColorOptions)
            {
                if (option.Hex is not null && string.Equals(option.Hex, hex, StringComparison.OrdinalIgnoreCase))
                    return option;
            }
        }
        return ColorOptions[0];
    }

    private ResolutionOption MatchResolution(int width, int height)
    {
        foreach (var option in ResolutionOptions)
        {
            if (!option.IsCustom && !option.IsMatchMonitor && option.Width == width && option.Height == height)
                return option;
        }
        return ResolutionOptions[^1];
    }

    private static RdpConnection CreateDefault(AppServices services, Guid? defaultGroupId)
    {
        var connection = new RdpConnection
        {
            Name = string.Empty,
            Host = string.Empty,
            Port = 3389,
            GroupId = defaultGroupId,
            CredentialDelivery = services.Settings.DefaultCredentialDelivery,
            AutoReconnect = true,
        };

        connection.Display.ScreenMode = Models.ScreenMode.Fullscreen;
        connection.Display.Placement = WindowPlacementMode.SpecificMonitorFullscreen;
        connection.Display.TargetMonitorIndex = 0;
        connection.Display.DynamicResolution = true;
        connection.Redirection.Clipboard = true;

        return connection;
    }

    /// <summary>Pulls stored values onto the steps the combo boxes offer, so nothing resets to blank.</summary>
    private static void Normalize(RdpConnection connection)
    {
        var d = connection.Display;

        d.ColorDepth = d.ColorDepth switch
        {
            32 or 24 or 16 or 15 => d.ColorDepth,
            _ => 32,
        };

        d.DesktopScaleFactor = d.DesktopScaleFactor switch
        {
            100 or 125 or 150 or 175 or 200 or 250 or 300 or 400 or 500 => d.DesktopScaleFactor,
            _ => 100,
        };

        d.DeviceScaleFactor = d.DeviceScaleFactor switch
        {
            100 or 140 or 180 => d.DeviceScaleFactor,
            _ => 100,
        };

        if (d.DesktopWidth <= 0) d.DesktopWidth = 1920;
        if (d.DesktopHeight <= 0) d.DesktopHeight = 1080;
        if (d.CustomWidth <= 0) d.CustomWidth = 1280;
        if (d.CustomHeight <= 0) d.CustomHeight = 800;
        if (d.TargetMonitorIndex < 0) d.TargetMonitorIndex = 0;

        connection.Redirection.KeyboardHook = Math.Clamp(connection.Redirection.KeyboardHook, 0, 2);

        if (connection.MaxReconnectAttempts < 0) connection.MaxReconnectAttempts = 0;
        if (connection.ReconnectDelaySeconds < 0) connection.ReconnectDelaySeconds = 0;
        if (connection.Port <= 0 || connection.Port > 65535) connection.Port = 3389;

        if (string.IsNullOrWhiteSpace(connection.Redirection.DriveList)) connection.Redirection.DriveList = "*";
        if (string.IsNullOrWhiteSpace(connection.Redirection.CameraList)) connection.Redirection.CameraList = "*";
    }

    // ....................................................................
    // Option tables
    // ....................................................................

    private static IReadOnlyList<Option> BuildCredentialDeliveryOptions() => new[]
    {
        new Option(CredentialDelivery.Both, "Vault entry and embedded blob (recommended)"),
        new Option(CredentialDelivery.WindowsVault, "Windows Credential Vault only"),
        new Option(CredentialDelivery.EmbeddedInRdpFile, "Embedded in the .rdp file only"),
        new Option(CredentialDelivery.Prompt, "Prompt every time"),
    };

    private static IReadOnlyList<ColorOption> BuildColorOptions() => new[]
    {
        new ColorOption(null, "No colour"),
        new ColorOption("#E5484D", "Red"),
        new ColorOption("#F76B15", "Orange"),
        new ColorOption("#F5A524", "Amber"),
        new ColorOption("#46A758", "Green"),
        new ColorOption("#12A594", "Teal"),
        new ColorOption("#2A94FF", "Blue"),
        new ColorOption("#5B6CFF", "Indigo"),
        new ColorOption("#8E4EC6", "Purple"),
        new ColorOption("#D6409F", "Pink"),
        new ColorOption("#7E8894", "Slate"),
    };

    private static IReadOnlyList<ResolutionOption> BuildResolutionOptions() => new[]
    {
        ResolutionOption.MatchMonitor(),
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
        new Option(32, "32-bit (highest quality)"),
        new Option(24, "24-bit"),
        new Option(16, "16-bit (lighter on bandwidth)"),
        new Option(15, "15-bit"),
    };

    private static IReadOnlyList<Option> BuildDesktopScaleOptions() => new[]
    {
        new Option(100, "100%"),
        new Option(125, "125%"),
        new Option(150, "150%"),
        new Option(175, "175%"),
        new Option(200, "200%"),
        new Option(250, "250%"),
        new Option(300, "300%"),
        new Option(400, "400%"),
        new Option(500, "500%"),
    };

    private static IReadOnlyList<Option> BuildDeviceScaleOptions() => new[]
    {
        new Option(100, "100%"),
        new Option(140, "140%"),
        new Option(180, "180%"),
    };

    private static IReadOnlyList<Option> BuildQualityOptions() => new[]
    {
        new Option(ConnectionQuality.AutoDetect, "Detect automatically (recommended)"),
        new Option(ConnectionQuality.Lan, "LAN (10 Mbps or better)"),
        new Option(ConnectionQuality.WanHighSpeed, "WAN (10 Mbps or better, high latency)"),
        new Option(ConnectionQuality.HighSpeedBroadband, "High-speed broadband (2 to 10 Mbps)"),
        new Option(ConnectionQuality.SatelliteHighLatency, "Satellite (2 to 16 Mbps, high latency)"),
        new Option(ConnectionQuality.LowSpeedBroadband, "Low-speed broadband (256 Kbps to 2 Mbps)"),
        new Option(ConnectionQuality.Modem, "Modem (56 Kbps to 256 Kbps)"),
    };

    private static IReadOnlyList<Option> BuildAudioPlaybackOptions() => new[]
    {
        new Option(AudioMode.PlayOnThisComputer, "Play on this computer"),
        new Option(AudioMode.PlayOnRemoteComputer, "Play on the remote computer"),
        new Option(AudioMode.DoNotPlay, "Do not play"),
    };

    private static IReadOnlyList<Option> BuildAudioCaptureOptions() => new[]
    {
        new Option(AudioCaptureMode.DoNotCapture, "Do not record"),
        new Option(AudioCaptureMode.CaptureFromThisComputer, "Record from this computer"),
    };

    private static IReadOnlyList<Option> BuildVideoPlaybackOptions() => new[]
    {
        new Option(VideoPlaybackMode.MultimediaRedirection, "Multimedia redirection (smoother video)"),
        new Option(VideoPlaybackMode.Legacy, "Legacy playback"),
    };

    private static IReadOnlyList<Option> BuildKeyboardHookOptions() => new[]
    {
        new Option(0, "On this computer"),
        new Option(1, "On the remote computer"),
        new Option(2, "Only when using full screen"),
    };

    private static IReadOnlyList<Option> BuildGatewayUsageOptions() => new[]
    {
        new Option(GatewayUsageMethod.DoNotUse, "Do not use a gateway"),
        new Option(GatewayUsageMethod.AlwaysUse, "Always use the gateway"),
        new Option(GatewayUsageMethod.UseForNonLocal, "Use for addresses outside the local network"),
        new Option(GatewayUsageMethod.UseDefault, "Use the default gateway settings"),
        new Option(GatewayUsageMethod.NoneDetect, "Detect the gateway automatically"),
    };

    private static IReadOnlyList<Option> BuildGatewayCredentialSourceOptions() => new[]
    {
        new Option(GatewayCredentialSource.AskForPassword, "Ask for a password (NTLM)"),
        new Option(GatewayCredentialSource.SmartCard, "Smart card"),
        new Option(GatewayCredentialSource.UseConnectionCredentials, "Use the session credential"),
    };

    private static IReadOnlyList<Option> BuildAuthLevelOptions() => new[]
    {
        new Option(AuthenticationLevel.WarnOnFailure, "Warn me if authentication fails"),
        new Option(AuthenticationLevel.RequireAuthentication, "Do not connect if authentication fails"),
        new Option(AuthenticationLevel.NoAuthentication, "Connect without warning me"),
        new Option(AuthenticationLevel.NotSpecified, "Not specified"),
    };

    // ....................................................................
    // Bindable helper types. Nested so they cannot collide with other views.
    // ....................................................................

    /// <summary>A labelled value for a combo box bound through SelectedValuePath.</summary>
    public sealed class Option
    {
        public Option(object? value, string label)
        {
            Value = value;
            Label = label;
        }

        public object? Value { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>A group in the flattened, indented group list. A null id means the root.</summary>
    public sealed class GroupOption
    {
        public GroupOption(Guid? id, string label)
        {
            Id = id;
            Label = label;
        }

        public Guid? Id { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>A credential set choice. A null id means "no stored credential".</summary>
    public sealed class CredentialOption
    {
        public CredentialOption(Guid? id, string label)
        {
            Id = id;
            Label = label;
        }

        public Guid? Id { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>One entry of the session-resolution list.</summary>
    public sealed class ResolutionOption
    {
        public ResolutionOption(string label, int width, int height)
        {
            Label = label;
            Width = width;
            Height = height;
        }

        private ResolutionOption(string label, bool isCustom, bool isMatchMonitor)
        {
            Label = label;
            IsCustom = isCustom;
            IsMatchMonitor = isMatchMonitor;
        }

        public static ResolutionOption MatchMonitor() =>
            new("Match the target monitor", isCustom: false, isMatchMonitor: true);

        public static ResolutionOption Custom() =>
            new("Custom size", isCustom: true, isMatchMonitor: false);

        public string Label { get; }
        public int Width { get; }
        public int Height { get; }
        public bool IsCustom { get; }
        public bool IsMatchMonitor { get; }

        public override string ToString() => Label;
    }

    /// <summary>One swatch of the fixed colour palette. A null hex means "no colour".</summary>
    public sealed class ColorOption
    {
        public ColorOption(string? hex, string name)
        {
            Hex = hex;
            Name = name;
        }

        public string? Hex { get; }
        public string Name { get; }
        public bool IsNone => Hex is null;

        public override string ToString() => Name;
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

    /// <summary>One raw key/value row of the advanced editor.</summary>
    public sealed class CustomProperty : ObservableObject
    {
        private Action? _changed;

        public CustomProperty(string key, string value, Action changed)
        {
            _key = key;
            _value = value;
            _changed = changed;
        }

        private string _key;
        public string Key
        {
            get => _key;
            set
            {
                if (!SetProperty(ref _key, value ?? string.Empty)) return;
                _changed?.Invoke();
            }
        }

        private string _value;
        public string Value
        {
            get => _value;
            set
            {
                if (!SetProperty(ref _value, value ?? string.Empty)) return;
                _changed?.Invoke();
            }
        }

        /// <summary>Drops the callback so a removed row can never resurrect the preview.</summary>
        public void Detach() => _changed = null;
    }
}
