using System.Diagnostics;
using System.IO;
using System.Text;

namespace DynatecRDM.Services;

/// <summary>
/// Tiny asynchronous file logger. Writes are queued and flushed on a background thread so
/// nothing on the UI path ever touches the disk.
/// </summary>
public static class AppLog
{
    private static readonly System.Collections.Concurrent.BlockingCollection<string> Queue = new(4096);
    private static readonly Lazy<Thread> Writer = new(StartWriter);
    private static string _logPath = string.Empty;
    private static volatile bool _enabled = true;

    /// <summary>Directory holding the application's data (database, snapshots, logs).</summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string LogPath => _logPath;

    private static string ResolveDataDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynatecRDM");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Fall back to the executable folder when LocalAppData is unavailable.
            dir = AppContext.BaseDirectory;
        }
        return dir;
    }

    private static Thread StartWriter()
    {
        _logPath = Path.Combine(DataDirectory, "dynatec-rdm.log");
        TrimIfLarge();

        var t = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "DynatecRDM.Log",
            Priority = ThreadPriority.BelowNormal,
        };
        t.Start();
        return t;
    }

    private static void TrimIfLarge()
    {
        try
        {
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var backup = _logPath + ".1";
                File.Delete(backup);
                File.Move(_logPath, backup);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    private static void WriterLoop()
    {
        var buffer = new StringBuilder(8192);
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            buffer.Clear();
            buffer.AppendLine(line);

            // Drain anything else already queued so bursts cost one write.
            while (buffer.Length < 32768 && Queue.TryTake(out var more))
                buffer.AppendLine(more);

            try
            {
                File.AppendAllText(_logPath, buffer.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Disk problems must not take the app down.
            }
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        if (!_enabled) return;

        var line = ex is null
            ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}"
            : $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";

        Debug.WriteLine(line);

        try
        {
            _ = Writer.Value;
            Queue.TryAdd(line, 0);
        }
        catch
        {
            // Queue full or shutting down - drop the line rather than block a caller.
        }
    }

    public static void Info(string message) => Write("INF", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    public static void Debug_(string message) => Write("DBG", message, null);

    /// <summary>Stops accepting new entries (called during shutdown).</summary>
    public static void Shutdown()
    {
        _enabled = false;
        try
        {
            Queue.CompleteAdding();
        }
        catch
        {
            // Already completed.
        }
    }
}
