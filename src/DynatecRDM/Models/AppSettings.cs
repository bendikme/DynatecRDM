namespace DynatecRDM.Models;

/// <summary>User preferences for the manager itself (persisted in the local database).</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Start minimised to the notification area. Off by default: a first launch that shows
    /// nothing at all reads as a failure, so the window opens until the user opts out.
    /// The --tray switch (used by the run-at-logon entry) still starts hidden regardless.
    /// </summary>
    public bool StartInTray { get; set; }

    /// <summary>Close button hides to tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Register the app to start with Windows.</summary>
    public bool LaunchAtLogon { get; set; }

    /// <summary>Capture session thumbnails for the tray menu.</summary>
    public bool EnableSnapshots { get; set; } = true;

    /// <summary>Seconds between background snapshot refreshes; 0 disables periodic capture.</summary>
    public int SnapshotIntervalSeconds { get; set; } = 30;

    /// <summary>Longest edge of a stored snapshot, in pixels.</summary>
    public int SnapshotMaxEdge { get; set; } = 480;

    /// <summary>JPEG quality for snapshots (1-100).</summary>
    public int SnapshotQuality { get; set; } = 72;

    /// <summary>Master switch for the reconnect watchdog.</summary>
    public bool WatchdogEnabled { get; set; } = true;

    /// <summary>Seconds between watchdog health checks.</summary>
    public int WatchdogPollSeconds { get; set; } = 2;

    /// <summary>Delete generated .rdp files once the session has started.</summary>
    public bool ShredRdpFiles { get; set; } = true;

    /// <summary>Default credential delivery for new connections.</summary>
    public CredentialDelivery DefaultCredentialDelivery { get; set; } = CredentialDelivery.Both;

    /// <summary>Which API writes TERMSRV vault entries.</summary>
    public VaultWriteMethod VaultWriteMethod { get; set; } = VaultWriteMethod.NativeCredentialApi;

    /// <summary>Global hotkey that opens the quick-launch menu, for example "Ctrl+Alt+R".</summary>
    public string? QuickLaunchHotkey { get; set; } = "Ctrl+Alt+R";

    public bool ShowGroupsInTray { get; set; } = true;
    public bool ShowSnapshotsInTray { get; set; } = true;
    public int TrayMenuMaxItems { get; set; } = 40;

    /// <summary>Theme name: Dark or Light.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Accent colour as #RRGGBB.</summary>
    public string AccentColor { get; set; } = "#0A84FF";

    public double MainWindowWidth { get; set; } = 1280;
    public double MainWindowHeight { get; set; } = 800;
    public double MainWindowLeft { get; set; } = double.NaN;
    public double MainWindowTop { get; set; } = double.NaN;
    public bool MainWindowMaximized { get; set; }

    /// <summary>Confirm before closing a running session from the UI.</summary>
    public bool ConfirmSessionClose { get; set; } = true;

    // ------------------------------------------------------------------ updates

    /// <summary>Check GitHub for a newer release in the background.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// The GitHub repository releases are published to, as "owner/repo".
    /// Empty disables update checking entirely.
    /// </summary>
    public string UpdateRepository { get; set; } = string.Empty;

    /// <summary>Offer pre-release builds as well as stable ones.</summary>
    public bool UpdateIncludePrereleases { get; set; }

    /// <summary>Hours between background update checks.</summary>
    public int UpdateCheckIntervalHours { get; set; } = 24;

    /// <summary>Download and install without asking, rather than prompting first.</summary>
    public bool UpdateInstallAutomatically { get; set; }

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>A version the user chose to skip; it is never offered again.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Optional token for update checks against a private repository.</summary>
    public string? UpdateAccessToken { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
