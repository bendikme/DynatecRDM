using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Emits a complete .rdp file - including the advanced options Remote Desktop Connection
/// never exposes in its own UI - and parses one back into a connection.
/// </summary>
public sealed class RdpFileBuilder : IRdpFileBuilder
{
    private const int DefaultPort = 3389;
    private const int MinDesktopEdge = 200;
    private const int MaxDesktopEdge = 8192;

    // mstsc honours the BOM; without one it falls back to the ANSI code page and mangles
    // non-ASCII host names, user names and RemoteApp titles.
    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: true);

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly int[] DesktopScaleSteps = { 100, 125, 150, 175, 200, 250, 300, 400, 500 };
    private static readonly char[] MonitorListSeparators = { ',', ';', ' ' };
    private static readonly string[] SmallInts = CreateSmallInts();

    private static readonly string[] KnownNames =
    {
        "screen mode id", "use multimon", "span monitors", "selectedmonitors",
        "desktopwidth", "desktopheight", "session bpp", "smart sizing", "dynamic resolution",
        "desktopscalefactor", "devicescalefactor", "winposstr",
        "compression", "keyboardhook",
        "audiocapturemode", "audiomode", "videoplaybackmode",
        "connection type", "networkautodetect", "bandwidthautodetect",
        "displayconnectionbar", "enableworkspacereconnect",
        "disable wallpaper", "allow font smoothing", "allow desktop composition",
        "disable full window drag", "disable menu anims", "disable themes", "disable cursor setting",
        "bitmapcachepersistenable",
        "full address", "alternate full address", "username", "domain", "password 51",
        "prompt for credentials", "promptcredentialonce", "authentication level",
        "enablecredsspsupport", "negotiate security layer", "administrative session",
        "autoreconnection enabled", "public mode",
        "redirectclipboard", "redirectprinters", "redirectcomports", "redirectsmartcards",
        "redirectposdevices", "redirectdirectx", "devicestoredirect", "drivestoredirect",
        "camerastoredirect", "redirectwebauthn", "redirectlocation",
        "gatewayhostname", "gatewayusagemethod", "gatewaycredentialssource",
        "gatewayprofileusagemethod", "gatewaybrokeringtype",
        "use redirection server name", "rdgiskdcproxy", "kdcproxyname",
        "remoteapplicationmode", "remoteapplicationname", "remoteapplicationprogram",
        "remoteapplicationcmdline", "alternate shell", "shell working directory",
        "loadbalanceinfo",
    };

    /// <summary>Every property name this builder writes or understands, for editor completion.</summary>
    public static IReadOnlyList<string> KnownPropertyNames => KnownNames;

    private readonly ISecretProtector _protector;

    public RdpFileBuilder(ISecretProtector protector) =>
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public string Build(RdpConnection connection, RdpBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (context is null) context = new RdpBuildContext();

        var display = context.Display ?? connection.Display;
        var exp = connection.Experience;
        var red = connection.Redirection;
        var sec = connection.Security;
        var gw = connection.Gateway;

        var lines = new LineSet();

        var screenMode = ResolveScreenMode(display);

        // mstsc only honours multimon in a full-screen session; the two flags must agree or
        // the session silently opens on one monitor.
        var multimon = screenMode == ScreenMode.Fullscreen
            && (display.UseAllMonitors
                || display.Placement == WindowPlacementMode.SpanAllMonitors
                || (display.Placement == WindowPlacementMode.SelectedMonitors && display.SelectedMonitors.Count > 0));

        var monitorIds = multimon ? JoinMonitorIds(display.SelectedMonitors) : string.Empty;
        ResolveDesktopSize(display, context.Monitors, out var desktopWidth, out var desktopHeight);

        lines.Int("screen mode id", (int)screenMode);
        lines.Bool("use multimon", multimon);
        lines.Int("span monitors", 0);
        if (monitorIds.Length != 0) lines.Text("selectedmonitors", monitorIds);

        lines.Int("desktopwidth", desktopWidth);
        lines.Int("desktopheight", desktopHeight);
        lines.Int("session bpp", NormalizeColorDepth(display.ColorDepth));
        lines.Bool("smart sizing", display.SmartSizing);
        // The two are mutually exclusive; asking for smart sizing is always a deliberate choice,
        // while dynamic resolution is merely the default.
        lines.Bool("dynamic resolution", display.DynamicResolution && !display.SmartSizing);

        var desktopScale = NormalizeDesktopScale(display.DesktopScaleFactor);
        var deviceScale = NormalizeDeviceScale(display.DeviceScaleFactor);
        // mstsc ignores desktopscalefactor unless devicescalefactor is present too.
        if (desktopScale != 100 || deviceScale != 100)
        {
            lines.Int("desktopscalefactor", desktopScale);
            lines.Int("devicescalefactor", deviceScale);
        }

        lines.Text("winposstr", BuildWindowPosition(display, context.Monitors, screenMode, desktopWidth, desktopHeight));

        lines.Bool("compression", exp.Compression);
        lines.Int("keyboardhook", Math.Clamp(red.KeyboardHook, 0, 2));
        lines.Int("audiocapturemode", (int)exp.AudioCaptureMode);
        lines.Int("audiomode", (int)exp.AudioMode);
        lines.Int("videoplaybackmode", (int)exp.VideoPlaybackMode);

        var quality = exp.ConnectionQuality;
        lines.Int("connection type", quality == ConnectionQuality.AutoDetect ? 7 : (int)quality);
        // A fixed connection type is ignored while the stack is still auto-detecting the link.
        lines.Bool("networkautodetect", exp.NetworkAutoDetect && quality == ConnectionQuality.AutoDetect);
        lines.Bool("bandwidthautodetect", exp.BandwidthAutoDetect);

        lines.Int("displayconnectionbar", 1);
        lines.Int("enableworkspacereconnect", 0);

        lines.Bool("disable wallpaper", !exp.ShowWallpaper);
        lines.Bool("allow font smoothing", exp.FontSmoothing);
        lines.Bool("allow desktop composition", exp.DesktopComposition);
        lines.Bool("disable full window drag", !exp.FullWindowDrag);
        lines.Bool("disable menu anims", !exp.MenuAnimations);
        lines.Bool("disable themes", !exp.VisualStyles);
        lines.Bool("disable cursor setting", !exp.CursorShadow);
        lines.Bool("bitmapcachepersistenable", exp.PersistentBitmapCache);

        var address = FormatAddress(connection);
        if (address.Length != 0)
        {
            lines.Text("full address", address);
            lines.Text("alternate full address", address);
        }
        else
        {
            AppLog.Warn($"RdpFileBuilder: connection '{connection.Name}' has no host, the .rdp file cannot connect.");
        }

        var credential = context.Credential;
        var userName = string.Empty;
        if (credential is not null && !string.IsNullOrWhiteSpace(credential.Username))
        {
            var bare = Clean(credential.Username);
            // A login that already carries its own domain must not be qualified a second time.
            userName = HasDomainPart(bare) ? bare : Clean(credential.GetLogonName(connection.Host));
            if (userName.Length != 0)
            {
                // The vault entry is written with exactly this name, so the file has to say the
                // same thing or mstsc treats it as a different login and prompts.
                lines.Text("username", userName);
                if (!HasDomainPart(userName)) lines.Optional("domain", credential.Domain);
            }
        }

        if (context.EmbedPassword && !string.IsNullOrEmpty(context.PlainPassword))
        {
            try
            {
                var blob = _protector.ProtectForRdpFile(context.PlainPassword!);
                if (!string.IsNullOrWhiteSpace(blob)) lines.Binary("password 51", blob);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"RdpFileBuilder: could not embed the password for '{connection.Name}'.", ex);
            }
        }

        var mustPrompt = connection.CredentialDelivery == CredentialDelivery.Prompt || userName.Length == 0;
        lines.Bool("prompt for credentials", mustPrompt);
        lines.Int("authentication level", (int)sec.AuthenticationLevel);
        lines.Bool("enablecredsspsupport", sec.EnableCredSsp);
        lines.Int("negotiate security layer", 1);
        lines.Bool("administrative session", sec.AdministrativeSession);
        lines.Bool("autoreconnection enabled", exp.AutoReconnection);
        lines.Bool("public mode", sec.PublicMode);

        lines.Bool("redirectclipboard", red.Clipboard);
        lines.Bool("redirectprinters", red.Printers);
        lines.Bool("redirectcomports", red.Ports);
        lines.Bool("redirectsmartcards", red.SmartCards);
        lines.Int("redirectposdevices", 0);
        lines.Int("redirectdirectx", 1);
        if (red.PnpDevices) lines.Text("devicestoredirect", "*");
        if (red.RedirectDrives) lines.Text("drivestoredirect", FallbackToAll(red.DriveList));
        if (red.RedirectCameras) lines.Text("camerastoredirect", FallbackToAll(red.CameraList));
        lines.Bool("redirectwebauthn", red.WebAuthn);
        lines.Bool("redirectlocation", red.Location);

        var gatewayHost = CleanHost(gw.HostName);
        var gatewayMethod = gw.UsageMethod;
        if (gatewayHost.Length == 0)
            gatewayMethod = GatewayUsageMethod.DoNotUse;
        else if (gatewayMethod == GatewayUsageMethod.AlwaysUse && gw.BypassForLocalAddresses)
            gatewayMethod = GatewayUsageMethod.UseForNonLocal;

        if (gatewayHost.Length != 0) lines.Text("gatewayhostname", gatewayHost);
        lines.Int("gatewayusagemethod", (int)gatewayMethod);
        lines.Int("gatewaycredentialssource", (int)gw.CredentialSource);
        lines.Int("gatewayprofileusagemethod", 1);
        lines.Int("gatewaybrokeringtype", 0);
        lines.Bool("promptcredentialonce", sec.PromptForCredentialsOnce);

        lines.Int("use redirection server name", 0);
        lines.Int("rdgiskdcproxy", 0);
        lines.Text("kdcproxyname", string.Empty);

        lines.Bool("remoteapplicationmode", sec.RemoteAppMode);
        if (sec.RemoteAppMode)
        {
            lines.Optional("remoteapplicationname", sec.RemoteApplicationName);
            lines.Optional("remoteapplicationprogram", sec.RemoteApplicationProgram);
            lines.Optional("remoteapplicationcmdline", sec.RemoteApplicationCmdLine);
        }

        var shell = Clean(sec.AlternateShell);
        if (shell.Length == 0 && sec.RemoteAppMode)
        {
            // Without a shell the server opens a desktop instead of the app: prefer the program
            // alias mstsc itself writes, and fall back to the RemoteApp bootstrapper.
            shell = Clean(sec.RemoteApplicationProgram);
            if (shell.Length == 0) shell = "rdpinit.exe";
        }
        if (shell.Length != 0) lines.Text("alternate shell", shell);
        lines.Optional("shell working directory", sec.ShellWorkingDirectory);
        lines.Optional("loadbalanceinfo", sec.LoadBalanceInfo);

        MergeCustom(lines, connection.CustomProperties);
        MergeCustom(lines, context.ExtraProperties);

        var sb = new StringBuilder(lines.Count * 28);
        lines.Render(sb);
        return sb.ToString();
    }

    public string WriteToTempFile(RdpConnection connection, RdpBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var body = Build(connection, context);
        var directory = Path.Combine(AppLog.DataDirectory, "sessions");
        Directory.CreateDirectory(directory);

        var shortId = Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(directory, $"{SanitizeFileName(connection.Name)}-{shortId}.rdp");
        File.WriteAllText(path, body, FileEncoding);
        return path;
    }

    public void WriteToFile(RdpConnection connection, RdpBuildContext context, string path)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A destination path is required.", nameof(path));

        var body = Build(connection, context);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, body, FileEncoding);
    }

    public RdpConnection Parse(string rdpFileContent, string fallbackName)
    {
        var named = !string.IsNullOrWhiteSpace(fallbackName);
        var conn = new RdpConnection
        {
            Name = named ? fallbackName.Trim() : "Imported connection",
            Port = DefaultPort,
        };
        if (string.IsNullOrWhiteSpace(rdpFileContent)) return conn;

        string? fullAddress = null;
        string? alternateAddress = null;
        string? userName = null;
        string? domain = null;
        var monitorsSeen = false;
        var multimonSeen = false;

        var rows = rdpFileContent.Split('\n');
        for (var r = 0; r < rows.Length; r++)
        {
            var raw = rows[r].Trim('\r', ' ', '\t');
            if (raw.Length == 0 || raw[0] == '#' || raw[0] == ';') continue;

            var firstColon = raw.IndexOf(':');
            var secondColon = firstColon < 0 ? -1 : raw.IndexOf(':', firstColon + 1);
            if (firstColon <= 0 || secondColon < 0)
            {
                AppLog.Warn($"RdpFileBuilder: skipping malformed .rdp line '{Truncate(raw)}'.");
                continue;
            }

            var name = raw[..firstColon].Trim().ToLowerInvariant();
            var typeText = raw.Substring(firstColon + 1, secondColon - firstColon - 1).Trim();
            var value = raw[(secondColon + 1)..].Trim();

            if (name.Length == 0 || typeText.Length != 1)
            {
                AppLog.Warn($"RdpFileBuilder: skipping malformed .rdp line '{Truncate(raw)}'.");
                continue;
            }

            var type = char.ToLowerInvariant(typeText[0]);
            if (type is not ('i' or 's' or 'b'))
            {
                AppLog.Warn($"RdpFileBuilder: skipping .rdp line with unknown type '{typeText}' ('{Truncate(raw)}').");
                continue;
            }

            try
            {
                switch (name)
                {
                    case "screen mode id":
                        conn.Display.ScreenMode = ToInt(value, 2) == 1 ? ScreenMode.Windowed : ScreenMode.Fullscreen;
                        break;
                    case "use multimon":
                        conn.Display.UseAllMonitors = ToBool(value);
                        multimonSeen = true;
                        break;
                    case "selectedmonitors":
                        ParseMonitorList(value, conn.Display.SelectedMonitors);
                        monitorsSeen = conn.Display.SelectedMonitors.Count > 0;
                        break;
                    case "desktopwidth":
                        conn.Display.DesktopWidth = Clamp(ToInt(value, 1920), MinDesktopEdge, MaxDesktopEdge, 1920);
                        break;
                    case "desktopheight":
                        conn.Display.DesktopHeight = Clamp(ToInt(value, 1080), MinDesktopEdge, MaxDesktopEdge, 1080);
                        break;
                    case "session bpp":
                        conn.Display.ColorDepth = NormalizeColorDepth(ToInt(value, 32));
                        break;
                    case "smart sizing":
                        conn.Display.SmartSizing = ToBool(value);
                        break;
                    case "dynamic resolution":
                        conn.Display.DynamicResolution = ToBool(value);
                        break;
                    case "desktopscalefactor":
                        conn.Display.DesktopScaleFactor = NormalizeDesktopScale(ToInt(value, 100));
                        break;
                    case "devicescalefactor":
                        conn.Display.DeviceScaleFactor = NormalizeDeviceScale(ToInt(value, 100));
                        break;
                    case "winposstr":
                        ApplyWindowPosition(conn.Display, value);
                        break;
                    case "compression":
                        conn.Experience.Compression = ToBool(value);
                        break;
                    case "keyboardhook":
                        conn.Redirection.KeyboardHook = Math.Clamp(ToInt(value, 2), 0, 2);
                        break;
                    case "audiocapturemode":
                        conn.Experience.AudioCaptureMode = ToBool(value)
                            ? AudioCaptureMode.CaptureFromThisComputer
                            : AudioCaptureMode.DoNotCapture;
                        break;
                    case "audiomode":
                        conn.Experience.AudioMode = (AudioMode)Math.Clamp(ToInt(value, 0), 0, 2);
                        break;
                    case "videoplaybackmode":
                        conn.Experience.VideoPlaybackMode = ToBool(value)
                            ? VideoPlaybackMode.MultimediaRedirection
                            : VideoPlaybackMode.Legacy;
                        break;
                    case "connection type":
                    {
                        var quality = ToInt(value, 7);
                        conn.Experience.ConnectionQuality = quality is >= 1 and <= 6
                            ? (ConnectionQuality)quality
                            : ConnectionQuality.AutoDetect;
                        break;
                    }
                    case "networkautodetect":
                        conn.Experience.NetworkAutoDetect = ToBool(value);
                        break;
                    case "bandwidthautodetect":
                        conn.Experience.BandwidthAutoDetect = ToBool(value);
                        break;
                    case "disable wallpaper":
                        conn.Experience.ShowWallpaper = !ToBool(value);
                        break;
                    case "allow font smoothing":
                        conn.Experience.FontSmoothing = ToBool(value);
                        break;
                    case "allow desktop composition":
                        conn.Experience.DesktopComposition = ToBool(value);
                        break;
                    case "disable full window drag":
                        conn.Experience.FullWindowDrag = !ToBool(value);
                        break;
                    case "disable menu anims":
                        conn.Experience.MenuAnimations = !ToBool(value);
                        break;
                    case "disable themes":
                        conn.Experience.VisualStyles = !ToBool(value);
                        break;
                    case "disable cursor setting":
                        conn.Experience.CursorShadow = !ToBool(value);
                        break;
                    case "bitmapcachepersistenable":
                        conn.Experience.PersistentBitmapCache = ToBool(value);
                        break;
                    case "full address":
                        fullAddress = value;
                        break;
                    case "alternate full address":
                        alternateAddress = value;
                        break;
                    case "username":
                        userName = value;
                        break;
                    case "domain":
                        domain = value;
                        break;
                    case "prompt for credentials":
                        if (ToBool(value)) conn.CredentialDelivery = CredentialDelivery.Prompt;
                        break;
                    case "promptcredentialonce":
                        conn.Security.PromptForCredentialsOnce = ToBool(value);
                        break;
                    case "authentication level":
                        conn.Security.AuthenticationLevel = (AuthenticationLevel)Math.Clamp(ToInt(value, 2), 0, 3);
                        break;
                    case "enablecredsspsupport":
                        conn.Security.EnableCredSsp = ToBool(value);
                        break;
                    case "administrative session":
                        conn.Security.AdministrativeSession = ToBool(value);
                        break;
                    case "autoreconnection enabled":
                        conn.Experience.AutoReconnection = ToBool(value);
                        break;
                    case "public mode":
                        conn.Security.PublicMode = ToBool(value);
                        break;
                    case "redirectclipboard":
                        conn.Redirection.Clipboard = ToBool(value);
                        break;
                    case "redirectprinters":
                        conn.Redirection.Printers = ToBool(value);
                        break;
                    case "redirectcomports":
                        conn.Redirection.Ports = ToBool(value);
                        break;
                    case "redirectsmartcards":
                        conn.Redirection.SmartCards = ToBool(value);
                        break;
                    case "devicestoredirect":
                        conn.Redirection.PnpDevices = value.Length != 0;
                        break;
                    case "drivestoredirect":
                        conn.Redirection.RedirectDrives = value.Length != 0;
                        if (value.Length != 0) conn.Redirection.DriveList = value;
                        break;
                    case "camerastoredirect":
                        conn.Redirection.RedirectCameras = value.Length != 0;
                        if (value.Length != 0) conn.Redirection.CameraList = value;
                        break;
                    case "redirectwebauthn":
                        conn.Redirection.WebAuthn = ToBool(value);
                        break;
                    case "redirectlocation":
                        conn.Redirection.Location = ToBool(value);
                        break;
                    case "gatewayhostname":
                        conn.Gateway.HostName = NullIfEmpty(value);
                        break;
                    case "gatewayusagemethod":
                    {
                        var method = Math.Clamp(ToInt(value, 0), 0, 4);
                        conn.Gateway.UsageMethod = (GatewayUsageMethod)method;
                        conn.Gateway.BypassForLocalAddresses = method == (int)GatewayUsageMethod.UseForNonLocal;
                        break;
                    }
                    case "gatewaycredentialssource":
                        conn.Gateway.CredentialSource = ToInt(value, 0) switch
                        {
                            1 => GatewayCredentialSource.SmartCard,
                            4 => GatewayCredentialSource.UseConnectionCredentials,
                            _ => GatewayCredentialSource.AskForPassword,
                        };
                        break;
                    case "use redirection server name":
                        if (ToBool(value)) conn.CustomProperties["use redirection server name:i"] = "1";
                        break;
                    case "rdgiskdcproxy":
                        if (ToBool(value)) conn.CustomProperties["rdgiskdcproxy:i"] = "1";
                        break;
                    case "kdcproxyname":
                        if (value.Length != 0) conn.CustomProperties["kdcproxyname:s"] = value;
                        break;
                    case "remoteapplicationmode":
                        conn.Security.RemoteAppMode = ToBool(value);
                        break;
                    case "remoteapplicationname":
                        conn.Security.RemoteApplicationName = NullIfEmpty(value);
                        break;
                    case "remoteapplicationprogram":
                        conn.Security.RemoteApplicationProgram = NullIfEmpty(value);
                        break;
                    case "remoteapplicationcmdline":
                        conn.Security.RemoteApplicationCmdLine = NullIfEmpty(value);
                        break;
                    case "alternate shell":
                        conn.Security.AlternateShell = NullIfEmpty(value);
                        break;
                    case "shell working directory":
                        conn.Security.ShellWorkingDirectory = NullIfEmpty(value);
                        break;
                    case "loadbalanceinfo":
                        conn.Security.LoadBalanceInfo = NullIfEmpty(value);
                        break;

                    // Understood, but carrying no state worth importing.
                    case "password 51":
                    case "span monitors":
                    case "displayconnectionbar":
                    case "enableworkspacereconnect":
                    case "negotiate security layer":
                    case "redirectposdevices":
                    case "redirectdirectx":
                    case "gatewayprofileusagemethod":
                    case "gatewaybrokeringtype":
                        break;

                    default:
                        conn.CustomProperties[$"{name}:{type}"] = value;
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"RdpFileBuilder: could not apply .rdp line '{Truncate(raw)}'.", ex);
            }
        }

        var address = !string.IsNullOrWhiteSpace(fullAddress) ? fullAddress : alternateAddress;
        if (!string.IsNullOrWhiteSpace(address))
        {
            SplitAddress(address!, out var host, out var port);
            conn.Host = host;
            conn.Port = port;
            // The caller's name (normally the file name) wins; the host is only a last resort.
            if (!named && host.Length != 0) conn.Name = host;
        }

        // The connection itself has no login fields; keep what the file carried so the import
        // screen can offer it when the user picks or creates a credential set.
        if (!string.IsNullOrWhiteSpace(userName)) conn.CustomProperties["username:s"] = userName!.Trim();
        if (!string.IsNullOrWhiteSpace(domain)) conn.CustomProperties["domain:s"] = domain!.Trim();

        // A monitor list only takes effect under multimon; exports sometimes omit the flag.
        if (monitorsSeen && !multimonSeen) conn.Display.UseAllMonitors = true;

        return conn;
    }

    private static ScreenMode ResolveScreenMode(DisplaySettings display) => display.Placement switch
    {
        WindowPlacementMode.SpecificMonitorFullscreen => ScreenMode.Fullscreen,
        WindowPlacementMode.SpanAllMonitors => ScreenMode.Fullscreen,
        WindowPlacementMode.SelectedMonitors => ScreenMode.Fullscreen,
        WindowPlacementMode.SpecificMonitorMaximized => ScreenMode.Windowed,
        WindowPlacementMode.CustomRectangle => ScreenMode.Windowed,
        _ => display.ScreenMode == ScreenMode.Windowed && !display.UseAllMonitors
            ? ScreenMode.Windowed
            : ScreenMode.Fullscreen,
    };

    /// <summary>
    /// The session resolution to request. An explicit placement decides it: a custom rectangle
    /// is the window, a monitor placement is that monitor - otherwise the stored size wins.
    /// </summary>
    private static void ResolveDesktopSize(
        DisplaySettings display, IReadOnlyList<MonitorInfo>? monitors, out int width, out int height)
    {
        if (display.Placement == WindowPlacementMode.CustomRectangle)
        {
            width = Clamp(display.CustomWidth, MinDesktopEdge, MaxDesktopEdge, 1280);
            height = Clamp(display.CustomHeight, MinDesktopEdge, MaxDesktopEdge, 800);
            return;
        }

        if (display.Placement is WindowPlacementMode.SpecificMonitorFullscreen
            or WindowPlacementMode.SpecificMonitorMaximized)
        {
            var monitor = PickMonitor(monitors, display);
            if (monitor is not null)
            {
                var full = display.Placement == WindowPlacementMode.SpecificMonitorFullscreen;
                var w = full || monitor.WorkWidth <= 0 ? monitor.Width : monitor.WorkWidth;
                var h = full || monitor.WorkHeight <= 0 ? monitor.Height : monitor.WorkHeight;
                width = Clamp(w, MinDesktopEdge, MaxDesktopEdge, 1920);
                height = Clamp(h, MinDesktopEdge, MaxDesktopEdge, 1080);
                return;
            }
        }

        width = Clamp(display.DesktopWidth, MinDesktopEdge, MaxDesktopEdge, 1920);
        height = Clamp(display.DesktopHeight, MinDesktopEdge, MaxDesktopEdge, 1080);
    }

    /// <summary>
    /// Host names get a stricter scrub than ordinary text. Stripping CR/LF alone still lets a
    /// pasted or imported value carry trailing text into the line, so only the first whitespace
    /// token survives and anything outside the character set of a DNS name or IP literal is
    /// dropped. A malformed host then simply fails to resolve instead of quietly resolving
    /// somewhere unintended.
    /// </summary>
    private static string CleanHost(string? value)
    {
        var host = Clean(value);
        if (host.Length == 0) return string.Empty;

        var end = 0;
        while (end < host.Length && !char.IsWhiteSpace(host[end])) end++;

        var sb = new StringBuilder(end);
        for (var i = 0; i < end; i++)
        {
            var c = host[i];
            // Unicode letters are kept so internationalised host names survive intact.
            if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']' or '%')
                sb.Append(c);
        }

        var cleaned = sb.ToString();
        if (cleaned.Length != host.Length)
        {
            AppLog.Warn(
                $"RdpFileBuilder: host '{Truncate(host)}' contained characters that cannot appear in a " +
                $"host name; the address was reduced to '{Truncate(cleaned)}'.");
        }

        return cleaned;
    }

    private static string FormatAddress(RdpConnection connection)
    {
        var host = CleanHost(connection.Host);
        if (host.Length == 0) return string.Empty;

        var port = connection.Port;
        if (port <= 0 || port == DefaultPort) return host;

        var portText = port.ToString(CultureInfo.InvariantCulture);
        // A bare IPv6 literal has to be bracketed before a port can be appended to it.
        return host[0] != '[' && host.IndexOf(':') >= 0
            ? $"[{host}]:{portText}"
            : $"{host}:{portText}";
    }

    private static bool HasDomainPart(string userName) =>
        userName.IndexOf('\\') >= 0 || userName.IndexOf('@') >= 0;

    /// <summary>
    /// Login lines are owned by the credential set, never by a custom property: a stale
    /// "username" carried in from an import would otherwise override the chosen credential.
    /// </summary>
    private static bool IsCredentialProperty(string name) =>
        name.Equals("username", StringComparison.OrdinalIgnoreCase)
        || name.Equals("domain", StringComparison.OrdinalIgnoreCase)
        || name.Equals("password 51", StringComparison.OrdinalIgnoreCase);

    private static string BuildWindowPosition(
        DisplaySettings display,
        IReadOnlyList<MonitorInfo>? monitors,
        ScreenMode screenMode,
        int desktopWidth,
        int desktopHeight)
    {
        var showCmd = display.Placement switch
        {
            WindowPlacementMode.SpecificMonitorMaximized => 3,
            WindowPlacementMode.SpecificMonitorFullscreen => 3,
            WindowPlacementMode.CustomRectangle => 1,
            _ => screenMode == ScreenMode.Fullscreen ? 3 : 1,
        };

        int left, top, right, bottom;

        if (display.Placement == WindowPlacementMode.CustomRectangle)
        {
            left = display.CustomLeft;
            top = display.CustomTop;
            right = left + desktopWidth;
            bottom = top + desktopHeight;
        }
        else
        {
            var monitor = PickMonitor(monitors, display);
            if (monitor is null)
            {
                left = 0;
                top = 0;
                right = desktopWidth;
                bottom = desktopHeight;
            }
            else if (display.Placement == WindowPlacementMode.SpecificMonitorFullscreen)
            {
                left = monitor.Left;
                top = monitor.Top;
                right = monitor.Right;
                bottom = monitor.Bottom;
            }
            else
            {
                var workWidth = monitor.WorkWidth > 0 ? monitor.WorkWidth : monitor.Width;
                var workHeight = monitor.WorkHeight > 0 ? monitor.WorkHeight : monitor.Height;
                var width = desktopWidth;
                var height = desktopHeight;
                if (workWidth > 0 && width > workWidth) width = workWidth;
                if (workHeight > 0 && height > workHeight) height = workHeight;

                left = monitor.WorkLeft + Math.Max(0, (workWidth - width) / 2);
                top = monitor.WorkTop + Math.Max(0, (workHeight - height) / 2);
                right = left + width;
                bottom = top + height;
            }
        }

        var sb = new StringBuilder(48);
        sb.Append('0').Append(',').Append(IntString(showCmd)).Append(',')
          .Append(left.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(top.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(right.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(bottom.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static MonitorInfo? PickMonitor(IReadOnlyList<MonitorInfo>? monitors, DisplaySettings display)
    {
        if (monitors is null || monitors.Count == 0) return null;

        if (display.Placement is WindowPlacementMode.SpecificMonitorFullscreen
            or WindowPlacementMode.SpecificMonitorMaximized)
        {
            var index = display.TargetMonitorIndex;
            if (index >= 0 && index < monitors.Count) return monitors[index];
            for (var i = 0; i < monitors.Count; i++)
                if (monitors[i].Index == index) return monitors[i];
        }

        for (var i = 0; i < monitors.Count; i++)
            if (monitors[i].IsPrimary) return monitors[i];

        return monitors[0];
    }

    private static void ApplyWindowPosition(DisplaySettings display, string value)
    {
        var parts = value.Split(',');
        if (parts.Length < 6) return;

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var right) ||
            !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bottom))
        {
            return;
        }

        var width = right - left;
        var height = bottom - top;
        if (width < MinDesktopEdge || height < MinDesktopEdge) return;

        display.CustomLeft = left;
        display.CustomTop = top;
        display.CustomWidth = width;
        display.CustomHeight = height;
    }

    private static void ParseMonitorList(string value, List<int> target)
    {
        target.Clear();
        if (value.Length == 0) return;

        var parts = value.Split(MonitorListSeparators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            if (id < 0 || target.Contains(id)) continue;
            target.Add(id);
        }
    }

    private static string JoinMonitorIds(List<int> ids)
    {
        var sb = new StringBuilder(ids.Count * 3);
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] < 0) continue;
            if (sb.Length != 0) sb.Append(',');
            sb.Append(ids[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static void SplitAddress(string address, out string host, out int port)
    {
        port = DefaultPort;
        host = address.Trim();
        if (host.Length == 0) return;

        if (host[0] == '[')
        {
            var close = host.IndexOf(']');
            if (close <= 0) return;

            var rest = host[(close + 1)..];
            host = host.Substring(1, close - 1);
            if (rest.Length > 1 && rest[0] == ':' &&
                int.TryParse(rest[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bracketed) &&
                bracketed is > 0 and <= 65535)
            {
                port = bracketed;
            }
            return;
        }

        var last = host.LastIndexOf(':');
        // More than one colon means a bare IPv6 literal, which carries no port.
        if (last <= 0 || host.IndexOf(':') != last) return;

        if (int.TryParse(host[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is > 0 and <= 65535)
        {
            port = parsed;
            host = host[..last];
        }
    }

    private static void MergeCustom(LineSet lines, Dictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0) return;

        foreach (var pair in properties)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            SplitCustomKey(pair.Key, pair.Value, out var name, out var type);
            if (name.Length == 0 || IsCredentialProperty(name)) continue;
            lines.Set(name, type, Clean(pair.Value));
        }
    }

    private static void SplitCustomKey(string key, string? value, out string name, out char type)
    {
        var trimmed = key.Trim().TrimEnd(':').Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator > 0 && separator == trimmed.Length - 2)
        {
            var candidate = char.ToLowerInvariant(trimmed[^1]);
            if (candidate is 'i' or 's' or 'b')
            {
                name = trimmed[..separator].Trim();
                type = candidate;
                return;
            }
        }

        name = trimmed;
        type = int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? 'i' : 's';
    }

    private static string SanitizeFileName(string? name)
    {
        var source = string.IsNullOrWhiteSpace(name) ? "session" : name.Trim();
        var sb = new StringBuilder(Math.Min(source.Length, 60));

        for (var i = 0; i < source.Length && sb.Length < 60; i++)
        {
            var ch = source[i];
            var bad = ch < ' ';
            if (!bad)
            {
                for (var j = 0; j < InvalidFileNameChars.Length; j++)
                {
                    if (InvalidFileNameChars[j] != ch) continue;
                    bad = true;
                    break;
                }
            }
            sb.Append(bad ? '_' : ch);
        }

        var result = sb.ToString().Trim().Trim('.');
        return result.Length == 0 ? "session" : result;
    }

    private static string FallbackToAll(string? list)
    {
        var cleaned = Clean(list);
        return cleaned.Length == 0 ? "*" : cleaned;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static int NormalizeColorDepth(int bpp) => bpp switch
    {
        <= 8 => 8,
        <= 15 => 15,
        <= 16 => 16,
        <= 24 => 24,
        _ => 32,
    };

    private static int NormalizeDesktopScale(int value)
    {
        if (value <= 100) return 100;

        var best = 100;
        var bestDelta = int.MaxValue;
        for (var i = 0; i < DesktopScaleSteps.Length; i++)
        {
            var delta = Math.Abs(DesktopScaleSteps[i] - value);
            if (delta >= bestDelta) continue;
            bestDelta = delta;
            best = DesktopScaleSteps[i];
        }
        return best;
    }

    private static int NormalizeDeviceScale(int value) => value >= 180 ? 180 : value >= 140 ? 140 : 100;

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value <= 0) return fallback;
        if (value < min) return min;
        return value > max ? max : value;
    }

    private static int ToInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool ToBool(string value)
    {
        if (value.Length == 0) return false;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed != 0;
        return bool.TryParse(value, out var flag) && flag;
    }

    private static string Truncate(string text) => text.Length <= 120 ? text : text[..120];

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var hasBreak = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\r' && c != '\n') continue;
            hasBreak = true;
            break;
        }

        if (!hasBreak) return value.Trim();

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\r' || c == '\n') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string IntString(int value) =>
        (uint)value < (uint)SmallInts.Length ? SmallInts[value] : value.ToString(CultureInfo.InvariantCulture);

    private static string[] CreateSmallInts()
    {
        var values = new string[65];
        for (var i = 0; i < values.Length; i++) values[i] = i.ToString(CultureInfo.InvariantCulture);
        return values;
    }

    /// <summary>
    /// Ordered set of .rdp lines in which writing a name that already exists replaces the value
    /// in place, so a custom property overrides a generated one instead of duplicating the line.
    /// </summary>
    private sealed class LineSet
    {
        private readonly List<Entry> _entries = new(96);
        private readonly Dictionary<string, int> _index = new(96, StringComparer.OrdinalIgnoreCase);

        private struct Entry
        {
            public string Name;
            public char Type;
            public string Value;
        }

        public int Count => _entries.Count;

        public void Set(string name, char type, string value)
        {
            if (_index.TryGetValue(name, out var at))
            {
                var existing = _entries[at];
                existing.Type = type;
                existing.Value = value;
                _entries[at] = existing;
                return;
            }

            _index[name] = _entries.Count;
            _entries.Add(new Entry { Name = name, Type = type, Value = value });
        }

        public void Int(string name, int value) => Set(name, 'i', IntString(value));

        public void Bool(string name, bool value) => Set(name, 'i', value ? "1" : "0");

        public void Text(string name, string? value) => Set(name, 's', Clean(value));

        public void Binary(string name, string value) => Set(name, 'b', Clean(value));

        /// <summary>Writes the line only when there is a value to write.</summary>
        public void Optional(string name, string? value)
        {
            var cleaned = Clean(value);
            if (cleaned.Length != 0) Set(name, 's', cleaned);
        }

        public void Render(StringBuilder sb)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                sb.Append(entry.Name).Append(':').Append(entry.Type).Append(':').Append(entry.Value)
                  .Append('\r').Append('\n');
            }
        }
    }
}
