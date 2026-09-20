using System.Diagnostics;
using Microsoft.Win32;

namespace DynatecRDM.Services;

/// <summary>Registers the app under HKCU Run so it comes back with the user's session.</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynatecRDM";

    /// <summary>Argument that tells the app to start hidden in the notification area.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>True when a Run entry exists for this app.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read the Windows startup entry.", ex);
                return false;
            }
        }
    }

    /// <summary>The command line currently registered, or null when there is none.</summary>
    public static string? RegisteredCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) as string;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read the Windows startup entry.", ex);
                return null;
            }
        }
    }

    public static bool SetEnabled(bool enabled) => enabled ? Enable() : Disable();

    /// <summary>Writes (or refreshes) the Run entry. Returns false when the exe path is unknown.</summary>
    public static bool Enable()
    {
        var command = BuildCommand();
        if (command is null)
        {
            AppLog.Warn("Cannot enable launch at logon: the executable path is unknown.");
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                AppLog.Warn("Cannot enable launch at logon: the Run key is unavailable.");
                return false;
            }

            if (key.GetValue(ValueName) as string == command) return true;

            key.SetValue(ValueName, command, RegistryValueKind.String);
            AppLog.Info($"Launch at logon enabled: {command}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Enabling launch at logon failed.", ex);
            return false;
        }
    }

    /// <summary>Removes the Run entry. Succeeds when there was nothing to remove.</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return true;
            if (key.GetValue(ValueName) is null) return true;

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            AppLog.Info("Launch at logon disabled.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Disabling launch at logon failed.", ex);
            return false;
        }
    }

    /// <summary>Rewrites the entry when it is enabled but points somewhere stale (after a move or update).</summary>
    public static bool Refresh() => !IsEnabled || Enable();

    private static string? BuildCommand()
    {
        var exe = ResolveExecutablePath();
        return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\" {TrayArgument}";
    }

    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)) return path;

        try
        {
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not resolve the executable path.", ex);
            return null;
        }
    }
}
