using System.Diagnostics;
using System.IO;
using System.Reflection;
using DynatecRDM.Models;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Backs the settings dialog. Edits live on this object until Save, so Cancel simply discards
/// them. Save replaces AppServices.Settings, persists it, then applies the few settings that
/// have to take effect immediately (launch at logon, the quick-launch hotkey, the accent).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly string[] AccentPalette =
    {
        "#2A94FF", "#0A84FF", "#5E5CE6", "#BF5AF2",
        "#FF375F", "#FF9F0A", "#30D158", "#64D2FF",
    };

    private readonly AppServices _services;
    private readonly IAppShell _shell;

    private bool _startInTray;
    private bool _closeToTray;
    private bool _launchAtLogon;

    private string _hotkeyText = string.Empty;
    private string? _hotkeyError;
    private bool _showGroupsInTray;
    private bool _showSnapshotsInTray;
    private int _trayMenuMaxItems;

    private bool _enableSnapshots;
    private int _snapshotIntervalSeconds;
    private int _snapshotMaxEdge;
    private int _snapshotQuality;
    private string _snapshotFolderText = "Measuring...";

    private bool _watchdogEnabled;
    private int _watchdogPollSeconds;

    private int _credentialDeliveryIndex;
    private int _vaultWriteMethodIndex;
    private bool _shredRdpFiles;

    private bool _updateCheckEnabled;
    private string _updateRepository = string.Empty;
    private bool _updateIncludePrereleases;
    private int _updateCheckIntervalHours;
    private bool _updateInstallAutomatically;
    private string _updateAccessToken = string.Empty;

    private string _accentColor = "#2A94FF";
    private bool _confirmSessionClose;

    private bool _isBusy;
    private string _status = string.Empty;
    private bool _statusIsError;

    public SettingsViewModel(AppServices services, IAppShell shell)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        var settings = services.Settings;

        _startInTray = settings.StartInTray;
        _closeToTray = settings.CloseToTray;
        _launchAtLogon = settings.LaunchAtLogon;

        _hotkeyText = settings.QuickLaunchHotkey ?? string.Empty;
        _hotkeyError = _hotkeyText.Length == 0 ? null : HotkeyService.Validate(_hotkeyText);
        _showGroupsInTray = settings.ShowGroupsInTray;
        _showSnapshotsInTray = settings.ShowSnapshotsInTray;
        _trayMenuMaxItems = settings.TrayMenuMaxItems;

        _enableSnapshots = settings.EnableSnapshots;
        _snapshotIntervalSeconds = settings.SnapshotIntervalSeconds;
        _snapshotMaxEdge = settings.SnapshotMaxEdge;
        _snapshotQuality = settings.SnapshotQuality;

        _watchdogEnabled = settings.WatchdogEnabled;
        _watchdogPollSeconds = settings.WatchdogPollSeconds;

        _credentialDeliveryIndex = IndexOfDelivery(settings.DefaultCredentialDelivery);
        _vaultWriteMethodIndex = settings.VaultWriteMethod == VaultWriteMethod.CmdKey ? 1 : 0;
        _shredRdpFiles = settings.ShredRdpFiles;

        _updateCheckEnabled = settings.UpdateCheckEnabled;
        _updateRepository = settings.UpdateRepository ?? string.Empty;
        _updateIncludePrereleases = settings.UpdateIncludePrereleases;
        _updateCheckIntervalHours = settings.UpdateCheckIntervalHours;
        _updateInstallAutomatically = settings.UpdateInstallAutomatically;
        _updateAccessToken = settings.UpdateAccessToken ?? string.Empty;

        _accentColor = string.IsNullOrWhiteSpace(settings.AccentColor) ? "#2A94FF" : settings.AccentColor;
        _confirmSessionClose = settings.ConfirmSessionClose;

        VersionText = ResolveVersion();

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
        ClearSnapshotsCommand = new AsyncRelayCommand(ClearSnapshotsAsync, () => !IsBusy);
        OpenDataFolderCommand = new RelayCommand(() => OpenPath(DataFolder, isFolder: true));
        CheckForUpdatesCommand = new RelayCommand(() => _shell.ShowUpdates());
        OpenTransferCommand = new RelayCommand(() => _shell.ShowTransfer());
        OpenLogCommand = new RelayCommand(OpenLog);
    }

    /// <summary>True when the settings were saved, false when the user cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand ClearSnapshotsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand OpenTransferCommand { get; }

    // ----------------------------------------------------------------- startup

    public bool StartInTray
    {
        get => _startInTray;
        set => SetProperty(ref _startInTray, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    public bool LaunchAtLogon
    {
        get => _launchAtLogon;
        set => SetProperty(ref _launchAtLogon, value);
    }

    // ------------------------------------------------------------ quick launch

    public string HotkeyText
    {
        get => _hotkeyText;
        set
        {
            if (!SetProperty(ref _hotkeyText, value)) return;
            HotkeyError = string.IsNullOrWhiteSpace(value) ? null : HotkeyService.Validate(value);
        }
    }

    /// <summary>Null while the gesture is usable; otherwise a message fit for the dialog.</summary>
    public string? HotkeyError
    {
        get => _hotkeyError;
        private set => SetProperty(ref _hotkeyError, value);
    }

    public bool ShowGroupsInTray
    {
        get => _showGroupsInTray;
        set => SetProperty(ref _showGroupsInTray, value);
    }

    public bool ShowSnapshotsInTray
    {
        get => _showSnapshotsInTray;
        set => SetProperty(ref _showSnapshotsInTray, value);
    }

    public int TrayMenuMaxItems
    {
        get => _trayMenuMaxItems;
        set => SetClamped(ref _trayMenuMaxItems, value, 5, 200, nameof(TrayMenuMaxItems));
    }

    // --------------------------------------------------------------- snapshots

    public bool EnableSnapshots
    {
        get => _enableSnapshots;
        set => SetProperty(ref _enableSnapshots, value);
    }

    public int SnapshotIntervalSeconds
    {
        get => _snapshotIntervalSeconds;
        set => SetClamped(ref _snapshotIntervalSeconds, value, 0, 3600, nameof(SnapshotIntervalSeconds));
    }

    public int SnapshotMaxEdge
    {
        get => _snapshotMaxEdge;
        set => SetClamped(ref _snapshotMaxEdge, value, 48, 4096, nameof(SnapshotMaxEdge));
    }

    public int SnapshotQuality
    {
        get => _snapshotQuality;
        set => SetClamped(ref _snapshotQuality, value, 1, 100, nameof(SnapshotQuality));
    }

    public string SnapshotFolderText
    {
        get => _snapshotFolderText;
        private set => SetProperty(ref _snapshotFolderText, value);
    }

    // ---------------------------------------------------------------- watchdog

    public bool WatchdogEnabled
    {
        get => _watchdogEnabled;
        set => SetProperty(ref _watchdogEnabled, value);
    }

    public int WatchdogPollSeconds
    {
        get => _watchdogPollSeconds;
        set => SetClamped(ref _watchdogPollSeconds, value, 1, 120, nameof(WatchdogPollSeconds));
    }

    // ---------------------------------------------------------------- security

    /// <summary>0 vault, 1 embedded, 2 both, 3 prompt - matches the combo box order.</summary>
    public int CredentialDeliveryIndex
    {
        get => _credentialDeliveryIndex;
        set => SetProperty(ref _credentialDeliveryIndex, Math.Clamp(value, 0, 3));
    }

    /// <summary>0 native credential API, 1 cmdkey.exe - matches the combo box order.</summary>
    public int VaultWriteMethodIndex
    {
        get => _vaultWriteMethodIndex;
        set
        {
            if (!SetProperty(ref _vaultWriteMethodIndex, Math.Clamp(value, 0, 1))) return;
            OnPropertyChanged(nameof(VaultWriteMethodExplanation));
        }
    }

    public string VaultWriteMethodExplanation => _vaultWriteMethodIndex == 1
        ? "cmdkey.exe is the documented manual route, but the password is visible on its command line while it runs."
        : "The Windows credential API writes in-process, so the password never reaches a command line.";

    public bool ShredRdpFiles
    {
        get => _shredRdpFiles;
        set => SetProperty(ref _shredRdpFiles, value);
    }

    // ----------------------------------------------------------------- updates

    public bool UpdateCheckEnabled
    {
        get => _updateCheckEnabled;
        set => SetProperty(ref _updateCheckEnabled, value);
    }

    /// <summary>The GitHub repository releases come from, as "owner/repo".</summary>
    public string UpdateRepository
    {
        get => _updateRepository;
        set
        {
            if (!SetProperty(ref _updateRepository, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(UpdateRepositoryError));
            OnPropertyChanged(nameof(HasUpdateRepositoryError));
            OnPropertyChanged(nameof(UpdateStateText));
        }
    }

    /// <summary>Null when the value is usable, otherwise a message for the user.</summary>
    public string? UpdateRepositoryError
    {
        get
        {
            var text = (_updateRepository ?? string.Empty).Trim();
            if (text.Length == 0) return null;

            var parts = text.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return "Use the owner/repo form, for example DYNATEC/DynatecRDM.";

            foreach (var part in parts)
                foreach (var c in part)
                    if (!char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                        return $"'{c}' cannot appear in a GitHub owner or repository name.";

            return null;
        }
    }

    public bool HasUpdateRepositoryError => UpdateRepositoryError is not null;

    public bool UpdateIncludePrereleases
    {
        get => _updateIncludePrereleases;
        set => SetProperty(ref _updateIncludePrereleases, value);
    }

    public int UpdateCheckIntervalHours
    {
        get => _updateCheckIntervalHours;
        set => SetClamped(ref _updateCheckIntervalHours, value, 1, 720, nameof(UpdateCheckIntervalHours));
    }

    public bool UpdateInstallAutomatically
    {
        get => _updateInstallAutomatically;
        set => SetProperty(ref _updateInstallAutomatically, value);
    }

    /// <summary>Optional token, only needed for a private repository.</summary>
    public string UpdateAccessToken
    {
        get => _updateAccessToken;
        set => SetProperty(ref _updateAccessToken, value ?? string.Empty);
    }

    public string UpdateStateText =>
        string.IsNullOrWhiteSpace(_updateRepository)
            ? "No repository set, so update checking is off."
            : $"Checking {_updateRepository.Trim()} for new releases.";

    public string CurrentVersionText => $"Installed version {UpdateService.CurrentVersion}";

    public string LastUpdateCheckText
    {
        get
        {
            var at = _services.Settings.LastUpdateCheckUtc;
            return at is null
                ? "Not checked yet."
                : $"Last checked {at.Value.ToLocalTime():yyyy-MM-dd HH:mm}.";
        }
    }

    // -------------------------------------------------------------- appearance

    public IReadOnlyList<string> AccentSwatches { get; } = AccentPalette;

    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            SetProperty(ref _accentColor, value);
        }
    }

    public bool ConfirmSessionClose
    {
        get => _confirmSessionClose;
        set => SetProperty(ref _confirmSessionClose, value);
    }

    // ------------------------------------------------------------------- about

    public string ProductName => "DYNATEC Remote Desktop Manager";

    public string VersionText { get; }

    public string DataFolder => AppLog.DataDirectory;

    // ------------------------------------------------------------------ shared

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
            ClearSnapshotsCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    // -------------------------------------------------------------- operations

    /// <summary>
    /// Reflects the real world: the Run key decides whether launch-at-logon is on, not the
    /// stored flag, and the snapshot folder is measured off the UI thread.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            var registered = await Task.Run(() => StartupService.IsEnabled).ConfigureAwait(true);
            if (registered != _launchAtLogon)
            {
                _launchAtLogon = registered;
                OnPropertyChanged(nameof(LaunchAtLogon));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the Windows startup entry failed.", ex);
        }

        await MeasureSnapshotsAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        var hotkey = (_hotkeyText ?? string.Empty).Trim();
        if (hotkey.Length > 0)
        {
            var problem = HotkeyService.Validate(hotkey);
            if (problem is not null)
            {
                HotkeyError = problem;
                SetStatus(problem, true);
                return;
            }
        }

        if (UpdateRepositoryError is { } repoProblem)
        {
            SetStatus(repoProblem, true);
            return;
        }

        IsBusy = true;
        try
        {
            var settings = _services.Settings.Clone();

            settings.StartInTray = StartInTray;
            settings.CloseToTray = CloseToTray;
            settings.LaunchAtLogon = LaunchAtLogon;

            settings.QuickLaunchHotkey = hotkey.Length == 0 ? null : hotkey;
            settings.ShowGroupsInTray = ShowGroupsInTray;
            settings.ShowSnapshotsInTray = ShowSnapshotsInTray;
            settings.TrayMenuMaxItems = TrayMenuMaxItems;

            settings.EnableSnapshots = EnableSnapshots;
            settings.SnapshotIntervalSeconds = SnapshotIntervalSeconds;
            settings.SnapshotMaxEdge = SnapshotMaxEdge;
            settings.SnapshotQuality = SnapshotQuality;

            settings.WatchdogEnabled = WatchdogEnabled;
            settings.WatchdogPollSeconds = WatchdogPollSeconds;

            settings.DefaultCredentialDelivery = DeliveryFromIndex(CredentialDeliveryIndex);
            settings.VaultWriteMethod = VaultWriteMethodIndex == 1
                ? VaultWriteMethod.CmdKey
                : VaultWriteMethod.NativeCredentialApi;
            settings.ShredRdpFiles = ShredRdpFiles;

            settings.UpdateCheckEnabled = UpdateCheckEnabled;
            settings.UpdateRepository = (UpdateRepository ?? string.Empty).Trim();
            settings.UpdateIncludePrereleases = UpdateIncludePrereleases;
            settings.UpdateCheckIntervalHours = UpdateCheckIntervalHours;
            settings.UpdateInstallAutomatically = UpdateInstallAutomatically;
            settings.UpdateAccessToken = string.IsNullOrWhiteSpace(UpdateAccessToken)
                ? null
                : UpdateAccessToken.Trim();

            settings.AccentColor = AccentColor;
            settings.ConfirmSessionClose = ConfirmSessionClose;

            _services.Settings = settings;
            await _services.SaveSettingsAsync().ConfigureAwait(true);

            await ApplyStartupAsync(settings.LaunchAtLogon).ConfigureAwait(true);
            ApplyAccent(settings.AccentColor);
            ApplyHotkey(settings.QuickLaunchHotkey);

            AppLog.Info("Settings saved.");
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving the settings failed.", ex);
            SetStatus("The settings could not be saved. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyStartupAsync(bool enabled)
    {
        try
        {
            var applied = await Task.Run(() => StartupService.SetEnabled(enabled)).ConfigureAwait(true);
            if (applied) return;

            AppLog.Warn("The Windows startup entry could not be updated.");
            _shell.Notify(
                "DYNATEC RDM",
                "Launch at logon could not be changed. See the log for details.",
                true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Updating the Windows startup entry failed.", ex);
        }
    }

    private void ApplyHotkey(string? gesture)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                _services.Hotkeys.Unregister();
                return;
            }

            if (_services.Hotkeys.Register(gesture)) return;

            AppLog.Warn($"The quick-launch hotkey '{gesture}' could not be registered.");
            _shell.Notify(
                "DYNATEC RDM",
                $"The shortcut {gesture} is already taken by another application.",
                true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Re-registering the quick-launch hotkey failed.", ex);
        }
    }

    private async Task ClearSnapshotsAsync()
    {
        IsBusy = true;
        try
        {
            await Task.Run(SnapshotService.PurgeAll).ConfigureAwait(true);
            await MeasureSnapshotsAsync().ConfigureAwait(true);
            SetStatus("Stored snapshots cleared.", false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clearing the snapshots failed.", ex);
            SetStatus("The snapshots could not be cleared. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MeasureSnapshotsAsync()
    {
        var folder = SnapshotService.SnapshotDirectory;

        var measured = await Task.Run(() => Measure(folder)).ConfigureAwait(true);

        SnapshotFolderText = measured.Count == 0
            ? "No snapshots stored."
            : $"{measured.Count} file{(measured.Count == 1 ? string.Empty : "s")}, {FormatSize(measured.Bytes)} on disk.";
    }

    private static (int Count, long Bytes) Measure(string folder)
    {
        var count = 0;
        var bytes = 0L;

        try
        {
            if (!Directory.Exists(folder)) return (0, 0L);

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch (Exception ex)
                {
                    AppLog.Debug_($"Snapshot '{file}' could not be measured: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Measuring the snapshot folder failed.", ex);
        }

        return (count, bytes);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private static void OpenPath(string path, bool isFolder)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (isFolder) Directory.CreateDirectory(path);

                using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Opening '{path}' failed.", ex);
            }
        });
    }

    private static void OpenLog()
    {
        _ = Task.Run(() =>
        {
            var path = AppLog.LogPath;
            if (string.IsNullOrEmpty(path)) path = Path.Combine(AppLog.DataDirectory, "dynatec-rdm.log");

            try
            {
                if (!File.Exists(path))
                {
                    AppLog.Info("The log file was requested before anything had been written.");
                    using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.DataDirectory}\"")
                    {
                        UseShellExecute = true,
                    });
                    return;
                }

                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Opening the log with its default application failed; falling back to Notepad.", ex);
                try
                {
                    using var notepad = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
                    {
                        UseShellExecute = true,
                    });
                }
                catch (Exception fallbackEx)
                {
                    AppLog.Warn("Opening the log failed.", fallbackEx);
                }
            }
        });
    }

    /// <summary>
    /// Re-tints the accent brushes in place. They are left thawed in Theme.xaml precisely so
    /// this costs one colour assignment instead of a resource-dictionary rebuild.
    /// </summary>
    private static void ApplyAccent(string hex)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;

            var source = DynatecRDM.Converters.HexToBrushConverter.GetBrush(hex);
            if (source is null) return;

            var accent = source.Color;
            var background = System.Windows.Media.Color.FromRgb(0x16, 0x18, 0x1D);

            SetAccent(app, "AccentBrush", accent);
            SetAccent(app, "AccentHoverBrush", Mix(accent, System.Windows.Media.Colors.White, 0.18));
            SetAccent(app, "AccentPressedBrush", Mix(accent, System.Windows.Media.Colors.Black, 0.18));
            SetAccent(app, "AccentSubtleBrush", Mix(accent, background, 0.78));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Applying the accent colour failed.", ex);
        }
    }

    private static void SetAccent(System.Windows.Application app, string key, System.Windows.Media.Color color)
    {
        try
        {
            if (app.TryFindResource(key) is not System.Windows.Media.SolidColorBrush brush) return;
            if (brush.IsFrozen) return;
            brush.Color = color;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Re-tinting '{key}' failed.", ex);
        }
    }

    private static System.Windows.Media.Color Mix(
        System.Windows.Media.Color from,
        System.Windows.Media.Color to,
        double amount)
    {
        var t = Math.Clamp(amount, 0d, 1d);
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    private static int IndexOfDelivery(CredentialDelivery delivery) => delivery switch
    {
        CredentialDelivery.WindowsVault => 0,
        CredentialDelivery.EmbeddedInRdpFile => 1,
        CredentialDelivery.Both => 2,
        CredentialDelivery.Prompt => 3,
        _ => 2,
    };

    private static CredentialDelivery DeliveryFromIndex(int index) => index switch
    {
        0 => CredentialDelivery.WindowsVault,
        1 => CredentialDelivery.EmbeddedInRdpFile,
        3 => CredentialDelivery.Prompt,
        _ => CredentialDelivery.Both,
    };

    private static string ResolveVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "1.0.0";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the assembly version failed.", ex);
            return "1.0.0";
        }
    }

    private void SetStatus(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }

    /// <summary>
    /// Stores a clamped number and always notifies when the typed value had to be corrected, so
    /// the text box snaps back to what was actually kept instead of showing a rejected value.
    /// </summary>
    private void SetClamped(ref int field, int value, int min, int max, string name)
    {
        var clamped = Math.Clamp(value, min, max);

        if (field == clamped)
        {
            if (clamped != value) OnPropertyChanged(name);
            return;
        }

        field = clamped;
        OnPropertyChanged(name);
    }
}
