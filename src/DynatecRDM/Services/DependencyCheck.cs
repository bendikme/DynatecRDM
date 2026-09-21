using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DynatecRDM.Resources;

namespace DynatecRDM.Services;

/// <summary>What is missing, as far as the app can tell.</summary>
public enum DependencyKind
{
    /// <summary>Windows' Remote Desktop control (mstscax.dll) is not there, not registered or blocked.</summary>
    RemoteDesktopControlMissing,
    /// <summary>The control is there but older than the one the in-app client needs.</summary>
    RemoteDesktopControlTooOld,
    /// <summary>The app's own bridge to the control (MSTSCLib.dll, AxMSTSCLib.dll) is missing.</summary>
    InteropFilesMissing,
    /// <summary>Remote Desktop Connection (mstsc.exe) is missing.</summary>
    MstscMissing,
    /// <summary>mstsc.exe is there but Windows refused to start it (policy, access).</summary>
    MstscBlocked,
}

/// <summary>A missing dependency, with what the user can do about it.</summary>
public sealed record DependencyProblem(DependencyKind Kind, string Message);

/// <summary>
/// Checks that what a connection needs is on this PC, and recognises the failures that mean it is
/// not. Everything the app ships installs with it; what can be missing is what Windows provides - the
/// Remote Desktop control for the in-app client, Remote Desktop Connection for the external one - or
/// an app file that was deleted or quarantined afterwards. Either way the user is told what to
/// install or do, instead of seeing "could not be started".
/// </summary>
public static class DependencyCheck
{
    /// <summary>The Remote Desktop control the in-app client hosts (see RdpControlHost).</summary>
    public static readonly Guid RemoteDesktopControlClsid = new("1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8");

    private static readonly string[] InteropFiles = { "MSTSCLib.dll", "AxMSTSCLib.dll" };

    // COM's answers for a class that is not there, not usable, or whose DLL could not be loaded.
    private const int RegdbEClassNotReg = unchecked((int)0x80040154);
    private const int ClassEClassNotAvailable = unchecked((int)0x80040111);
    private const int CoEAppNotFound = unchecked((int)0x800401F5);
    private const int ModuleNotFound = unchecked((int)0x8007007E);

    // Process.Start's answers for mstsc.exe.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBlockedByPolicy = 1260;

    private static readonly object Gate = new();
    private static bool _controlChecked;
    private static DependencyProblem? _controlResult;

    /// <summary>Whatever the client in use needs: the in-app client, or the external mstsc.</summary>
    public static DependencyProblem? CheckClient(bool embedded) => embedded ? CheckEmbeddedClient() : CheckExternalClient();

    /// <summary>
    /// The in-app client: the app's two interop files, and Windows' Remote Desktop control, new
    /// enough. Creates the control once to find out, so call it on the UI (STA) thread; the answer
    /// is kept for the rest of the run.
    /// </summary>
    public static DependencyProblem? CheckEmbeddedClient()
    {
        foreach (var file in InteropFiles)
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, file)))
            {
                AppLog.Warn($"The in-app client cannot run: {file} is missing from {AppContext.BaseDirectory}.");
                return new DependencyProblem(DependencyKind.InteropFilesMissing, Strings.Dependency_InteropMissing);
            }
        }

        lock (Gate)
        {
            if (!_controlChecked)
            {
                _controlResult = ProbeRemoteDesktopControl();
                _controlChecked = true;
            }
            return _controlResult;
        }
    }

    /// <summary>The external client: Remote Desktop Connection in the Windows system folder.</summary>
    public static DependencyProblem? CheckExternalClient()
    {
        var path = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        if (File.Exists(path)) return null;

        AppLog.Warn($"Remote Desktop Connection is missing: {path} does not exist.");
        return new DependencyProblem(DependencyKind.MstscMissing, Strings.Dependency_MstscMissing);
    }

    /// <summary>
    /// The missing dependency <paramref name="ex"/> - or anything it wraps - points to, or null
    /// when it is some other failure.
    /// </summary>
    public static DependencyProblem? FromException(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException
                    when Mentions(current, "MSTSCLib", "AxMSTSCLib"):
                    return new DependencyProblem(DependencyKind.InteropFilesMissing, Strings.Dependency_InteropMissing);

                case COMException com when com.HResult is RegdbEClassNotReg or ClassEClassNotAvailable or CoEAppNotFound or ModuleNotFound:
                    return new DependencyProblem(DependencyKind.RemoteDesktopControlMissing, Strings.Dependency_ControlMissing);

                case DllNotFoundException when Mentions(current, "mstscax"):
                    return new DependencyProblem(DependencyKind.RemoteDesktopControlMissing, Strings.Dependency_ControlMissing);

                // The control was created but lacks the interfaces the app asks for: an older build.
                case System.Windows.Forms.AxHost.InvalidActiveXStateException:
                    return new DependencyProblem(DependencyKind.RemoteDesktopControlTooOld, Strings.Dependency_ControlMissing);

                case Win32Exception win32 when win32.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound:
                    return new DependencyProblem(DependencyKind.MstscMissing, Strings.Dependency_MstscMissing);

                case Win32Exception win32 when win32.NativeErrorCode is ErrorBlockedByPolicy or ErrorAccessDenied:
                    return new DependencyProblem(DependencyKind.MstscBlocked, Strings.Dependency_MstscMissing);
            }
        }
        return null;
    }

    /// <summary>
    /// The message for a failure while the app itself was starting: its database component, or any
    /// other of its own files, missing. Null when the failure is something else.
    /// </summary>
    public static string? DescribeStartupFailure(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return Mentions(current, "sqlite")
                    ? Strings.Dependency_DatabaseMissing
                    : Strings.Dependency_AppFilesMissing;
            }
        }
        return null;
    }

    /// <summary>
    /// Creates the control the way the session window will, and asks it for the display-control
    /// interface the app relies on. Missing class, failed creation and missing interface each get
    /// their own log line, so a report says which it was.
    /// </summary>
    private static DependencyProblem? ProbeRemoteDesktopControl()
    {
        Type? type;
        try
        {
            type = Type.GetTypeFromCLSID(RemoteDesktopControlClsid, throwOnError: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Looking up the Remote Desktop control failed.", ex);
            type = null;
        }

        if (type is null)
        {
            AppLog.Warn($"The Remote Desktop control {{{RemoteDesktopControlClsid}}} is not registered on this PC.");
            return new DependencyProblem(DependencyKind.RemoteDesktopControlMissing, Strings.Dependency_ControlMissing);
        }

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            if (instance is null)
            {
                AppLog.Warn("The Remote Desktop control could not be created.");
                return new DependencyProblem(DependencyKind.RemoteDesktopControlMissing, Strings.Dependency_ControlMissing);
            }

            if (!ExposesDisplayControl(instance))
            {
                AppLog.Warn("The Remote Desktop control on this PC is too old: it does not offer IMsRdpClient10.");
                return new DependencyProblem(DependencyKind.RemoteDesktopControlTooOld, Strings.Dependency_ControlMissing);
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The Remote Desktop control could not be created.", ex);
            return FromException(ex) ?? new DependencyProblem(DependencyKind.RemoteDesktopControlMissing, Strings.Dependency_ControlMissing);
        }
        finally
        {
            try
            {
                if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
            catch
            {
                // Released or never fully created; nothing to clean up.
            }
        }
    }

    /// <summary>Kept apart so MSTSCLib.dll is only loaded here, after the file check has passed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ExposesDisplayControl(object instance) => instance is MSTSCLib.IMsRdpClient10;

    private static bool Mentions(Exception ex, params string[] names)
    {
        var text = string.Join(" ",
            (ex as FileNotFoundException)?.FileName,
            (ex as FileLoadException)?.FileName,
            (ex as BadImageFormatException)?.FileName,
            (ex as TypeLoadException)?.TypeName,
            ex.Message);
        foreach (var name in names)
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>For code written before the problems were told apart.</summary>
    public static bool IsRemoteDesktopControlFailure(Exception? ex) =>
        FromException(ex)?.Kind is DependencyKind.RemoteDesktopControlMissing
            or DependencyKind.RemoteDesktopControlTooOld or DependencyKind.InteropFilesMissing;
}
