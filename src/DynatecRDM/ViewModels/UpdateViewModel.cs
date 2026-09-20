using System.Diagnostics;
using DynatecRDM.Models;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Drives the update dialog. The dialog shows one state at a time and every transition runs
/// through <see cref="SetStage"/>, so the state flags the view binds to can never disagree.
/// Nothing here throws: a failed check or download ends in the failed state with a sentence
/// the user can act on, and the details go to the log.
/// </summary>
public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    private const string CheckFailedText =
        "The update check could not be completed. Check your internet connection and try again.";

    private const string DownloadFailedText =
        "The update could not be downloaded. You can try again, or download it from the release page.";

    private const string NoRepositoryText =
        "No GitHub repository is set for updates, so there is nothing to check. " +
        "Add the repository that publishes the releases in Settings.";

    private const string DisabledText =
        "Update checking is switched off. Turn it back on in Settings to be told about new versions.";

    private const int MaxMessageLength = 300;
    private const int MaxReleaseNotesLength = 20000;

    private enum Stage
    {
        Checking,
        UpToDate,
        Available,
        Downloading,
        ReadyToInstall,
        Failed,
        NotConfigured,
    }

    private static readonly string[] StageFlags =
    {
        nameof(IsChecking),
        nameof(IsUpToDate),
        nameof(HasUpdate),
        nameof(IsDownloading),
        nameof(IsReadyToInstall),
        nameof(HasFailed),
        nameof(NotConfigured),
        nameof(ShowReleaseLink),
    };

    private readonly AppServices _services;
    private readonly IAppShell _shell;
    private readonly UpdateService _updates;
    private readonly bool _ownsUpdates;

    private CancellationTokenSource? _download;
    private UpdateInfo? _update;
    private string? _installerPath;
    private bool _checkInFlight;
    private bool _disposed;

    private Stage _stage = Stage.Checking;

    private string _newVersion = string.Empty;
    private string _releaseUrl = string.Empty;
    private string _publishedText = string.Empty;
    private string _releaseNotes = string.Empty;
    private string _lastCheckedText;
    private string _message = string.Empty;
    private string _statusDetail = string.Empty;
    private string _notice = string.Empty;
    private string _notConfiguredText = NoRepositoryText;
    private bool _isPrerelease;
    private double _downloadProgress;

    public UpdateViewModel(AppServices services, IAppShell shell)
        : this(services, shell, updates: null)
    {
    }

    /// <summary>
    /// Takes an explicit update service instead of the application's own. Whatever service this
    /// dialog does not create itself belongs to its owner and keeps running after the dialog closes.
    /// </summary>
    public UpdateViewModel(AppServices services, IAppShell shell, UpdateService? updates)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        // The application owns one update service - the one running the periodic checks - so the
        // dialog shares it. Sharing matters: two services would run two checks against GitHub, and
        // disposing a second one here would tear down the first one's background loop.
        UpdateService? service = updates;
        service ??= _services.Updates;

        if (service is null)
        {
            service = new UpdateService(() => _services.Settings, () => _services.SaveSettingsAsync());
            _ownsUpdates = true;
        }

        _updates = service;
        _lastCheckedText = DescribeStoredCheck(_services.Settings.LastUpdateCheckUtc);

        CheckCommand = new AsyncRelayCommand(RecheckAsync, () => !IsDownloading);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => _update is not null && !IsDownloading);
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => IsDownloading);
        InstallCommand = new RelayCommand(Install, () => !string.IsNullOrWhiteSpace(_installerPath));
        SkipCommand = new RelayCommand(SkipVersion, () => _update is not null);
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage, () => !string.IsNullOrWhiteSpace(_releaseUrl));
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        CloseCommand = new RelayCommand(CloseDialog);
    }

    /// <summary>Raised when the dialog should close; true only after an installer was started.</summary>
    public event EventHandler<bool>? RequestClose;

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand OpenReleasePageCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CloseCommand { get; }

    // ------------------------------------------------------------------ state

    public bool IsChecking => _stage == Stage.Checking;
    public bool IsUpToDate => _stage == Stage.UpToDate;
    public bool HasUpdate => _stage == Stage.Available;
    public bool IsDownloading => _stage == Stage.Downloading;
    public bool IsReadyToInstall => _stage == Stage.ReadyToInstall;
    public bool HasFailed => _stage == Stage.Failed;
    public bool NotConfigured => _stage == Stage.NotConfigured;

    /// <summary>The release page link only makes sense once a release has been found.</summary>
    public bool ShowReleaseLink =>
        !string.IsNullOrWhiteSpace(_releaseUrl) &&
        _stage is Stage.Available or Stage.Downloading or Stage.ReadyToInstall;

    // ------------------------------------------------------------------ text

    /// <summary>The release being offered; the view binds its assets through this.</summary>
    public UpdateInfo? Update => _update;

    public string CurrentVersionText => $"You are running version {UpdateService.CurrentVersion}";

    public string RepositoryText
    {
        get
        {
            var repository = _services.Settings.UpdateRepository;
            return string.IsNullOrWhiteSpace(repository)
                ? "No repository is configured"
                : $"github.com/{repository.Trim()}";
        }
    }

    public string UpToDateText => $"You are on the latest version ({UpdateService.CurrentVersion})";

    public string AvailableVersionText =>
        _newVersion.Length == 0 ? "An update is available" : $"Version {_newVersion} is available";

    public string ReadyToInstallText =>
        _newVersion.Length == 0
            ? "The update is ready to install"
            : $"Version {_newVersion} is ready to install";

    public string VersionComparisonText =>
        $"You have {UpdateService.CurrentVersion}" +
        (_publishedText.Length == 0 ? string.Empty : $"  ·  Published {_publishedText}");

    public string PublishedText
    {
        get => _publishedText;
        private set
        {
            if (SetProperty(ref _publishedText, value)) OnPropertyChanged(nameof(VersionComparisonText));
        }
    }

    public bool IsPrerelease
    {
        get => _isPrerelease;
        private set => SetProperty(ref _isPrerelease, value);
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set => SetProperty(ref _releaseNotes, value);
    }

    public string LastCheckedText
    {
        get => _lastCheckedText;
        private set => SetProperty(ref _lastCheckedText, value);
    }

    /// <summary>The sentence shown in the failed state.</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>
    /// What the service had to say about an otherwise uneventful check, for example that the
    /// newest release is one the user chose to skip. Empty when there is nothing to add.
    /// </summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    /// <summary>A transient line shown above the buttons, for example after a cancelled download.</summary>
    public string Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public string NotConfiguredText
    {
        get => _notConfiguredText;
        private set => SetProperty(ref _notConfiguredText, value);
    }

    /// <summary>Download progress as a percentage, for a determinate progress bar.</summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (SetProperty(ref _downloadProgress, value)) OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{_downloadProgress:0} %";

    // ------------------------------------------------------------------ flow

    /// <summary>Runs the first check. Called once, when the window is shown.</summary>
    public Task InitializeAsync() => CheckAsync();

    /// <summary>
    /// The "check again" button. Someone who asks a second time has to be told the truth, so a
    /// version they skipped earlier stops being hidden from this point on.
    /// </summary>
    private Task RecheckAsync()
    {
        if (!string.IsNullOrWhiteSpace(_services.Settings.SkippedUpdateVersion))
        {
            try
            {
                _updates.Skip(string.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Clearing the skipped update version failed.", ex);
            }
        }

        return CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_checkInFlight) return;
        _checkInFlight = true;

        try
        {
            Notice = string.Empty;
            StatusDetail = string.Empty;
            _installerPath = null;
            ClearRelease();
            OnPropertyChanged(nameof(RepositoryText));

            if (string.IsNullOrWhiteSpace(_services.Settings.UpdateRepository))
            {
                NotConfiguredText = NoRepositoryText;
                SetStage(Stage.NotConfigured);
                return;
            }

            SetStage(Stage.Checking);

            UpdateCheckResult result;
            try
            {
                result = await _updates.CheckAsync(force: true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("The update check failed.", ex);
                Fail(CheckFailedText);
                return;
            }

            LastCheckedText = $"Checked at {DateTime.Now:HH:mm}";

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Update is { } update:
                    Apply(update);
                    SetStage(Stage.Available);
                    break;

                // A status of "available" without a release to show is nothing the user can act on.
                case UpdateCheckStatus.UpdateAvailable:
                case UpdateCheckStatus.UpToDate:
                    StatusDetail = Shorten(result.Message) ?? string.Empty;
                    SetStage(Stage.UpToDate);
                    break;

                case UpdateCheckStatus.NotConfigured:
                    NotConfiguredText = NoRepositoryText;
                    SetStage(Stage.NotConfigured);
                    break;

                case UpdateCheckStatus.Disabled:
                    NotConfiguredText = DisabledText;
                    SetStage(Stage.NotConfigured);
                    break;

                default:
                    Fail(Shorten(result.Message) ?? CheckFailedText);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("The update check failed.", ex);
            Fail(CheckFailedText);
        }
        finally
        {
            _checkInFlight = false;
            CheckCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Forgets the release a previous check found, so nothing from it - the pre-release pill, the
    /// notes, the GitHub link - can survive into the result of the next one.
    /// </summary>
    private void ClearRelease()
    {
        _update = null;
        _newVersion = string.Empty;
        _releaseUrl = string.Empty;
        IsPrerelease = false;
        PublishedText = string.Empty;
        ReleaseNotes = string.Empty;

        Raise(
            nameof(Update),
            nameof(AvailableVersionText),
            nameof(ReadyToInstallText),
            nameof(VersionComparisonText));
    }

    private void Apply(UpdateInfo update)
    {
        _update = update;

        _newVersion = update.Version?.Trim() ?? string.Empty;
        _releaseUrl = update.HtmlUrl?.Trim() ?? string.Empty;
        IsPrerelease = update.IsPrerelease;
        PublishedText = DescribePublished(update.PublishedUtc);
        ReleaseNotes = CleanNotes(update.ReleaseNotes);

        Raise(
            nameof(Update),
            nameof(AvailableVersionText),
            nameof(ReadyToInstallText),
            nameof(VersionComparisonText));
    }

    private async Task DownloadAsync()
    {
        if (_update is not { } update) return;

        var cts = new CancellationTokenSource();
        _download = cts;

        Notice = string.Empty;
        DownloadProgress = 0;
        SetStage(Stage.Downloading);

        try
        {
            var progress = new Progress<double>(ReportProgress);
            var path = await _updates.DownloadAsync(update, progress, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                Notice = "The download was cancelled.";
                SetStage(Stage.Available);
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                Fail(DownloadFailedText);
                return;
            }

            _installerPath = path;
            DownloadProgress = 100;
            SetStage(Stage.ReadyToInstall);
        }
        catch (OperationCanceledException)
        {
            Notice = "The download was cancelled.";
            SetStage(Stage.Available);
        }
        catch (Exception ex)
        {
            AppLog.Error("Downloading the update failed.", ex);
            Fail(DownloadFailedText);
        }
        finally
        {
            _download = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// The service reports a 0-1 fraction, including a final report of exactly 1. Anything above
    /// 1 is taken as a percentage already, so a future change of unit cannot empty the bar.
    /// </summary>
    private void ReportProgress(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;

        var percent = value <= 1.0 ? value * 100.0 : value;
        DownloadProgress = Math.Clamp(percent, 0, 100);
    }

    private void CancelDownload()
    {
        try
        {
            _download?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download finished between the click and the cancel; there is nothing to stop.
        }
        catch (Exception ex)
        {
            AppLog.Warn("Cancelling the update download failed.", ex);
        }
    }

    private void Install()
    {
        var path = _installerPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Fail(DownloadFailedText);
            return;
        }

        bool started;
        try
        {
            started = _updates.LaunchInstaller(path, silent: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the update installer failed.", ex);
            started = false;
        }

        if (!started)
        {
            Fail("The installer could not be started. You can run it yourself from the release page.");
            return;
        }

        AppLog.Info("The update installer was started; the application is closing.");
        RequestClose?.Invoke(this, true);

        try
        {
            _shell.ExitApplication();
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing the application for the update failed.", ex);
        }
    }

    private void SkipVersion()
    {
        var version = _newVersion;

        try
        {
            if (!string.IsNullOrWhiteSpace(version)) _updates.Skip(version);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Skipping version '{version}' failed.", ex);
        }

        RequestClose?.Invoke(this, false);
    }

    private void OpenReleasePage()
    {
        var url = _releaseUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        // The address came off the network. ShellExecute happily runs a local path or a custom
        // protocol, so only a plain web address is ever handed to it.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            AppLog.Warn("The release page address was refused; it is not an http or https address.");
            Notice = "The release page address could not be used.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Opening the release page '{url}' failed.", ex);
            Notice = "The release page could not be opened in your browser.";
        }
    }

    private void OpenSettings()
    {
        RequestClose?.Invoke(this, false);

        // Settings opens its own modal window, so let this one finish closing first.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ShowSettings();
            return;
        }

        dispatcher.BeginInvoke(new Action(ShowSettings), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowSettings()
    {
        try
        {
            // Reaching this dialog from Settings is a normal route. A second Settings window on
            // top of the first would leave two editors over one AppSettings object, and the stale
            // one would win the next save - so an open window is brought forward instead.
            var open = FindOpenSettingsWindow();
            if (open is not null)
            {
                open.Activate();
                return;
            }

            _shell.ShowSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("Opening settings from the update dialog failed.", ex);
        }
    }

    private static System.Windows.Window? FindOpenSettingsWindow()
    {
        var windows = System.Windows.Application.Current?.Windows;
        if (windows is null) return null;

        foreach (System.Windows.Window window in windows)
        {
            if (window is Views.SettingsWindow && window.IsVisible) return window;
        }

        return null;
    }

    private void CloseDialog()
    {
        if (IsDownloading)
        {
            CancelDownload();
            return;
        }

        RequestClose?.Invoke(this, false);
    }

    // ------------------------------------------------------------------ helpers

    private void Fail(string message)
    {
        Message = string.IsNullOrWhiteSpace(message) ? CheckFailedText : message.Trim();
        SetStage(Stage.Failed);
    }

    private void SetStage(Stage stage)
    {
        if (_stage != stage)
        {
            _stage = stage;
            Raise(StageFlags);
        }

        CheckCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        CancelDownloadCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        SkipCommand.RaiseCanExecuteChanged();
        OpenReleasePageCommand.RaiseCanExecuteChanged();
    }

    private static string CleanNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "This release does not have any notes.";

        var text = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

        if (text.Length > MaxReleaseNotesLength)
            text = text[..MaxReleaseNotesLength].TrimEnd() + "\n\n(These notes were shortened.)";

        return text.Length == 0 ? "This release does not have any notes." : text;
    }

    private static string? Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var text = message.Trim();
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength].TrimEnd() + "...";
    }

    /// <summary>A release with no publication date is common enough that it must not read "0001".</summary>
    private static string DescribePublished(DateTime publishedUtc)
    {
        if (publishedUtc == default || publishedUtc.Year < 2000) return string.Empty;

        var utc = publishedUtc.Kind == DateTimeKind.Utc
            ? publishedUtc
            : DateTime.SpecifyKind(publishedUtc, DateTimeKind.Utc);

        return utc.ToLocalTime().ToString("d MMMM yyyy");
    }

    private static string DescribeStoredCheck(DateTime? stamp)
    {
        if (stamp is not { } utc || utc == default) return "Not checked yet";

        var normalised = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return $"Last checked {normalised.ToLocalTime():d MMM yyyy, HH:mm}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _download?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download already finished.
        }
        catch (Exception ex)
        {
            AppLog.Warn("Stopping the update download during shutdown failed.", ex);
        }

        // A service handed in by the application keeps running after this dialog closes.
        if (!_ownsUpdates) return;

        try
        {
            _updates.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Disposing the update service failed.", ex);
        }
    }
}
