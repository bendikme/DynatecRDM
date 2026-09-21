using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Threading;
using DynatecRDM.Data;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Rdp;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;
using DynatecRDM.Views;
using MSTSCLib;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

public static class Program
{
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
        foreach (var resource in new[] { "Palette.Dark", "Theme", "Controls", "EditorStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/DynatecRDM;component/Themes/{resource}.xaml")
            });

        if (args.Contains("--transport"))
        {
            Run("RDP Connect sends negotiation to a loopback listener", ConnectTransport);
            app.Shutdown();
            return _failed == 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] is "--live" or "--live-hover" or "--live-edge")
        {
            Run("Live saved RDP connection and session controls", () => LiveSessionChecks.Run(args[1], args[0] != "--live", args[0] == "--live-edge"));
            app.Shutdown();
            return _failed == 0 ? 0 : 1;
        }

        Run("Disconnect confirmation belongs to one session", Confirmation);
        Run("Removed tabs cannot switch or disconnect sessions", RemovedTabs);
        Run("Mouse wheel wraps sessions in both directions", Wheel);
        Run("UI launch exceptions and cancellation propagate", UiFailures);
        Run("Session bar renders and does not activate on click", BarWindow);
        Run("Full-screen minimize/restore preserves the layout", FullScreen);
        Run("Reconfiguring the RDP control clears old identity and settings", Credentials);
        Run("Session bar follows window identity and hides when the session ends", BarTracking);
        Run("Session bar reveal strip scales and respects monitor boundaries", RevealBand);
        Run("Update tokens are encrypted, round-trip, and migrate from older settings", TokenStorage);
        Run("RDP Connect sends negotiation to a loopback listener", ConnectTransport);
        Console.WriteLine(_failed == 0 ? "All regression checks passed." : $"{_failed} regression check(s) failed.");
        app.Shutdown();
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static (SessionBarViewModel Vm, Host Host, RdpSession A, RdpSession B) Sessions()
    {
        var host = new Host();
        var vm = new SessionBarViewModel(host, () => true);
        host.Vm = vm;
        var a = new RdpSession { DisplayName = "Test A", State = SessionState.Connected };
        var b = new RdpSession { DisplayName = "Test B", State = SessionState.Connected };
        vm.Sync(new[] { a, b });
        vm.SetCurrent(a.Id);
        return (vm, host, a, b);
    }

    private static void Confirmation()
    {
        var (vm, host, a, b) = Sessions();
        vm.DisconnectCommand.Execute(null);
        Check(vm.IsConfirmingDisconnect && host.Closed is null, "First click disconnected immediately.");
        vm.SetCurrent(b.Id);
        Check(!vm.IsConfirmingDisconnect, "Confirmation crossed to another session.");
        vm.DisconnectCommand.Execute(null);
        Check(host.Closed is null, "New session inherited confirmation.");
        vm.DisconnectCommand.Execute(null);
        Check(host.Closed == b.Id, "Second click did not close the selected session.");
        vm.ResetConfirm();
    }

    private static void RemovedTabs()
    {
        var (vm, host, a, b) = Sessions();
        var stale = vm.Tabs[0];
        vm.DisconnectCommand.Execute(null);
        a.State = SessionState.Disconnected;
        vm.DisconnectCommand.Execute(null);
        Check(host.Closed is null, "Disconnected an inactive session during an event race.");
        vm.Sync(new[] { b });
        vm.SwitchCommand.Execute(stale);
        Check(host.Switched is null && vm.Current is null && !vm.IsConfirmingDisconnect,
            "Stale tab retained actions or confirmation.");
        Check(!vm.MinimizeCommand.CanExecute(null), "Minimize stayed enabled without a current session.");
    }

    private static void Wheel()
    {
        var (vm, host, a, b) = Sessions();
        vm.SwitchBy(-1);
        Check(host.Switched == b.Id, "Previous did not wrap.");
        vm.SwitchBy(1);
        Check(host.Switched == a.Id, "Next did not wrap.");
    }

    private static void UiFailures()
    {
        var method = typeof(EmbeddedSessionManager).GetMethod("OnUiAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            method.Invoke(null, new object[] { (Action)(() => throw new InvalidOperationException("expected")), CancellationToken.None });
            throw new Exception("Launch failure was swallowed.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException { Message: "expected" }) { }
        var ran = false;
        try
        {
            method.Invoke(null, new object[] { (Action)(() => ran = true), new CancellationToken(true) });
            throw new Exception("Canceled launch was accepted.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
        Check(!ran, "A canceled launch still created a window.");
    }

    private static void BarWindow()
    {
        var (vm, host, a, b) = Sessions();
        var window = new SessionBarWindow(vm);
        try
        {
            // Exercise real WPF layout and native messages off-screen, without taking user focus.
            var monitor = new MonitorInfo(0, "test", "test", -20000, -20000, 1280, 720,
                -20000, -20000, 1280, 720, false, 96, 96);
            Check(window.Reveal(monitor), "Reveal failed.");
            Pump();
            Check(window.IsRevealed && window.IsVisible && window.ActualWidth > 300, "Bar has no usable content.");
            var tabs = (ItemsControl)window.FindName("TabList");
            var buttons = Descendants<Button>(tabs).ToArray();
            Check(buttons.Length == 2, "Session tabs did not render.");
            ((IInvokeProvider)new ButtonAutomationPeer(buttons[1]).GetPattern(PatternInterface.Invoke)).Invoke();
            Pump();
            Check(host.Switched == b.Id && vm.Current == b, "Clicking a rendered tab did not select its session.");
            window.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 60)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            Check(vm.Current == b, "Partial wheel notch switched too early.");
            window.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 60)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            Check(vm.Current == a, "Wheel input on the bar did not switch sessions.");
            var handle = new WindowInteropHelper(window).Handle;
            Check(SendMessage(handle, 0x0021, handle, new IntPtr(1)).ToInt32() == 3,
                "WM_MOUSEACTIVATE would steal focus from RDP.");
            window.Conceal(true);
            Check(window.Reveal(monitor), "Reveal during a hide failed.");
            Pump();
            Check(window.IsRevealed && window.IsVisible, "Old hide callback closed a newly revealed bar.");
            window.Conceal(false);
            Check(!window.IsVisible && !window.IsRevealed, "Conceal left the bar visible.");
        }
        finally { window.ForceClose(); }
    }

    private static void FullScreen()
    {
        using var window = new RdpSessionWindow("Regression test");
        window.ShowInTaskbar = false;
        var bounds = new System.Drawing.Rectangle(-20000, -20000, 1280, 720);
        window.PlaceAt(bounds, true, new System.Drawing.Rectangle(-19900, -19900, 800, 600));
        ShowWithoutActivation(window);
        window.MinimizeSession();
        // The ActiveX control can request this in response to minimizing the container.
        var leave = (EventHandler?)typeof(RdpControlHost)
            .GetField("RequestLeaveFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Host);
        leave?.Invoke(window.Host, EventArgs.Empty);
        Check(window.IsFullScreen, "Minimize lost full-screen state.");
        Check(!window.TopMost, "Minimized window stayed topmost.");
        window.WindowState = Forms.FormWindowState.Normal;
        Check(window.IsFullScreen && window.TopMost, "Restore lost the full-screen z-order.");
        window.LeaveFullScreen();
        Check(!window.IsFullScreen && !window.TopMost && window.FormBorderStyle == Forms.FormBorderStyle.Sizable,
            "Exit full-screen failed to restore window chrome.");
    }

    private static void Credentials()
    {
        using var window = new RdpSessionWindow("Disconnected RDP test");
        window.ShowInTaskbar = false;
        window.Location = new System.Drawing.Point(-20000, -20000);
        ShowWithoutActivation(window);
        var ax = window.Host.Control;
        var plan = new RdpDisplayPlan(1024, 768, 32, false, ResizeBehavior.FollowWindow, 100, 100);
        var connection = new RdpConnection { Host = "test.invalid" };
        connection.Security.AlternateShell = "old-shell.exe";
        connection.Security.ShellWorkingDirectory = "C:\\old";
        connection.CredentialDelivery = CredentialDelivery.Prompt;
        RdpControlConfigurator.Configure(ax, connection, plan,
            new RdpCredential(new CredentialSet { Username = "old-user", Domain = "old-domain" }, "test-only-password"));
        connection.Security.AlternateShell = null;
        connection.Security.ShellWorkingDirectory = null;
        connection.CredentialDelivery = CredentialDelivery.WindowsVault;
        RdpControlConfigurator.Configure(ax, connection, plan, null);
        var ns = (IMsRdpClientNonScriptable5)window.Host.Ocx;
        Check(ax.UserName == "" && ax.Domain == "", "Reused a removed identity.");
        Check(ax.SecuredSettings2.StartProgram == "" && ax.SecuredSettings2.WorkDir == "", "Reused a removed shell.");
        Check(!ns.PromptForCredentials, "Prompt mode stayed latched after changing delivery.");
        Check(!ns.ShowRedirectionWarningDialog, "Unsupported pre-connection dialog is enabled.");
        Check(ax.AdvancedSettings9.AuthenticationLevel == (uint)connection.Security.AuthenticationLevel
            && ax.AdvancedSettings9.EnableCredSspSupport == connection.Security.EnableCredSsp,
            "Server authentication settings changed.");
        Check(!ax.AdvancedSettings9.RedirectDrives, "Default connection exposes local drives.");
        RdpControlConfigurator.Configure(ax, connection, plan, null, hideConnectionBar: false);
        Check(ax.AdvancedSettings9.DisplayConnectionBar && ax.AdvancedSettings9.ConnectionBarShowRestoreButton,
            "Disabling the custom bar left no native restore button.");
    }

    private static void BarTracking()
    {
        using var first = new Forms.Form { FormBorderStyle = Forms.FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(-20000, -20000, 1280, 720) };
        using var second = new Forms.Form { ShowInTaskbar = false, StartPosition = Forms.FormStartPosition.Manual,
            Bounds = new System.Drawing.Rectangle(-18000, -20000, 800, 600) };
        var child = new Forms.Panel { Dock = Forms.DockStyle.Fill };
        first.Controls.Add(child);
        ShowWithoutActivation(first);
        ShowWithoutActivation(second);
        var monitor = new MonitorInfo(0, "test", "test", first.Left, first.Top, first.Width, first.Height,
            first.Left, first.Top, first.Width, first.Height, false, 96, 96);
        var session = new RdpSession { DisplayName = "Test", ProcessId = Environment.ProcessId,
            WindowHandle = first.Handle, State = SessionState.Connected };
        var sessions = new FakeSessions();
        sessions.Items.Add(session);
        var services = (AppServices)Activator.CreateInstance(typeof(AppServices), nonPublic: true)!;
        typeof(AppServices).GetProperty(nameof(AppServices.Sessions))!.SetValue(services, sessions);
        typeof(AppServices).GetProperty(nameof(AppServices.Monitors))!.SetValue(services, new FakeMonitors(monitor));
        using var bar = new SessionBarService(services, DispatchProxy.Create<IAppShell, NoCalls>());
        var vm = (SessionBarViewModel)typeof(SessionBarService).GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
        var windowField = typeof(SessionBarService).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Check(ReferenceEquals(Call(bar, "SessionFor", child.Handle), session), "RDP child window did not match its session.");
        Check(Call(bar, "SessionFor", second.Handle) is null, "Unrelated app window matched by shared process id.");
        Call(bar, "ShowBar", monitor, session, false);
        var barWindow = (SessionBarWindow)windowField.GetValue(bar)!;
        Check(barWindow.IsRevealed && vm.Current == session, "Bar did not select the displayed session.");
        // A focus request can be rejected by Windows. The bar must not claim the requested tab won.
        bar.SwitchTo(session);
        Check(!barWindow.IsRevealed && vm.Current is null, "Failed focus retained destructive actions.");
        Call(bar, "ShowBar", monitor, session, false);
        first.Hide();
        Call(bar, "Poll");
        Check(!barWindow.IsRevealed, "Bar remained visible over a hidden session.");
        ShowWithoutActivation(first);
        Call(bar, "ShowBar", monitor, session, false);
        sessions.End(session);
        Check(!barWindow.IsRevealed && vm.Current is null && vm.Tabs.Count == 0,
            "Ending the last session left a ghost bar.");
    }

    private static void RevealBand()
    {
        foreach (var dpi in new uint[] { 96, 120, 144, 192 })
        {
            var monitor = new MonitorInfo(1, "upper-left", "test", -2560, -1440, 2560, 1440,
                -2560, -1440, 2560, 1440, false, dpi, dpi);
            var x = monitor.Left + monitor.Width / 2;
            var height = (int)Math.Ceiling(12 * dpi / 96.0);
            bool Inside(int px, int py) => SessionBarService.IsInRevealBand(new Win32.POINT { X = px, Y = py }, monitor);
            Check(Inside(x, monitor.Top) && Inside(x, monitor.Top + height - 1), $"Reveal strip is too narrow at {dpi} DPI.");
            Check(!Inside(x, monitor.Top - 1) && !Inside(x, monitor.Top + height), "Reveal crossed a vertical boundary.");
            Check(Inside(monitor.Left + monitor.Width / 4, monitor.Top + 8), "Middle edge is not reachable.");
            Check(!Inside(monitor.Left + monitor.Width / 4 - 1, monitor.Top)
                && !Inside(monitor.Right - monitor.Width / 4, monitor.Top), "Reveal covers remote window buttons.");
        }
    }

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static void TokenStorage()
    {
        // Only synthetic settings in a uniquely named database beside this test executable.
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, $"regression-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteDataStore(path);
            store.InitializeAsync().GetAwaiter().GetResult();
            var settings = new AppSettings { UpdateAccessToken = "test-only-secret" };
            store.SaveSettingsAsync(settings).GetAwaiter().GetResult();
            Check(settings.UpdateAccessToken == "test-only-secret", "Saving mutated the UI token.");
            using var cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key='app'";
            var raw = (string)cmd.ExecuteScalar()!;
            Check(!raw.Contains("test-only-secret") && raw.Contains("dpapi:v1:"), "Database contains the plaintext token.");
            Check(store.GetSettingsAsync().GetAwaiter().GetResult().UpdateAccessToken == "test-only-secret", "Token did not round-trip.");
            cmd.CommandText = "UPDATE settings SET value='{\"updateAccessToken\":\"legacy-test-token\"}' WHERE key='app'";
            cmd.ExecuteNonQuery();
            Check(store.GetSettingsAsync().GetAwaiter().GetResult().UpdateAccessToken == "legacy-test-token", "Legacy token stopped working.");
            cmd.CommandText = "SELECT value FROM settings WHERE key='app'";
            Check(!((string)cmd.ExecuteScalar()!).Contains("legacy-test-token"), "Legacy token was not encrypted on load.");
            cmd.CommandText = "UPDATE settings SET value='{\"updateAccessToken\":\"dpapi:v1:corrupt\",\"startInTray\":true}' WHERE key='app'";
            cmd.ExecuteNonQuery();
            var damaged = store.GetSettingsAsync().GetAwaiter().GetResult();
            Check(damaged.UpdateAccessToken is null && damaged.StartInTray, "Damaged token erased unrelated settings or became a bearer token.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) System.IO.File.Delete(path + suffix);
        }
    }

    private static void ConnectTransport()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        using var window = new RdpSessionWindow("RDP transport regression");
        window.ShowInTaskbar = false;
        window.Location = new System.Drawing.Point(-20000, -20000);
        ShowWithoutActivation(window);
        RdpDisconnectInfo? disconnect = null;
        window.Disconnected += (_, info) => disconnect = info;
        var connection = new RdpConnection { Host = "127.0.0.1", Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port };
        var plan = new RdpDisplayPlan(1024, 768, 32, false, ResizeBehavior.FollowWindow, 100, 100);
        window.Start(connection, plan, null);
        var frame = new DispatcherFrame();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        poll.Tick += (_, _) =>
        {
            if (accept.IsCompleted || disconnect is not null || deadline.IsCancellationRequested) frame.Continue = false;
        };
        poll.Start();
        Dispatcher.PushFrame(frame);
        poll.Stop();
        Check(accept.IsCompletedSuccessfully, $"Connect never opened a socket; disconnect={disconnect?.Code}, kind={disconnect?.Kind}, message={disconnect?.Message}");
        using var client = accept.Result;
        var bytes = new byte[4096];
        var read = client.GetStream().ReadAsync(bytes, deadline.Token).AsTask();
        while (!read.IsCompleted && !deadline.IsCancellationRequested) Pump();
        Check(read.GetAwaiter().GetResult() >= 4 && bytes[0] == 3, "Client sent no RDP negotiation packet.");
        window.Host.Disconnect();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void ShowWithoutActivation(Forms.Form window)
    {
        var handle = window.Handle;
        var style = DynatecRDM.Interop.Win32.GetWindowLongPtr(handle, -20).ToInt64();
        DynatecRDM.Interop.Win32.SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000));
        window.Show();
        Pump();
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private sealed class Host : ISessionBarHost
    {
        public SessionBarViewModel Vm = null!;
        public Guid? Closed;
        public Guid? Switched;
        public void SwitchTo(RdpSession session) { Switched = session.Id; Vm.SetCurrent(session.Id); }
        public void Disconnect(RdpSession session) => Closed = session.Id;
        public void Minimize(RdpSession session) { }
        public void ExitFullScreen(RdpSession session) { }
        public void LaunchAnother() { }
        public void OpenManager() { }
    }

    public class NoCalls : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected shell call: {targetMethod?.Name}");
    }

    private sealed class FakeMonitors(MonitorInfo monitor) : IMonitorService
    {
        public IReadOnlyList<MonitorInfo> GetMonitors(bool refresh = false) => new[] { monitor };
        public MonitorInfo GetMonitorAt(int x, int y) => monitor;
        public MonitorInfo GetByIndex(int index) => monitor;
        public IReadOnlyList<int> ToMstscIds(IEnumerable<int> indexes) => new[] { 0 };
        public event EventHandler? MonitorsChanged { add { } remove { } }
    }

    private sealed class FakeSessions : ISessionManager
    {
        public readonly List<RdpSession> Items = new();
        public IReadOnlyList<RdpSession> Sessions => Items;
        public event EventHandler<RdpSession>? SessionStarted { add { } remove { } }
        public event EventHandler<RdpSession>? SessionStateChanged { add { } remove { } }
        public event EventHandler<RdpSession>? SessionEnded;
        public void End(RdpSession session) { Items.Remove(session); session.State = SessionState.Disconnected; SessionEnded?.Invoke(this, session); }
        public void Focus(Guid id) { }
        public RdpSession? FindByConnection(Guid id) => null;
        public bool IsMultiConfigRunning(Guid id) => false;
        public Task CloseAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseMultiAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReconnectAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshSnapshotsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<RdpSession?> LaunchAsync(RdpConnection c, DisplaySettings? d = null, Guid? credential = null,
            Guid? multi = null, Dictionary<string, string>? extra = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RdpSession>> LaunchMultiAsync(MultiConfig config, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
