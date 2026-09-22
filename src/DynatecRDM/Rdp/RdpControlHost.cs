using System;
using AxMSTSCLib;
using DynatecRDM.Services;
using MSTSCLib;

namespace DynatecRDM.Rdp;

/// <summary>
/// Reason a hosted RDP session ended, distilled from the ActiveX control's disconnect events into
/// the few cases the session layer actually reacts to.
/// </summary>
public enum RdpDisconnectKind
{
    /// <summary>The user or the app asked to disconnect; do not auto-reconnect.</summary>
    UserInitiated,
    /// <summary>Sign-in failed (bad credentials, denied logon); auto-reconnect would just loop.</summary>
    LogonFailed,
    /// <summary>The link dropped or the server closed it; a reconnect may succeed.</summary>
    ConnectionLost,
}

/// <summary>Details of a session ending, passed to <see cref="RdpControlHost.Disconnected"/>.</summary>
public sealed record RdpDisconnectInfo(RdpDisconnectKind Kind, int Code, string? Message);

/// <summary>
/// Hosts the Remote Desktop ActiveX control (<c>mstscax.dll</c>) in-process and drives it
/// programmatically - no <c>.rdp</c> file and no external <c>mstsc</c>/<c>msrdc</c> process. Because
/// the connection is not started by opening a file, the April-2026 per-file security dialog never
/// appears, and the control renegotiates the remote resolution on resize
/// (<see cref="UpdateResolution"/>), which external clients no longer allow us to control.
///
/// The control has thread affinity: create it, and call every method here, on the UI thread. Expose
/// <see cref="WinFormsControl"/> to a WPF <c>WindowsFormsHost</c> to show the session.
///
/// This is the low-level host. Mapping a connection's full settings and credentials onto the control
/// lives one layer up; this type only owns the control, its lifecycle and its events.
/// </summary>
public sealed class RdpControlHost : IDisposable
{
    // AxMsRdpClient11NotSafeForScripting wraps coclass {1df7c823-…}, the newest RDP control that
    // actually instantiates (the registered v13 {3F859AA3-…} returns CLASS_E_CLASSNOTAVAILABLE).
    // Its OCX implements IMsRdpClient9/10 (UpdateSessionDisplaySettings), and the NotSafeForScripting
    // variant is the one that lets us set the password programmatically.
    private readonly AxMsRdpClient11NotSafeForScripting _ax = new();
    private bool _disposed;
    private bool _handledDisconnect;

    public RdpControlHost()
    {
        _ax.Dock = System.Windows.Forms.DockStyle.Fill;
        _ax.OnConnecting += (_, _) => Connecting?.Invoke(this, EventArgs.Empty);
        // Only observed: the control's own answer (continue the logon) is passed back untouched.
        _ax.OnReceivedTSPublicKey += (_, _) => ReceivedServerKey?.Invoke(this, EventArgs.Empty);
        _ax.OnAuthenticationWarningDisplayed += (_, _) => AuthenticationWarning?.Invoke(this, true);
        _ax.OnAuthenticationWarningDismissed += (_, _) => AuthenticationWarning?.Invoke(this, false);
        _ax.OnConnected += (_, _) => Connected?.Invoke(this, EventArgs.Empty);
        _ax.OnLoginComplete += (_, _) => LoginComplete?.Invoke(this, EventArgs.Empty);
        _ax.OnDisconnected += OnAxDisconnected;
        _ax.OnFatalError += OnAxFatalError;

        // With ContainerHandledFullScreen set, the control never makes its own full-screen window;
        // it asks the host to do it. That is what keeps full screen under the app's control - and
        // stops the control drawing a connection bar whose buttons have no container to act on.
        _ax.OnRequestGoFullScreen += (_, _) => RequestGoFullScreen?.Invoke(this, EventArgs.Empty);
        _ax.OnRequestLeaveFullScreen += (_, _) => RequestLeaveFullScreen?.Invoke(this, EventArgs.Empty);
        _ax.OnRequestContainerMinimize += (_, _) => RequestContainerMinimize?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The WinForms control to place in a <c>WindowsFormsHost</c>.</summary>
    public System.Windows.Forms.Control WinFormsControl => _ax;

    /// <summary>Raised when the control starts opening the connection.</summary>
    public event EventHandler? Connecting;

    /// <summary>
    /// Raised when the server's public key has arrived: the secure channel is up and the
    /// credentials are being checked next.
    /// </summary>
    public event EventHandler? ReceivedServerKey;

    /// <summary>
    /// Raised with true when the control puts up its server-authentication (certificate) warning
    /// and waits for the user, and with false when the user has answered it.
    /// </summary>
    public event EventHandler<bool>? AuthenticationWarning;

    /// <summary>Raised when the transport is up (before sign-in completes).</summary>
    public event EventHandler? Connected;

    /// <summary>Raised once the remote sign-in has finished and the desktop is usable.</summary>
    public event EventHandler? LoginComplete;

    /// <summary>Raised once when the session ends, with why.</summary>
    public event EventHandler<RdpDisconnectInfo>? Disconnected;

    /// <summary>The session wants to go full screen (Ctrl+Alt+Break, or <see cref="FullScreen"/> set).</summary>
    public event EventHandler? RequestGoFullScreen;

    /// <summary>The session wants to leave full screen.</summary>
    public event EventHandler? RequestLeaveFullScreen;

    /// <summary>The session wants its host window minimised.</summary>
    public event EventHandler? RequestContainerMinimize;

    /// <summary>
    /// The control's own idea of whether the session is full screen. Setting it does not resize
    /// anything by itself: because the host handles full screen, the control answers by raising
    /// <see cref="RequestGoFullScreen"/> or <see cref="RequestLeaveFullScreen"/> for the host to act on.
    /// </summary>
    public bool FullScreen
    {
        get { try { return !_disposed && _ax.FullScreen; } catch { return false; } }
        set { try { if (!_disposed) _ax.FullScreen = value; } catch { /* not connected yet */ } }
    }

    /// <summary>True between a successful connect and its disconnect.</summary>
    public bool IsConnected => !_disposed && _ax.IsHandleCreated && _ax.Connected == 1;

    /// <summary>
    /// The control's own state: 0 disconnected, 1 connected, 2 still connecting. A connection has
    /// to be all the way back at 0 before the control accepts a new target and Connect().
    /// </summary>
    public int ConnectionState
    {
        get
        {
            try { return _disposed || !_ax.IsHandleCreated ? 0 : _ax.Connected; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Starts the connection. The control must already be parented (added to a host) so its window
    /// handle exists; callers set the target/credentials/settings beforehand via <see cref="Ocx"/>
    /// and the strongly-typed helpers on the underlying control.
    /// </summary>
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _handledDisconnect = false;
        _ax.Connect();
    }

    /// <summary>Ends the session. Safe to call when already disconnected.</summary>
    public void Disconnect()
    {
        if (_disposed) return;
        try { if (_ax.Connected != 0) _ax.Disconnect(); } catch { /* already gone */ }
    }

    /// <summary>The raw control, for the settings/credential mapping layer to configure.</summary>
    public AxMsRdpClient11NotSafeForScripting Control => _ax;

    /// <summary>The underlying OCX, for casts to <see cref="IMsRdpClient9"/> and friends.</summary>
    public object Ocx => _ax.GetOcx() ?? throw new InvalidOperationException("The RDP control has not been created.");

    /// <summary>
    /// Renegotiates the remote resolution to match the given size (device-independent pixels are the
    /// caller's concern; pass the pixel size the session should render at). This is the dynamic
    /// resolution that external clients no longer let us drive. No-op until the session is connected.
    /// </summary>
    /// <summary>
    /// Sends the session the display layout the control works out for itself - for a full-screen
    /// multimon session, every display it covers. False when the session would not take it yet.
    /// </summary>
    public bool SyncDisplaySettings()
    {
        if (!IsConnected) return false;
        try
        {
            ((IMsRdpClient9)Ocx).SyncSessionDisplaySettings();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"The session would not take the display layout yet: {ex.Message}");
            return false;
        }
    }

    public bool UpdateResolution(int width, int height, int desktopScaleFactor = 100, int deviceScaleFactor = 100)
    {
        if (!IsConnected) return false;

        // The control rejects odd sizes and out-of-range factors with E_UNEXPECTED, so clamp to its
        // documented limits: sizes even and within [200, 8192]; desktop scale 100-500; device scale
        // one of {100, 140, 180}.
        var w = (uint)Clamp(width & ~1, 200, 8192);
        var h = (uint)Clamp(height & ~1, 200, 8192);
        var desktopScale = (uint)Clamp(desktopScaleFactor, 100, 500);
        var deviceScale = (uint)(deviceScaleFactor >= 180 ? 180 : deviceScaleFactor >= 140 ? 140 : 100);

        try
        {
            ((IMsRdpClient9)Ocx).UpdateSessionDisplaySettings(w, h, w, h, 0, desktopScale, deviceScale);
            return true;
        }
        catch
        {
            // The session may not support display control yet - just after signing in, say. The
            // caller tries again a little later, and on the next resize anyway.
            return false;
        }
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private void OnAxDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e)
        => RaiseDisconnected(ClassifyDisconnect(e.discReason), e.discReason, DescribeDisconnect(e.discReason));

    /// <summary>
    /// The control's own wording for why a session ended. Without it a failed session shows no
    /// reason at all, which is the difference between "Failed" and "Failed: the logon attempt failed".
    /// </summary>
    private string? DescribeDisconnect(int discReason)
    {
        try
        {
            var extended = (uint)(int)_ax.ExtendedDisconnectReason;
            var text = ((IMsRdpClient9)Ocx).GetErrorDescription((uint)discReason, extended);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;   // no description available; the reason code still travels
        }
    }

    private void OnAxFatalError(object? sender, IMsTscAxEvents_OnFatalErrorEvent e)
        => RaiseDisconnected(RdpDisconnectKind.ConnectionLost, e.errorCode, $"Fatal error {e.errorCode}.");

    private void RaiseDisconnected(RdpDisconnectKind kind, int code, string? message)
    {
        if (_handledDisconnect) return;
        _handledDisconnect = true;
        Disconnected?.Invoke(this, new RdpDisconnectInfo(kind, code, message));
    }

    // Disconnect reason codes: 1/2/3 are the local/remote user or app calling Disconnect (clean);
    // the sign-in failures cluster around the "logon" reasons. Anything else is treated as a
    // dropped link so the reconnect policy can decide whether to retry.
    private static RdpDisconnectKind ClassifyDisconnect(int discReason) => discReason switch
    {
        1 or 2 or 3 => RdpDisconnectKind.UserInitiated,
        // extended logon-error reasons (the control reports these when NLA/credentials fail)
        2308 or 2311 or 2312 or 5639 or 3591 => RdpDisconnectKind.LogonFailed,
        _ => RdpDisconnectKind.ConnectionLost,
    };

    public void Dispose()
    {
        if (_disposed) return;
        try { Disconnect(); } catch { }
        _disposed = true;
        try { _ax.Dispose(); } catch { }
    }
}
