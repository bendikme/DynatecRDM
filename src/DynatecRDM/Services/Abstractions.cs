using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>Local persistent store for every entity the manager owns.</summary>
public interface IDataStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RdpConnection>> GetConnectionsAsync(CancellationToken ct = default);
    Task<RdpConnection?> GetConnectionAsync(Guid id, CancellationToken ct = default);
    Task UpsertConnectionAsync(RdpConnection connection, CancellationToken ct = default);
    Task DeleteConnectionAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectionGroup>> GetGroupsAsync(CancellationToken ct = default);
    Task UpsertGroupAsync(ConnectionGroup group, CancellationToken ct = default);
    /// <summary>Deletes the group; children are re-parented to the deleted group's parent.</summary>
    Task DeleteGroupAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<CredentialSet>> GetCredentialSetsAsync(CancellationToken ct = default);
    Task<CredentialSet?> GetCredentialSetAsync(Guid id, CancellationToken ct = default);
    Task UpsertCredentialSetAsync(CredentialSet credential, CancellationToken ct = default);
    Task DeleteCredentialSetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<MultiConfig>> GetMultiConfigsAsync(CancellationToken ct = default);
    Task<MultiConfig?> GetMultiConfigAsync(Guid id, CancellationToken ct = default);
    Task UpsertMultiConfigAsync(MultiConfig config, CancellationToken ct = default);
    Task DeleteMultiConfigAsync(Guid id, CancellationToken ct = default);

    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Records a successful launch (increments counters, stamps last-connected).</summary>
    Task RecordLaunchAsync(Guid connectionId, CancellationToken ct = default);
    Task RecordMultiConfigLaunchAsync(Guid multiConfigId, CancellationToken ct = default);

    /// <summary>
    /// Moves library items: the group that holds each one and its position among its siblings,
    /// all in one transaction. Nothing else about the items is written.
    /// </summary>
    Task UpdateLayoutAsync(IReadOnlyList<LayoutChange> changes, CancellationToken ct = default);

    /// <summary>Adds a session to the connection history, or updates it as the session goes on.</summary>
    Task SaveLogEntryAsync(ConnectionLogEntry entry, CancellationToken ct = default);

    /// <summary>A connection's most recent sessions, newest first.</summary>
    Task<IReadOnlyList<ConnectionLogEntry>> GetConnectionLogAsync(Guid connectionId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Marks sessions a previous run never saw end as <see cref="SessionOutcome.Unknown"/>.
    /// Returns how many there were.
    /// </summary>
    Task<int> CloseAbandonedLogEntriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a connection's finished sessions from its history. A session still running stays,
    /// so its end can still be recorded. Returns how many were removed.
    /// </summary>
    Task<int> ClearConnectionLogAsync(Guid connectionId, CancellationToken ct = default);
}

/// <summary>Protects secrets at rest with DPAPI (CurrentUser scope).</summary>
public interface ISecretProtector
{
    byte[] Protect(string plainText);
    string? Unprotect(byte[] cipherText);

    /// <summary>
    /// Produces the hex payload for the "password 51:b:" line of an .rdp file: the UTF-16
    /// password encrypted with CryptProtectData using the RDP entropy conventions.
    /// </summary>
    string ProtectForRdpFile(string plainPassword);
}

/// <summary>Reads and writes TERMSRV entries in the Windows Credential Vault.</summary>
public interface IWindowsCredentialService
{
    /// <summary>Enumerates every TERMSRV/* entry (the equivalent of cmdkey /list:TERMSRV/*).</summary>
    IReadOnlyList<StoredCredentialInfo> ListTermsrvCredentials();

    /// <summary>True when a vault entry exists for this host.</summary>
    bool Exists(string host);

    /// <summary>Reads the user name stored for a host, or null when there is none.</summary>
    string? GetStoredUsername(string host);

    /// <summary>
    /// Replaces the vault entry for the host: delete, then create with the supplied login.
    /// Mirrors cmdkey /delete + cmdkey /generic, without ever putting the password on a
    /// command line when <paramref name="method"/> is NativeCredentialApi.
    /// </summary>
    bool SaveCredential(string host, string username, string password, VaultWriteMethod method);

    /// <summary>Removes the vault entry for the host. Returns false when nothing was stored.</summary>
    bool DeleteCredential(string host, VaultWriteMethod method = VaultWriteMethod.NativeCredentialApi);
}

/// <summary>A credential-vault entry as shown in the credential manager UI.</summary>
public sealed record StoredCredentialInfo(
    string TargetName,
    string Host,
    string? Username,
    string TypeName,
    string PersistName,
    DateTime? LastWrittenUtc);

/// <summary>Turns a connection into a complete .rdp file.</summary>
public interface IRdpFileBuilder
{
    /// <summary>Builds the full .rdp file body.</summary>
    string Build(RdpConnection connection, RdpBuildContext context);

    /// <summary>Writes the .rdp body to a temp file and returns its path.</summary>
    string WriteToTempFile(RdpConnection connection, RdpBuildContext context);

    /// <summary>Writes the .rdp body to an explicit path (used by Export).</summary>
    void WriteToFile(RdpConnection connection, RdpBuildContext context, string path);

    /// <summary>Parses an existing .rdp file into a connection (used by Import).</summary>
    RdpConnection Parse(string rdpFileContent, string fallbackName);
}

/// <summary>Extra inputs the builder needs that do not live on the connection itself.</summary>
public sealed class RdpBuildContext
{
    /// <summary>Resolved display settings (after multi-config overrides).</summary>
    public DisplaySettings? Display { get; set; }

    /// <summary>Resolved credential, when one is attached.</summary>
    public CredentialSet? Credential { get; set; }

    /// <summary>Plain password, only when it must be embedded in the file.</summary>
    public string? PlainPassword { get; set; }

    /// <summary>Embed "password 51:b:" in the output.</summary>
    public bool EmbedPassword { get; set; }

    /// <summary>Extra property overrides merged last.</summary>
    public Dictionary<string, string>? ExtraProperties { get; set; }

    /// <summary>Monitors known to the app, for resolving placement-driven geometry.</summary>
    public IReadOnlyList<MonitorInfo>? Monitors { get; set; }

    /// <summary>
    /// Leave out Remote Desktop's full-screen connection bar because the session bar replaces it.
    /// Only a launch sets this; an exported file keeps the bar for use outside the app.
    /// </summary>
    public bool HideConnectionBar { get; set; }
}

/// <summary>Enumerates physical displays.</summary>
public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetMonitors(bool refresh = false);

    /// <summary>Monitor containing the point, or the primary monitor.</summary>
    MonitorInfo GetMonitorAt(int x, int y);

    /// <summary>Safe lookup that clamps out-of-range indexes to the primary monitor.</summary>
    MonitorInfo GetByIndex(int index);

    /// <summary>The mstsc monitor ids (as used by selectedmonitors:s:) for our indexes.</summary>
    IReadOnlyList<int> ToMstscIds(IEnumerable<int> indexes);

    event EventHandler? MonitorsChanged;
}

/// <summary>Moves and sizes session windows after mstsc has created them.</summary>
public interface IWindowPlacementService
{
    /// <summary>Applies the placement described by <paramref name="display"/> to a window.</summary>
    Task ApplyAsync(IntPtr hwnd, DisplaySettings display, CancellationToken ct = default);

    /// <summary>Brings a session window to the foreground, restoring it if minimised.</summary>
    void FocusWindow(IntPtr hwnd);

    void SetAlwaysOnTop(IntPtr hwnd, bool onTop);
}

/// <summary>Captures thumbnails of live session windows.</summary>
public interface ISnapshotService
{
    /// <summary>Captures the window and writes a JPEG; returns the file path, or null on failure.</summary>
    Task<string?> CaptureAsync(RdpSession session, CancellationToken ct = default);

    /// <summary>Deletes stored snapshots for a session.</summary>
    void Remove(RdpSession session);

    /// <summary>Removes snapshot files that no longer belong to a live session.</summary>
    void CleanupOrphans(IEnumerable<Guid> liveSessionIds);
}

/// <summary>Owns every live session, launching, tracking and reconnecting them.</summary>
public interface ISessionManager
{
    IReadOnlyList<RdpSession> Sessions { get; }

    /// <summary>Launches a connection, optionally with overrides from a multi-config item.</summary>
    Task<RdpSession?> LaunchAsync(
        RdpConnection connection,
        DisplaySettings? displayOverride = null,
        Guid? credentialOverride = null,
        Guid? multiConfigId = null,
        Dictionary<string, string>? extraProperties = null,
        CancellationToken ct = default);

    /// <summary>Launches every enabled item of a multi-config.</summary>
    Task<IReadOnlyList<RdpSession>> LaunchMultiAsync(MultiConfig config, CancellationToken ct = default);

    /// <summary>Closes a session; the watchdog will not try to bring it back.</summary>
    Task CloseAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Closes every session started by a multi-config.</summary>
    Task CloseMultiAsync(Guid multiConfigId, CancellationToken ct = default);

    /// <summary>Forces a reconnect of a session that is down.</summary>
    Task ReconnectAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Brings the session window to the front.</summary>
    void Focus(Guid sessionId);

    /// <summary>Active session for a connection, or null.</summary>
    RdpSession? FindByConnection(Guid connectionId);

    /// <summary>True when at least one live session belongs to the multi-config.</summary>
    bool IsMultiConfigRunning(Guid multiConfigId);

    /// <summary>Requests an immediate snapshot refresh for all live sessions.</summary>
    Task RefreshSnapshotsAsync(CancellationToken ct = default);

    event EventHandler<RdpSession>? SessionStarted;
    event EventHandler<RdpSession>? SessionStateChanged;
    event EventHandler<RdpSession>? SessionEnded;
}
