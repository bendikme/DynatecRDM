namespace DynatecRDM.Models;

/// <summary>
/// Everything that drives the display-related lines of an .rdp file, including the
/// multi-monitor options that Remote Desktop Connection never exposes in its own UI.
/// </summary>
public sealed class DisplaySettings
{
    public ScreenMode ScreenMode { get; set; } = ScreenMode.Fullscreen;

    /// <summary>use multimon:i:</summary>
    public bool UseAllMonitors { get; set; }

    /// <summary>selectedmonitors:s: - monitor ids as reported by "mstsc /l".</summary>
    public List<int> SelectedMonitors { get; set; } = new();

    public int DesktopWidth { get; set; } = 1920;
    public int DesktopHeight { get; set; } = 1080;

    /// <summary>session bpp:i: - 8/15/16/24/32.</summary>
    public int ColorDepth { get; set; } = 32;

    /// <summary>smart sizing:i: - scale the remote desktop to fit the window.</summary>
    public bool SmartSizing { get; set; }

    /// <summary>dynamic resolution:i: - resize the session when the window resizes.</summary>
    public bool DynamicResolution { get; set; } = true;

    /// <summary>desktopscalefactor:i: - 100/125/150/175/200/250/300/400/500.</summary>
    public int DesktopScaleFactor { get; set; } = 100;

    /// <summary>devicescalefactor:i: - 100/140/180 only.</summary>
    public int DeviceScaleFactor { get; set; } = 100;

    /// <summary>Post-launch window placement performed by the manager itself.</summary>
    public WindowPlacementMode Placement { get; set; } = WindowPlacementMode.Default;

    /// <summary>Zero-based index into the manager's monitor list.</summary>
    public int TargetMonitorIndex { get; set; }

    public int CustomLeft { get; set; }
    public int CustomTop { get; set; }
    public int CustomWidth { get; set; } = 1280;
    public int CustomHeight { get; set; } = 800;

    /// <summary>Keep the session window above other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    public DisplaySettings Clone() => new()
    {
        ScreenMode = ScreenMode,
        UseAllMonitors = UseAllMonitors,
        SelectedMonitors = new List<int>(SelectedMonitors),
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

/// <summary>Bandwidth and visual-experience tuning.</summary>
public sealed class ExperienceSettings
{
    public ConnectionQuality ConnectionQuality { get; set; } = ConnectionQuality.AutoDetect;

    /// <summary>networkautodetect:i:</summary>
    public bool NetworkAutoDetect { get; set; } = true;

    /// <summary>bandwidthautodetect:i:</summary>
    public bool BandwidthAutoDetect { get; set; } = true;

    /// <summary>compression:i:</summary>
    public bool Compression { get; set; } = true;

    /// <summary>allow font smoothing:i:</summary>
    public bool FontSmoothing { get; set; } = true;

    /// <summary>allow desktop composition:i:</summary>
    public bool DesktopComposition { get; set; } = true;

    /// <summary>disable wallpaper:i: (inverted when written).</summary>
    public bool ShowWallpaper { get; set; } = true;

    /// <summary>disable full window drag:i: (inverted when written).</summary>
    public bool FullWindowDrag { get; set; } = true;

    /// <summary>disable menu anims:i: (inverted when written).</summary>
    public bool MenuAnimations { get; set; } = true;

    /// <summary>disable themes:i: (inverted when written).</summary>
    public bool VisualStyles { get; set; } = true;

    /// <summary>disable cursor setting:i: (inverted when written).</summary>
    public bool CursorShadow { get; set; } = true;

    /// <summary>autoreconnection enabled:i: - the reconnect banner built into mstsc.</summary>
    public bool AutoReconnection { get; set; } = true;

    /// <summary>bitmapcachepersistenable:i:</summary>
    public bool PersistentBitmapCache { get; set; } = true;

    public AudioMode AudioMode { get; set; } = AudioMode.PlayOnThisComputer;
    public AudioCaptureMode AudioCaptureMode { get; set; } = AudioCaptureMode.DoNotCapture;
    public VideoPlaybackMode VideoPlaybackMode { get; set; } = VideoPlaybackMode.MultimediaRedirection;

    public ExperienceSettings Clone() => new()
    {
        ConnectionQuality = ConnectionQuality,
        NetworkAutoDetect = NetworkAutoDetect,
        BandwidthAutoDetect = BandwidthAutoDetect,
        Compression = Compression,
        FontSmoothing = FontSmoothing,
        DesktopComposition = DesktopComposition,
        ShowWallpaper = ShowWallpaper,
        FullWindowDrag = FullWindowDrag,
        MenuAnimations = MenuAnimations,
        VisualStyles = VisualStyles,
        CursorShadow = CursorShadow,
        AutoReconnection = AutoReconnection,
        PersistentBitmapCache = PersistentBitmapCache,
        AudioMode = AudioMode,
        AudioCaptureMode = AudioCaptureMode,
        VideoPlaybackMode = VideoPlaybackMode,
    };
}

/// <summary>Local-resource redirection.</summary>
public sealed class RedirectionSettings
{
    public bool Clipboard { get; set; } = true;
    public bool Printers { get; set; }
    public bool SmartCards { get; set; }
    public bool Ports { get; set; }
    public bool PnpDevices { get; set; }
    public bool WebAuthn { get; set; } = true;
    public bool Location { get; set; }

    /// <summary>drivestoredirect:s: - use * for all drives, or a list such as C:;D:</summary>
    public bool RedirectDrives { get; set; }
    public string DriveList { get; set; } = "*";

    /// <summary>camerastoredirect:s:</summary>
    public bool RedirectCameras { get; set; }
    public string CameraList { get; set; } = "*";

    /// <summary>keyboardhook:i: - 0 local, 1 remote, 2 full screen only.</summary>
    public int KeyboardHook { get; set; } = 2;

    public RedirectionSettings Clone() => new()
    {
        Clipboard = Clipboard,
        Printers = Printers,
        SmartCards = SmartCards,
        Ports = Ports,
        PnpDevices = PnpDevices,
        WebAuthn = WebAuthn,
        Location = Location,
        RedirectDrives = RedirectDrives,
        DriveList = DriveList,
        RedirectCameras = RedirectCameras,
        CameraList = CameraList,
        KeyboardHook = KeyboardHook,
    };
}

/// <summary>RD Gateway configuration.</summary>
public sealed class GatewaySettings
{
    public GatewayUsageMethod UsageMethod { get; set; } = GatewayUsageMethod.DoNotUse;
    public string? HostName { get; set; }
    public GatewayCredentialSource CredentialSource { get; set; } = GatewayCredentialSource.AskForPassword;
    public bool BypassForLocalAddresses { get; set; } = true;

    /// <summary>Optional dedicated gateway credential; falls back to the session credential.</summary>
    public Guid? CredentialSetId { get; set; }

    public GatewaySettings Clone() => new()
    {
        UsageMethod = UsageMethod,
        HostName = HostName,
        CredentialSource = CredentialSource,
        BypassForLocalAddresses = BypassForLocalAddresses,
        CredentialSetId = CredentialSetId,
    };
}

/// <summary>Security, session and RemoteApp options.</summary>
public sealed class SecuritySettings
{
    public AuthenticationLevel AuthenticationLevel { get; set; } = AuthenticationLevel.WarnOnFailure;
    public bool EnableCredSsp { get; set; } = true;
    public bool PromptForCredentialsOnce { get; set; } = true;

    /// <summary>administrative session:i: - the equivalent of mstsc /admin.</summary>
    public bool AdministrativeSession { get; set; }

    /// <summary>Do not cache anything locally for this session.</summary>
    public bool PublicMode { get; set; }

    /// <summary>alternate shell:s: - program to start instead of the desktop.</summary>
    public string? AlternateShell { get; set; }
    public string? ShellWorkingDirectory { get; set; }

    /// <summary>remoteapplicationmode:i:</summary>
    public bool RemoteAppMode { get; set; }
    public string? RemoteApplicationName { get; set; }
    public string? RemoteApplicationProgram { get; set; }
    public string? RemoteApplicationCmdLine { get; set; }

    /// <summary>loadbalanceinfo:s: - connection-broker routing token.</summary>
    public string? LoadBalanceInfo { get; set; }

    public SecuritySettings Clone() => new()
    {
        AuthenticationLevel = AuthenticationLevel,
        EnableCredSsp = EnableCredSsp,
        PromptForCredentialsOnce = PromptForCredentialsOnce,
        AdministrativeSession = AdministrativeSession,
        PublicMode = PublicMode,
        AlternateShell = AlternateShell,
        ShellWorkingDirectory = ShellWorkingDirectory,
        RemoteAppMode = RemoteAppMode,
        RemoteApplicationName = RemoteApplicationName,
        RemoteApplicationProgram = RemoteApplicationProgram,
        RemoteApplicationCmdLine = RemoteApplicationCmdLine,
        LoadBalanceInfo = LoadBalanceInfo,
    };
}
