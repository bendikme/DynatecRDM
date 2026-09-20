using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynatecRDM.Models;
using DynatecRDM.Services;
using Microsoft.Data.Sqlite;

namespace DynatecRDM.Data;

/// <summary>
/// SQLite-backed <see cref="IDataStore"/>. A single connection stays open for the lifetime of the
/// process and every call is serialised through a semaphore - the fastest and simplest arrangement
/// for a single-user desktop application.
/// </summary>
public sealed class SqliteDataStore : IDataStore, IDisposable
{
    private const int SchemaVersion = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const string SettingsRowKey = "app";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // AppSettings stores "not positioned" as double.NaN, which plain JSON cannot express.
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // DictionaryKeyPolicy deliberately stays null: raw .rdp property names must survive verbatim.
        WriteIndented = false,
    };

    private static readonly string[] GroupColumns =
    [
        "id", "name", "parent_id", "color", "description", "sort_order", "is_expanded",
        "created_utc", "modified_utc",
    ];

    private static readonly string[] CredentialColumns =
    [
        "id", "name", "domain", "username", "protected_password", "notes", "is_default",
        "created_utc", "modified_utc",
    ];

    private static readonly string[] ConnectionColumns =
    [
        "id", "name", "description", "group_id", "host", "port", "credential_set_id",
        "credential_delivery", "display_json", "experience_json", "redirection_json",
        "gateway_json", "security_json", "custom_properties_json", "auto_reconnect",
        "max_reconnect_attempts", "reconnect_delay_seconds", "color", "tags", "favorite",
        "sort_order", "created_utc", "modified_utc", "last_connected_utc", "launch_count",
    ];

    private static readonly string[] MultiConfigColumns =
    [
        "id", "name", "description", "group_id", "color", "sequential", "initial_delay_ms",
        "close_together", "favorite", "sort_order", "created_utc", "modified_utc",
        "last_launched_utc", "launch_count",
    ];

    private static readonly string[] MultiConfigItemColumns =
    [
        "id", "multiconfig_id", "connection_id", "display_override_json",
        "credential_set_id_override", "auto_reconnect_override", "custom_property_overrides_json",
        "sort_index", "delay_ms", "enabled", "display_name_override",
    ];

    private static readonly string SelectGroupsSql = BuildSelect("\"groups\"", GroupColumns);
    private static readonly string SelectCredentialsSql = BuildSelect("credentials", CredentialColumns);
    private static readonly string SelectConnectionsSql = BuildSelect("connections", ConnectionColumns);
    private static readonly string SelectMultiConfigsSql = BuildSelect("multiconfigs", MultiConfigColumns);
    private static readonly string SelectMultiConfigItemsSql = BuildSelect("multiconfig_items", MultiConfigItemColumns);

    private static readonly string UpsertGroupSql = BuildUpsert("\"groups\"", GroupColumns);
    private static readonly string UpsertCredentialSql = BuildUpsert("credentials", CredentialColumns);
    private static readonly string UpsertConnectionSql = BuildUpsert("connections", ConnectionColumns);
    private static readonly string UpsertMultiConfigSql = BuildUpsert("multiconfigs", MultiConfigColumns);
    private static readonly string UpsertMultiConfigItemSql = BuildUpsert("multiconfig_items", MultiConfigItemColumns);

    private const string PragmaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 3000;
        """;

    private const string SchemaV1Sql = """
        CREATE TABLE IF NOT EXISTS "groups" (
            id           TEXT    NOT NULL PRIMARY KEY,
            name         TEXT    NOT NULL DEFAULT '',
            parent_id    TEXT    NULL,
            color        TEXT    NULL,
            description  TEXT    NULL,
            sort_order   INTEGER NOT NULL DEFAULT 0,
            is_expanded  INTEGER NOT NULL DEFAULT 1,
            created_utc  TEXT    NOT NULL,
            modified_utc TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS credentials (
            id                 TEXT    NOT NULL PRIMARY KEY,
            name               TEXT    NOT NULL DEFAULT '',
            domain             TEXT    NULL,
            username           TEXT    NOT NULL DEFAULT '',
            protected_password BLOB    NULL,
            notes              TEXT    NULL,
            is_default         INTEGER NOT NULL DEFAULT 0,
            created_utc        TEXT    NOT NULL,
            modified_utc       TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS connections (
            id                      TEXT    NOT NULL PRIMARY KEY,
            name                    TEXT    NOT NULL DEFAULT '',
            description             TEXT    NULL,
            group_id                TEXT    NULL,
            host                    TEXT    NOT NULL DEFAULT '',
            port                    INTEGER NOT NULL DEFAULT 3389,
            credential_set_id       TEXT    NULL,
            credential_delivery     INTEGER NOT NULL DEFAULT 2,
            display_json            TEXT    NULL,
            experience_json         TEXT    NULL,
            redirection_json        TEXT    NULL,
            gateway_json            TEXT    NULL,
            security_json           TEXT    NULL,
            custom_properties_json  TEXT    NULL,
            auto_reconnect          INTEGER NOT NULL DEFAULT 1,
            max_reconnect_attempts  INTEGER NOT NULL DEFAULT 10,
            reconnect_delay_seconds INTEGER NOT NULL DEFAULT 5,
            color                   TEXT    NULL,
            tags                    TEXT    NULL,
            favorite                INTEGER NOT NULL DEFAULT 0,
            sort_order              INTEGER NOT NULL DEFAULT 0,
            created_utc             TEXT    NOT NULL,
            modified_utc            TEXT    NOT NULL,
            last_connected_utc      TEXT    NULL,
            launch_count            INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS multiconfigs (
            id                TEXT    NOT NULL PRIMARY KEY,
            name              TEXT    NOT NULL DEFAULT '',
            description       TEXT    NULL,
            group_id          TEXT    NULL,
            color             TEXT    NULL,
            sequential        INTEGER NOT NULL DEFAULT 1,
            initial_delay_ms  INTEGER NOT NULL DEFAULT 0,
            close_together    INTEGER NOT NULL DEFAULT 0,
            favorite          INTEGER NOT NULL DEFAULT 0,
            sort_order        INTEGER NOT NULL DEFAULT 0,
            created_utc       TEXT    NOT NULL,
            modified_utc      TEXT    NOT NULL,
            last_launched_utc TEXT    NULL,
            launch_count      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS multiconfig_items (
            id                             TEXT    NOT NULL PRIMARY KEY,
            multiconfig_id                 TEXT    NOT NULL REFERENCES multiconfigs(id) ON DELETE CASCADE,
            connection_id                  TEXT    NULL,
            display_override_json          TEXT    NULL,
            credential_set_id_override     TEXT    NULL,
            auto_reconnect_override        INTEGER NULL,
            custom_property_overrides_json TEXT    NULL,
            sort_index                     INTEGER NOT NULL DEFAULT 0,
            delay_ms                       INTEGER NOT NULL DEFAULT 400,
            enabled                        INTEGER NOT NULL DEFAULT 1,
            display_name_override          TEXT    NULL
        );

        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_connections_group_id ON connections(group_id);
        CREATE INDEX IF NOT EXISTS ix_connections_name ON connections(name);
        CREATE INDEX IF NOT EXISTS ix_multiconfig_items_parent ON multiconfig_items(multiconfig_id);
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _databasePath;

    private SqliteConnection? _cn;
    private SqliteCommand? _connectionsQuery;
    private int _disposed;

    public SqliteDataStore(string? databasePath = null)
    {
        _databasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(AppLog.DataDirectory, "dynatec-rdm.db")
            : databasePath);
    }

    /// <summary>Absolute path of the database file backing this store.</summary>
    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // LeaseAsync opens and migrates on first use; taking a lease here moves that cost to
        // startup instead of the first query.
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RdpConnection>> GetConnectionsAsync(CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);

        // The connection list is the hottest query in the app (tray menu, tree, search), so the
        // statement is compiled once and reused.
        var cmd = _connectionsQuery;
        if (cmd is null)
        {
            cmd = lease.Connection.CreateCommand();
            try
            {
                cmd.CommandText = SelectConnectionsSql + " ORDER BY sort_order, name COLLATE NOCASE;";
                cmd.Prepare();
            }
            catch
            {
                cmd.Dispose();
                throw;
            }
            _connectionsQuery = cmd;
        }

        var result = new List<RdpConnection>(32);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadConnection(reader));
        return result;
    }

    public async Task<RdpConnection?> GetConnectionAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SelectConnectionsSql + " WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", ToDb(id));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadConnection(reader) : null;
    }

    public async Task UpsertConnectionAsync(RdpConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Id == Guid.Empty) connection.Id = Guid.NewGuid();

        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = UpsertConnectionSql;
        BindConnection(cmd, connection);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteConnectionAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;
        var key = ToDb(id);
        using var tx = cn.BeginTransaction();

        // A multi-config item pointing at a deleted connection can never launch, so it goes too.
        var orphans = await ExecuteAsync(
            cn, tx, "DELETE FROM multiconfig_items WHERE connection_id = $id;", ct, ("$id", key))
            .ConfigureAwait(false);
        await ExecuteAsync(cn, tx, "DELETE FROM connections WHERE id = $id;", ct, ("$id", key))
            .ConfigureAwait(false);
        tx.Commit();

        if (orphans > 0)
            AppLog.Info($"Removed {orphans} multi-config item(s) that referenced the deleted connection {id:D}.");
    }

    public async Task<IReadOnlyList<ConnectionGroup>> GetGroupsAsync(CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SelectGroupsSql + " ORDER BY sort_order, name COLLATE NOCASE;";

        var result = new List<ConnectionGroup>(16);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadGroup(reader));
        return result;
    }

    public async Task UpsertGroupAsync(ConnectionGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Id == Guid.Empty) group.Id = Guid.NewGuid();
        if (group.ParentId == group.Id)
        {
            AppLog.Warn($"Group '{group.Name}' was parented to itself; storing it at the root instead.");
            group.ParentId = null;
        }

        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = UpsertGroupSql;
        Set(cmd, "$id", ToDb(group.Id));
        Set(cmd, "$name", Text(group.Name));
        Set(cmd, "$parent_id", ToDb(group.ParentId));
        Set(cmd, "$color", NullIfEmpty(group.Color));
        Set(cmd, "$description", NullIfEmpty(group.Description));
        Set(cmd, "$sort_order", group.SortOrder);
        Set(cmd, "$is_expanded", ToDb(group.IsExpanded));
        Set(cmd, "$created_utc", ToDb(group.CreatedUtc));
        Set(cmd, "$modified_utc", ToDb(group.ModifiedUtc));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;
        using var tx = cn.BeginTransaction();

        string? parentId;
        using (var lookup = cn.CreateCommand())
        {
            lookup.Transaction = tx;
            lookup.CommandText = "SELECT parent_id FROM \"groups\" WHERE id = $id;";
            lookup.Parameters.AddWithValue("$id", ToDb(id));
            var scalar = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is null)
            {
                tx.Rollback();
                return;
            }
            parentId = scalar as string;
        }

        var key = ToDb(id);
        var now = ToDb(DateTime.UtcNow);

        await ExecuteAsync(cn, tx,
            "UPDATE \"groups\" SET parent_id = $parent, modified_utc = $now WHERE parent_id = $id;",
            ct, ("$parent", parentId), ("$now", now), ("$id", key)).ConfigureAwait(false);
        await ExecuteAsync(cn, tx,
            "UPDATE connections SET group_id = $parent, modified_utc = $now WHERE group_id = $id;",
            ct, ("$parent", parentId), ("$now", now), ("$id", key)).ConfigureAwait(false);
        await ExecuteAsync(cn, tx,
            "UPDATE multiconfigs SET group_id = $parent, modified_utc = $now WHERE group_id = $id;",
            ct, ("$parent", parentId), ("$now", now), ("$id", key)).ConfigureAwait(false);
        await ExecuteAsync(cn, tx, "DELETE FROM \"groups\" WHERE id = $id;", ct, ("$id", key))
            .ConfigureAwait(false);

        tx.Commit();
    }

    public async Task<IReadOnlyList<CredentialSet>> GetCredentialSetsAsync(CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SelectCredentialsSql + " ORDER BY is_default DESC, name COLLATE NOCASE;";

        var result = new List<CredentialSet>(8);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadCredential(reader));
        return result;
    }

    public async Task<CredentialSet?> GetCredentialSetAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SelectCredentialsSql + " WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", ToDb(id));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadCredential(reader) : null;
    }

    public async Task UpsertCredentialSetAsync(CredentialSet credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Id == Guid.Empty) credential.Id = Guid.NewGuid();

        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;
        using var tx = cn.BeginTransaction();

        if (credential.IsDefault)
        {
            await ExecuteAsync(cn, tx,
                "UPDATE credentials SET is_default = 0 WHERE id <> $id AND is_default <> 0;",
                ct, ("$id", ToDb(credential.Id))).ConfigureAwait(false);
        }

        using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = UpsertCredentialSql;
            Set(cmd, "$id", ToDb(credential.Id));
            Set(cmd, "$name", Text(credential.Name));
            Set(cmd, "$domain", NullIfEmpty(credential.Domain));
            Set(cmd, "$username", Text(credential.Username));
            Set(cmd, "$protected_password", credential.ProtectedPassword is { Length: > 0 } blob ? blob : null);
            Set(cmd, "$notes", NullIfEmpty(credential.Notes));
            Set(cmd, "$is_default", ToDb(credential.IsDefault));
            Set(cmd, "$created_utc", ToDb(credential.CreatedUtc));
            Set(cmd, "$modified_utc", ToDb(credential.ModifiedUtc));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        tx.Commit();
    }

    public async Task DeleteCredentialSetAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;
        var key = ToDb(id);
        using var tx = cn.BeginTransaction();

        // Leaving a dangling credential id behind would silently break those connections.
        await ExecuteAsync(cn, tx,
            "UPDATE connections SET credential_set_id = NULL WHERE credential_set_id = $id;",
            ct, ("$id", key)).ConfigureAwait(false);
        await ExecuteAsync(cn, tx,
            "UPDATE multiconfig_items SET credential_set_id_override = NULL WHERE credential_set_id_override = $id;",
            ct, ("$id", key)).ConfigureAwait(false);
        await ExecuteAsync(cn, tx, "DELETE FROM credentials WHERE id = $id;", ct, ("$id", key))
            .ConfigureAwait(false);

        tx.Commit();
    }

    public async Task<IReadOnlyList<MultiConfig>> GetMultiConfigsAsync(CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;

        var result = new List<MultiConfig>(8);
        var byId = new Dictionary<Guid, MultiConfig>();

        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = SelectMultiConfigsSql + " ORDER BY sort_order, name COLLATE NOCASE;";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var config = ReadMultiConfig(reader);
                result.Add(config);
                byId[config.Id] = config;
            }
        }

        if (result.Count > 0)
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = SelectMultiConfigItemsSql + " ORDER BY multiconfig_id, sort_index;";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (byId.TryGetValue(GetGuid(reader, 1), out var config))
                    config.Items.Add(ReadMultiConfigItem(reader));
            }
        }

        return result;
    }

    public async Task<MultiConfig?> GetMultiConfigAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;
        var key = ToDb(id);

        MultiConfig? config;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = SelectMultiConfigsSql + " WHERE id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", key);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            config = await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadMultiConfig(reader) : null;
        }

        if (config is null) return null;

        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = SelectMultiConfigItemsSql + " WHERE multiconfig_id = $id ORDER BY sort_index;";
            cmd.Parameters.AddWithValue("$id", key);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                config.Items.Add(ReadMultiConfigItem(reader));
        }

        return config;
    }

    public async Task UpsertMultiConfigAsync(MultiConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Id == Guid.Empty) config.Id = Guid.NewGuid();

        var items = config.Items;
        if (items.Count > 0)
        {
            // MultiConfigItem.Clone keeps its id, so a "duplicate row" in the editor can produce
            // two items with the same key - which would collapse into one row on the next load.
            var seen = new HashSet<Guid>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Id != Guid.Empty && seen.Add(item.Id)) continue;
                var fresh = Guid.NewGuid();
                item.Id = fresh;
                seen.Add(fresh);
            }
        }

        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;
        using var tx = cn.BeginTransaction();

        using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = UpsertMultiConfigSql;
            Set(cmd, "$id", ToDb(config.Id));
            Set(cmd, "$name", Text(config.Name));
            Set(cmd, "$description", NullIfEmpty(config.Description));
            Set(cmd, "$group_id", ToDb(config.GroupId));
            Set(cmd, "$color", NullIfEmpty(config.Color));
            Set(cmd, "$sequential", ToDb(config.Sequential));
            Set(cmd, "$initial_delay_ms", config.InitialDelayMs);
            Set(cmd, "$close_together", ToDb(config.CloseTogether));
            Set(cmd, "$favorite", ToDb(config.Favorite));
            Set(cmd, "$sort_order", config.SortOrder);
            Set(cmd, "$created_utc", ToDb(config.CreatedUtc));
            Set(cmd, "$modified_utc", ToDb(config.ModifiedUtc));
            Set(cmd, "$last_launched_utc", ToDb(config.LastLaunchedUtc));
            Set(cmd, "$launch_count", config.LaunchCount);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var prune = cn.CreateCommand())
        {
            prune.Transaction = tx;
            prune.Parameters.AddWithValue("$mc", ToDb(config.Id));
            if (items.Count == 0)
            {
                prune.CommandText = "DELETE FROM multiconfig_items WHERE multiconfig_id = $mc;";
            }
            else
            {
                var sql = new StringBuilder(96 + items.Count * 6);
                sql.Append("DELETE FROM multiconfig_items WHERE multiconfig_id = $mc AND id NOT IN (");
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
                    var name = "$k" + i.ToString(CultureInfo.InvariantCulture);
                    sql.Append(name);
                    prune.Parameters.AddWithValue(name, ToDb(items[i].Id));
                }
                sql.Append(");");
                prune.CommandText = sql.ToString();
            }
            await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (items.Count > 0)
        {
            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = UpsertMultiConfigItemSql;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                Set(cmd, "$id", ToDb(item.Id));
                Set(cmd, "$multiconfig_id", ToDb(config.Id));
                Set(cmd, "$connection_id", ToDb(item.ConnectionId));
                Set(cmd, "$display_override_json", JsonSerializer.Serialize(item.Display, Json));
                Set(cmd, "$credential_set_id_override", ToDb(item.CredentialSetIdOverride));
                Set(cmd, "$auto_reconnect_override", ToDb(item.AutoReconnectOverride));
                Set(cmd, "$custom_property_overrides_json",
                    item.CustomPropertyOverrides is { Count: > 0 } overrides
                        ? JsonSerializer.Serialize(overrides, Json)
                        : null);
                Set(cmd, "$sort_index", item.Order);
                Set(cmd, "$delay_ms", item.DelayMs);
                Set(cmd, "$enabled", ToDb(item.Enabled));
                Set(cmd, "$display_name_override", NullIfEmpty(item.DisplayNameOverride));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        tx.Commit();
    }

    public async Task DeleteMultiConfigAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        var cn = lease.Connection;
        using var tx = cn.BeginTransaction();

        // foreign_keys = ON lets ON DELETE CASCADE take the items with it.
        await ExecuteAsync(cn, tx, "DELETE FROM multiconfigs WHERE id = $id;", ct, ("$id", ToDb(id)))
            .ConfigureAwait(false);
        tx.Commit();
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", SettingsRowKey);

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (scalar is not string raw || raw.Length == 0) return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(raw, Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("Stored application settings were unreadable; falling back to defaults.", ex);
            return new AppSettings();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var payload = JsonSerializer.Serialize(settings, Json);

        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO settings (key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", SettingsRowKey);
        cmd.Parameters.AddWithValue("$value", payload);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordLaunchAsync(Guid connectionId, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(lease.Connection, null,
            "UPDATE connections SET launch_count = launch_count + 1, last_connected_utc = $now WHERE id = $id;",
            ct, ("$now", ToDb(DateTime.UtcNow)), ("$id", ToDb(connectionId))).ConfigureAwait(false);
    }

    public async Task RecordMultiConfigLaunchAsync(Guid multiConfigId, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(lease.Connection, null,
            "UPDATE multiconfigs SET launch_count = launch_count + 1, last_launched_utc = $now WHERE id = $id;",
            ct, ("$now", ToDb(DateTime.UtcNow)), ("$id", ToDb(multiConfigId))).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _gate.Wait(TimeSpan.FromSeconds(2));
        }
        catch (ObjectDisposedException)
        {
            // Nothing left to wait for.
        }

        try
        {
            CloseConnection();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The database did not close cleanly.", ex);
        }

        _gate.Dispose();
    }

    private void CloseConnection()
    {
        _connectionsQuery?.Dispose();
        _connectionsQuery = null;

        var cn = _cn;
        _cn = null;
        cn?.Dispose();
    }

    private readonly struct Lease : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        internal Lease(SemaphoreSlim gate, SqliteConnection connection)
        {
            _gate = gate;
            Connection = connection;
        }

        public SqliteConnection Connection { get; }

        public void Dispose()
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // The store was disposed while this operation was still in flight; there is
                // nothing left to hand the slot back to.
            }
        }
    }

    private async ValueTask<Lease> LeaseAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_cn is null) await OpenCoreAsync(ct).ConfigureAwait(false);
            return new Lease(_gate, _cn!);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async Task OpenCoreAsync(CancellationToken ct)
    {
        try
        {
            _cn = await OpenAndMigrateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            AppLog.Error($"The database at '{_databasePath}' is corrupt and will be replaced by an empty one.", ex);
            CloseConnection();
            QuarantineDatabaseFiles();
            _cn = await OpenAndMigrateAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAndMigrateAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            // Pooling off: this connection is ours for the whole process, and Dispose must really
            // release the file handle - the quarantine path and tests depend on it.
            Pooling = false,
        };

        SqliteConnection? cn = null;
        try
        {
            cn = new SqliteConnection(builder.ConnectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);

            using (var pragma = cn.CreateCommand())
            {
                pragma.CommandText = PragmaSql;
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await MigrateAsync(cn, ct).ConfigureAwait(false);
            return cn;
        }
        catch
        {
            cn?.Dispose();
            throw;
        }
    }

    private static async Task MigrateAsync(SqliteConnection cn, CancellationToken ct)
    {
        int version;
        using (var read = cn.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            var scalar = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
            version = scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        }

        if (version >= SchemaVersion)
        {
            if (version > SchemaVersion)
                AppLog.Warn($"Database schema is version {version}; this build understands {SchemaVersion}.");
            return;
        }

        using var tx = cn.BeginTransaction();
        for (var from = version; from < SchemaVersion; from++)
        {
            switch (from)
            {
                case 0:
                    await ExecuteAsync(cn, tx, SchemaV1Sql, ct).ConfigureAwait(false);
                    break;
                // Future migrations: add "case 1:" here and raise SchemaVersion.
            }
        }

        await ExecuteAsync(cn, tx,
            "PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture) + ";", ct)
            .ConfigureAwait(false);
        tx.Commit();

        AppLog.Info($"Database schema migrated from version {version} to {SchemaVersion}.");
    }

    // Extended result codes carry the primary code in the low byte (SQLITE_CORRUPT_VTAB = 267).
    private static bool IsCorruption(Exception ex) =>
        ex is SqliteException sqlite &&
        ((sqlite.SqliteErrorCode & 0xFF) is SqliteCorrupt or SqliteNotADatabase ||
         (sqlite.SqliteExtendedErrorCode & 0xFF) is SqliteCorrupt or SqliteNotADatabase);

    private void QuarantineDatabaseFiles()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string[] paths = [_databasePath, _databasePath + "-wal", _databasePath + "-shm"];

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var quarantined = $"{path}.corrupt-{stamp}";
                File.Move(path, quarantined, overwrite: true);
                AppLog.Warn($"Moved '{path}' aside to '{quarantined}'.");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not move '{path}' aside; deleting it instead.", ex);
                try
                {
                    File.Delete(path);
                }
                catch (Exception deleteEx)
                {
                    AppLog.Error($"Could not delete '{path}' either.", deleteEx);
                }
            }
        }
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection cn,
        SqliteTransaction? tx,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void BindConnection(SqliteCommand cmd, RdpConnection c)
    {
        Set(cmd, "$id", ToDb(c.Id));
        Set(cmd, "$name", Text(c.Name));
        Set(cmd, "$description", NullIfEmpty(c.Description));
        Set(cmd, "$group_id", ToDb(c.GroupId));
        Set(cmd, "$host", Text(c.Host));
        Set(cmd, "$port", c.Port);
        Set(cmd, "$credential_set_id", ToDb(c.CredentialSetId));
        Set(cmd, "$credential_delivery", (int)c.CredentialDelivery);
        Set(cmd, "$display_json", JsonSerializer.Serialize(c.Display, Json));
        Set(cmd, "$experience_json", JsonSerializer.Serialize(c.Experience, Json));
        Set(cmd, "$redirection_json", JsonSerializer.Serialize(c.Redirection, Json));
        Set(cmd, "$gateway_json", JsonSerializer.Serialize(c.Gateway, Json));
        Set(cmd, "$security_json", JsonSerializer.Serialize(c.Security, Json));
        Set(cmd, "$custom_properties_json",
            c.CustomProperties is { Count: > 0 } properties ? JsonSerializer.Serialize(properties, Json) : null);
        Set(cmd, "$auto_reconnect", ToDb(c.AutoReconnect));
        Set(cmd, "$max_reconnect_attempts", c.MaxReconnectAttempts);
        Set(cmd, "$reconnect_delay_seconds", c.ReconnectDelaySeconds);
        Set(cmd, "$color", NullIfEmpty(c.Color));
        Set(cmd, "$tags", NullIfEmpty(c.Tags));
        Set(cmd, "$favorite", ToDb(c.Favorite));
        Set(cmd, "$sort_order", c.SortOrder);
        Set(cmd, "$created_utc", ToDb(c.CreatedUtc));
        Set(cmd, "$modified_utc", ToDb(c.ModifiedUtc));
        Set(cmd, "$last_connected_utc", ToDb(c.LastConnectedUtc));
        Set(cmd, "$launch_count", c.LaunchCount);
    }

    private static RdpConnection ReadConnection(DbDataReader r)
    {
        var name = GetText(r, 1);
        var display = ReadJson<DisplaySettings>(r, 8, name);
        if (display.SelectedMonitors is null) display.SelectedMonitors = [];

        return new RdpConnection
        {
            Id = GetGuid(r, 0),
            Name = name,
            Description = GetTextOrNull(r, 2),
            GroupId = GetNullableGuid(r, 3),
            Host = GetText(r, 4),
            Port = GetInt(r, 5, 3389),
            CredentialSetId = GetNullableGuid(r, 6),
            CredentialDelivery = ToDelivery(GetInt(r, 7, (int)CredentialDelivery.Both)),
            Display = display,
            Experience = ReadJson<ExperienceSettings>(r, 9, name),
            Redirection = ReadJson<RedirectionSettings>(r, 10, name),
            Gateway = ReadJson<GatewaySettings>(r, 11, name),
            Security = ReadJson<SecuritySettings>(r, 12, name),
            CustomProperties = ReadProperties(r, 13, name),
            AutoReconnect = GetBool(r, 14, true),
            MaxReconnectAttempts = GetInt(r, 15, 10),
            ReconnectDelaySeconds = GetInt(r, 16, 5),
            Color = GetTextOrNull(r, 17),
            Tags = GetTextOrNull(r, 18),
            Favorite = GetBool(r, 19),
            SortOrder = GetInt(r, 20),
            CreatedUtc = GetUtc(r, 21),
            ModifiedUtc = GetUtc(r, 22),
            LastConnectedUtc = GetNullableUtc(r, 23),
            LaunchCount = GetInt(r, 24),
        };
    }

    /// <summary>Keeps a hand-edited database from producing an enum value no switch handles.</summary>
    private static CredentialDelivery ToDelivery(int raw) =>
        raw is >= (int)CredentialDelivery.WindowsVault and <= (int)CredentialDelivery.Prompt
            ? (CredentialDelivery)raw
            : CredentialDelivery.Both;

    private static ConnectionGroup ReadGroup(DbDataReader r) => new()
    {
        Id = GetGuid(r, 0),
        Name = GetText(r, 1),
        ParentId = GetNullableGuid(r, 2),
        Color = GetTextOrNull(r, 3),
        Description = GetTextOrNull(r, 4),
        SortOrder = GetInt(r, 5),
        IsExpanded = GetBool(r, 6, true),
        CreatedUtc = GetUtc(r, 7),
        ModifiedUtc = GetUtc(r, 8),
    };

    private static CredentialSet ReadCredential(DbDataReader r) => new()
    {
        Id = GetGuid(r, 0),
        Name = GetText(r, 1),
        Domain = GetTextOrNull(r, 2),
        Username = GetText(r, 3),
        ProtectedPassword = GetBlob(r, 4),
        Notes = GetTextOrNull(r, 5),
        IsDefault = GetBool(r, 6),
        CreatedUtc = GetUtc(r, 7),
        ModifiedUtc = GetUtc(r, 8),
    };

    private static MultiConfig ReadMultiConfig(DbDataReader r) => new()
    {
        Id = GetGuid(r, 0),
        Name = GetText(r, 1),
        Description = GetTextOrNull(r, 2),
        GroupId = GetNullableGuid(r, 3),
        Color = GetTextOrNull(r, 4),
        Sequential = GetBool(r, 5, true),
        InitialDelayMs = GetInt(r, 6),
        CloseTogether = GetBool(r, 7),
        Favorite = GetBool(r, 8),
        SortOrder = GetInt(r, 9),
        CreatedUtc = GetUtc(r, 10),
        ModifiedUtc = GetUtc(r, 11),
        LastLaunchedUtc = GetNullableUtc(r, 12),
        LaunchCount = GetInt(r, 13),
    };

    private static MultiConfigItem ReadMultiConfigItem(DbDataReader r) => new()
    {
        Id = GetGuid(r, 0),
        ConnectionId = GetGuid(r, 2),
        Display = ReadJson<DisplayOverride>(r, 3, "multi-config item"),
        CredentialSetIdOverride = GetNullableGuid(r, 4),
        AutoReconnectOverride = GetNullableBool(r, 5),
        CustomPropertyOverrides = ReadProperties(r, 6, "multi-config item"),
        Order = GetInt(r, 7),
        DelayMs = GetInt(r, 8, 400),
        Enabled = GetBool(r, 9, true),
        DisplayNameOverride = GetTextOrNull(r, 10),
    };

    private static T ReadJson<T>(DbDataReader r, int ordinal, string context) where T : new()
    {
        if (r.IsDBNull(ordinal)) return new T();

        var raw = r.GetString(ordinal);
        if (raw.Length == 0) return new T();

        try
        {
            return JsonSerializer.Deserialize<T>(raw, Json) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn($"Corrupt {typeof(T).Name} stored for '{context}'; using defaults.", ex);
            return new T();
        }
    }

    private static Dictionary<string, string> ReadProperties(DbDataReader r, int ordinal, string context)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (r.IsDBNull(ordinal)) return map;

        var raw = r.GetString(ordinal);
        if (raw.Length == 0 || raw == "{}") return map;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw, Json);
            if (parsed is not null)
            {
                foreach (var pair in parsed)
                {
                    if (pair.Value is not null) map[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn($"Corrupt custom properties stored for '{context}'; ignoring them.", ex);
        }

        return map;
    }

    private static void Set(SqliteCommand cmd, string name, object? value)
    {
        var boxed = value ?? DBNull.Value;
        if (cmd.Parameters.Contains(name)) cmd.Parameters[name].Value = boxed;
        else cmd.Parameters.AddWithValue(name, boxed);
    }

    private static string Text(string? value) => value ?? string.Empty;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string ToDb(Guid value) => value.ToString("D");

    private static string? ToDb(Guid? value) => value?.ToString("D");

    private static int ToDb(bool value) => value ? 1 : 0;

    private static int? ToDb(bool? value) => value.HasValue ? (value.Value ? 1 : 0) : null;

    private static string ToDb(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? ToDb(DateTime? value) => value.HasValue ? ToDb(value.Value) : null;

    private static string GetText(DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? string.Empty : r.GetString(ordinal);

    private static string? GetTextOrNull(DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static int GetInt(DbDataReader r, int ordinal, int fallback = 0) =>
        r.IsDBNull(ordinal) ? fallback : (int)r.GetInt64(ordinal);

    private static bool GetBool(DbDataReader r, int ordinal, bool fallback = false) =>
        r.IsDBNull(ordinal) ? fallback : r.GetInt64(ordinal) != 0;

    private static bool? GetNullableBool(DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal) != 0;

    private static byte[]? GetBlob(DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetFieldValue<byte[]>(ordinal);

    private static Guid GetGuid(DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return Guid.Empty;
        return Guid.TryParse(r.GetString(ordinal), out var value) ? value : Guid.Empty;
    }

    private static Guid? GetNullableGuid(DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return null;
        return Guid.TryParse(r.GetString(ordinal), out var value) ? value : null;
    }

    private static DateTime GetUtc(DbDataReader r, int ordinal) => GetNullableUtc(r, ordinal) ?? DateTime.UtcNow;

    private static DateTime? GetNullableUtc(DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return null;

        var raw = r.GetString(ordinal);
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return null;

        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
        };
    }

    private static string BuildSelect(string table, string[] columns)
    {
        var sql = new StringBuilder(64 + columns.Length * 20);
        sql.Append("SELECT ");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(columns[i]);
        }
        sql.Append(" FROM ").Append(table);
        return sql.ToString();
    }

    /// <summary>Builds "INSERT ... ON CONFLICT(id) DO UPDATE SET ..."; <paramref name="columns"/>[0] must be the key.</summary>
    private static string BuildUpsert(string table, string[] columns)
    {
        var sql = new StringBuilder(128 + columns.Length * 48);
        sql.Append("INSERT INTO ").Append(table).Append(" (");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(columns[i]);
        }

        sql.Append(") VALUES (");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append('$').Append(columns[i]);
        }

        sql.Append(") ON CONFLICT(").Append(columns[0]).Append(") DO UPDATE SET ");
        for (var i = 1; i < columns.Length; i++)
        {
            if (i > 1) sql.Append(", ");
            sql.Append(columns[i]).Append(" = excluded.").Append(columns[i]);
        }

        sql.Append(';');
        return sql.ToString();
    }
}
