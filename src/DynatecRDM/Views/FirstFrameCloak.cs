using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynatecRDM.Views;

/// <summary>
/// Shows a see-through window without its previous picture. Windows keeps the last frame of a
/// hidden layered window and puts it back on screen the moment the window is shown again, before
/// WPF has drawn the new one: a popup reappears for an instant as it last looked - fully there,
/// wherever it last was - and then jumps to how it looks now. That is the flash. The window is
/// cloaked from just before it is shown until a frame drawn since has reached the screen; DWM
/// keeps composing a cloaked window and it can be active, it is only not shown.
/// </summary>
internal sealed class FirstFrameCloak
{
    private const int DwmwaCloak = 13;

    /// <summary>
    /// Frames to wait once the caller is done showing: the first draws the window as it now is,
    /// the ones after it only start once that frame is on the screen.
    /// </summary>
    private const int FramesToWait = 3;

    /// <summary>Never cloaked longer than this, whatever happens to rendering.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(250);

    private readonly Window _window;
    private readonly DispatcherTimer _fallback;
    private int _generation;
    private int _frames;
    private TimeSpan _lastFrame;
    private bool _cloaked;
    private bool _waiting;

    public FirstFrameCloak(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _fallback = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = Longest };
        _fallback.Tick += (_, _) => Uncloak();
    }

    /// <summary>
    /// Right before Show(): the window's handle is made if it has none yet, and the window is
    /// cloaked until a frame drawn after the showing is on the screen. Where cloaking is not
    /// available the window simply shows as before.
    /// </summary>
    public void BeforeShow()
    {
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        if (!SetCloaked(handle, true)) return;

        _cloaked = true;
        var generation = ++_generation;
        StopCounting();
        _fallback.Stop();
        _fallback.Start();

        // Counted from the next turn of the dispatcher. A window's very first Show() draws frames
        // there and then - before the caller has placed it or started its animation - and those
        // are not the frame that has to reach the screen.
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (generation != _generation || !_cloaked) return;
            _frames = 0;
            _waiting = true;
            CompositionTarget.Rendering += OnRendering;
        }));
    }

    /// <summary>After Hide(): nothing is left waiting, and the window is never left cloaked.</summary>
    public void Hidden() => Uncloak();

    private void OnRendering(object? sender, EventArgs e)
    {
        // A render WPF is merely asked for again carries the time of the frame before; only a new
        // frame counts.
        if (e is RenderingEventArgs { RenderingTime: var time })
        {
            if (time == _lastFrame) return;
            _lastFrame = time;
        }
        if (++_frames >= FramesToWait) Uncloak();
    }

    private void StopCounting()
    {
        if (!_waiting) return;
        _waiting = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void Uncloak()
    {
        _generation++;
        _fallback.Stop();
        StopCounting();
        if (!_cloaked) return;
        _cloaked = false;

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero) SetCloaked(handle, false);
    }

    private static bool SetCloaked(IntPtr handle, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        try
        {
            return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
