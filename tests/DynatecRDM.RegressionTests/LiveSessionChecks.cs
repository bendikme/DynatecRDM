using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using DynatecRDM.Data;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;
using DynatecRDM.Views;
using Microsoft.Data.Sqlite;

// Explicit opt-in: authenticates with a named saved connection. Database access is read-only.
internal static class LiveSessionChecks
{
    public static void Run(string connectionName, bool hoverOnly = false, bool edgeOnly = false, ConsoleCheck console = ConsoleCheck.None)
    {
        var (connection, credential) = ReadConnection(connectionName);
        connection.AutoReconnect = false;
        // Only the copy in memory: the database is never written.
        connection.LeaveOnConsole = console == ConsoleCheck.HandOff;
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
            if (console == ConsoleCheck.DryRun)
            {
                ConsoleDryRun(session);
                return;
            }
            if (console == ConsoleCheck.HandOff)
            {
                ConsoleHandOff(manager, session);
                return;
            }

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

    public enum ConsoleCheck { None, DryRun, HandOff }

    /// <summary>
    /// Two saved connections opened the way the app opens them - full screen where they are set to
    /// be - and the bar's tab of the one behind clicked with the real mouse, as a user switches.
    /// Every step is traced: the native messages the bar gets, the WPF click, the bar's current
    /// session and what is in front. Then the console hand-over is typed into the second session
    /// without Enter, on the threads the app types it on, and read back. Nothing runs remotely.
    /// </summary>
    public static void RunSwitch(string firstName, string secondName, string? screenshotPath)
    {
        var (first, firstCredential) = ReadConnection(firstName);
        var (second, secondCredential) = ReadConnection(secondName);
        // Only the copies in memory: the database is never written.
        foreach (var connection in new[] { first, second })
        {
            connection.AutoReconnect = false;
            connection.LeaveOnConsole = false;
        }
        var config = new AppSettings { EnableSnapshots = false, SessionBarEnabled = true, WatchdogEnabled = false };
        using var monitors = new MonitorService();
        var store = DispatchProxy.Create<IDataStore, StoreProxy>();
        var proxy = (StoreProxy)(object)store;
        proxy.Connection = first;
        proxy.Credential = firstCredential;
        proxy.Others.Add((second, secondCredential));
        using var manager = new EmbeddedSessionManager(store, new SecretProtector(), monitors,
            new WindowPlacementService(monitors), new NoSnapshots(), () => config);
        var services = (AppServices)Activator.CreateInstance(typeof(AppServices), nonPublic: true)!;
        services.Settings = config;
        typeof(AppServices).GetProperty(nameof(AppServices.Sessions))!.SetValue(services, manager);
        typeof(AppServices).GetProperty(nameof(AppServices.Monitors))!.SetValue(services, monitors);
        using var bar = new SessionBarService(services, DispatchProxy.Create<IAppShell, Program.NoCalls>());
        var vm = (SessionBarViewModel)typeof(SessionBarService)
            .GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
        var windowField = typeof(SessionBarService).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!;
        SessionBarWindow? Window() => (SessionBarWindow?)windowField.GetValue(bar);

        var originalForeground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var originalCursor);
        RdpSession? a = null, b = null;
        try
        {
            a = LaunchConnected(manager, first);
            b = LaunchConnected(manager, second);
            Check(FullScreen(a) && FullScreen(b), $"Both sessions must be full screen: {first.Name}={FullScreen(a)}, {second.Name}={FullScreen(b)}.");
            Win32.TryGetClientScreenRect(a.WindowHandle, out var ra);
            Win32.TryGetClientScreenRect(b.WindowHandle, out var rb);
            var monitor = monitors.GetMonitorAt(rb.Left + 20, rb.Top + 20);
            Console.WriteLine($"SWITCH {first.Name} at {ra.Left},{ra.Top} {ra.Width}x{ra.Height}; {second.Name} at {rb.Left},{rb.Top} {rb.Width}x{rb.Height}; monitor {monitor.DeviceName} {monitor.Left},{monitor.Top} {monitor.Width}x{monitor.Height}");

            Win32.ForceForeground(b.WindowHandle);
            var middle = monitor.Left + monitor.Width / 2;
            SetCursorPos(middle, monitor.Top + monitor.Height / 2);
            // Let the login peek come and go; the bar must come from hovering, as the user's does.
            PumpFor(4500);
            Describe("before", a, b, vm, Window(), monitor);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var target = attempt == 1 ? a : b;
                var other = attempt == 1 ? b : a;
                SetCursorPos(middle, monitor.Top + monitor.Height / 2);
                WaitUntil(() => Window()?.IsRevealed != true, "bar to hide", 4);
                PumpFor(200);
                SetCursorPos(middle, monitor.Top + 2);
                WaitUntil(() => Window()?.IsRevealed == true, "hover reveal", 3);
                PumpFor(300);
                var window = Window()!;
                Check(ReferenceEquals(vm.Current, other), $"The bar opened over {vm.Current?.DisplayName ?? "nothing"}, not {other.DisplayName}.");
                using var trace = new BarTrace(window, vm, a, b, monitor);
                var tab = Descendants<System.Windows.Controls.Button>((System.Windows.Controls.ItemsControl)window.FindName("TabList"))
                    .First(t => t.DataContext is SessionBarTab { } st && ReferenceEquals(st.Session, target));
                var centre = tab.PointToScreen(new System.Windows.Point(tab.ActualWidth / 2, tab.ActualHeight / 2));
                var point = new Win32.POINT { X = (int)centre.X, Y = (int)centre.Y };
                var hit = Win32.WindowFromPoint(point);
                Console.WriteLine($"SWITCH {attempt}: tab '{target.DisplayName}' at {point.X},{point.Y}; under it {Win32.GetClassName(hit)} root={Win32.GetRootWindow(hit)} bar={new WindowInteropHelper(window).Handle}");
                SetCursorPos(point.X, point.Y);
                PumpFor(500);
                MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
                PumpFor(80);
                MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
                PumpFor(1500);
                Describe($"after click {attempt}", a, b, vm, Window(), monitor);
                Console.WriteLine($"SWITCH {attempt}: {trace.Summary()}");
            }

            SetCursorPos(middle, monitor.Top + monitor.Height / 2);
            WaitUntil(() => Window()?.IsRevealed != true, "bar to hide", 4);
            TypeHandOffLikeTheApp(manager, b, screenshotPath, monitor);
        }
        finally
        {
            if (a is not null) manager.CloseAsync(a.Id).GetAwaiter().GetResult();
            if (b is not null) manager.CloseAsync(b.Id).GetAwaiter().GetResult();
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != IntPtr.Zero) Win32.ForceForeground(originalForeground);
        }
    }

    /// <summary>
    /// Sends Windows+R to a full-screen session in several ways and photographs the result of each,
    /// to see which one the session actually receives. Escape follows each, so nothing is left open.
    /// </summary>
    public static void RunKeyProbe(string connectionName, string directory, string[] wanted)
    {
        var (connection, credential) = ReadConnection(connectionName);
        connection.AutoReconnect = false;
        connection.LeaveOnConsole = false;
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
        var windowField = typeof(SessionBarService).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!;
        SessionBarWindow? Window() => (SessionBarWindow?)windowField.GetValue(bar);
        var originalForeground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var originalCursor);
        RdpSession? session = null;
        try
        {
            session = LaunchConnected(manager, connection);
            var form = (DynatecRDM.Rdp.RdpSessionWindow)System.Windows.Forms.Control.FromHandle(session.WindowHandle)!;
            Win32.TryGetClientScreenRect(session.WindowHandle, out var rect);
            var monitor = monitors.GetMonitorAt(rect.Left + 20, rect.Top + 20);

            // The foreground the way a user gives it: a click on the session's own tab on the bar.
            var middle = monitor.Left + monitor.Width / 2;
            SetCursorPos(middle, monitor.Top + monitor.Height / 2);
            PumpFor(4500);
            Shoot(monitor, System.IO.Path.Combine(directory, "keys-0-connected.png"), whole: true);
            var top = Win32.GetRootWindow(Win32.WindowFromPoint(new Win32.POINT { X = middle, Y = monitor.Top + 2 }));
            Console.WriteLine($"KEYS connected: foreground is the session={Win32.GetForegroundWindow() == session.WindowHandle}; " +
                              $"at the top edge {Win32.GetClassName(top)} '{Win32.GetWindowText(top)}' (session: {top == session.WindowHandle})");
            SetCursorPos(middle, monitor.Top + 2);
            try { WaitUntil(() => Window()?.IsRevealed == true, "hover reveal", 3); }
            catch (Exception ex) { Console.WriteLine($"KEYS {ex.Message}"); }
            if (Window()?.IsRevealed == true)
            {
                PumpFor(300);
                var tab = Descendants<System.Windows.Controls.Button>((System.Windows.Controls.ItemsControl)Window()!.FindName("TabList")).First();
                var centre = tab.PointToScreen(new System.Windows.Point(tab.ActualWidth / 2, tab.ActualHeight / 2));
                SetCursorPos((int)centre.X, (int)centre.Y);
                PumpFor(300);
                MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
                PumpFor(80);
                MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            }
            SetCursorPos(middle, monitor.Top + monitor.Height / 2);
            PumpFor(800);
            Console.WriteLine($"KEYS ready: foreground is the session={Win32.GetForegroundWindow() == session.WindowHandle}; focus={Win32.GetClassName(GetFocus())}");

            var winR = DynatecRDM.Rdp.RdpKeyboard.WindowsR();
            var escape = DynatecRDM.Rdp.RdpKeyboard.Escape;
            var variants = new (string Name, Action<DynatecRDM.Rdp.RdpKeyEvent[]> Send)[]
            {
                ("interop-one-per-call", keys => form.Host.SendKeys(keys)),
                ("vtable-one-call", keys => SendRaw(form.Host.Ocx, keys, k => k.LParam)),
                ("vtable-one-call-ih-focus", keys => { FocusInputWindow(form); SendRaw(form.Host.Ocx, keys, k => k.LParam); }),
                ("vtable-one-call-scancode", keys => SendRaw(form.Host.Ocx, keys, k => k.ScanCode | (k.Extended ? 0x100 : 0))),
            };
            foreach (var (name, send) in variants)
            {
                if (!wanted.Contains(name)) continue;
                Console.WriteLine($"KEYS {name}: foreground is the session={Win32.GetForegroundWindow() == session.WindowHandle}; focus={Win32.GetClassName(GetFocus())}");
                send(winR);
                PumpFor(1500);
                Shoot(monitor, System.IO.Path.Combine(directory, $"keys-{name}.png"), whole: true);
                send(escape);
                PumpFor(800);
            }
        }
        finally
        {
            if (session is not null) manager.CloseAsync(session.Id).GetAwaiter().GetResult();
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != IntPtr.Zero) Win32.ForceForeground(originalForeground);
        }
    }

    private delegate int SendKeysFn(IntPtr self, int count, short[] keyUp, int[] keyData);

    /// <summary>The whole sequence in one SendKeys call, straight through the vtable with real arrays.</summary>
    private static void SendRaw(object ocx, DynatecRDM.Rdp.RdpKeyEvent[] keys, Func<DynatecRDM.Rdp.RdpKeyEvent, int> data)
    {
        var unknown = Marshal.GetComInterfaceForObject(ocx, typeof(MSTSCLib.IMsRdpClientNonScriptable));
        try
        {
            var slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(unknown), 14 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<SendKeysFn>(slot);
            var hr = fn(unknown, keys.Length, keys.Select(k => (short)(k.Up ? -1 : 0)).ToArray(), keys.Select(data).ToArray());
            if (hr != 0) Console.WriteLine($"KEYS SendKeys returned 0x{hr:X8}");
        }
        finally { Marshal.Release(unknown); }
    }

    private static void FocusInputWindow(DynatecRDM.Rdp.RdpSessionWindow form)
    {
        var found = IntPtr.Zero;
        Win32.EnumChildWindows(form.Handle, child =>
        {
            if (Win32.GetClassName(child) == "IHWindowClass") { found = child; return false; }
            return true;
        });
        if (found != IntPtr.Zero) SetFocus(found);
        Console.WriteLine($"KEYS input window 0x{found.ToInt64():X}, focus now {Win32.GetClassName(GetFocus())}");
    }

    private static void Shoot(MonitorInfo monitor, string path, bool whole = false)
    {
        using var shot = new System.Drawing.Bitmap(monitor.Width, monitor.Height);
        using (var g = System.Drawing.Graphics.FromImage(shot))
            g.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, shot.Size);
        // The lower-left quarter is where the Run box opens; that is all the check needs to see.
        var area = whole
            ? new System.Drawing.Rectangle(0, 0, monitor.Width, monitor.Height)
            : new System.Drawing.Rectangle(0, monitor.Height / 2, monitor.Width / 2, monitor.Height / 2);
        using var corner = shot.Clone(area, shot.PixelFormat);
        using var small = new System.Drawing.Bitmap(corner, corner.Width / 2, corner.Height / 2);
        small.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);

    private static RdpSession LaunchConnected(EmbeddedSessionManager manager, RdpConnection connection)
    {
        var launch = manager.LaunchAsync(connection);
        WaitUntil(() => launch.IsCompleted, $"launch of '{connection.Name}'", 15);
        var session = launch.GetAwaiter().GetResult() ?? throw new Exception($"Launch of '{connection.Name}' failed.");
        WaitUntil(() => session.State == SessionState.Connected || !session.IsActive, $"login to '{connection.Name}'", 30);
        Check(session.State == SessionState.Connected, $"RDP login to '{connection.Name}' failed: {session.State}, {session.LastError}");
        Console.WriteLine($"LIVE '{connection.Name}' connected.");
        return session;
    }

    /// <summary>
    /// Types the hand-over the way leaving a session does - focus from a pool thread, the typing
    /// handed to the UI thread - but without Enter, and reads it back.
    /// </summary>
    private static void TypeHandOffLikeTheApp(EmbeddedSessionManager manager, RdpSession session, string? screenshotPath, MonitorInfo monitor)
    {
        var form = (DynatecRDM.Rdp.RdpSessionWindow)System.Windows.Forms.Control.FromHandle(session.WindowHandle)!;
        var command = DynatecRDM.Rdp.RdpSessionWindow.ConsoleHandOffCommand;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var saved = ClipboardText();
        try
        {
            var typing = Task.Run(async () =>
            {
                manager.Focus(session.Id);
                var inner = await dispatcher.InvokeAsync(() => form.TypeIntoRunBoxAsync(command, submit: false)).Task;
                return await inner;
            });
            WaitUntil(() => typing.IsCompleted, "typing into the Run box", 20);
            Console.WriteLine($"CONSOLE typed={typing.Result}; foreground is the session: {Win32.GetForegroundWindow() == session.WindowHandle}");
            PumpFor(700);
            if (screenshotPath is not null)
            {
                using var shot = new System.Drawing.Bitmap(monitor.Width, monitor.Height);
                using (var g = System.Drawing.Graphics.FromImage(shot))
                    g.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, shot.Size);
                using var small = new System.Drawing.Bitmap(shot, monitor.Width / 2, monitor.Height / 2);
                small.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"CONSOLE screenshot: {screenshotPath}");
            }
            form.Host.SendKeys(Control(0x1E));
            form.Host.SendKeys(Control(0x2E));
            try { WaitUntil(() => ClipboardText() == command, "the typed command to come back on the clipboard", 5); }
            catch { }
            Console.WriteLine(ClipboardText() == command
                ? "CONSOLE the Run box holds exactly the hand-over command."
                : $"CONSOLE read back: '{ClipboardText()}'");
        }
        finally
        {
            form.Host.SendKeys(DynatecRDM.Rdp.RdpKeyboard.Escape);
            PumpFor(300);
            try { if (saved is not null) System.Windows.Clipboard.SetText(saved); } catch { }
        }
    }

    private static void Describe(string when, RdpSession a, RdpSession b, SessionBarViewModel vm, SessionBarWindow? window, MonitorInfo monitor)
    {
        var centre = new Win32.POINT { X = monitor.Left + monitor.Width / 2, Y = monitor.Top + monitor.Height / 2 };
        var top = Win32.GetRootWindow(Win32.WindowFromPoint(centre));
        var fg = Win32.GetForegroundWindow();
        string Name(IntPtr h) => h == a.WindowHandle ? a.DisplayName : h == b.WindowHandle ? b.DisplayName : $"{Win32.GetClassName(h)} 0x{h.ToInt64():X}";
        Console.WriteLine($"SWITCH {when}: on top={Name(top)}; foreground={Name(fg)}; bar current={vm.Current?.DisplayName ?? "none"}; bar revealed={window?.IsRevealed}");
    }

    /// <summary>What reaches the bar while a tab is clicked: native messages, WPF input and the bar's choice.</summary>
    private sealed class BarTrace : IDisposable
    {
        private readonly List<string> _events = new();
        private readonly HwndSource? _source;
        private readonly SessionBarWindow _window;
        private readonly SessionBarViewModel _vm;
        private readonly DispatcherTimer _timer;
        private readonly long _started = Environment.TickCount64;
        private string? _last;

        public BarTrace(SessionBarWindow window, SessionBarViewModel vm, RdpSession a, RdpSession b, MonitorInfo monitor)
        {
            _window = window;
            _vm = vm;
            _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            _source?.AddHook(Hook);
            window.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, (System.Windows.Input.MouseButtonEventHandler)OnDown, true);
            window.AddHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent, (System.Windows.Input.MouseButtonEventHandler)OnUp, true);
            window.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, (System.Windows.RoutedEventHandler)OnClick, true);
            vm.PropertyChanged += OnVm;
            _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
            _timer.Tick += (_, _) =>
            {
                var centre = new Win32.POINT { X = monitor.Left + monitor.Width / 2, Y = monitor.Top + monitor.Height / 2 };
                var top = Win32.GetRootWindow(Win32.WindowFromPoint(centre));
                var fg = Win32.GetForegroundWindow();
                string Name(IntPtr h) => h == a.WindowHandle ? a.DisplayName : h == b.WindowHandle ? b.DisplayName : Win32.GetClassName(h);
                var state = $"top={Name(top)} fg={Name(fg)} current={_vm.Current?.DisplayName} revealed={_window.IsRevealed} captured={System.Windows.Input.Mouse.Captured?.GetType().Name}";
                if (state != _last) Add(state);
                _last = state;
            };
            _timer.Start();
        }

        private void Add(string what) => _events.Add($"+{Environment.TickCount64 - _started}ms {what}");

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (message)
            {
                case 0x0021: Add("WM_MOUSEACTIVATE"); break;
                case 0x0201: Add("WM_LBUTTONDOWN"); break;
                case 0x0202: Add("WM_LBUTTONUP"); break;
                case 0x0215: Add($"WM_CAPTURECHANGED to 0x{lParam.ToInt64():X}"); break;
                case 0x0006: Add($"WM_ACTIVATE {wParam}"); break;
            }
            return IntPtr.Zero;
        }

        private void OnDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            Add($"WPF down on {(e.OriginalSource as System.Windows.FrameworkElement)?.GetType().Name} handled={e.Handled}");

        private void OnUp(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            Add($"WPF up on {(e.OriginalSource as System.Windows.FrameworkElement)?.GetType().Name} handled={e.Handled}");

        private void OnClick(object sender, System.Windows.RoutedEventArgs e) =>
            Add($"WPF Click on {((e.OriginalSource as System.Windows.FrameworkElement)?.DataContext as SessionBarTab)?.Session.DisplayName ?? e.OriginalSource?.GetType().Name}");

        private void OnVm(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SessionBarViewModel.Current)) Add($"current -> {_vm.Current?.DisplayName ?? "none"}");
        }

        public string Summary() => string.Join(" | ", _events);

        public void Dispose()
        {
            _timer.Stop();
            _source?.RemoveHook(Hook);
            _window.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, (System.Windows.Input.MouseButtonEventHandler)OnDown);
            _window.RemoveHandler(System.Windows.UIElement.PreviewMouseLeftButtonUpEvent, (System.Windows.Input.MouseButtonEventHandler)OnUp);
            _window.RemoveHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, (System.Windows.RoutedEventHandler)OnClick);
            _vm.PropertyChanged -= OnVm;
        }
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// Types the console hand-over into the session's Run box without submitting it, then reads it
    /// back: Ctrl+A, Ctrl+C in the Run box, and clipboard redirection brings the text here. Escape
    /// closes the box again, so nothing runs in the session.
    /// </summary>
    private static void ConsoleDryRun(RdpSession session)
    {
        var form = (DynatecRDM.Rdp.RdpSessionWindow)System.Windows.Forms.Control.FromHandle(session.WindowHandle)!;
        var command = DynatecRDM.Rdp.RdpSessionWindow.ConsoleHandOffCommand;
        var saved = ClipboardText();
        try
        {
            var typing = form.TypeIntoRunBoxAsync(command, submit: false);
            WaitUntil(() => typing.IsCompleted, "typing into the Run box", 15);
            Check(typing.Result, "Nothing was typed into the session.");
            PumpFor(700);
            Check(form.Host.SendKeys(Control(0x1E)) && form.Host.SendKeys(Control(0x2E)), "The session refused Ctrl+A, Ctrl+C.");
            try { WaitUntil(() => ClipboardText() == command, "the typed command to come back on the clipboard", 8); }
            catch
            {
                Console.WriteLine($"CONSOLE expected: {command}");
                Console.WriteLine($"CONSOLE typed:    {ClipboardText()}");
                throw;
            }
            Console.WriteLine("CONSOLE the Run box holds exactly the hand-over command.");
        }
        finally
        {
            form.Host.SendKeys(DynatecRDM.Rdp.RdpKeyboard.Escape);
            PumpFor(300);
            try { if (saved is not null) System.Windows.Clipboard.SetText(saved); } catch { }
        }
    }

    /// <summary>
    /// Leaves the session the way the app does for a connection set to stay on the remote screen,
    /// and waits for the operator to confirm the elevation in the session. The session must then
    /// end as a normal close, not be brought back.
    /// </summary>
    private static void ConsoleHandOff(EmbeddedSessionManager manager, RdpSession session)
    {
        manager.CloseAsync(session.Id).GetAwaiter().GetResult();
        Check(session.IsActive, "Leaving disconnected the session instead of handing it over.");
        Console.WriteLine("CONSOLE confirm the administrator prompt in the session within two minutes.");
        WaitUntil(() => !session.IsActive, "the session to move to the remote console", 130);
        Check(session.State == SessionState.Disconnected, $"The hand-over ended as {session.State}: {session.LastError}");
        Console.WriteLine("CONSOLE the session moved to the remote console and ended here without reconnecting.");
    }

    private static DynatecRDM.Rdp.RdpKeyEvent[] Control(int scanCode) =>
    [
        new(0x1D, false, false), new(scanCode, false, false), new(scanCode, false, true), new(0x1D, false, true),
    ];

    private static string? ClipboardText()
    {
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch { return null; }
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

        /// <summary>Further connections a check launches, found by id; the first one answers when none matches.</summary>
        public readonly List<(RdpConnection Connection, CredentialSet? Credential)> Others = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IDataStore.GetCredentialSetAsync) => Task.FromResult(
                Others.FirstOrDefault(o => o.Credential?.Id == (Guid)args![0]!).Credential ?? Credential),
            nameof(IDataStore.GetConnectionAsync) => Task.FromResult<RdpConnection?>(
                Others.FirstOrDefault(o => o.Connection.Id == (Guid)args![0]!).Connection ?? Connection),
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
