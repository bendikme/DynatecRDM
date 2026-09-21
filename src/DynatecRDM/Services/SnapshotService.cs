using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using DynatecRDM.Interop;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Captures a JPEG thumbnail of a live session window for the tray menu. Capture runs on the
/// thread pool; the calling thread only pays for a handful of cheap window checks.
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    private const int SampleColumns = 6;
    private const int SampleRows = 4;
    private const int MaxCaptureEdge = 16384;
    private const int ResponsivenessTimeoutMs = 400;

    private static readonly ImageCodecInfo? JpegCodec = ResolveJpegCodec();
    private static volatile bool _directoryReady;

    private readonly Func<AppSettings> _settings;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public SnapshotService(Func<AppSettings> settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Folder holding the generated thumbnails.</summary>
    public static string SnapshotDirectory { get; } = Path.Combine(AppLog.DataDirectory, "snapshots");

    /// <summary>
    /// The last capture of each connection, one file per connection. A session's own thumbnail
    /// goes when the session does; this copy stays, so the quick-launch list can show what a
    /// connection looked like last time. Being a subfolder keeps it out of the orphan cleanup.
    /// </summary>
    public static string LastKnownDirectory { get; } = Path.Combine(SnapshotDirectory, "last");

    public static string LastKnownPath(Guid connectionId) =>
        Path.Combine(LastKnownDirectory, connectionId.ToString("D") + ".jpg");

    /// <summary>Every connection with a last-known snapshot, and when it was taken. Never throws.</summary>
    public static Dictionary<Guid, DateTime> LastKnownSnapshots()
    {
        var result = new Dictionary<Guid, DateTime>();
        try
        {
            if (!Directory.Exists(LastKnownDirectory)) return result;

            foreach (var path in Directory.EnumerateFiles(LastKnownDirectory, "*.jpg"))
            {
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id))
                    result[id] = File.GetLastWriteTimeUtc(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Listing the last-known snapshots failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>Drops a connection's last-known snapshot, for when the connection is deleted.</summary>
    public static void ForgetConnection(Guid connectionId) => TryDelete(LastKnownPath(connectionId));

    /// <summary>
    /// A connection's last-known snapshot was replaced by a newer capture. Raised on the capture's
    /// thread pool thread with the connection's id; listeners hop to their own thread.
    /// </summary>
    public static event EventHandler<Guid>? LastKnownChanged;

    private static void KeepAsLastKnown(Guid connectionId, string capturePath)
    {
        if (connectionId == Guid.Empty) return;

        var target = LastKnownPath(connectionId);
        var temp = Path.ChangeExtension(target, ".tmp");
        try
        {
            Directory.CreateDirectory(LastKnownDirectory);
            File.Copy(capturePath, temp, overwrite: true);
            if (!ReplaceFile(temp, target))
            {
                TryDelete(temp);
                return;
            }

            LastKnownChanged?.Invoke(null, connectionId);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            AppLog.Debug_($"Keeping the last snapshot of connection {connectionId} failed: {ex.Message}");
        }
    }

    public async Task<string?> CaptureAsync(RdpSession session, CancellationToken ct = default)
    {
        if (session is null || ct.IsCancellationRequested) return null;

        AppSettings settings;
        try
        {
            settings = _settings();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Snapshot settings lookup failed.", ex);
            return null;
        }

        if (settings is null || !settings.EnableSnapshots) return null;

        var hwnd = session.WindowHandle;
        if (hwnd == IntPtr.Zero ||
            !Win32.IsWindow(hwnd) ||
            !Win32.IsWindowVisible(hwnd) ||
            Win32.IsIconic(hwnd) ||
            Win32.IsWindowCloaked(hwnd))
        {
            return null;
        }

        // One capture per session at a time: a slow capture must never queue up behind itself.
        var gate = _gates.GetOrAdd(session.Id, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) return session.SnapshotPath;

        try
        {
            return await Task.Run(() => CaptureCore(session, hwnd, settings, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Snapshot capture failed for '{session.DisplayName}'.", ex);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? CaptureCore(RdpSession session, IntPtr hwnd, AppSettings settings, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return null;
            if (!Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return null;
            if (Win32.IsWindowCloaked(hwnd)) return null;

            // PrintWindow is a synchronous WM_PRINT to the owning thread: never send it to a hung window.
            if (!Win32.IsWindowResponsive(hwnd, ResponsivenessTimeoutMs)) return null;

            if (!Win32.GetWindowRect(hwnd, out var windowRect)) return null;

            // Nothing drawable behind the frame - not worth a capture.
            if (Win32.GetClientRect(hwnd, out var clientRect) && (clientRect.Width <= 0 || clientRect.Height <= 0))
                return null;

            // PrintWindow draws the whole window starting at the window-rect origin, so the
            // target bitmap has to cover the frame as well as the client area.
            var width = windowRect.Width;
            var height = windowRect.Height;
            if (width <= 0 || height <= 0 || width > MaxCaptureEdge || height > MaxCaptureEdge) return null;

            if (!EnsureDirectory()) return null;

            using var source = CapturePixels(hwnd, windowRect, width, height);
            if (source is null) return null;
            if (ct.IsCancellationRequested) return null;

            var maxEdge = Math.Clamp(settings.SnapshotMaxEdge, 48, 4096);
            var quality = Math.Clamp(settings.SnapshotQuality, 1, 100);

            using var thumbnail = Downscale(source, maxEdge);
            if (ct.IsCancellationRequested) return null;

            var finalPath = Path.Combine(SnapshotDirectory, session.Id.ToString("D") + ".jpg");
            var tempPath = Path.Combine(SnapshotDirectory, session.Id.ToString("D") + ".tmp");

            if (!SaveJpeg(thumbnail, tempPath, quality))
            {
                TryDelete(tempPath);
                return null;
            }

            if (!ReplaceFile(tempPath, finalPath))
            {
                TryDelete(tempPath);
                return null;
            }

            KeepAsLastKnown(session.ConnectionId, finalPath);

            // The session is bound to the UI, so the caller publishes the path on the UI thread.
            return finalPath;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Snapshot capture failed for '{session.DisplayName}'.", ex);
            return null;
        }
    }

    /// <summary>
    /// PrintWindow with PW_RENDERFULLCONTENT reaches occluded and off-screen windows, which a
    /// screen copy cannot. Some hardware-accelerated windows render a single flat colour through
    /// it, so that result is detected and replaced with a plain screen grab.
    /// </summary>
    private static Bitmap? CapturePixels(IntPtr hwnd, Win32.RECT windowRect, int width, int height)
    {
        var printed = PrintWindowToBitmap(hwnd, width, height);
        if (printed is not null)
        {
            var trimmed = CropToVisibleFrame(printed, hwnd, windowRect);
            if (trimmed is not null)
            {
                printed.Dispose();
                printed = trimmed;
            }

            if (!IsUniformColour(printed)) return printed;
        }

        var rect = Win32.TryGetExtendedFrameBounds(hwnd, out var frame) && frame.Width > 0 && frame.Height > 0
            ? frame
            : windowRect;

        var grabbed = CopyFromScreen(rect);
        if (grabbed is not null && !IsUniformColour(grabbed))
        {
            printed?.Dispose();
            return grabbed;
        }

        // Both look flat. The window's own pixels are still the window's; a screen grab of an
        // occluded window is whatever happened to be in front of it, which must never be shown.
        if (printed is not null)
        {
            grabbed?.Dispose();
            return printed;
        }

        return grabbed;
    }

    /// <summary>
    /// GetWindowRect covers the invisible resize border DWM keeps around a window, which PrintWindow
    /// renders as a black margin. Trimming to the extended frame bounds keeps it out of the thumbnail.
    /// </summary>
    private static Bitmap? CropToVisibleFrame(Bitmap printed, IntPtr hwnd, Win32.RECT windowRect)
    {
        try
        {
            if (!Win32.TryGetExtendedFrameBounds(hwnd, out var frame)) return null;
            if (frame.Width <= 0 || frame.Height <= 0) return null;

            var full = new Rectangle(0, 0, printed.Width, printed.Height);
            var visible = new Rectangle(
                frame.Left - windowRect.Left,
                frame.Top - windowRect.Top,
                frame.Width,
                frame.Height);

            var crop = Rectangle.Intersect(full, visible);
            if (crop == full || crop.Width < 32 || crop.Height < 32) return null;

            return printed.Clone(crop, printed.PixelFormat);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Trimming the snapshot frame failed.", ex);
            return null;
        }
    }

    private static Bitmap? PrintWindowToBitmap(IntPtr hwnd, int width, int height)
    {
        var windowDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;

        try
        {
            windowDc = GetWindowDC(hwnd);
            if (windowDc == IntPtr.Zero) return null;

            memoryDc = CreateCompatibleDC(windowDc);
            if (memoryDc == IntPtr.Zero) return null;

            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (bitmap == IntPtr.Zero) return null;

            previousBitmap = SelectObject(memoryDc, bitmap);
            var ok = Win32.PrintWindow(hwnd, memoryDc, Win32.PW_RENDERFULLCONTENT);

            // Deselect before handing the bitmap to GDI+, which copies the pixels out of it.
            if (previousBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousBitmap);
                previousBitmap = IntPtr.Zero;
            }

            return ok ? Image.FromHbitmap(bitmap) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("PrintWindow capture failed.", ex);
            return null;
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero) SelectObject(memoryDc, previousBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (windowDc != IntPtr.Zero) ReleaseDC(hwnd, windowDc);
        }
    }

    private static Bitmap? CopyFromScreen(Win32.RECT rect)
    {
        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0 || width > MaxCaptureEdge || height > MaxCaptureEdge) return null;

        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch (Exception ex)
        {
            bitmap?.Dispose();
            AppLog.Warn("Screen copy capture failed.", ex);
            return null;
        }
    }

    /// <summary>Sparse grid sample: a capture that is one flat colour everywhere is a failed capture.</summary>
    private static bool IsUniformColour(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width < SampleColumns || height < SampleRows) return false;

        var first = 0;
        var haveFirst = false;

        for (var row = 0; row < SampleRows; row++)
        {
            var y = (int)(((row + 0.5) * height) / SampleRows);
            if (y >= height) y = height - 1;

            for (var col = 0; col < SampleColumns; col++)
            {
                var x = (int)(((col + 0.5) * width) / SampleColumns);
                if (x >= width) x = width - 1;

                var argb = bitmap.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                if (!haveFirst)
                {
                    first = argb;
                    haveFirst = true;
                }
                else if (argb != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Bitmap Downscale(Bitmap source, int maxEdge)
    {
        var longest = Math.Max(source.Width, source.Height);
        var scale = longest > maxEdge ? maxEdge / (double)longest : 1.0;

        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using var g = Graphics.FromImage(target);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.None;

            // TileFlipXY stops bicubic sampling from bleeding transparent edges into the border.
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel,
                attributes);

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static bool SaveJpeg(Bitmap bitmap, string path, int quality)
    {
        try
        {
            var codec = JpegCodec;
            if (codec is null)
            {
                bitmap.Save(path, ImageFormat.Jpeg);
                return true;
            }

            using var parameters = new EncoderParameters(1);
            using var qualityParameter = new EncoderParameter(Encoder.Quality, (long)quality);
            parameters.Param[0] = qualityParameter;
            bitmap.Save(path, codec, parameters);
            return true;
        }
        catch (Exception ex)
        {
            // Re-probe the folder next time: it may have been deleted underneath us.
            _directoryReady = false;
            AppLog.Warn($"Writing snapshot '{path}' failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Moves the freshly written file over the live one. The tray may briefly hold the target
    /// open while WPF decodes it, so a couple of quick retries avoid losing the capture.
    /// </summary>
    private static bool ReplaceFile(string tempPath, string finalPath)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                File.Move(tempPath, finalPath, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Publishing snapshot '{finalPath}' failed.", ex);
                return false;
            }
        }

        return false;
    }

    public void Remove(RdpSession session)
    {
        if (session is null) return;

        // Left undisposed on purpose: an in-flight capture still owns it and has to be able to release it.
        _gates.TryRemove(session.Id, out _);

        var name = session.Id.ToString("D");
        TryDelete(Path.Combine(SnapshotDirectory, name + ".jpg"));
        TryDelete(Path.Combine(SnapshotDirectory, name + ".tmp"));

        session.SnapshotPath = null;
        session.LastSnapshotUtc = null;
    }

    public void CleanupOrphans(IEnumerable<Guid> liveSessionIds)
    {
        try
        {
            if (!Directory.Exists(SnapshotDirectory)) return;

            var live = liveSessionIds is null ? new HashSet<Guid>() : new HashSet<Guid>(liveSessionIds);
            var cutoff = DateTime.UtcNow.AddHours(-24);

            foreach (var path in Directory.EnumerateFiles(SnapshotDirectory))
            {
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var keep = Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id) && live.Contains(id);
                if (keep)
                {
                    try
                    {
                        keep = File.GetLastWriteTimeUtc(path) >= cutoff;
                    }
                    catch
                    {
                        keep = false;
                    }
                }

                if (!keep) TryDelete(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Snapshot cleanup failed.", ex);
        }
    }

    /// <summary>Empties the snapshot folder, last-known ones included (exposed for the settings screen).</summary>
    public static void PurgeAll()
    {
        try
        {
            if (!Directory.Exists(SnapshotDirectory)) return;
            foreach (var path in Directory.EnumerateFiles(SnapshotDirectory))
                TryDelete(path);

            if (!Directory.Exists(LastKnownDirectory)) return;
            foreach (var path in Directory.EnumerateFiles(LastKnownDirectory))
                TryDelete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Purging snapshots failed.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A snapshot that refuses to disappear is not worth reporting.
        }
    }

    private static bool EnsureDirectory()
    {
        if (_directoryReady) return true;

        try
        {
            Directory.CreateDirectory(SnapshotDirectory);
            _directoryReady = true;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Cannot create snapshot folder '{SnapshotDirectory}'.", ex);
            return false;
        }
    }

    private static ImageCodecInfo? ResolveJpegCodec()
    {
        try
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            for (var i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == ImageFormat.Jpeg.Guid) return codecs[i];
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("JPEG encoder lookup failed.", ex);
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);
}
