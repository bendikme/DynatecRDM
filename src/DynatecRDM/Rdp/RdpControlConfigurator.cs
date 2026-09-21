using System;
using System.Globalization;
using AxMSTSCLib;
using DynatecRDM.Models;
using MSTSCLib;

namespace DynatecRDM.Rdp;

/// <summary>Login to apply to the control: the resolved logon name and its plaintext password.</summary>
public sealed record RdpCredential(CredentialSet? Set, string? PlainPassword);

/// <summary>
/// The already-resolved display geometry to apply, mirroring what <see cref="DisplayLayout"/> works
/// out for the .rdp file. The embedded session manager resolves this once (reusing
/// <c>DisplayLayout.Resolve</c>) and hands the values here, so this type stays free of monitor
/// enumeration and is easy to reason about on its own.
/// </summary>
public readonly record struct RdpDisplayPlan(
    int DesktopWidth,
    int DesktopHeight,
    int ColorDepth,
    bool UseMultimon,
    ResizeBehavior Resize,
    int DesktopScaleFactor,
    int DeviceScaleFactor)
{
    /// <summary>
    /// The displays a multi-monitor session should cover, as the ids Remote Desktop numbers them by.
    /// Empty means every display. The order matters: the first id becomes the session's primary.
    /// </summary>
    public IReadOnlyList<int> SelectedMonitorIds { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Applies a connection's full settings and credentials onto the hosted RDP ActiveX control before
/// it connects - the in-process equivalent of the .rdp file <see cref="RdpFileBuilder"/> writes for
/// the external clients. Everything <c>RdpFileBuilder.Build</c> expresses as an .rdp line is set here
/// as a control property instead; the value semantics are kept identical so a connection behaves the
/// same whichever path launches it.
/// </summary>
public static class RdpControlConfigurator
{
    private const int DefaultPort = 3389;

    // RDP performance-flag bits (TS_PERF_*). The .rdp "disable …"/"allow …"/"enable …" lines collapse
    // into this single bitmask on the control.
    private const int PerfDisableWallpaper = 0x00000001;
    private const int PerfDisableFullWindowDrag = 0x00000002;
    private const int PerfDisableMenuAnims = 0x00000004;
    private const int PerfDisableThemes = 0x00000008;
    private const int PerfDisableCursorShadow = 0x00000020;
    private const int PerfEnableFontSmoothing = 0x00000080;
    private const int PerfEnableDesktopComposition = 0x00000100;

    /// <summary>
    /// Configures <paramref name="ax"/> for <paramref name="connection"/>. Call before
    /// <c>Connect()</c>, on the UI thread. <paramref name="plan"/> is the resolved display geometry;
    /// <paramref name="credential"/> may be null to let Windows prompt.
    /// </summary>
    public static IReadOnlyList<string> Configure(
        AxMsRdpClient11NotSafeForScripting ax,
        RdpConnection connection,
        RdpDisplayPlan plan,
        RdpCredential? credential,
        IReadOnlyDictionary<string, string>? extraProperties = null,
        bool hideConnectionBar = true)
    {
        ArgumentNullException.ThrowIfNull(ax);
        ArgumentNullException.ThrowIfNull(connection);

        var exp = connection.Experience;
        var red = connection.Redirection;
        var sec = connection.Security;
        var gw = connection.Gateway;
        var adv = ax.AdvancedSettings9;

        // --- Target ---
        SplitHostPort(connection.Host, connection.Port, out var host, out var port);
        ax.Server = host;
        if (port is > 0 and <= 65535) adv.RDPPort = port;

        // --- Display ---
        ax.DesktopWidth = plan.DesktopWidth;
        ax.DesktopHeight = plan.DesktopHeight;
        ax.ColorDepth = plan.ColorDepth;
        // smart sizing scales the picture; dynamic resolution (the default) is driven at runtime by
        // UpdateSessionDisplaySettings, so only the scale case needs a control flag set here.
        adv.SmartSizing = plan.Resize == ResizeBehavior.Scale;
        SetMultimon(ax, plan);
        ApplyScaleFactors(ax, plan);

        // --- Experience / performance ---
        adv.Compress = exp.Compression ? 1 : 0;
        adv.PerformanceFlags = PerformanceFlags(exp);
        adv.BitmapPersistence = exp.PersistentBitmapCache ? 1 : 0;
        adv.AudioRedirectionMode = (uint)exp.AudioMode;
        adv.AudioCaptureRedirectionMode = exp.AudioCaptureMode == AudioCaptureMode.CaptureFromThisComputer;
        adv.VideoPlaybackMode = (uint)exp.VideoPlaybackMode;
        // A fixed connection type is the value most often refused by an older control, so it is
        // guarded on its own: losing the bandwidth hint must never cost the whole session.
        Try(() => adv.NetworkConnectionType = NetworkConnectionType(exp.ConnectionQuality));
        Try(() => adv.BandwidthDetection = exp.BandwidthAutoDetect);
        adv.EnableAutoReconnect = exp.AutoReconnection;

        // --- Chrome and who owns full screen ---
        // Full screen is the host's job. Without this the control builds its OWN full-screen window
        // when the session asks (Ctrl+Alt+Break), complete with a connection bar whose minimise and
        // restore buttons act on a container that never answers - so they appear dead. With it set,
        // the control instead raises OnRequestGoFullScreen/OnRequestLeaveFullScreen and the window
        // the app owns does the resizing, which is also what lets the remote resolution follow.
        adv.ContainerHandledFullScreen = 1;
        // The app draws its own chrome, so the control's floating bar is never wanted. Hiding it is
        // not the same as removing it - DisplayConnectionBar only unpins the bar, so it can still
        // slide in on a top-edge hover. The buttons are taken away as well, because the bar is drawn
        // by the control and its buttons only ever reach a container that handles full screen.
        adv.DisplayConnectionBar = false;
        Try(() => adv.PinConnectionBar = false);
        Try(() => adv.ConnectionBarShowMinimizeButton = false);
        Try(() => adv.ConnectionBarShowRestoreButton = false);
        Try(() => adv.ConnectionBarShowPinButton = false);

        // --- Security ---
        adv.AuthenticationLevel = (uint)sec.AuthenticationLevel;
        adv.EnableCredSspSupport = sec.EnableCredSsp;
        adv.NegotiateSecurityLayer = true;
        adv.ConnectToAdministerServer = sec.AdministrativeSession;
        adv.PublicMode = sec.PublicMode;
        adv.LoadBalanceInfo = sec.LoadBalanceInfo ?? string.Empty;

        // --- Redirection ---
        adv.RedirectClipboard = red.Clipboard;
        adv.RedirectPrinters = red.Printers;
        adv.RedirectPorts = red.Ports;
        adv.RedirectSmartCards = red.SmartCards;
        adv.RedirectDevices = red.PnpDevices;
        Try(() => adv.RedirectPOSDevices = false);
        ApplyDriveScope(ax, red);
        ax.SecuredSettings2.KeyboardHookMode = Math.Clamp(red.KeyboardHook, 0, 2);

        // --- Alternate shell ---
        // RemoteApp is deliberately NOT supported on this path. Launching one needs the RAIL channel
        // set up through IMsRdpClientRemoteProgram, and the old code's shortcut - starting
        // "rdpinit.exe" as the shell without that channel - made the session connect and then log
        // straight off again. A RemoteApp connection therefore opens an ordinary desktop here, which
        // is a working session rather than one that dies on arrival. Only an explicitly configured
        // alternate shell is honoured.
        var shell = Trim(sec.AlternateShell);
        ax.SecuredSettings2.StartProgram = shell;
        ax.SecuredSettings2.WorkDir = sec.ShellWorkingDirectory ?? string.Empty;

        // --- Gateway ---
        ConfigureGateway(ax.TransportSettings2, gw, sec);

        // --- Credentials + the redirection/credential prompts ---
        ConfigureCredentials(ax, connection, host, credential);

        // Last, so a pinned value wins over the generated one - exactly the precedence the .rdp
        // writer gives these lines. Anything with no equivalent on the control is reported back so
        // the caller can say so, rather than letting it disappear silently.
        var unsupported = ApplyCustomProperties(ax, connection.CustomProperties, extraProperties);
        SetConnectionBarVisible(ax, !hideConnectionBar);
        return unsupported;
    }

    internal static void SetConnectionBarVisible(AxMsRdpClient11NotSafeForScripting ax, bool visible)
    {
        var ns = (IMsRdpClientNonScriptable5)RequireOcx(ax);
        ns.DisableConnectionBar = !visible;
        var adv = ax.AdvancedSettings9;
        adv.DisplayConnectionBar = visible;
        adv.ConnectionBarShowMinimizeButton = visible;
        adv.ConnectionBarShowRestoreButton = visible;
        adv.ConnectionBarShowPinButton = visible;
    }

    /// <summary>
    /// Applies the raw .rdp lines a connection pins in <see cref="RdpConnection.CustomProperties"/>,
    /// plus any per-item overrides from a multi-config, onto their control equivalents.
    ///
    /// These override generated values, so this runs after everything else. Returns the names that
    /// could not be applied: the control has no general "set this .rdp line" entry point, so a few
    /// settings genuinely have nowhere to go, and the caller warns about those instead of pretending.
    /// Login lines are ignored here exactly as the .rdp writer ignores them - the credential owns them.
    /// </summary>
    private static IReadOnlyList<string> ApplyCustomProperties(
        AxMsRdpClient11NotSafeForScripting ax,
        IReadOnlyDictionary<string, string>? custom,
        IReadOnlyDictionary<string, string>? extra)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (custom is not null) foreach (var pair in custom) merged[NameOf(pair.Key)] = pair.Value ?? string.Empty;
        // A multi-config item's overrides win over the connection's own.
        if (extra is not null) foreach (var pair in extra) merged[NameOf(pair.Key)] = pair.Value ?? string.Empty;
        if (merged.Count == 0) return Array.Empty<string>();

        var adv = ax.AdvancedSettings9;
        var unapplied = new List<string>();

        foreach (var (name, raw) in merged)
        {
            if (name.Length == 0) continue;
            // The credential set owns these; a stale imported value must not override the real login.
            if (name is "username" or "domain" or "password 51") continue;

            var value = (raw ?? string.Empty).Trim();
            var applied = true;
            try
            {
                switch (name)
                {
                    case "desktopwidth": ax.DesktopWidth = Int(value, ax.DesktopWidth); break;
                    case "desktopheight": ax.DesktopHeight = Int(value, ax.DesktopHeight); break;
                    case "session bpp": ax.ColorDepth = Int(value, ax.ColorDepth); break;
                    case "smart sizing": adv.SmartSizing = Bool(value); break;
                    case "compression": adv.Compress = Bool(value) ? 1 : 0; break;
                    case "bitmapcachepersistenable": adv.BitmapPersistence = Bool(value) ? 1 : 0; break;
                    case "keyboardhook": ax.SecuredSettings2.KeyboardHookMode = Math.Clamp(Int(value, 2), 0, 2); break;
                    case "audiomode": adv.AudioRedirectionMode = (uint)Math.Clamp(Int(value, 0), 0, 2); break;
                    case "audiocapturemode": adv.AudioCaptureRedirectionMode = Bool(value); break;
                    case "videoplaybackmode": adv.VideoPlaybackMode = (uint)Math.Clamp(Int(value, 1), 0, 1); break;
                    case "connection type": adv.NetworkConnectionType = (uint)Math.Clamp(Int(value, 7), 1, 7); break;
                    case "bandwidthautodetect": adv.BandwidthDetection = Bool(value); break;
                    case "authentication level": adv.AuthenticationLevel = (uint)Math.Clamp(Int(value, 2), 0, 3); break;
                    case "enablecredsspsupport": adv.EnableCredSspSupport = Bool(value); break;
                    case "negotiate security layer": adv.NegotiateSecurityLayer = Bool(value); break;
                    case "administrative session": adv.ConnectToAdministerServer = Bool(value); break;
                    case "autoreconnection enabled": adv.EnableAutoReconnect = Bool(value); break;
                    case "public mode": adv.PublicMode = Bool(value); break;
                    case "redirectclipboard": adv.RedirectClipboard = Bool(value); break;
                    case "redirectprinters": adv.RedirectPrinters = Bool(value); break;
                    case "redirectcomports": adv.RedirectPorts = Bool(value); break;
                    case "redirectsmartcards": adv.RedirectSmartCards = Bool(value); break;
                    case "redirectposdevices": adv.RedirectPOSDevices = Bool(value); break;
                    case "redirectdirectx": adv.RedirectDirectX = Bool(value); break;
                    case "devicestoredirect": adv.RedirectDevices = value.Length != 0; break;
                    case "alternate shell": ax.SecuredSettings2.StartProgram = value; break;
                    case "shell working directory": ax.SecuredSettings2.WorkDir = value; break;
                    case "loadbalanceinfo": adv.LoadBalanceInfo = value; break;
                    case "displayconnectionbar": adv.DisplayConnectionBar = Bool(value); break;
                    case "gatewayhostname": ax.TransportSettings2.GatewayHostname = value; break;
                    case "gatewayusagemethod": ax.TransportSettings2.GatewayUsageMethod = (uint)Math.Clamp(Int(value, 0), 0, 4); break;
                    case "gatewayprofileusagemethod": ax.TransportSettings2.GatewayProfileUsageMethod = (uint)Math.Clamp(Int(value, 1), 0, 1); break;
                    case "promptcredentialonce": ax.TransportSettings2.GatewayCredSharing = Bool(value) ? 1u : 0u; break;

                    // Settings the .rdp file can carry but the control has no member for. Reported,
                    // never silently dropped.
                    default: applied = false; break;
                }
            }
            catch
            {
                applied = false;   // the control refused it; report it as unapplied
            }

            if (!applied) unapplied.Add(name);
        }

        return unapplied;
    }

    /// <summary>The bare property name from a custom key such as "kdcproxyname:s".</summary>
    private static string NameOf(string key)
    {
        var trimmed = (key ?? string.Empty).Trim().TrimEnd(':').Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator > 0 && separator == trimmed.Length - 2 && char.ToLowerInvariant(trimmed[^1]) is 'i' or 's' or 'b')
            trimmed = trimmed[..separator].Trim();
        return trimmed.ToLowerInvariant();
    }

    private static int Int(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool Bool(string value)
    {
        if (value.Length == 0) return false;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed != 0;
        return bool.TryParse(value, out var flag) && flag;
    }

    /// <summary>The RDP performance-flags bitmask for an experience profile (pure; unit-testable).</summary>
    internal static int PerformanceFlags(ExperienceSettings exp)
    {
        var flags = 0;
        if (!exp.ShowWallpaper) flags |= PerfDisableWallpaper;
        if (!exp.FullWindowDrag) flags |= PerfDisableFullWindowDrag;
        if (!exp.MenuAnimations) flags |= PerfDisableMenuAnims;
        if (!exp.VisualStyles) flags |= PerfDisableThemes;
        if (!exp.CursorShadow) flags |= PerfDisableCursorShadow;
        if (exp.FontSmoothing) flags |= PerfEnableFontSmoothing;
        if (exp.DesktopComposition) flags |= PerfEnableDesktopComposition;
        return flags;
    }

    /// <summary>CONNECTION_TYPE for a quality: 1-6 map straight through, auto-detect is 7 (pure).</summary>
    internal static uint NetworkConnectionType(ConnectionQuality quality) =>
        quality == ConnectionQuality.AutoDetect ? 7u : (uint)quality;

    /// <summary>
    /// The effective gateway usage method, matching <c>RdpFileBuilder</c>: no host means never use;
    /// "always" plus bypass-local becomes "use for non-local" (pure; unit-testable).
    /// </summary>
    internal static GatewayUsageMethod EffectiveGatewayMethod(GatewaySettings gw)
    {
        if (string.IsNullOrWhiteSpace(gw.HostName)) return GatewayUsageMethod.DoNotUse;
        if (gw.UsageMethod == GatewayUsageMethod.AlwaysUse && gw.BypassForLocalAddresses)
            return GatewayUsageMethod.UseForNonLocal;
        return gw.UsageMethod;
    }

    /// <summary>
    /// True when raw .rdp lines - the connection's own or a multi-config item's - set the gateway,
    /// so the control's gateway is not the one the connection's settings describe.
    /// </summary>
    internal static bool OverridesGateway(
        IReadOnlyDictionary<string, string>? custom, IReadOnlyDictionary<string, string>? extra)
    {
        static bool Sets(IReadOnlyDictionary<string, string>? lines)
        {
            if (lines is null) return false;
            foreach (var key in lines.Keys)
                if (NameOf(key) is "gatewayhostname" or "gatewayusagemethod") return true;
            return false;
        }
        return Sets(custom) || Sets(extra);
    }

    private static void ConfigureGateway(IMsRdpClientTransportSettings2 transport, GatewaySettings gw, SecuritySettings sec)
    {
        var method = EffectiveGatewayMethod(gw);
        transport.GatewayUsageMethod = (uint)method;
        transport.GatewayProfileUsageMethod = 1; // explicit settings, not the RDP file's defaults

        // GatewayCredsSource only understands 0 (user name and password) and 1 (smart card). The
        // connection's "use the same credentials as the connection" is the .rdp file's value 4, which
        // is not a credentials SOURCE at all - it is credential sharing, and has its own property.
        var smartCard = gw.CredentialSource == GatewayCredentialSource.SmartCard;
        transport.GatewayCredsSource = smartCard ? 1u : 0u;

        // promptcredentialonce: share the connection's credentials with the gateway instead of asking
        // for them again. The control defaults to not sharing, so leaving this unset would make every
        // existing gateway connection start prompting.
        var share = sec.PromptForCredentialsOnce
            || gw.CredentialSource == GatewayCredentialSource.UseConnectionCredentials;
        transport.GatewayCredSharing = share ? 1u : 0u;

        transport.GatewayHostname = gw.HostName?.Trim() ?? string.Empty;
    }

    private static void ConfigureCredentials(
        AxMsRdpClient11NotSafeForScripting ax, RdpConnection connection, string host, RdpCredential? credential)
    {
        var nonScriptable = (IMsRdpClientNonScriptable5)RequireOcx(ax);

        // A reconnect reuses the control. Clear every old identity/password before applying the
        // current credential, including when the credential was removed or switched to prompting.
        nonScriptable.ResetPassword();
        ax.UserName = string.Empty;
        ax.Domain = string.Empty;
        nonScriptable.PromptForCredentials = connection.CredentialDelivery == CredentialDelivery.Prompt;

        // The connection bar is suppressed here as well as through AdvancedSettings: this flag stops
        // the bar being created at all, which is what keeps it away in full screen.
        nonScriptable.DisableConnectionBar = true;

        // On the installed control this consent dialog makes our programmatic connection cancel
        // locally (reason 1) before opening a socket; the transport regression test covers it. Server
        // authentication and NLA remain governed by AuthenticationLevel and EnableCredSspSupport.
        nonScriptable.ShowRedirectionWarningDialog = false;
        nonScriptable.WarnAboutSendingCredentials = true;
        nonScriptable.WarnAboutClipboardRedirection = true;
        nonScriptable.WarnAboutPrinterRedirection = true;
        nonScriptable.WarnAboutDirectXRedirection = true;

        var set = credential?.Set;
        if (set is not null && !string.IsNullOrWhiteSpace(set.Username))
        {
            var bare = set.Username.Trim();
            // A login that already carries its own domain must not be qualified a second time.
            var userName = HasDomainPart(bare) ? bare : set.GetLogonName(host).Trim();
            if (userName.Length != 0)
            {
                ax.UserName = userName;
                if (!HasDomainPart(userName) && !string.IsNullOrWhiteSpace(set.Domain))
                    ax.Domain = set.Domain.Trim();
            }
        }

        var plain = credential?.PlainPassword;
        var hasPassword = !string.IsNullOrEmpty(plain);

        if (connection.CredentialDelivery == CredentialDelivery.Prompt)
        {
            // The connection is set to always prompt: ask, and do not offer a stored password.
            nonScriptable.PromptForCredentials = true;
        }
        else if (hasPassword)
        {
            // A password is available: the NotSafeForScripting control takes it directly, so it never
            // touches disk or a command line and no prompt appears.
            nonScriptable.ClearTextPassword = plain;
        }
        // Otherwise leave the prompting properties at their defaults. Forcing PromptForCredentials on
        // here stops the control from using the cached Windows credential (TERMSRV/<host>) or single
        // sign-on and leaves it stuck negotiating - the same connection mstsc makes silently. The
        // control still prompts on its own if no cached credential exists.
    }

    /// <summary>
    /// Sets drive redirection, limited to the drives the connection actually names.
    ///
    /// <c>AdvancedSettings.RedirectDrives</c> is not a flag beside the per-drive collection - it IS
    /// the collection: writing true switches every volume on AND turns dynamic redirection on, and
    /// writing false switches everything off. Reading it back is an aggregate ("is anything on"), not
    /// the flag that was written.
    ///
    /// Everything here is therefore ordered to fail CLOSED. The bulk clear comes first and the wanted
    /// drives are opted in afterwards, so a failure half way through can only ever leave fewer drives
    /// shared than asked for, never more. Doing it the other way round - switch everything on, then
    /// narrow - would expose every volume, mapped network drives included, if anything threw in
    /// between.
    ///
    /// Drive names arrive as 'C' ':' '\' plus an embedded NUL, so drives are matched on their letter.
    /// </summary>
    private static void ApplyDriveScope(AxMsRdpClient11NotSafeForScripting ax, RedirectionSettings red)
    {
        var adv = ax.AdvancedSettings9;

        if (!red.RedirectDrives)
        {
            adv.RedirectDrives = false;   // if clearing fails, abort rather than expose old drives
            return;
        }

        var list = Trim(red.DriveList);
        var dynamic = list.Contains("dynamicdrives", StringComparison.OrdinalIgnoreCase);
        var wanted = DriveLetters(list);

        // "*" (and an empty list, which the .rdp writer also treats as everything) means every drive
        // including ones plugged in later - exactly what the bulk write already does.
        if (list.Length == 0 || list == "*")
        {
            Try(() => adv.RedirectDrives = true);
            return;
        }

        // Scoped. Clear everything first, then opt the named drives back in.
        adv.RedirectDrives = false;

        try
        {
            var nonScriptable = (IMsRdpClientNonScriptable5)RequireOcx(ax);
            if (wanted.Count > 0)
            {
                var drives = nonScriptable.DriveCollection;
                drives.RescanDrives(dynamic);
                for (uint i = 0; i < drives.DriveCount; i++)
                {
                    var drive = drives.DriveByIndex[i];
                    drive.RedirectionState = wanted.Contains(DriveLetterOf(drive.Name));
                }
            }

            // Both ways, always: the bulk write above also moved this, so leaving it alone would let
            // a plain "C:;D:" session keep redirecting drives plugged in later - the opposite of what
            // the .rdp list asks for.
            nonScriptable.RedirectDynamicDrives = dynamic;
        }
        catch
        {
            // The collection is unusable on this control. Everything is already off from the clear
            // above, so the session simply redirects nothing rather than redirecting everything.
        }
    }

    /// <summary>The drive letters named in a drivestoredirect list such as "C:;D:".</summary>
    private static HashSet<char> DriveLetters(string list)
    {
        var letters = new HashSet<char>();
        foreach (var part in list.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (token.Length == 0) continue;
            var letter = char.ToUpperInvariant(token[0]);
            if (letter is >= 'A' and <= 'Z' && (token.Length == 1 || token[1] == ':')) letters.Add(letter);
        }
        return letters;
    }

    /// <summary>The drive letter of a control drive name like "C:\ ", or '\0' when it has none.</summary>
    private static char DriveLetterOf(string? name)
    {
        var text = (name ?? string.Empty).TrimStart();
        if (text.Length == 0) return '\0';
        var letter = char.ToUpperInvariant(text[0]);
        return letter is >= 'A' and <= 'Z' ? letter : '\0';
    }

    /// <summary>
    /// High-DPI scaling. The control has no scale-factor property of its own - the factors go through
    /// the named extended-settings bag, and only before connecting. As in the .rdp writer they are
    /// sent only when they are not the plain 100% default, and clamped to the ranges the control
    /// accepts (desktop 100-500, device one of 100/140/180); anything else is refused outright.
    /// </summary>
    private static void ApplyScaleFactors(AxMsRdpClient11NotSafeForScripting ax, RdpDisplayPlan plan)
    {
        var desktop = Math.Clamp(plan.DesktopScaleFactor <= 0 ? 100 : plan.DesktopScaleFactor, 100, 500);
        var device = plan.DeviceScaleFactor >= 180 ? 180 : plan.DeviceScaleFactor >= 140 ? 140 : 100;
        if (desktop == 100 && device == 100) return;

        try
        {
            var extended = (IMsRdpExtendedSettings)RequireOcx(ax);
            object desktopValue = (uint)desktop;
            extended.set_Property("DesktopScaleFactor", ref desktopValue);
            object deviceValue = (uint)device;
            extended.set_Property("DeviceScaleFactor", ref deviceValue);
        }
        catch
        {
            // An older control without the extended-settings bag, or a factor it will not take:
            // the session still connects, just at the unscaled size.
        }
    }

    /// <summary>
    /// Turns multi-monitor on and, when the connection names particular displays, limits the session
    /// to those.
    ///
    /// There is no typed member for this: the display list is a named entry in the same
    /// extended-settings bag the scale factors use, it must be a string ("0,2"), and it cannot be
    /// changed once connected. The first id in the list becomes the session's primary display, so
    /// the connection's order is preserved.
    ///
    /// The control accepts whatever it is given here - an out-of-range id, a non-contiguous set and
    /// even nonsense all return success, and the value cannot be read back to check - so the list is
    /// validated before it is sent. An invalid one falls back to covering every display, which is a
    /// working session, rather than a session that comes up wrong with nothing to explain it.
    /// </summary>
    private static void SetMultimon(AxMsRdpClient11NotSafeForScripting ax, RdpDisplayPlan plan)
    {
        try { ((IMsRdpClientNonScriptable5)RequireOcx(ax)).UseMultimon = plan.UseMultimon; }
        catch { return; /* an older control without multimon; one display is the safe fallback */ }

        if (!plan.UseMultimon) return;

        var ids = plan.SelectedMonitorIds;
        // Empty means every display, which is what UseMultimon alone already does. The control caps
        // a session at 16 displays, and a negative id is never valid.
        if (ids is not { Count: > 0 } || ids.Count > 16) return;

        var seen = new HashSet<int>();
        foreach (var id in ids)
        {
            if (id < 0 || !seen.Add(id)) return;   // invalid or duplicated: cover everything instead
        }

        try
        {
            var extended = (IMsRdpExtendedSettings)RequireOcx(ax);
            object value = string.Join(',', ids);
            extended.set_Property("SelectedMonitors", ref value);
        }
        catch
        {
            // The control would not take the list; the session still opens across every display.
        }
    }

    private static object RequireOcx(AxMsRdpClient11NotSafeForScripting ax) =>
        ax.GetOcx() ?? throw new InvalidOperationException("The RDP control has not been created.");

    internal static void SplitHostPort(string? rawHost, int port, out string host, out int resolvedPort)
    {
        host = (rawHost ?? string.Empty).Trim();
        resolvedPort = port <= 0 ? DefaultPort : port;

        if (host.Length == 0) return;

        // A "host:port" typed straight into the host field wins over the separate Port property, the
        // same precedence the .rdp "full address" line has.
        if (host[0] == '[')
        {
            var close = host.IndexOf(']');
            if (close > 0)
            {
                var rest = host[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) && bp is > 0 and <= 65535)
                    resolvedPort = bp;
                host = host.Substring(1, close - 1);
            }
            return;
        }

        var last = host.LastIndexOf(':');
        if (last > 0 && host.IndexOf(':') == last
            && int.TryParse(host[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65535)
        {
            resolvedPort = parsed;
            host = host[..last];
        }
    }

    /// <summary>
    /// Applies one setting, tolerating a control that refuses it. The control rejects a handful of
    /// values outright (an unknown connection type, a bar property on an older build) and a single
    /// refusal would otherwise abort the whole launch - losing the session over a cosmetic setting.
    /// </summary>
    private static void Try(Action apply)
    {
        try { apply(); }
        catch { /* the setting is not available here; the rest of the connection is still valid */ }
    }

    private static bool HasDomainPart(string userName) =>
        userName.IndexOf('\\') >= 0 || userName.IndexOf('@') >= 0;

    private static string Trim(string? value) => (value ?? string.Empty).Trim();
}
