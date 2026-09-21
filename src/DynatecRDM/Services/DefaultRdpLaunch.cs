using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DynatecRDM.Services;

/// <summary>
/// Starts a connection with its full settings and without Remote Desktop's security warning.
///
/// Passing an .rdp file on the command line always warns, because the file is unsigned and a user
/// cannot permanently accept that. Started as "mstsc /v:host" with no file there is no warning at
/// all - but the command line can only express a handful of settings.
///
/// Remote Desktop also reads Documents\Default.rdp when it starts without a file. Putting the
/// connection's settings there and launching with no file argument gives both: every setting
/// applies, and no file is named, so no warning. The one thing that still prompts is local device
/// redirection - COM ports, plug-and-play devices, drives - which raises the consent dialog until
/// the host is recorded under LocalDevices, exactly as the "don't ask me again" box would.
///
/// Default.rdp belongs to the user and to Remote Desktop, so it is borrowed rather than owned:
/// backed up byte for byte, replaced, and restored once mstsc has read it. A named mutex serialises
/// launches, and a leftover backup is restored at startup if this process ever dies mid-launch.
/// </summary>
public static class DefaultRdpLaunch
{
    private const string MutexName = @"Local\DynatecRDM.DefaultRdp";
    private const string LocalDevicesKey = @"Software\Microsoft\Terminal Server Client\LocalDevices";

    /// <summary>How long mstsc is given to read Default.rdp before it is put back.</summary>
    private const int ReadGraceMs = 3000;

    private static string DefaultRdpPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Default.rdp");

    private static string BackupPath =>
        Path.Combine(AppLog.DataDirectory, "Default.rdp.backup");

    /// <summary>
    /// Restores a Default.rdp left behind by a previous run that did not finish. Called once at
    /// startup; doing nothing is the normal case.
    /// </summary>
    public static void RecoverIfInterrupted()
    {
        try
        {
            var backup = BackupPath;
            if (!File.Exists(backup)) return;

            WriteBytesPreservingAttributes(DefaultRdpPath, File.ReadAllBytes(backup));
            File.Delete(backup);
            AppLog.Warn("A previous run was interrupted mid-launch; Default.rdp has been restored.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not restore Default.rdp after an interrupted run.", ex);
        }
    }

    /// <summary>
    /// True when the user's own Default.rdp stops the session from following the window size.
    /// Started without a file, Remote Desktop takes every setting the command line does not name
    /// from there, so a connection that wants dynamic resolution cannot rely on it.
    /// </summary>
    public static bool DisablesDynamicResolution()
    {
        try
        {
            var path = DefaultRdpPath;
            if (!File.Exists(path)) return false;

            foreach (var line in File.ReadLines(path))
            {
                var setting = line.Trim();
                if (setting.Equals("dynamic resolution:i:0", StringComparison.OrdinalIgnoreCase) ||
                    setting.Equals("smart sizing:i:1", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Reading Default.rdp for its display settings failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// True unless the user's own Default.rdp unpins the full-screen connection bar. Remote
    /// Desktop pins it when nothing says otherwise, so a missing file or line counts as pinned.
    /// </summary>
    public static bool PinsConnectionBar()
    {
        try
        {
            var path = DefaultRdpPath;
            if (!File.Exists(path)) return true;

            foreach (var line in File.ReadLines(path))
            {
                if (line.Trim().Equals("pinconnectionbar:i:0", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Reading Default.rdp for its connection bar failed: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// True when this host has already been allowed to use local devices, so the consent dialog
    /// will not appear.
    /// </summary>
    public static bool IsHostTrusted(string host)
    {
        var name = BareHost(host);
        if (name.Length == 0) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LocalDevicesKey);
            return key?.GetValue(name) is int value && value != 0;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Reading the local-devices entry for {name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Records that this host may use local devices - the same answer the "don't ask me again"
    /// checkbox stores. Without it, a connection that redirects devices asks on every launch.
    /// </summary>
    public static bool TrustHost(string host)
    {
        var name = BareHost(host);
        if (name.Length == 0) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LocalDevicesKey, writable: true);
            if (key is null) return false;

            key.SetValue(name, unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not record local-device consent for {name}.", ex);
            return false;
        }
    }

    /// <summary>Forgets the host, so Windows asks about local devices again.</summary>
    public static bool RevokeHost(string host)
    {
        var name = BareHost(host);
        if (name.Length == 0) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LocalDevicesKey, writable: true);
            if (key?.GetValue(name) is null) return false;

            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not clear local-device consent for {name}.", ex);
            return false;
        }
    }

    /// <summary>
    /// Installs <paramref name="rdpContent"/> as Default.rdp and returns a handle whose disposal
    /// allows it to be restored. Returns null when Default.rdp cannot be borrowed, and the caller
    /// should fall back to passing a file.
    ///
    /// The whole borrow lives on one dedicated thread. A named mutex belongs to the thread that
    /// waited on it and must be released by that same thread; releasing it from a task continuation
    /// throws, and then every later launch waits on a mutex nobody will ever give back.
    /// </summary>
    public static IDisposable? Borrow(string rdpContent, string host)
    {
        if (string.IsNullOrWhiteSpace(rdpContent)) return null;

        var path = DefaultRdpPath;
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) return null;

        var ready = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var installed = false;

        var worker = new Thread(() =>
        {
            Mutex? mutex = null;
            var held = false;
            try
            {
                mutex = new Mutex(false, MutexName);
                try
                {
                    held = mutex.WaitOne(TimeSpan.FromSeconds(60));
                }
                catch (AbandonedMutexException)
                {
                    // A previous owner died holding it; the file is recovered at startup anyway.
                    held = true;
                }

                if (!held)
                {
                    AppLog.Warn("Another launch is still using Default.rdp; falling back to an .rdp file.");
                    return;
                }

                byte[]? original = null;
                if (File.Exists(path))
                {
                    original = File.ReadAllBytes(path);
                    File.WriteAllBytes(BackupPath, original);
                }

                // Remote Desktop writes Default.rdp as UTF-16, so match it rather than hand the
                // client back a file in an encoding it did not put there.
                var bytes = new List<byte>(Encoding.Unicode.GetPreamble());
                bytes.AddRange(Encoding.Unicode.GetBytes(rdpContent));
                WriteBytesPreservingAttributes(path, bytes.ToArray());

                installed = true;
                ready.Set();

                // Stay in place until the caller says mstsc has started, then a moment longer,
                // because the client reads the file while it is coming up.
                release.Wait(TimeSpan.FromSeconds(120));
                Thread.Sleep(ReadGraceMs);

                if (original is not null) WriteBytesPreservingAttributes(path, original);
                else if (File.Exists(path)) File.Delete(path);

                if (File.Exists(BackupPath)) File.Delete(BackupPath);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Borrowing Default.rdp for {host} failed.", ex);
            }
            finally
            {
                if (held)
                {
                    try { mutex!.ReleaseMutex(); }
                    catch (Exception ex) { AppLog.Debug_($"Releasing the Default.rdp mutex failed: {ex.Message}"); }
                }

                mutex?.Dispose();
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "DynatecRDM.DefaultRdp",
        };

        worker.Start();
        ready.Wait(TimeSpan.FromSeconds(70));

        if (!installed)
        {
            release.Set();
            return null;
        }

        return new Handle(release);
    }

    /// <summary>Tells the worker that mstsc has started and Default.rdp may be put back.</summary>
    private sealed class Handle : IDisposable
    {
        private readonly ManualResetEventSlim _release;
        private int _disposed;

        public Handle(ManualResetEventSlim release) => _release = release;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _release.Set(); }
            catch (ObjectDisposedException) { }
        }
    }

    private static void WriteBytesPreservingAttributes(string path, byte[] bytes)
    {
        // Default.rdp is hidden, and a hidden file cannot simply be overwritten: clear the
        // attribute for the write and put it back exactly as it was.
        FileAttributes? attributes = null;
        if (File.Exists(path))
        {
            attributes = File.GetAttributes(path);
            File.SetAttributes(path, FileAttributes.Normal);
        }

        File.WriteAllBytes(path, bytes);

        if (attributes.HasValue) File.SetAttributes(path, attributes.Value);
    }

    private static string BareHost(string? host)
    {
        var text = (host ?? string.Empty).Trim();
        if (text.StartsWith("TERMSRV/", StringComparison.OrdinalIgnoreCase))
            text = text["TERMSRV/".Length..];

        if (!text.StartsWith('['))
        {
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out _)) text = text[..colon];
        }

        return text.Trim();
    }
}
