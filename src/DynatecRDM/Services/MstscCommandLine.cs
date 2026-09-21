using System.Globalization;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Builds a Remote Desktop command line for connections that do not need an .rdp file.
///
/// Windows shows its "unknown publisher" security warning for every unsigned .rdp file, on every
/// launch, and there is no per-user way to switch that off: the warning is not the local-resource
/// consent (so the LocalDevices entry does not help), the policy that allows unsigned files lives
/// in a registry hive users cannot write, and signing the file only moves the problem to trusting
/// the certificate, which needs a machine-wide policy. Started as "mstsc /v:host" instead, with no
/// file argument at all, there is no warning.
///
/// The command line cannot express most .rdp settings, so this is offered only when nothing the
/// user configured would be silently dropped. Anything richer keeps using a file, and
/// <see cref="TryBuild"/> reports which setting forced that.
/// </summary>
public static class MstscCommandLine
{
    /// <summary>
    /// Returns the arguments to start this connection without a file, or null when a file is
    /// required. <paramref name="requiredBy"/> then names the setting that needs one.
    /// </summary>
    public static IReadOnlyList<string>? TryBuild(
        RdpConnection connection,
        DisplaySettings display,
        CredentialDelivery delivery,
        out string? requiredBy)
    {
        requiredBy = Blocker(connection, display, delivery);
        if (requiredBy is not null) return null;

        var args = new List<string>(6);

        var address = connection.Port is 3389 or <= 0
            ? connection.Host.Trim()
            : $"{connection.Host.Trim()}:{connection.Port.ToString(CultureInfo.InvariantCulture)}";
        args.Add("/v:" + address);

        if (connection.Security.AdministrativeSession) args.Add("/admin");
        if (connection.Security.PublicMode) args.Add("/public");

        if (display.UseAllMonitors) args.Add("/multimon");
        else if (display.Placement == WindowPlacementMode.SpanAllMonitors) args.Add("/span");
        else if (display.ScreenMode == ScreenMode.Fullscreen) args.Add("/f");
        else
        {
            args.Add("/w:" + display.DesktopWidth.ToString(CultureInfo.InvariantCulture));
            args.Add("/h:" + display.DesktopHeight.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }

    /// <summary>The first configured setting that only an .rdp file can carry, or null.</summary>
    private static string? Blocker(RdpConnection c, DisplaySettings d, CredentialDelivery delivery)
    {
        if (string.IsNullOrWhiteSpace(c.Host)) return "an empty host";

        // The password can only be embedded in a file. The vault works either way, so only the
        // file-only delivery modes force a file.
        if (delivery == CredentialDelivery.EmbeddedInRdpFile)
            return "the password being embedded in the .rdp file";

        if (c.CustomProperties.Count > 0) return "custom .rdp properties";

        if (c.Gateway.UsageMethod != GatewayUsageMethod.DoNotUse) return "an RD Gateway";

        var s = c.Security;
        if (s.RemoteAppMode) return "RemoteApp mode";
        if (!string.IsNullOrWhiteSpace(s.AlternateShell)) return "an alternate shell";
        if (!string.IsNullOrWhiteSpace(s.ShellWorkingDirectory)) return "a shell working directory";
        if (!string.IsNullOrWhiteSpace(s.LoadBalanceInfo)) return "broker load-balance info";

        var defaults = new SecuritySettings();
        if (s.AuthenticationLevel != defaults.AuthenticationLevel) return "a custom authentication level";
        if (s.EnableCredSsp != defaults.EnableCredSsp) return "a custom CredSSP setting";

        var red = c.Redirection;
        var redDefaults = new RedirectionSettings();
        if (red.Printers != redDefaults.Printers) return "printer redirection";
        if (red.Clipboard != redDefaults.Clipboard) return "a custom clipboard setting";
        if (red.SmartCards != redDefaults.SmartCards) return "smart-card redirection";
        if (red.Ports != redDefaults.Ports) return "port redirection";
        if (red.PnpDevices != redDefaults.PnpDevices) return "plug-and-play redirection";
        if (red.RedirectDrives) return "drive redirection";
        if (red.RedirectCameras) return "camera redirection";
        if (red.WebAuthn != redDefaults.WebAuthn) return "a custom WebAuthn setting";
        if (red.Location != redDefaults.Location) return "location redirection";
        if (red.KeyboardHook != redDefaults.KeyboardHook) return "a custom keyboard setting";

        var exp = c.Experience;
        var expDefaults = new ExperienceSettings();
        if (exp.ConnectionQuality != expDefaults.ConnectionQuality) return "a fixed connection quality";
        if (exp.AudioMode != expDefaults.AudioMode) return "a custom audio setting";
        if (exp.AudioCaptureMode != expDefaults.AudioCaptureMode) return "microphone redirection";
        if (exp.VideoPlaybackMode != expDefaults.VideoPlaybackMode) return "a custom video setting";
        if (exp.ShowWallpaper != expDefaults.ShowWallpaper
            || exp.FontSmoothing != expDefaults.FontSmoothing
            || exp.DesktopComposition != expDefaults.DesktopComposition
            || exp.FullWindowDrag != expDefaults.FullWindowDrag
            || exp.MenuAnimations != expDefaults.MenuAnimations
            || exp.VisualStyles != expDefaults.VisualStyles
            || exp.CursorShadow != expDefaults.CursorShadow)
        {
            return "custom experience settings";
        }

        var displayDefaults = new DisplaySettings();
        if (d.ColorDepth != displayDefaults.ColorDepth) return "a custom colour depth";
        if (d.SmartSizing != displayDefaults.SmartSizing) return "smart sizing";
        if (d.DesktopScaleFactor != displayDefaults.DesktopScaleFactor
            || d.DeviceScaleFactor != displayDefaults.DeviceScaleFactor)
        {
            return "a custom scale factor";
        }

        // selectedmonitors has no command-line equivalent; /multimon takes them all.
        if (d.SelectedMonitors.Count > 0 && d.Placement == WindowPlacementMode.SelectedMonitors)
            return "a specific set of monitors";

        return null;
    }
}
