namespace DynatecRDM.Models;

/// <summary>How the mstsc window is presented when the session starts.</summary>
public enum ScreenMode
{
    Windowed = 1,
    Fullscreen = 2,
}

/// <summary>Where the session window should be placed once launched.</summary>
public enum WindowPlacementMode
{
    /// <summary>Let mstsc decide (whatever the .rdp file says).</summary>
    Default = 0,
    /// <summary>Full screen on one specific monitor.</summary>
    SpecificMonitorFullscreen = 1,
    /// <summary>Maximized window on one specific monitor.</summary>
    SpecificMonitorMaximized = 2,
    /// <summary>Span every monitor (multimon).</summary>
    SpanAllMonitors = 3,
    /// <summary>Use the explicit monitor set in <c>SelectedMonitors</c>.</summary>
    SelectedMonitors = 4,
    /// <summary>Exact pixel rectangle in virtual-desktop coordinates.</summary>
    CustomRectangle = 5,
}

public enum AudioMode
{
    PlayOnThisComputer = 0,
    PlayOnRemoteComputer = 1,
    DoNotPlay = 2,
}

public enum AudioCaptureMode
{
    DoNotCapture = 0,
    CaptureFromThisComputer = 1,
}

public enum VideoPlaybackMode
{
    Legacy = 0,
    MultimediaRedirection = 1,
}

public enum ConnectionQuality
{
    /// <summary>Let RDP detect the link quality (recommended).</summary>
    AutoDetect = 0,
    Modem = 1,
    LowSpeedBroadband = 2,
    SatelliteHighLatency = 3,
    HighSpeedBroadband = 4,
    WanHighSpeed = 5,
    Lan = 6,
}

public enum AuthenticationLevel
{
    /// <summary>Connect and don't warn me.</summary>
    NoAuthentication = 0,
    /// <summary>Do not connect if authentication fails.</summary>
    RequireAuthentication = 1,
    /// <summary>Warn me if authentication fails.</summary>
    WarnOnFailure = 2,
    /// <summary>No authentication requirement specified.</summary>
    NotSpecified = 3,
}

public enum GatewayUsageMethod
{
    DoNotUse = 0,
    AlwaysUse = 1,
    UseForNonLocal = 2,
    UseDefault = 3,
    NoneDetect = 4,
}

public enum GatewayCredentialSource
{
    AskForPassword = 0,
    SmartCard = 1,
    UseConnectionCredentials = 4,
}

public enum SessionState
{
    /// <summary>The .rdp file has been written and mstsc is starting.</summary>
    Launching = 0,
    /// <summary>Process alive, window not yet confirmed connected.</summary>
    Connecting = 1,
    /// <summary>Session window is alive and responsive.</summary>
    Connected = 2,
    /// <summary>The watchdog is trying to bring the session back.</summary>
    Reconnecting = 3,
    /// <summary>Closed by the user, or by us on request.</summary>
    Disconnected = 4,
    /// <summary>Dropped or never established, and not recoverable.</summary>
    Failed = 5,
}

/// <summary>How a credential is made available to mstsc.</summary>
public enum CredentialDelivery
{
    /// <summary>Write it into the Windows Credential Vault as TERMSRV/&lt;host&gt;.</summary>
    WindowsVault = 0,
    /// <summary>Embed a DPAPI blob in the .rdp file as "password 51:b:".</summary>
    EmbeddedInRdpFile = 1,
    /// <summary>Both: vault entry plus embedded blob (most reliable).</summary>
    Both = 2,
    /// <summary>Let Windows prompt.</summary>
    Prompt = 3,
}

/// <summary>Which credential-store API writes the TERMSRV entry.</summary>
public enum VaultWriteMethod
{
    /// <summary>Direct CredWriteW P/Invoke - no password on any command line.</summary>
    NativeCredentialApi = 0,
    /// <summary>Shell out to cmdkey.exe (the documented manual procedure).</summary>
    CmdKey = 1,
}
