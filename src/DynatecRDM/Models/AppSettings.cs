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

    /// <summary>
    /// Start Remote Desktop without an .rdp file when the connection does not need one.
    ///
    /// Windows shows an "unknown publisher" security warning for every unsigned .rdp file, on
    /// every launch, and it cannot be switched off per user. Connections that only use settings
    /// the command line can express are started as "mstsc /v:host" instead, which shows no
    /// warning. Anything richer still uses a file, so no configured setting is ever dropped.
    /// </summary>
    public bool AvoidRdpFilePrompt { get; set; } = true;

    /// <summary>
    /// Sign the generated .rdp files with a per-user certificate. Windows then names this
    /// application as the publisher and offers "Remember my choices for remote connections from
    /// this publisher", so the warning can be accepted once instead of on every launch.
    /// </summary>
    public bool SignRdpFiles { get; set; }

    /// <summary>
    /// Thumbprint of the certificate used to sign generated .rdp files. Leave empty to use the
    /// self-signed one the application creates.
    ///
    /// A self-signed certificate makes Windows treat the file as signed, but it cannot name the
    /// publisher, and with no publisher identity the "remember my choices" answer is not kept. A
    /// certificate issued by a certification authority the machine trusts - an internal AD CS is
    /// the usual source - does name the publisher, and then the choice sticks.
    /// </summary>
    public string? SigningCertificateThumbprint { get; set; }

    /// <summary>Confirm before closing a running session from the UI.</summary>
    public bool ConfirmSessionClose { get; set; } = true;

    // ------------------------------------------------------------------ updates

    /// <summary>Check GitHub for a newer release in the background.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// The GitHub repository releases are published to, as "owner/repo".
    /// Empty disables update checking entirely.
    /// </summary>
    public string UpdateRepository { get; set; } = "bendikme/DynatecRDM";

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
