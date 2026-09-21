using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DynatecRDM.Resources;
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
    private SessionBarService? _sessionBar;
    private MainWindow? _main;
    private MainViewModel? _mainViewModel;
    private bool _shuttingDown;
    private bool _reopeningMain;

    /// <summary>
    /// Set once startup has finished. Before that an unhandled error means the app cannot run, and
    /// it must exit - swallowing it would leave a process with no window holding the single-instance
    /// lock, so every later start would just signal it and give up.
    /// </summary>
    private bool _startupComplete;

    /// <summary>The oldest Windows the app supports: Windows 10 version 1809.</summary>
    private const int MinimumWindowsBuild = 17763;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows' language until the settings are read, so even a startup failure speaks it.
        UiLanguage.Apply(null);

        if (!ClaimSingleInstance())
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Modeless WinForms session windows need their input preprocessor in WPF's message loop.
        System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLog.Info($"{AppIdentity.Name} starting (data: {AppLog.DataDirectory})");

        if (!CheckCanRun()) return;

        // If a previous run was killed mid-launch, Default.rdp may still hold our settings.
        DefaultRdpLaunch.RecoverIfInterrupted();

        try
        {
            _services = await AppServices.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex);
            // A missing file of the app's own is something reinstalling fixes; say so, rather than
            // passing on whatever the type loader happened to report.
            MessageBox.Show(
                DependencyCheck.DescribeStartupFailure(ex)
                    ?? UiLanguage.Format(Strings.App_StartupFailed, ex.Message, AppLog.LogPath),
                AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Before any window or the tray menu exists, so nothing is ever drawn in the wrong theme
        // or the wrong language.
        UiLanguage.Apply(_services.Settings.Language);
        ThemeService.Start(this, _services.Settings.AccentColor);

        _tray = new TrayIconManager(_services, this);

        try
        {
            _sessionBar = new SessionBarService(_services, this);
        }
        catch (Exception ex)
        {
            AppLog.Error("The session bar could not be started.", ex);
        }

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

        _startupComplete = true;
        AppLog.Info("Startup complete");

        // Once the window is up: can the client in use start a connection at all on this PC? Every
        // connection would fail without it, so the user hears about it now, not at the first click.
        var embedded = _services.Sessions is EmbeddedSessionManager;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_shuttingDown) return;
            if (DependencyCheck.CheckClient(embedded) is not { } problem) return;

            AppLog.Warn($"Startup check: the {(embedded ? "in-app" : "external")} client cannot connect ({problem.Kind}).");
            ShowNotice(Strings.Dependency_Title, problem.Message);
        }));
    }

    /// <summary>
    /// What has to be true before anything else runs: a supported Windows, and a data folder the
    /// app can write to. Says what is wrong and exits when it is not.
    /// </summary>
    private bool CheckCanRun()
    {
        var build = Environment.OSVersion.Version.Build;
        if (build < MinimumWindowsBuild)
        {
            AppLog.Error($"Windows build {build} is older than the supported {MinimumWindowsBuild}.");
            MessageBox.Show(Strings.Dependency_OsTooOld, AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return false;
        }

        var folder = AppLog.DataDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-check-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            AppLog.Error($"The data folder {folder} cannot be written to.", ex);
            MessageBox.Show(UiLanguage.Format(Strings.Dependency_DataFolder, folder),
                AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return false;
        }

        return true;
    }

    /// <summary>A dialog for something the user has to act on; the tray is the fallback.</summary>
    public void ShowNotice(string title, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowNotice(title, message));
            return;
        }

        try
        {
            var dialog = new NoticeDialog(title, message);
            if (_main is { IsVisible: true } owner) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The notice could not be shown as a dialog.", ex);
            Notify(title, message, true);
        }
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
                if (_shuttingDown || _reopeningMain || !_services.Settings.CloseToTray) return;
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

        if (UiLanguage.Apply(_services.Settings.Language)) ShowNewLanguage();
    }

    /// <summary>
    /// Puts a language chosen in Settings on screen straight away. Dialogs are built each time they
    /// open, so they pick it up by themselves; what lives on - the main window, the tray and the
    /// session bar - is rebuilt.
    /// </summary>
    private void ShowNewLanguage()
    {
        AppLog.Info($"Switched the UI language to {UiLanguage.Current}.");
        _tray?.ReloadText();
        _sessionBar?.ReloadText();
        ReopenMain();
    }

    /// <summary>
    /// Closes the main window and opens a fresh one in the same place, with the same selection.
    /// While a dialog it owns is still open - Settings can be reached from the update window - it
    /// waits for that dialog, since closing an owner takes its dialogs with it.
    /// </summary>
    private void ReopenMain()
    {
        if (_main is null || _shuttingDown) return;

        var dialog = _main.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
        if (dialog is not null)
        {
            dialog.Closed += (_, _) => Dispatcher.BeginInvoke(ReopenMain);
            return;
        }

        var wasVisible = _main.IsVisible;
        var selected = _mainViewModel?.SelectedNode?.Id;

        _reopeningMain = true;
        try
        {
            _main.Close();
        }
        finally
        {
            _reopeningMain = false;
        }

        if (!wasVisible) return;
        if (selected is { } id) ShowMain(id);
        else ShowMain();
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
            _sessionBar?.Dispose();
            _tray?.Dispose();
            _main?.Close();
            ThemeService.Current?.Dispose();
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
                Strings.App_UpdateAvailable_Title,
                UiLanguage.Format(Strings.App_UpdateAvailable_Message, update.Version));
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;

        if (!_startupComplete)
        {
            // Startup did not finish, so there is no working app to go back to: say why and exit,
            // which also frees the single-instance lock for the next start.
            MessageBox.Show(
                DependencyCheck.DescribeStartupFailure(e.Exception)
                    ?? UiLanguage.Format(Strings.App_StartupFailed, e.Exception.Message, AppLog.LogPath),
                AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MessageBox.Show(
            UiLanguage.Format(Strings.App_UnhandledError, e.Exception.Message, AppLog.LogPath),
            AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
