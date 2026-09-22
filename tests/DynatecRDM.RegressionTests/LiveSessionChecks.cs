using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using DynatecRDM.Data;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Services;
using DynatecRDM.Views;
using Microsoft.Data.Sqlite;

// Explicit opt-in: authenticates with a named saved connection. Database access is read-only.
internal static class LiveSessionChecks
{
    public static void Run(string connectionName, bool hoverOnly = false, bool edgeOnly = false)
    {
        var (connection, credential) = ReadConnection(connectionName);
        connection.AutoReconnect = false;
        var config = new AppSettings { EnableSnapshots = false, SessionBarEnabled = true, WatchdogEnabled = false };
        using var monitors = new MonitorService();
        var store = DispatchProxy.Create<IDataStore, StoreProxy>();
        ((StoreProxy)(object)store).Connection = connection;
        ((StoreProxy)(object)store).Credential = credential;
        using var manager = new EmbeddedSessionManager(store, new SecretProtector(), monitors,
            new WindowPlacementService(monitors), new NoSnapshots(), () => config);
        var services = (AppServices)Activator.CreateInstance(typeof(AppServices), nonPublic: true)!;
        services.Settings = config;
        typeof(AppServices).GetProperty(nameof(AppServices.Sessions))!.SetValue(services, manager);
        typeof(AppServices).GetProperty(nameof(AppServices.Monitors))!.SetValue(services, monitors);
        using var bar = new SessionBarService(services, DispatchProxy.Create<IAppShell, Program.NoCalls>());
        manager.SessionStateChanged += (_, session) => Console.WriteLine($"LIVE state={session.State}; error={session.LastError}");
        manager.SessionEnded += (_, session) => Console.WriteLine($"LIVE ended={session.State}; error={session.LastError}");

        var display = connection.Display.Clone();
        display.ScreenMode = ScreenMode.Windowed;
        display.UseAllMonitors = false;
        display.SelectedMonitors.Clear();
        display.Placement = WindowPlacementMode.Default;
        display.DesktopWidth = 1024;
        display.DesktopHeight = 768;
        display.AlwaysOnTop = false;
        var originalForeground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var originalCursor);
        RdpSession? session = null;
        try
        {
            var launch = manager.LaunchAsync(connection, display);
            WaitUntil(() => launch.IsCompleted, "launch", 15);
            session = launch.GetAwaiter().GetResult() ?? throw new Exception("Launch failed.");
            WaitUntil(() => session.State == SessionState.Connected || !session.IsActive, "login", 30);
            Check(session.State == SessionState.Connected, $"RDP login failed: {session.State}, {session.LastError}");
            Console.WriteLine("LIVE login completed.");

            Check(manager.TrySetFullScreen(session.Id, true), "Full-screen request failed.");
            WaitUntil(() => FullScreen(session), "full-screen", 5);
            Check(Win32.TryGetClientScreenRect(session.WindowHandle, out var rect), "Session window disappeared.");
            var monitor = monitors.GetMonitorAt(rect.Left + 20, rect.Top + 20);
            if (hoverOnly)
            {
                if (edgeOnly) RapidEdgeCycles(bar, session, monitor);
                else HoverCycles(bar, session, monitor);
                return;
            }
            // The bar hangs from a surface: here the monitor the full-screen session fills.
            var fullScreen = Activator.CreateInstance(
                typeof(SessionBarService).GetNestedType("BarSurface", BindingFlags.NonPublic)!, monitor, monitor.Bounds, false)!;
            typeof(SessionBarService).GetMethod("ShowBar", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(bar, new object[] { fullScreen, session, false });
            var barWindow = (SessionBarWindow)typeof(SessionBarService).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
            Check(barWindow.IsRevealed, "Bar did not reveal over the connected desktop.");
            bar.Minimize(session);
            WaitUntil(() => Win32.IsIconic(session.WindowHandle), "minimize", 5);
            manager.Focus(session.Id);
            WaitUntil(() => FullScreen(session), "restore full-screen", 5);
            bar.ExitFullScreen(session);
            WaitUntil(() => !FullScreen(session), "exit full-screen", 5);
            Check(session.State == SessionState.Connected, "Session disconnected during window controls.");
            Console.WriteLine("LIVE bar reveal, minimize, restore, and exit full-screen passed while connected.");

            var reconnect = manager.ReconnectAsync(session.Id);
            WaitUntil(() => reconnect.IsCompleted, "reconnect request", 15);
            reconnect.GetAwaiter().GetResult();
            WaitUntil(() => session.State == SessionState.Connected || !session.IsActive, "reconnect login", 30);
            Check(session.State == SessionState.Connected, $"Reconnect failed: {session.LastError}");
            Console.WriteLine("LIVE reconnect login completed.");
        }
        finally
        {
            if (session is not null) manager.CloseAsync(session.Id).GetAwaiter().GetResult();
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != IntPtr.Zero) Win32.ForceForeground(originalForeground);
        }
    }

    private static void RapidEdgeCycles(SessionBarService bar, RdpSession session, MonitorInfo monitor)
    {
        var field = typeof(SessionBarService).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!;
        SessionBarWindow? Window() => (SessionBarWindow?)field.GetValue(bar);
        var middle = monitor.Left + monitor.Width / 2;
        Win32.ForceForeground(session.WindowHandle);
        SetCursorPos(middle, monitor.Top + monitor.Height / 2);
        PumpFor(4500);
        EdgeHitTesting(bar, session, monitor);
        for (var cycle = 0; cycle < 6; cycle++)
        {
            if (cycle == 3) OpenAndCloseRemoteStart(session);
            SetCursorPos(middle, monitor.Top + monitor.Height / 2);
            WaitUntil(() => Window()?.IsRevealed != true, "bar to hide", 3);
            PumpFor(200);
            // One rapid relative move beyond the desktop boundary. Do not park the pointer
            // with repeated SetCursorPos calls or move it down to help the reveal.
            SetCursorPos(middle, monitor.Top + monitor.Height / 2);
            MouseEvent(1, 0, unchecked((uint)-10000), 0, UIntPtr.Zero);
            var started = Environment.TickCount64;
            Win32.GetCursorPos(out var atEdge);
            Console.WriteLine($"EDGE fling {cycle}: cursor={atEdge.X},{atEdge.Y}; monitorTop={monitor.Top}");
            var trace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            string? last = null;
            trace.Tick += (_, _) =>
            {
                Win32.GetCursorPos(out var cursor);
                var hit = Win32.WindowFromPoint(cursor);
                var root = Win32.GetRootWindow(hit);
                var state = $"cursor={cursor.X},{cursor.Y}; hit={Win32.GetClassName(hit)}; root={Win32.GetClassName(root)}({root}); session={session.WindowHandle}; fg={Win32.GetForegroundWindow()}; shown={Window()?.IsRevealed}";
                if (state != last) Console.WriteLine($"EDGE +{Environment.TickCount64 - started}ms {state}");
                last = state;
            };
            trace.Start();
            try
            {
                var moved = false;
                var probe = new Win32.POINT { X = middle, Y = monitor.Top + 16 };
                bool VisibleAtEdge()
                {
                    Win32.GetCursorPos(out var cursor);
                    moved |= cursor.X != middle || cursor.Y != monitor.Top;
                    return moved || (Window() is { IsRevealed: true } window
                        && Win32.GetRootWindow(Win32.WindowFromPoint(probe)) == new WindowInteropHelper(window).Handle);
                }
                // Complete only when native hit testing reaches the visible bar while the
                // pointer is still on the top row, without any corrective mouse movement.
                WaitUntil(VisibleAtEdge, "visible bar after fling to the topmost row", 2);
                Check(!moved, "Pointer moved before the bar became visible; edge test interrupted.");
                Check(Win32.GetForegroundWindow() == session.WindowHandle, "Hover stole keyboard focus.");
                Console.WriteLine($"EDGE fling {cycle}: bar visible in {Environment.TickCount64 - started} ms with pointer on topmost row.");
            }
            finally { trace.Stop(); }
        }
    }

    private static void EdgeHitTesting(SessionBarService bar, RdpSession session, MonitorInfo monitor)
    {
        // Reproduce a thin top-edge window stealing hit testing while RDP keeps the keyboard.
        // Query real HWNDs at fixed coordinates; this part does not depend on mouse movement.
        Win32.ForceForeground(session.WindowHandle);
        using var strip = new PassiveEdgeWindow
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Bounds = new System.Drawing.Rectangle(monitor.Left, monitor.Top, monitor.Width, 3),
            TopMost = true,
            BackColor = System.Drawing.Color.Black,
        };
        strip.Show();
        Win32.ForceForeground(session.WindowHandle);
        Win32.SetWindowPos(strip.Handle, Win32.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, 3,
            Win32.SWP_NOACTIVATE);
        Check(Win32.GetForegroundWindow() == session.WindowHandle,
            $"Could not prepare foreground session: foreground={Win32.GetForegroundWindow()}, session={session.WindowHandle}, strip={strip.Handle}.");
        var hitTest = typeof(SessionBarService).GetMethod("SessionAtEdge", BindingFlags.NonPublic | BindingFlags.Instance)!;
        RdpSession? At(int y) => (RdpSession?)hitTest.Invoke(bar, new object[]
            { new Win32.POINT { X = monitor.Left + monitor.Width / 2, Y = monitor.Top + y }, monitor });
        var exactTop = At(0);
        var slightlyDown = At(8);
        Console.WriteLine($"EDGE thin window: exact top matches={exactTop == session}; eight pixels down matches={slightlyDown == session}");
        Check(slightlyDown == session, "Fixture did not leave RDP visible beneath the edge.");
        Check(exactTop == session, "The topmost row is a dead zone when a thin edge window is above RDP.");
        strip.Height = 80;
        Check(At(0) is null, "A larger overlay incorrectly reveals the session behind it.");
        Win32.SetWindowPos(strip.Handle, Win32.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, 3,
            Win32.SWP_NOACTIVATE);
        Win32.ForceForeground(strip.Handle);
        Check(Win32.GetForegroundWindow() == strip.Handle, "Could not activate the other-window fixture.");
        Check(At(0) is null, "The bar reveals over another foreground window.");
        Win32.ForceForeground(session.WindowHandle);
        Console.WriteLine("EDGE fixed-coordinate top-row and overlay safety checks passed.");
    }

    private sealed class PassiveEdgeWindow : System.Windows.Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
                return parameters;
            }
        }
    }

    private static void HoverCycles(SessionBarService bar, RdpSession session, MonitorInfo monitor)
    {
        var field = typeof(SessionBarService).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!;
        SessionBarWindow? Window() => (SessionBarWindow?)field.GetValue(bar);
        var middle = monitor.Left + monitor.Width / 2;
        Win32.ForceForeground(session.WindowHandle);
        Console.WriteLine($"HOVER monitor={monitor.Left},{monitor.Top} {monitor.Width}x{monitor.Height}; dpi={monitor.DpiX}; session={session.WindowHandle}; foreground={Win32.GetForegroundWindow()}");
        SetCursorPos(middle, monitor.Top + monitor.Height / 2);
        // Let the initial login peek finish; every subsequent reveal must come from pointer dwell.
        PumpFor(4500);
        OpenAndCloseRemoteStart(session);
        foreach (var (percent, offset) in new[] { (50, 0), (50, 3), (50, 8), (50, 11), (50, 0), (30, 8) })
        {
            if (offset == 8) OpenAndCloseRemoteStart(session);
            SetCursorPos(middle, monitor.Top + monitor.Height / 2);
            WaitUntil(() => Window()?.IsRevealed != true, "bar to hide", 3);
            PumpFor(200);
            var started = Environment.TickCount64;
            var hoverX = monitor.Left + monitor.Width * percent / 100;
            Check(SetCursorPos(hoverX, monitor.Top + offset), "Could not move the local pointer.");
            // Keep the pointer stationary during this bounded check, including if the RDP
            // server restores an old pointer position while the local bar animates.
            var park = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
            park.Tick += (_, _) => SetCursorPos(hoverX, monitor.Top + offset);
            park.Start();
            try
            {
                WaitUntil(() => Window()?.IsRevealed == true, $"hover reveal at y={offset}", 2);
                PumpFor(percent == 50 ? 300 : 800); // Stay reachable while moving sideways toward the bar.
                var window = Window()!;
                var handle = new WindowInteropHelper(window).Handle;
                var hit = Win32.GetRootWindow(Win32.WindowFromPoint(new Win32.POINT { X = middle, Y = monitor.Top + 16 }));
                Check(hit == handle, $"Bar reports revealed but is covered by {Win32.GetClassName(hit)}.");
                Check(Win32.GetForegroundWindow() == session.WindowHandle, "Hover stole keyboard focus.");
                Console.WriteLine($"HOVER x={percent}%, y={offset}: visible without click in {Environment.TickCount64 - started} ms.");
                if (offset == 3)
                {
                    // RDP can reassert its topmost window after activation/display changes, with
                    // no new foreground event. The revealed bar must recover without a click.
                    Win32.SetWindowPos(session.WindowHandle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                        Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
                    PumpFor(350);
                    hit = Win32.GetRootWindow(Win32.WindowFromPoint(new Win32.POINT { X = middle, Y = monitor.Top + 16 }));
                    Check(hit == handle, "RDP covered the revealed bar and hover did not bring it back.");
                    Console.WriteLine("HOVER recovered after RDP raised its window, without a click.");
                }
            }
            catch
            {
                Win32.GetCursorPos(out var cursor);
                var hit = Win32.WindowFromPoint(cursor);
                var root = Win32.GetRootWindow(hit);
                var timer = (DispatcherTimer)typeof(SessionBarService).GetField("_poll", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
                Console.WriteLine($"HOVER failure: expected={hoverX},{monitor.Top + offset}; cursor={cursor.X},{cursor.Y}; hit={Win32.GetClassName(hit)}; root={Win32.GetClassName(root)}; session={session.WindowHandle}; rootHandle={root}; foreground={Win32.GetForegroundWindow()}; leftDown={Win32.IsKeyDown(1)}; pollEnabled={timer.IsEnabled}; revealed={Window()?.IsRevealed}");
                throw;
            }
            finally { park.Stop(); }
        }
    }

    private static void PumpFor(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        WaitUntil(() => Environment.TickCount64 >= end, "settle", milliseconds / 1000 + 2);
    }

    private static void OpenAndCloseRemoteStart(RdpSession session)
    {
        // Click the remote Windows 10 Start button only after checking that RDP receives it.
        var form = (DynatecRDM.Rdp.RdpSessionWindow)System.Windows.Forms.Control.FromHandle(session.WindowHandle)!;
        void ClickStart()
        {
            var point = new Win32.POINT { X = form.Left + 24, Y = form.Bottom - 20 };
            Check(SetCursorPos(point.X, point.Y), "Could not reach remote Start.");
            Check(Win32.GetRootWindow(Win32.WindowFromPoint(point)) == session.WindowHandle,
                "Remote Start is covered by a local window; refusing to click it.");
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
            MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
        }
        ClickStart();
        PumpFor(1000);
        ClickStart();
        PumpFor(700);
        Console.WriteLine($"REMOTE START opened and dismissed; foreground={Win32.GetForegroundWindow()}; session={session.WindowHandle}");
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static bool FullScreen(RdpSession session) =>
        System.Windows.Forms.Control.FromHandle(session.WindowHandle) is DynatecRDM.Rdp.RdpSessionWindow { IsFullScreen: true }
        && !Win32.IsIconic(session.WindowHandle);

    private static void WaitUntil(Func<bool> ready, string step, int seconds)
    {
        var limit = Environment.TickCount64 + seconds * 1000;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { if (ready() || Environment.TickCount64 >= limit) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(ready(), $"Timed out waiting for {step}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static (RdpConnection, CredentialSet?) ReadConnection(string name)
    {
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynatecRDM", "dynatec-rdm.db");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = (string)typeof(SqliteDataStore).GetField("SelectConnectionsSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
            + " WHERE name=$name COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$name", name);
        RdpConnection connection;
        using (var reader = cmd.ExecuteReader())
        {
            Check(reader.Read(), "Named connection was not found.");
            connection = (RdpConnection)typeof(SqliteDataStore).GetMethod("ReadConnection", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { reader })!;
        }
        CredentialSet? credential = null;
        if (connection.CredentialSetId is { } id)
        {
            cmd.Parameters.Clear();
            cmd.CommandText = (string)typeof(SqliteDataStore).GetField("SelectCredentialsSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
                + " WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            using var reader = cmd.ExecuteReader();
            Check(reader.Read(), "Saved credential was not found.");
            credential = (CredentialSet)typeof(SqliteDataStore).GetMethod("ReadCredential", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { reader })!;
        }
        return (connection, credential);
    }

    public class StoreProxy : DispatchProxy
    {
        public RdpConnection Connection = null!;
        public CredentialSet? Credential;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IDataStore.GetCredentialSetAsync) => Task.FromResult(Credential),
            nameof(IDataStore.GetConnectionAsync) => Task.FromResult<RdpConnection?>(Connection),
            nameof(IDataStore.RecordLaunchAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException($"Live test does not allow database operation {method?.Name}.")
        };
    }

    private sealed class NoSnapshots : ISnapshotService
    {
        public Task<string?> CaptureAsync(RdpSession session, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public void Remove(RdpSession session) { }
        public void CleanupOrphans(IEnumerable<Guid> ids) { }
    }
}
