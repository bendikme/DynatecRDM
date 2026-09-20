using DynatecRDM.Data;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// The application's service container. A hand-rolled singleton graph rather than a DI
/// container: it is a fixed set of services and this keeps cold start to a few milliseconds.
/// </summary>
public sealed class AppServices : IDisposable
{
    private static AppServices? _current;

    public static AppServices Current =>
        _current ?? throw new InvalidOperationException("AppServices has not been initialised.");

    public static bool IsInitialised => _current is not null;

    public SqliteDataStore Store { get; private set; } = null!;
    public ISecretProtector Protector { get; private set; } = null!;
    public IWindowsCredentialService Credentials { get; private set; } = null!;
    public IRdpFileBuilder RdpBuilder { get; private set; } = null!;
    public IMonitorService Monitors { get; private set; } = null!;
    public IWindowPlacementService Placement { get; private set; } = null!;
    public ISnapshotService Snapshots { get; private set; } = null!;
    public ISessionManager Sessions { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;
    public ConfigTransfer Transfer { get; private set; } = null!;

    private AppSettings _settings = new();

    /// <summary>Live application settings. Replaced wholesale when the user saves.</summary>
    public AppSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value ?? new AppSettings();
            SettingsChanged?.Invoke(this, _settings);
        }
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    private AppServices()
    {
    }

    /// <summary>Builds the graph and opens the database. Safe to call more than once.</summary>
    public static async Task<AppServices> InitializeAsync(CancellationToken ct = default)
    {
        if (_current is not null) return _current;

        var services = new AppServices();

        services.Store = new SqliteDataStore();
        services.Protector = new SecretProtector();
        services.Credentials = new WindowsCredentialService();
        services.RdpBuilder = new RdpFileBuilder(services.Protector);
        services.Monitors = new MonitorService();
        services.Placement = new WindowPlacementService(services.Monitors);
        services.Snapshots = new SnapshotService(() => services.Settings);
        services.Sessions = new SessionManager(
            services.Store,
            services.RdpBuilder,
            services.Protector,
            services.Credentials,
            services.Monitors,
            services.Placement,
            services.Snapshots,
            () => services.Settings);
        services.Hotkeys = new HotkeyService();
        services.Updates = new UpdateService(() => services.Settings, () => services.SaveSettingsAsync());
        services.Transfer = new ConfigTransfer(services.Store, services.Protector);

        await services.Store.InitializeAsync(ct).ConfigureAwait(false);
        services._settings = await services.Store.GetSettingsAsync(ct).ConfigureAwait(false);

        _current = services;
        return services;
    }

    /// <summary>Persists the current settings.</summary>
    public Task SaveSettingsAsync(CancellationToken ct = default) =>
        Store.SaveSettingsAsync(Settings, ct);

    public void Dispose()
    {
        (Sessions as IDisposable)?.Dispose();
        (Monitors as IDisposable)?.Dispose();
        Updates?.Dispose();
        Hotkeys?.Dispose();
        Store?.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
