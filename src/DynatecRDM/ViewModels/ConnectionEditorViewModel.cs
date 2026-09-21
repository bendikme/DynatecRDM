using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;
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

    private bool _previewRequested;
    private bool _populating;
    private bool _disposed;

    public ConnectionEditorViewModel(AppServices services, RdpConnection? existing, Guid? defaultGroupId)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = Dispatcher.CurrentDispatcher;

        IsNew = existing is null;
        _model = existing?.Clone() ?? CreateDefault(services, defaultGroupId);
        Result = _model;

        Normalize(_model);

        _portText = _model.Port.ToString(CultureInfo.InvariantCulture);
        _maxReconnectAttemptsText = _model.MaxReconnectAttempts.ToString(CultureInfo.InvariantCulture);
        _reconnectDelayText = _model.ReconnectDelaySeconds.ToString(CultureInfo.InvariantCulture);

        CredentialDeliveryOptions = BuildCredentialDeliveryOptions();
        ColorOptions = BuildColorOptions();
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

        foreach (var pair in _model.CustomProperties)
            CustomProperties.Add(new CustomProperty(pair.Key, pair.Value, OnCustomPropertyChanged));

        // RelayCommand does not swallow exceptions the way AsyncRelayCommand does, and a
        // command runs straight off a click, so every synchronous body is guarded here.
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsValid);
        CancelCommand = new RelayCommand(() => Guard(Cancel, "Cancelling the connection editor failed."));
        TestCommand = new AsyncRelayCommand(TestAsync, () => !string.IsNullOrWhiteSpace(_model.Host));
        ManageCredentialsCommand = new RelayCommand(ManageCredentials);
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

        // The Display tab is its own editor over the same settings object, so it can also be used
        // for a multi-config's items. Its changes come back through Invalidate like any other tab's.
        Display = new DisplayEditorViewModel(services, _model.Display, Invalidate);

        UpdateCredentialDeliveryHelp();
        UpdateWatchdogSummary();
        Validate();

        _ = LoadListsAsync();
    }

    /// <summary>The edited connection. Only written to the store when the user saves.</summary>
    public RdpConnection Result { get; private set; }

    /// <summary>Raised when the dialog should close: true when the connection was saved.</summary>
    public event EventHandler<bool>? RequestClose;

    public bool IsNew { get; }

    public string WindowTitle => IsNew ? Strings.Editor_Title_New : Strings.Editor_Title_Edit;

    public string HeaderSubtitle => IsNew
        ? Strings.Editor_Subtitle_New
        : Strings.Editor_Subtitle_Edit;

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
            if (value == TabDisplay) Display.EnsureMonitorMap();
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
            if (set is null) return Strings.Editor_CredentialSummary_Prompt;

            // Show the name actually sent, which for a credential with no domain includes this
            // connection's machine name.
            var logon = set.GetLogonName(_model.Host);
            return set.HasPassword
                ? UiLanguage.Format(Strings.Editor_CredentialSummary_SignsIn, logon)
                : UiLanguage.Format(Strings.Editor_CredentialSummary_SignsInNoPassword, logon);
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
    public RelayCommand AddCustomPropertyCommand { get; }
    public RelayCommand RemoveCustomPropertyCommand { get; }
    public RelayCommand LanPresetCommand { get; }
    public RelayCommand BalancedPresetCommand { get; }
    public RelayCommand LowBandwidthPresetCommand { get; }

    /// <summary>The Display tab: screen mode, monitors, the window rectangle, size and scaling.</summary>
    public DisplayEditorViewModel Display { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _previewDebounce.Stop();
        _previewDebounce.Tick -= OnPreviewTick;

        Display.Dispose();

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
            Groups.Add(new GroupOption(null, Strings.Editor_Group_None));
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

            Credentials.Add(new CredentialOption(null, Strings.Editor_Credential_Prompt));
            GatewayCredentialOptions.Add(new CredentialOption(null, Strings.Editor_Credential_UseSession));

            foreach (var c in _credentialSets)
            {
                var label = string.IsNullOrWhiteSpace(c.Name)
                    ? c.QualifiedUsername
                    : UiLanguage.Format(Strings.Editor_Credential_Label, c.Name, c.QualifiedUsername);
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

    private void UpdateWatchdogSummary()
    {
        if (!_model.AutoReconnect)
        {
            WatchdogSummary = Strings.Editor_Watchdog_Summary_Off;
            return;
        }

        var attempts = _model.MaxReconnectAttempts;
        var delay = _model.ReconnectDelaySeconds;

        // Every combination of attempt limit and delay is its own sentence, singulars included.
        var format = (attempts <= 0 ? 0 : attempts == 1 ? 1 : 2, delay <= 0 ? 0 : delay == 1 ? 1 : 2) switch
        {
            (0, 0) => Strings.Editor_Watchdog_Summary_Unlimited_NoDelay,
            (0, 1) => Strings.Editor_Watchdog_Summary_Unlimited_Delay_One,
            (0, _) => Strings.Editor_Watchdog_Summary_Unlimited_Delay_Many,
            (1, 0) => Strings.Editor_Watchdog_Summary_Once_NoDelay,
            (1, 1) => Strings.Editor_Watchdog_Summary_Once_Delay_One,
            (1, _) => Strings.Editor_Watchdog_Summary_Once_Delay_Many,
            (_, 0) => Strings.Editor_Watchdog_Summary_Limited_NoDelay,
            (_, 1) => Strings.Editor_Watchdog_Summary_Limited_Delay_One,
            _ => Strings.Editor_Watchdog_Summary_Limited_Delay_Many,
        };

        WatchdogSummary = UiLanguage.Format(
            format,
            attempts.ToString(CultureInfo.InvariantCulture),
            delay.ToString(CultureInfo.InvariantCulture));
    }

    private void UpdateCredentialDeliveryHelp() => CredentialDeliveryHelp = _model.CredentialDelivery switch
    {
        CredentialDelivery.WindowsVault => UiLanguage.Format(Strings.Editor_Delivery_Vault_Help, HostForHelp()),
        CredentialDelivery.EmbeddedInRdpFile => Strings.Editor_Delivery_Embedded_Help,
        CredentialDelivery.Both => Strings.Editor_Delivery_Both_Help,
        _ => Strings.Editor_Delivery_Prompt_Help,
    };

    private string HostForHelp()
    {
        var host = _model.Host;
        return string.IsNullOrWhiteSpace(host) ? Strings.Editor_Delivery_HostPlaceholder : host.Trim();
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
                Monitors = Display.KnownMonitors(),
            };

            var body = _services.RdpBuilder.Build(_model, context);
            var sb = new StringBuilder(body.Length + 256);

            // The ';' lines are the preview's own notes, never part of the file, so they follow the UI language.
            sb.Append("; ").Append(Strings.Editor_RdpPreview_Intro).AppendLine();
            sb.Append("; ").Append(Strings.Editor_RdpPreview_CommentsNote).AppendLine();
            sb.AppendLine();
            sb.Append(body);
            if (body.Length > 0 && !body.EndsWith('\n')) sb.AppendLine();

            var embeds = _model.CredentialDelivery is CredentialDelivery.EmbeddedInRdpFile or CredentialDelivery.Both;
            if (embeds && credential is not null && credential.HasPassword)
            {
                sb.AppendLine();
                sb.Append("; password 51:b:<stored>").AppendLine();
                sb.Append("; ").Append(Strings.Editor_RdpPreview_PasswordNote).AppendLine();
            }
            else if (embeds)
            {
                sb.AppendLine();
                sb.Append("; password 51:b:<stored>").AppendLine();
                sb.Append("; ").Append(Strings.Editor_RdpPreview_NoPasswordNote).AppendLine();
            }

            RdpPreview = sb.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Building the .rdp preview failed.", ex);
            RdpPreview = "; " + UiLanguage.Format(Strings.Editor_RdpPreview_Failed, ex.Message);
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
            NameError = Strings.Editor_Error_NameRequired;
            problems.Add(Strings.Editor_Problem_NoName);
        }
        else
        {
            NameError = null;
        }

        if (string.IsNullOrWhiteSpace(_model.Host))
        {
            HostError = Strings.Editor_Error_HostRequired;
            problems.Add(Strings.Editor_Problem_NoHost);
        }
        else
        {
            HostError = null;
        }

        if (!TryParseInt(_portText, out var port))
        {
            PortError = Strings.Editor_Error_PortNotNumber;
            problems.Add(Strings.Editor_Problem_PortNotNumber);
        }
        else if (port is < 1 or > 65535)
        {
            PortError = Strings.Editor_Error_PortRange;
            problems.Add(Strings.Editor_Problem_PortRange);
        }
        else
        {
            PortError = null;
        }

        IsValid = problems.Count == 0;
        ValidationSummary = problems.Count == 0
            ? null
            : UiLanguage.Format(Strings.Editor_Validation_Summary, DisplayEditorViewModel.JoinList(problems));
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
            ValidationSummary = UiLanguage.Format(Strings.Editor_Error_SaveFailed, ex.Message);
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
        TestResultText = Strings.Editor_Test_Running;

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
            ? UiLanguage.Format(
                Strings.Editor_Test_Answered,
                host,
                port.ToString(CultureInfo.InvariantCulture),
                elapsed.ToString(CultureInfo.InvariantCulture))
            : UiLanguage.Format(Strings.Editor_Test_NoAnswer, host, port.ToString(CultureInfo.InvariantCulture));
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
        // Out-of-range indexes resolve to the primary monitor by contract.
        connection.Display.TargetMonitorIndex = services.Monitors.GetByIndex(-1).Index;
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
        new Option(CredentialDelivery.Both, Strings.Editor_Delivery_Both),
        new Option(CredentialDelivery.WindowsVault, Strings.Editor_Delivery_Vault),
        new Option(CredentialDelivery.EmbeddedInRdpFile, Strings.Editor_Delivery_Embedded),
        new Option(CredentialDelivery.Prompt, Strings.Editor_Delivery_Prompt),
    };

    private static IReadOnlyList<ColorOption> BuildColorOptions() => new[]
    {
        new ColorOption(null, Strings.Editor_Colour_None),
        new ColorOption("#E5484D", Strings.Editor_Colour_Red),
        new ColorOption("#F76B15", Strings.Editor_Colour_Orange),
        new ColorOption("#F5A524", Strings.Editor_Colour_Amber),
        new ColorOption("#46A758", Strings.Editor_Colour_Green),
        new ColorOption("#12A594", Strings.Editor_Colour_Teal),
        new ColorOption("#2A94FF", Strings.Editor_Colour_Blue),
        new ColorOption("#5B6CFF", Strings.Editor_Colour_Indigo),
        new ColorOption("#8E4EC6", Strings.Editor_Colour_Purple),
        new ColorOption("#D6409F", Strings.Editor_Colour_Pink),
        new ColorOption("#7E8894", Strings.Editor_Colour_Slate),
    };


    private static IReadOnlyList<Option> BuildQualityOptions() => new[]
    {
        new Option(ConnectionQuality.AutoDetect, Strings.Editor_Quality_AutoDetect),
        new Option(ConnectionQuality.Lan, Strings.Editor_Quality_Lan),
        new Option(ConnectionQuality.WanHighSpeed, Strings.Editor_Quality_Wan),
        new Option(ConnectionQuality.HighSpeedBroadband, Strings.Editor_Quality_HighSpeedBroadband),
        new Option(ConnectionQuality.SatelliteHighLatency, Strings.Editor_Quality_Satellite),
        new Option(ConnectionQuality.LowSpeedBroadband, Strings.Editor_Quality_LowSpeedBroadband),
        new Option(ConnectionQuality.Modem, Strings.Editor_Quality_Modem),
    };

    private static IReadOnlyList<Option> BuildAudioPlaybackOptions() => new[]
    {
        new Option(AudioMode.PlayOnThisComputer, Strings.Editor_Audio_PlayHere),
        new Option(AudioMode.PlayOnRemoteComputer, Strings.Editor_Audio_PlayRemote),
        new Option(AudioMode.DoNotPlay, Strings.Editor_Audio_DoNotPlay),
    };

    private static IReadOnlyList<Option> BuildAudioCaptureOptions() => new[]
    {
        new Option(AudioCaptureMode.DoNotCapture, Strings.Editor_Audio_DoNotRecord),
        new Option(AudioCaptureMode.CaptureFromThisComputer, Strings.Editor_Audio_RecordHere),
    };

    private static IReadOnlyList<Option> BuildVideoPlaybackOptions() => new[]
    {
        new Option(VideoPlaybackMode.MultimediaRedirection, Strings.Editor_Video_Multimedia),
        new Option(VideoPlaybackMode.Legacy, Strings.Editor_Video_Legacy),
    };

    private static IReadOnlyList<Option> BuildKeyboardHookOptions() => new[]
    {
        new Option(0, Strings.Editor_Keyboard_Local),
        new Option(1, Strings.Editor_Keyboard_Remote),
        new Option(2, Strings.Editor_Keyboard_FullscreenOnly),
    };

    private static IReadOnlyList<Option> BuildGatewayUsageOptions() => new[]
    {
        new Option(GatewayUsageMethod.DoNotUse, Strings.Editor_Gateway_DoNotUse),
        new Option(GatewayUsageMethod.AlwaysUse, Strings.Editor_Gateway_Always),
        new Option(GatewayUsageMethod.UseForNonLocal, Strings.Editor_Gateway_NonLocal),
        new Option(GatewayUsageMethod.UseDefault, Strings.Editor_Gateway_Default),
        new Option(GatewayUsageMethod.NoneDetect, Strings.Editor_Gateway_Detect),
    };

    private static IReadOnlyList<Option> BuildGatewayCredentialSourceOptions() => new[]
    {
        new Option(GatewayCredentialSource.AskForPassword, Strings.Editor_GatewaySource_Password),
        new Option(GatewayCredentialSource.SmartCard, Strings.Editor_GatewaySource_SmartCard),
        new Option(GatewayCredentialSource.UseConnectionCredentials, Strings.Editor_GatewaySource_Session),
    };

    private static IReadOnlyList<Option> BuildAuthLevelOptions() => new[]
    {
        new Option(AuthenticationLevel.WarnOnFailure, Strings.Editor_Auth_Warn),
        new Option(AuthenticationLevel.RequireAuthentication, Strings.Editor_Auth_Require),
        new Option(AuthenticationLevel.NoAuthentication, Strings.Editor_Auth_None),
        new Option(AuthenticationLevel.NotSpecified, Strings.Editor_Auth_NotSpecified),
    };

    // ....................................................................
    // Bindable helper types. Nested so they cannot collide with other views.
    // ....................................................................


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
