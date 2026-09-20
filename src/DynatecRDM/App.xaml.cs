using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DynatecRDM.Services;
using DynatecRDM.Tray;
using DynatecRDM.ViewModels;
using DynatecRDM.Views;

namespace DynatecRDM;

public partial class App : Application, IAppShell
{
    private const string InstanceMutexName = @"Local\DynatecRDM.SingleInstance";
    private const string ActivateEventName = @"Local\DynatecRDM.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activateCts;

    private AppServices? _services;
    private TrayIconManager? _tray;
    private MainWindow? _main;
    private MainViewModel? _mainViewModel;
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLog.Info($"DYNATEC RDM starting (data: {AppLog.DataDirectory})");

        DarkTitleBar.ApplyToAllWindows();

        try
        {
            _services = await AppServices.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex);
            MessageBox.Show(
                $"DYNATEC Remote Desktop Manager could not start.\n\n{ex.Message}\n\nLog: {AppLog.LogPath}",
                "DYNATEC RDM", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _tray = new TrayIconManager(_services, this);

        if (_services.Settings.QuickLaunchHotkey is { Length: > 0 } gesture)
        {
            _services.Hotkeys.Pressed += (_, _) => ShowQuickLaunch();
            if (!_services.Hotkeys.Register(gesture))
                AppLog.Warn($"Could not register the quick-launch hotkey '{gesture}'.");
        }

        if (_services.Settings.UpdateCheckEnabled &&
            !string.IsNullOrWhiteSpace(_services.Settings.UpdateRepository))
        {
            _services.Updates.UpdateAvailable += OnUpdateAvailable;
            _services.Updates.StartBackgroundChecks();
        }

        var startHidden = _services.Settings.StartInTray
            || e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        AppLog.Info($"StartInTray={_services.Settings.StartInTray}, args=[{string.Join(" ", e.Args)}], startHidden={startHidden}");

        if (!startHidden)
        {
            ShowMain();
            AppLog.Info($"Main window shown: visible={_main?.IsVisible}, state={_main?.WindowState}, " +
                        $"left={_main?.Left}, top={_main?.Top}, w={_main?.ActualWidth}, h={_main?.ActualHeight}");
        }

        AppLog.Info("Startup complete");
    }

    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, InstanceMutexName, out var isNew);
            if (!isNew) return false;

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateCts = new CancellationTokenSource();
            var handle = _activateEvent;
            var token = _activateCts.Token;

            var listener = new Thread(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!handle.WaitOne(500)) continue;
                        if (token.IsCancellationRequested) return;
                        Dispatcher.BeginInvoke(ShowMain);
                    }
                    catch
                    {
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "DynatecRDM.Activate",
            };
            listener.Start();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Single-instance check failed; continuing anyway.", ex);
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not signal the running instance.", ex);
        }
    }

    public void ShowMain()
    {
        if (_services is null) return;

        if (_main is null)
        {
            _mainViewModel = new MainViewModel(_services, this);
            _main = new MainWindow(_mainViewModel);
            _main.Closing += (_, args) =>
            {
                if (_shuttingDown || !_services.Settings.CloseToTray) return;
                args.Cancel = true;
                _main.Hide();
            };

            // When the close is not cancelled the window is gone for good; holding the reference
            // would make the next ShowMain() call Show() on a closed window, which throws.
            _main.Closed += (_, _) =>
            {
                _main = null;
                _mainViewModel = null;
            };
            _ = _mainViewModel.LoadAsync();
        }

        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
        _main.Focus();
    }

    public void ShowMain(Guid selectId)
    {
        ShowMain();
        _mainViewModel?.SelectById(selectId);
    }

    public void HideMain() => _main?.Hide();

    public void ShowCredentials()
    {
        if (_services is null) return;
        ShowDialog(new CredentialsWindow(new CredentialsViewModel(_services)));
    }

    public void ShowSettings()
    {
        if (_services is null) return;
        ShowDialog(new SettingsWindow(new SettingsViewModel(_services, this)));
    }

    public void ShowUpdates()
    {
        if (_services is null) return;
        ShowDialog(new UpdateWindow(new UpdateViewModel(_services, this)));
    }

    public void ShowTransfer(bool startOnImportTab = false)
    {
        if (_services is null) return;
        ShowDialog(new TransferWindow(new TransferViewModel(_services, startOnImportTab)));
    }

    private void ShowDialog(Window window)
    {
        window.Owner = _main is { IsVisible: true } ? _main : null;
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public void ShowQuickLaunch() => _tray?.ShowQuickLaunch();

    public void Notify(string title, string message, bool isError = false) =>
        _tray?.Notify(title, message, isError);

    public void ExitApplication()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        AppLog.Info("Shutting down");

        try
        {
            _activateCts?.Cancel();
            _tray?.Dispose();
            _main?.Close();
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Error during shutdown", ex);
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activateCts?.Cancel();
            _activateEvent?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // Nothing useful to do this late.
        }

        AppLog.Shutdown();
        base.OnExit(e);
    }

    private void OnUpdateAvailable(object? sender, Models.UpdateInfo update)
    {
        // A background check must never steal focus: it notifies from the tray and lets the user
        // decide when to look at it.
        Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown) return;
            Notify(
                "Update available",
                $"Version {update.Version} is ready to install. Open the manager to update.");
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"Something went wrong.\n\n{e.Exception.Message}\n\nThe log is at {AppLog.LogPath}",
            "DYNATEC RDM", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
