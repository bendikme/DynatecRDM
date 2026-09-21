using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynatecRDM.Data;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using Microsoft.Data.Sqlite;

namespace DynatecRDM.Services;

/// <summary>
/// Exports and imports configuration: single connections and multi-configurations, or the whole
/// library. Files are plain indented JSON so they review and diff cleanly; passwords are the one
/// exception and travel encrypted under a passphrase.
/// </summary>
public sealed class ConfigTransfer
{
    /// <summary>Remote Desktop Manager bundle. JSON inside.</summary>
    public const string FileExtension = ".drdm";

    private const int CurrentFormatVersion = 1;
    private const long MaxBundleBytes = 64L * 1024 * 1024;
    private const int MinPassphraseLength = 8;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int Pbkdf2Iterations = 600_000;

    // Names this service gives imported items. They are stored with the item, in the UI language.
    private static string ImportedSuffix => Strings.Transfer_ImportedSuffix;
    private static string UnnamedLabel => Strings.Transfer_Unnamed;

    private const int MaxPixels = 32768;

    private static readonly JsonSerializerOptions BundleJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // AppSettings stores "not positioned" as double.NaN, which plain JSON cannot express.
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Enums by name: a bundle stays readable and survives a future reordering of the enums.
        Converters = { new JsonStringEnumConverter() },
        // DictionaryKeyPolicy deliberately stays null: raw .rdp property names must survive verbatim.
    };

    private static readonly string CurrentAppVersion = ResolveAppVersion();

    private readonly IDataStore _store;
    private readonly ISecretProtector _protector;

    public ConfigTransfer(IDataStore store, ISecretProtector protector)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    // ------------------------------------------------------------------ building bundles

    /// <summary>
    /// Builds a self-contained bundle for the selected items: every ancestor folder, every
    /// credential they reference and - for a multi-configuration - every connection its entries
    /// point at travel with them, so the file can never import into a dangling reference.
    /// </summary>
    public async Task<TransferBundle> BuildBundleAsync(
        IEnumerable<Guid> connectionIds,
        IEnumerable<Guid> multiConfigIds,
        bool includeCredentials,
        string? passphrase,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        ArgumentNullException.ThrowIfNull(multiConfigIds);
        if (includeCredentials) ValidatePassphrase(passphrase);

        var wantedConnections = new HashSet<Guid>(connectionIds);
        var wantedMultis = new HashSet<Guid>(multiConfigIds);
        wantedConnections.Remove(Guid.Empty);
        wantedMultis.Remove(Guid.Empty);

        if (wantedConnections.Count == 0 && wantedMultis.Count == 0)
            throw new ArgumentException(Strings.Transfer_Error_NothingSelected, nameof(connectionIds));

        var library = await LoadLibraryAsync(ct).ConfigureAwait(false);

        var multis = library.MultiConfigs.Where(m => wantedMultis.Contains(m.Id)).ToList();

        // A multi-configuration is worthless without the connections its entries launch.
        foreach (var multi in multis)
        {
            foreach (var item in multi.Items)
            {
                if (item.ConnectionId != Guid.Empty) wantedConnections.Add(item.ConnectionId);
            }
        }

        var connections = library.Connections.Where(c => wantedConnections.Contains(c.Id)).ToList();

        var groupsById = new Dictionary<Guid, ConnectionGroup>(library.Groups.Count);
        foreach (var group in library.Groups) groupsById[group.Id] = group;

        var neededGroups = new HashSet<Guid>();
        var neededCredentials = new HashSet<Guid>();

        foreach (var connection in connections)
        {
            CollectAncestors(groupsById, neededGroups, connection.GroupId);
            Collect(neededCredentials, connection.CredentialSetId);
            Collect(neededCredentials, connection.Gateway?.CredentialSetId);
        }

        foreach (var multi in multis)
        {
            CollectAncestors(groupsById, neededGroups, multi.GroupId);
            foreach (var item in multi.Items) Collect(neededCredentials, item.CredentialSetIdOverride);
        }

        var groups = library.Groups.Where(g => neededGroups.Contains(g.Id)).ToList();
        var credentials = library.Credentials.Where(c => neededCredentials.Contains(c.Id)).ToList();

        ReportMissing(multis.Count, wantedMultis.Count, "multi-configuration");
        ReportMissing(connections.Count, wantedConnections.Count, "connection");

        return await ComposeAsync(
            groups, connections, multis, credentials, settings: null, includeCredentials, passphrase, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Builds a bundle holding the complete library.</summary>
    public async Task<TransferBundle> BuildFullLibraryAsync(
        bool includeCredentials,
        bool includeSettings,
        string? passphrase,
        CancellationToken ct = default)
    {
        if (includeCredentials) ValidatePassphrase(passphrase);

        var library = await LoadLibraryAsync(ct).ConfigureAwait(false);

        AppSettings? settings = null;
        if (includeSettings)
        {
            var current = await _store.GetSettingsAsync(ct).ConfigureAwait(false);
            settings = current.Clone();
            // A personal access token belongs to one person on one machine, never to a shared file.
            settings.UpdateAccessToken = null;
        }

        return await ComposeAsync(
            library.Groups, library.Connections, library.MultiConfigs, library.Credentials,
            settings, includeCredentials, passphrase, ct)
            .ConfigureAwait(false);
    }

    private async Task<TransferBundle> ComposeAsync(
        IReadOnlyList<ConnectionGroup> groups,
        IReadOnlyList<RdpConnection> connections,
        IReadOnlyList<MultiConfig> multis,
        IReadOnlyList<CredentialSet> credentials,
        AppSettings? settings,
        bool includeCredentials,
        string? passphrase,
        CancellationToken ct)
    {
        var exported = new List<TransferCredential>(credentials.Count);
        var containsSecrets = false;

        byte[]? key = null;
        byte[]? salt = null;
        try
        {
            if (includeCredentials && credentials.Count > 0)
            {
                // One salt for the whole file, one nonce per password: the key is derived once
                // instead of once per credential, and GCM stays safe because the nonces differ.
                salt = RandomNumberGenerator.GetBytes(SaltBytes);
                var material = passphrase!;
                var saltCopy = salt;
                key = await Task.Run(() => DeriveKey(material, saltCopy), ct).ConfigureAwait(false);
            }

            foreach (var credential in credentials)
            {
                ct.ThrowIfCancellationRequested();

                var dto = new TransferCredential
                {
                    Id = credential.Id,
                    Name = credential.Name,
                    Domain = credential.Domain,
                    Username = credential.Username,
                    Notes = credential.Notes,
                    IsDefault = credential.IsDefault,
                };

                if (key is not null && salt is not null && credential.ProtectedPassword is { Length: > 0 } blob)
                {
                    var plain = _protector.Unprotect(blob);
                    if (string.IsNullOrEmpty(plain))
                    {
                        AppLog.Warn(
                            $"The stored password of credential '{credential.Name}' could not be read on this " +
                            "machine, so it was exported without a password.");
                    }
                    else
                    {
                        dto.EncryptedPassword = EncryptPassword(plain, key, salt);
                        containsSecrets = true;
                    }
                }

                exported.Add(dto);
            }
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }

        var bundle = new TransferBundle
        {
            FormatVersion = CurrentFormatVersion,
            ExportedBy = SafeMachineName(),
            ExportedUtc = DateTime.UtcNow,
            AppVersion = CurrentAppVersion,
            ContainsSecrets = containsSecrets,
            Groups = SortGroupsParentsFirst(groups, warnings: null),
            Connections = connections
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToList(),
            MultiConfigs = multis
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id).ToList(),
            Credentials = exported
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToList(),
            Settings = settings,
        };

        return bundle;
    }

    // ------------------------------------------------------------------ files

    /// <summary>Writes the bundle as indented UTF-8 JSON, atomically.</summary>
    public async Task ExportAsync(TransferBundle bundle, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException(Strings.Transfer_Error_ExportPathRequired, nameof(path));

        if (bundle.FormatVersion <= 0) bundle.FormatVersion = CurrentFormatVersion;
        if (bundle.ExportedUtc == default) bundle.ExportedUtc = DateTime.UtcNow;

        var full = ToFullPath(path, nameof(path));
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // The temp file sits beside the target so the move stays on one volume, and therefore
        // never leaves a half-written export behind.
        var temp = full + ".tmp";
        try
        {
            var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            try
            {
                await JsonSerializer.SerializeAsync(stream, bundle, BundleJson, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        AppLog.Info(
            $"Exported {bundle.Connections.Count} connection(s), {bundle.MultiConfigs.Count} multi-config(s), " +
            $"{bundle.Groups.Count} folder(s) and {bundle.Credentials.Count} credential(s) to '{full}'.");
    }

    /// <summary>Reads a bundle from disk, refusing anything this build cannot understand.</summary>
    public async Task<TransferBundle> ReadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException(Strings.Transfer_Error_FilePathRequired, nameof(path));

        var full = ToFullPath(path, nameof(path));
        var info = new FileInfo(full);

        if (!info.Exists)
            throw new FileNotFoundException(UiLanguage.Format(Strings.Transfer_Error_FileNotFound, full), full);
        if (info.Length == 0)
            throw new InvalidDataException(UiLanguage.Format(Strings.Transfer_Error_FileEmpty, info.Name));
        if (info.Length > MaxBundleBytes)
        {
            throw new InvalidDataException(
                UiLanguage.Format(Strings.Transfer_Error_FileTooLarge, info.Name, info.Length / (1024 * 1024)));
        }

        TransferBundle? bundle;
        try
        {
            var stream = new FileStream(
                full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                bundle = await JsonSerializer
                    .DeserializeAsync<TransferBundle>(stream, BundleJson, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                UiLanguage.Format(Strings.Transfer_Error_NotReadable, info.Name, ex.Message), ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException(
                UiLanguage.Format(Strings.Transfer_Error_UnsupportedValue, info.Name, ex.Message), ex);
        }

        if (bundle is null)
            throw new InvalidDataException(UiLanguage.Format(Strings.Transfer_Error_NoData, info.Name));

        if (bundle.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException(UiLanguage.Format(
                Strings.Transfer_Error_NewerFormat, info.Name, bundle.FormatVersion, CurrentFormatVersion));
        }

        Normalize(bundle);

        // Any JSON object deserialises into an empty bundle, so a file that carries nothing is
        // rejected here rather than reported later as an import that did nothing.
        if (bundle.Groups.Count == 0 && bundle.Connections.Count == 0 &&
            bundle.MultiConfigs.Count == 0 && bundle.Credentials.Count == 0 && bundle.Settings is null)
        {
            throw new InvalidDataException(UiLanguage.Format(Strings.Transfer_Error_NothingInFile, info.Name));
        }

        return bundle;
    }

    // ------------------------------------------------------------------ import

    /// <summary>Counts what an import would do. Nothing is decrypted and nothing is written.</summary>
    public async Task<ImportPreview> PreviewAsync(TransferBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        Normalize(bundle);

        var library = await LoadLibraryAsync(ct).ConfigureAwait(false);
        var existing = ExistingIds.From(library);

        var conflicts =
            bundle.Groups.Count(g => existing.Groups.Contains(g.Id)) +
            bundle.Connections.Count(c => existing.Connections.Contains(c.Id)) +
            bundle.MultiConfigs.Count(m => existing.MultiConfigs.Contains(m.Id)) +
            bundle.Credentials.Count(c => existing.Credentials.Contains(c.Id));

        // The header flag is only what the file claims; what matters is whether a password is
        // actually in there, so a hand-edited header cannot demand a passphrase for nothing.
        var hasPasswords = bundle.Credentials.Any(c => !string.IsNullOrEmpty(c.EncryptedPassword));

        return new ImportPreview(
            bundle.Groups.Count,
            bundle.Connections.Count,
            bundle.MultiConfigs.Count,
            bundle.Credentials.Count,
            conflicts,
            hasPasswords,
            hasPasswords,
            bundle.AppVersion,
            bundle.ExportedUtc);
    }

    /// <summary>
    /// Writes the bundle into the library. A single bad entry is skipped with a warning rather than
    /// aborting the whole import, and no entity is ever half-written.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        TransferBundle bundle,
        ImportMode mode,
        string? passphrase,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!Enum.IsDefined(mode))
            throw new ArgumentException(UiLanguage.Format(Strings.Transfer_Error_UnsupportedMode, mode), nameof(mode));

        Normalize(bundle);

        var warnings = new WarningSink();
        var library = await LoadLibraryAsync(ct).ConfigureAwait(false);
        var existing = ExistingIds.From(library);

        var plan = BuildPlan(bundle, mode, existing, warnings);
        var counters = new Counters { Skipped = plan.Rejected };

        // Passwords this machine already holds, so replacing a credential with one exported without
        // its password does not throw away a working login.
        var keptPasswords = new Dictionary<Guid, byte[]>();
        foreach (var credential in library.Credentials)
        {
            if (credential.ProtectedPassword is { Length: > 0 } blob) keptPasswords[credential.Id] = blob;
        }

        // IDataStore offers no batch write, so one upsert per entity is the smallest number of
        // round trips available; each of those calls is transactional on its own. That also means
        // there is nothing to roll back to, so a cancellation is reported rather than thrown and
        // the caller still learns exactly what reached the database.
        var cancelled = false;
        try
        {
            await ApplyGroupsAsync(plan, mode, existing, counters, warnings, ct).ConfigureAwait(false);
            await ApplyCredentialsAsync(plan, mode, existing, keptPasswords, passphrase, counters, warnings, ct)
                .ConfigureAwait(false);
            await ApplyConnectionsAsync(plan, mode, existing, counters, warnings, ct).ConfigureAwait(false);
            await ApplyMultiConfigsAsync(plan, mode, existing, counters, warnings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            warnings.Add(Strings.Transfer_Warning_Stopped);
        }

        if (bundle.Settings is not null)
        {
            warnings.Add(Strings.Transfer_Warning_SettingsIgnored);
        }

        var result = new ImportResult(
            counters.GroupsAdded,
            counters.ConnectionsAdded,
            counters.MultiConfigsAdded,
            counters.CredentialsAdded,
            counters.Skipped,
            counters.Replaced,
            warnings.Build());

        AppLog.Info(
            $"Import ({mode}, {(cancelled ? "cancelled" : "complete")}): " +
            $"{result.GroupsAdded} folder(s), {result.ConnectionsAdded} connection(s), " +
            $"{result.MultiConfigsAdded} multi-config(s) and {result.CredentialsAdded} credential(s) added, " +
            $"{result.Replaced} replaced, {result.Skipped} skipped, {result.Warnings.Count} warning(s).");

        return result;
    }

    private static ImportPlan BuildPlan(
        TransferBundle bundle, ImportMode mode, ExistingIds existing, WarningSink warnings)
    {
        var duplicate = mode == ImportMode.DuplicateWithNewId;
        var plan = new ImportPlan();

        // 1. Accept the well-formed entries. Everything is cloned so the caller's bundle can be
        //    imported again, in another mode, without carrying over the rewrites made below.
        var groupIds = new HashSet<Guid>();
        var groups = new List<ConnectionGroup>(bundle.Groups.Count);
        foreach (var source in bundle.Groups)
        {
            if (!Accept(source, source?.Id ?? Guid.Empty, source?.Name, EntryKind.Group, groupIds, plan, warnings)) continue;
            groups.Add(source!.Clone());
        }

        var connectionIds = new HashSet<Guid>();
        var connections = new List<RdpConnection>(bundle.Connections.Count);
        foreach (var source in bundle.Connections)
        {
            if (!Accept(source, source?.Id ?? Guid.Empty, source?.Name, EntryKind.Connection, connectionIds, plan, warnings))
                continue;
            connections.Add(source!.Clone());
        }

        var multiIds = new HashSet<Guid>();
        var multis = new List<MultiConfig>(bundle.MultiConfigs.Count);
        foreach (var source in bundle.MultiConfigs)
        {
            if (!Accept(source, source?.Id ?? Guid.Empty, source?.Name, EntryKind.MultiConfig, multiIds, plan, warnings))
                continue;
            multis.Add(source!.Clone());
        }

        var credentialIds = new HashSet<Guid>();
        var credentials = new List<TransferCredential>(bundle.Credentials.Count);
        foreach (var source in bundle.Credentials)
        {
            if (!Accept(source, source?.Id ?? Guid.Empty, source?.Name, EntryKind.Credential, credentialIds, plan, warnings))
                continue;
            credentials.Add(source!);
        }

        // 2. The id map is built in full before a single reference is rewritten - a partially built
        //    map would silently scatter references across old and new ids.
        var groupMap = duplicate ? NewIdMap(groupIds) : null;
        var connectionMap = duplicate ? NewIdMap(connectionIds) : null;
        var multiMap = duplicate ? NewIdMap(multiIds) : null;
        var credentialMap = duplicate ? NewIdMap(credentialIds) : null;

        // 3. Rewrite every reference, then the ids themselves.
        foreach (var group in groups)
        {
            if (duplicate && IsTopLevel(group.ParentId, groupIds)) group.Name = MarkImported(group.Name);

            var parent = ResolveReference(group.ParentId, groupMap, groupIds, existing.Groups);
            if (group.ParentId.HasValue && parent is null)
            {
                warnings.Add(UiLanguage.Format(Strings.Transfer_Warning_GroupParentMissing, Describe(group.Name)));
            }

            group.ParentId = parent;
            if (duplicate) group.Id = groupMap![group.Id];
            if (group.ParentId == group.Id) group.ParentId = null;
        }

        foreach (var connection in connections)
        {
            if (duplicate && IsTopLevel(connection.GroupId, groupIds))
                connection.Name = MarkImported(connection.Name);

            var group = ResolveReference(connection.GroupId, groupMap, groupIds, existing.Groups);
            if (connection.GroupId.HasValue && group is null)
            {
                warnings.Add(UiLanguage.Format(Strings.Transfer_Warning_GroupMissing, Describe(connection.Name)));
            }
            connection.GroupId = group;

            var credential = ResolveReference(
                connection.CredentialSetId, credentialMap, credentialIds, existing.Credentials);
            if (connection.CredentialSetId.HasValue && credential is null)
            {
                warnings.Add(UiLanguage.Format(Strings.Transfer_Warning_CredentialMissing, Describe(connection.Name)));
            }
            connection.CredentialSetId = credential;

            var gatewayCredential = ResolveReference(
                connection.Gateway.CredentialSetId, credentialMap, credentialIds, existing.Credentials);
            if (connection.Gateway.CredentialSetId.HasValue && gatewayCredential is null)
            {
                warnings.Add(UiLanguage.Format(
                    Strings.Transfer_Warning_GatewayCredentialMissing, Describe(connection.Name)));
            }
            connection.Gateway.CredentialSetId = gatewayCredential;

            if (duplicate) connection.Id = connectionMap![connection.Id];
        }

        foreach (var multi in multis)
        {
            if (duplicate && IsTopLevel(multi.GroupId, groupIds)) multi.Name = MarkImported(multi.Name);

            var group = ResolveReference(multi.GroupId, groupMap, groupIds, existing.Groups);
            if (multi.GroupId.HasValue && group is null)
            {
                warnings.Add(UiLanguage.Format(Strings.Transfer_Warning_GroupMissing, Describe(multi.Name)));
            }
            multi.GroupId = group;

            for (var i = multi.Items.Count - 1; i >= 0; i--)
            {
                var item = multi.Items[i];
                var target = ResolveReference(
                    item.ConnectionId, connectionMap, connectionIds, existing.Connections);

                if (target is null)
                {
                    // An entry with no connection behind it can never launch.
                    multi.Items.RemoveAt(i);
                    warnings.Add(UiLanguage.Format(
                        Strings.Transfer_Warning_EntryConnectionMissing, Describe(multi.Name)));
                    continue;
                }

                item.ConnectionId = target.Value;

                var overrideCredential = ResolveReference(
                    item.CredentialSetIdOverride, credentialMap, credentialIds, existing.Credentials);
                if (item.CredentialSetIdOverride.HasValue && overrideCredential is null)
                {
                    warnings.Add(UiLanguage.Format(
                        Strings.Transfer_Warning_EntryCredentialMissing, Describe(multi.Name)));
                }
                item.CredentialSetIdOverride = overrideCredential;

                if (duplicate) item.Id = Guid.NewGuid();
            }

            if (duplicate) multi.Id = multiMap![multi.Id];
        }

        var now = DateTime.UtcNow;
        foreach (var dto in credentials)
        {
            var set = new CredentialSet
            {
                Id = duplicate ? credentialMap![dto.Id] : dto.Id,
                Name = duplicate ? MarkImported(dto.Name) : dto.Name,
                Domain = dto.Domain,
                Username = dto.Username,
                Notes = dto.Notes,
                // A copy must not quietly take "default" away from the credential already in use.
                IsDefault = !duplicate && dto.IsDefault,
                CreatedUtc = now,
                ModifiedUtc = now,
            };

            if (string.IsNullOrWhiteSpace(set.Name)) set.Name = UnnamedLabel;
            plan.Credentials.Add(new PendingCredential(set, dto.EncryptedPassword));
        }

        plan.Groups.AddRange(SortGroupsParentsFirst(groups, warnings));
        plan.Connections.AddRange(connections);
        plan.MultiConfigs.AddRange(multis);
        return plan;
    }

    private async Task ApplyGroupsAsync(
        ImportPlan plan, ImportMode mode, ExistingIds existing, Counters counters,
        WarningSink warnings, CancellationToken ct)
    {
        foreach (var group in plan.Groups)
        {
            ct.ThrowIfCancellationRequested();

            var exists = existing.Groups.Contains(group.Id);
            if (exists && mode == ImportMode.Skip)
            {
                counters.Skipped++;
                continue;
            }

            try
            {
                await _store.UpsertGroupAsync(group, ct).ConfigureAwait(false);
                if (exists)
                {
                    counters.Replaced++;
                }
                else
                {
                    counters.GroupsAdded++;
                    existing.Groups.Add(group.Id);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                counters.Skipped++;
                warnings.Add(UiLanguage.Format(
                    Strings.Transfer_Warning_SaveGroupFailed, Describe(group.Name), ex.Message));
                AppLog.Warn($"Importing the folder '{group.Name}' failed.", ex);
            }
        }
    }

    private async Task ApplyCredentialsAsync(
        ImportPlan plan, ImportMode mode, ExistingIds existing, IReadOnlyDictionary<Guid, byte[]> keptPasswords,
        string? passphrase, Counters counters, WarningSink warnings, CancellationToken ct)
    {
        if (plan.Credentials.Count == 0) return;

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var passphraseRejected = false;
        var missingPassphraseReported = false;

        try
        {
            foreach (var pending in plan.Credentials)
            {
                ct.ThrowIfCancellationRequested();

                var set = pending.Set;
                var exists = existing.Credentials.Contains(set.Id);
                if (exists && mode == ImportMode.Skip)
                {
                    counters.Skipped++;
                    continue;
                }

                if (!string.IsNullOrEmpty(pending.EncryptedPassword) && !passphraseRejected)
                {
                    if (string.IsNullOrEmpty(passphrase))
                    {
                        if (!missingPassphraseReported)
                        {
                            missingPassphraseReported = true;
                            warnings.Add(Strings.Transfer_Warning_NoPassphrase);
                        }
                    }
                    else
                    {
                        var raw = DecodeSecret(pending.EncryptedPassword!);
                        if (raw is null)
                        {
                            warnings.Add(UiLanguage.Format(
                                Strings.Transfer_Warning_PasswordDamaged, Describe(set.Name)));
                        }
                        else
                        {
                            var key = await GetKeyAsync(keys, passphrase!, raw, ct).ConfigureAwait(false);
                            var (status, plain) = TryDecrypt(raw, key);

                            switch (status)
                            {
                                case SecretStatus.Decrypted when !string.IsNullOrEmpty(plain):
                                    try
                                    {
                                        set.ProtectedPassword = _protector.Protect(plain!);
                                    }
                                    catch (Exception ex)
                                    {
                                        warnings.Add(UiLanguage.Format(
                                            Strings.Transfer_Warning_PasswordNotProtected, Describe(set.Name)));
                                        AppLog.Warn($"Re-protecting the password of '{set.Name}' failed.", ex);
                                    }
                                    break;

                                case SecretStatus.WrongPassphrase:
                                    // Every password in one file shares the passphrase, so one report is enough.
                                    passphraseRejected = true;
                                    warnings.Add(Strings.Transfer_Warning_WrongPassphrase);
                                    break;

                                default:
                                    warnings.Add(UiLanguage.Format(
                                        Strings.Transfer_Warning_PasswordDamaged, Describe(set.Name)));
                                    break;
                            }
                        }
                    }
                }

                // Nothing usable came out of the file, so keep whatever this machine already had.
                if (set.ProtectedPassword is null && keptPasswords.TryGetValue(set.Id, out var previous))
                    set.ProtectedPassword = previous;

                try
                {
                    await _store.UpsertCredentialSetAsync(set, ct).ConfigureAwait(false);
                    if (exists)
                    {
                        counters.Replaced++;
                    }
                    else
                    {
                        counters.CredentialsAdded++;
                        existing.Credentials.Add(set.Id);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    counters.Skipped++;
                    warnings.Add(UiLanguage.Format(
                        Strings.Transfer_Warning_SaveCredentialFailed, Describe(set.Name), ex.Message));
                    AppLog.Warn($"Importing the credential '{set.Name}' failed.", ex);
                }
                finally
                {
                    set.ProtectedPassword = null;
                }
            }
        }
        finally
        {
            foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
            keys.Clear();
        }
    }

    private async Task ApplyConnectionsAsync(
        ImportPlan plan, ImportMode mode, ExistingIds existing, Counters counters,
        WarningSink warnings, CancellationToken ct)
    {
        foreach (var connection in plan.Connections)
        {
            ct.ThrowIfCancellationRequested();

            var exists = existing.Connections.Contains(connection.Id);
            if (exists && mode == ImportMode.Skip)
            {
                counters.Skipped++;
                continue;
            }

            try
            {
                await _store.UpsertConnectionAsync(connection, ct).ConfigureAwait(false);
                if (exists)
                {
                    counters.Replaced++;
                }
                else
                {
                    counters.ConnectionsAdded++;
                    existing.Connections.Add(connection.Id);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                counters.Skipped++;
                warnings.Add(UiLanguage.Format(
                    Strings.Transfer_Warning_SaveConnectionFailed, Describe(connection.Name), ex.Message));
                AppLog.Warn($"Importing the connection '{connection.Name}' failed.", ex);
            }
        }
    }

    private async Task ApplyMultiConfigsAsync(
        ImportPlan plan, ImportMode mode, ExistingIds existing, Counters counters,
        WarningSink warnings, CancellationToken ct)
    {
        foreach (var multi in plan.MultiConfigs)
        {
            ct.ThrowIfCancellationRequested();

            var exists = existing.MultiConfigs.Contains(multi.Id);
            if (exists && mode == ImportMode.Skip)
            {
                counters.Skipped++;
                continue;
            }

            try
            {
                await _store.UpsertMultiConfigAsync(multi, ct).ConfigureAwait(false);
                if (exists)
                {
                    counters.Replaced++;
                }
                else
                {
                    counters.MultiConfigsAdded++;
                    existing.MultiConfigs.Add(multi.Id);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                counters.Skipped++;
                warnings.Add(UiLanguage.Format(
                    Strings.Transfer_Warning_SaveMultiConfigFailed, Describe(multi.Name), ex.Message));
                AppLog.Warn($"Importing the multi-configuration '{multi.Name}' failed.", ex);
            }
        }
    }

    // ------------------------------------------------------------------ database backup

    /// <summary>
    /// Copies the live database to <paramref name="targetPath"/> using the SQLite online backup
    /// API, so the copy is consistent even while the application is running. When
    /// <paramref name="targetPath"/> is an existing directory a timestamped file name is chosen
    /// inside it. Returns the path actually written.
    /// </summary>
    public async Task<string> BackupDatabaseAsync(string targetPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException(Strings.Transfer_Error_BackupPathRequired, nameof(targetPath));

        var source = ResolveDatabasePath();
        if (!File.Exists(source))
        {
            throw new InvalidDataException(UiLanguage.Format(Strings.Transfer_Error_NoDatabase, source));
        }

        var target = ToFullPath(targetPath, nameof(targetPath));
        if (Directory.Exists(target))
        {
            target = Path.Combine(
                target, $"dynatec-rdm-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        }

        // The live database and its two journal files are never a legal target: overwriting any of
        // them would destroy exactly the data the backup exists to protect.
        if (IsSamePath(target, source) ||
            IsSamePath(target, source + "-wal") ||
            IsSamePath(target, source + "-shm"))
        {
            throw new ArgumentException(Strings.Transfer_Error_BackupOverLive, nameof(targetPath));
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = target + ".tmp";
        DeleteDatabaseFiles(temp);

        try
        {
            await Task.Run(() => CopyDatabase(source, temp), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            File.Move(temp, target, overwrite: true);
            MoveSidecar(temp + "-wal", target + "-wal");
            TryDelete(temp + "-shm");
        }
        catch
        {
            DeleteDatabaseFiles(temp);
            throw;
        }

        AppLog.Info($"Database backed up to '{target}'.");
        return target;
    }

    private static void CopyDatabase(string source, string destination)
    {
        try
        {
            BackupWithSqlite(source, destination);
            return;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            AppLog.Warn(
                "The SQLite backup API could not be used; falling back to a checkpointed file copy.", ex);
        }

        CheckpointAndCopy(source, destination);
    }

    private static void BackupWithSqlite(string source, string destination)
    {
        using var from = new SqliteConnection(ReadWriteConnectionString(source, create: false));
        from.Open();

        // Folding the write-ahead log back into the main file first keeps the backup short.
        using (var checkpoint = from.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA wal_checkpoint(PASSIVE);";
            checkpoint.ExecuteNonQuery();
        }

        using var to = new SqliteConnection(ReadWriteConnectionString(destination, create: true));
        to.Open();
        from.BackupDatabase(to);
        to.Close();
    }

    private static void CheckpointAndCopy(string source, string destination)
    {
        try
        {
            using var cn = new SqliteConnection(ReadWriteConnectionString(source, create: false));
            cn.Open();
            using var checkpoint = cn.CreateCommand();
            checkpoint.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            AppLog.Warn("Checkpointing the database before the backup copy failed.", ex);
        }

        File.Copy(source, destination, overwrite: true);

        // A log the checkpoint could not fold in still holds committed pages, so it travels along.
        var wal = source + "-wal";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0)
            File.Copy(wal, destination + "-wal", overwrite: true);
    }

    private static string ReadWriteConnectionString(string path, bool create) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // Read-write even for the source: a WAL database needs to create its shared-memory
            // index, and the store's own connection keeps it in WAL mode.
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ConnectionString;

    private string ResolveDatabasePath()
    {
        if (_store is SqliteDataStore sqlite) return sqlite.DatabasePath;

        // Any other store wrapping the SQLite one can still say where the file is.
        try
        {
            var property = _store.GetType().GetProperty(
                "DatabasePath", BindingFlags.Public | BindingFlags.Instance);
            if (property?.PropertyType == typeof(string) &&
                property.GetValue(_store) is string path &&
                !string.IsNullOrWhiteSpace(path))
            {
                return Path.GetFullPath(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Asking the store for its database path failed; using the default location.", ex);
        }

        return Path.Combine(AppLog.DataDirectory, "dynatec-rdm.db");
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void DeleteDatabaseFiles(string path)
    {
        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void MoveSidecar(string from, string to)
    {
        try
        {
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
            else TryDelete(to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not move '{from}' next to the backup.", ex);
        }
    }

    // ------------------------------------------------------------------ secrets

    private static void ValidatePassphrase(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ArgumentException(Strings.Transfer_Error_PassphraseRequired, nameof(passphrase));
        }

        if (passphrase.Length < MinPassphraseLength)
        {
            throw new ArgumentException(
                UiLanguage.Format(Strings.Transfer_Error_PassphraseTooShort, MinPassphraseLength), nameof(passphrase));
        }

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new ArgumentException(Strings.Transfer_Error_PassphraseBlank, nameof(passphrase));
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static async Task<byte[]> GetKeyAsync(
        Dictionary<string, byte[]> cache, string passphrase, byte[] blob, CancellationToken ct)
    {
        var salt = blob[..SaltBytes];
        var id = Convert.ToHexString(salt);
        if (cache.TryGetValue(id, out var cached)) return cached;

        // 600k PBKDF2 rounds are deliberately slow; keep them off the caller's thread.
        var key = await Task.Run(() => DeriveKey(passphrase, salt), ct).ConfigureAwait(false);
        cache[id] = key;
        return key;
    }

    /// <summary>Produces Base64 of salt + nonce + tag + ciphertext.</summary>
    private static string EncryptPassword(string plainText, byte[] key, byte[] salt)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        try
        {
            var blob = new byte[SaltBytes + NonceBytes + TagBytes + plain.Length];
            salt.CopyTo(blob, 0);

            var nonce = blob.AsSpan(SaltBytes, NonceBytes);
            RandomNumberGenerator.Fill(nonce);

            using var gcm = new AesGcm(key, TagBytes);
            gcm.Encrypt(
                nonce,
                plain,
                blob.AsSpan(SaltBytes + NonceBytes + TagBytes),
                blob.AsSpan(SaltBytes + NonceBytes, TagBytes));

            return Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[]? DecodeSecret(string base64)
    {
        try
        {
            var raw = Convert.FromBase64String(base64);
            return raw.Length > SaltBytes + NonceBytes + TagBytes ? raw : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static (SecretStatus Status, string? Password) TryDecrypt(byte[] blob, byte[] key)
    {
        var plain = new byte[blob.Length - SaltBytes - NonceBytes - TagBytes];
        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(
                blob.AsSpan(SaltBytes, NonceBytes),
                blob.AsSpan(SaltBytes + NonceBytes + TagBytes),
                blob.AsSpan(SaltBytes + NonceBytes, TagBytes),
                plain);

            return (SecretStatus.Decrypted, Encoding.UTF8.GetString(plain));
        }
        catch (AuthenticationTagMismatchException)
        {
            return (SecretStatus.WrongPassphrase, null);
        }
        catch (CryptographicException)
        {
            return (SecretStatus.Malformed, null);
        }
        catch (ArgumentException)
        {
            return (SecretStatus.Malformed, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Library> LoadLibraryAsync(CancellationToken ct)
    {
        var groups = await _store.GetGroupsAsync(ct).ConfigureAwait(false);
        var connections = await _store.GetConnectionsAsync(ct).ConfigureAwait(false);
        var multis = await _store.GetMultiConfigsAsync(ct).ConfigureAwait(false);
        var credentials = await _store.GetCredentialSetsAsync(ct).ConfigureAwait(false);
        return new Library(groups, connections, multis, credentials);
    }

    private static void Collect(HashSet<Guid> target, Guid? id)
    {
        if (id is { } value && value != Guid.Empty) target.Add(value);
    }

    private static void CollectAncestors(
        Dictionary<Guid, ConnectionGroup> groupsById, HashSet<Guid> target, Guid? groupId)
    {
        var current = groupId;
        // The guard also ends the walk on a corrupt parent cycle.
        var guard = 0;
        while (current is { } id && id != Guid.Empty && guard++ < 128)
        {
            if (!target.Add(id)) return;
            current = groupsById.TryGetValue(id, out var group) ? group.ParentId : null;
        }
    }

    private static void ReportMissing(int found, int wanted, string noun)
    {
        // "wanted" also holds the connections a selected multi-configuration depends on, so the
        // message says what is missing rather than claiming the user picked it.
        var missing = wanted - found;
        if (missing > 0) AppLog.Warn($"{missing} {noun}(s) the export needed no longer exist and were left out.");
    }

    private static bool Accept(
        object? entity, Guid id, string? name, EntryKind kind, HashSet<Guid> seen, ImportPlan plan, WarningSink warnings)
    {
        // One complete sentence per kind of entry: the noun changes the rest of the sentence in Norwegian.
        if (entity is null)
        {
            plan.Rejected++;
            warnings.Add(kind switch
            {
                EntryKind.Group => Strings.Transfer_Warning_EmptyEntry_Group,
                EntryKind.Connection => Strings.Transfer_Warning_EmptyEntry_Connection,
                EntryKind.MultiConfig => Strings.Transfer_Warning_EmptyEntry_MultiConfig,
                _ => Strings.Transfer_Warning_EmptyEntry_Credential,
            });
            return false;
        }

        if (id == Guid.Empty)
        {
            plan.Rejected++;
            warnings.Add(UiLanguage.Format(
                kind switch
                {
                    EntryKind.Group => Strings.Transfer_Warning_NoId_Group,
                    EntryKind.Connection => Strings.Transfer_Warning_NoId_Connection,
                    EntryKind.MultiConfig => Strings.Transfer_Warning_NoId_MultiConfig,
                    _ => Strings.Transfer_Warning_NoId_Credential,
                },
                Describe(name)));
            return false;
        }

        if (!seen.Add(id))
        {
            plan.Rejected++;
            warnings.Add(UiLanguage.Format(
                kind switch
                {
                    EntryKind.Group => Strings.Transfer_Warning_Twice_Group,
                    EntryKind.Connection => Strings.Transfer_Warning_Twice_Connection,
                    EntryKind.MultiConfig => Strings.Transfer_Warning_Twice_MultiConfig,
                    _ => Strings.Transfer_Warning_Twice_Credential,
                },
                Describe(name)));
            return false;
        }

        return true;
    }

    private static Dictionary<Guid, Guid> NewIdMap(HashSet<Guid> ids)
    {
        var map = new Dictionary<Guid, Guid>(ids.Count);
        foreach (var id in ids) map[id] = Guid.NewGuid();
        return map;
    }

    /// <summary>
    /// Points a reference at its imported copy when the file carries the target, keeps it when the
    /// library already has it, and returns null when it is dangling.
    /// </summary>
    private static Guid? ResolveReference(
        Guid? id, Dictionary<Guid, Guid>? map, HashSet<Guid> inBundle, HashSet<Guid> inLibrary)
    {
        if (id is not { } value || value == Guid.Empty) return null;
        if (map is not null && map.TryGetValue(value, out var mapped)) return mapped;
        return inBundle.Contains(value) || inLibrary.Contains(value) ? value : (Guid?)null;
    }

    /// <summary>True when nothing inside the same file owns this entity.</summary>
    private static bool IsTopLevel(Guid? parentId, HashSet<Guid> bundleGroupIds) =>
        parentId is not { } parent || parent == Guid.Empty || !bundleGroupIds.Contains(parent);

    private static string MarkImported(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? UnnamedLabel : name!;
        return trimmed.EndsWith(ImportedSuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + ImportedSuffix;
    }

    private static string Describe(string? name) => string.IsNullOrWhiteSpace(name) ? UnnamedLabel : name!;

    private static List<ConnectionGroup> SortGroupsParentsFirst(
        IEnumerable<ConnectionGroup> groups, WarningSink? warnings)
    {
        var source = groups.ToList();
        var known = new HashSet<Guid>(source.Count);
        foreach (var group in source) known.Add(group.Id);

        var ordered = new List<ConnectionGroup>(source.Count);
        var placed = new HashSet<Guid>(source.Count);
        var pending = source
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Id)
            .ToList();

        while (pending.Count > 0)
        {
            var next = new List<ConnectionGroup>();
            foreach (var group in pending)
            {
                if (group.ParentId is { } parent && parent != Guid.Empty &&
                    known.Contains(parent) && !placed.Contains(parent))
                {
                    next.Add(group);
                    continue;
                }

                ordered.Add(group);
                placed.Add(group.Id);
            }

            if (next.Count == pending.Count)
            {
                // Nothing moved, so what is left is a circular parent chain. Break it rather than
                // write a tree that can never be drawn.
                foreach (var group in next)
                {
                    warnings?.Add(UiLanguage.Format(Strings.Transfer_Warning_CircularGroup, Describe(group.Name)));
                    group.ParentId = null;
                    ordered.Add(group);
                }
                break;
            }

            pending = next;
        }

        return ordered;
    }

    /// <summary>Repairs what a hand-edited or partly written file can leave behind.</summary>
    private static void Normalize(TransferBundle bundle)
    {
        bundle.Groups = Compact(bundle.Groups);
        bundle.Connections = Compact(bundle.Connections);
        bundle.MultiConfigs = Compact(bundle.MultiConfigs);
        bundle.Credentials = Compact(bundle.Credentials);

        var now = DateTime.UtcNow;

        foreach (var group in bundle.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name)) group.Name = UnnamedLabel;
            if (group.ParentId == group.Id) group.ParentId = null;
            if (group.CreatedUtc == default) group.CreatedUtc = now;
            if (group.ModifiedUtc == default) group.ModifiedUtc = now;
        }

        foreach (var connection in bundle.Connections)
        {
            connection.Display ??= new DisplaySettings();
            connection.Experience ??= new ExperienceSettings();
            connection.Redirection ??= new RedirectionSettings();
            connection.Gateway ??= new GatewaySettings();
            connection.Security ??= new SecuritySettings();
            connection.CustomProperties = CaseInsensitive(connection.CustomProperties);

            NormalizeDisplay(connection.Display);
            connection.Redirection.DriveList = RedirectionList(connection.Redirection.DriveList);
            connection.Redirection.CameraList = RedirectionList(connection.Redirection.CameraList);
            connection.Redirection.KeyboardHook = Math.Clamp(connection.Redirection.KeyboardHook, 0, 2);

            connection.Host = connection.Host?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connection.Name))
            {
                connection.Name = string.IsNullOrEmpty(connection.Host)
                    ? Strings.Transfer_ImportedConnection_Name
                    : connection.Host;
            }

            if (connection.Port is <= 0 or > 65535) connection.Port = 3389;
            if (connection.MaxReconnectAttempts < 0) connection.MaxReconnectAttempts = 0;
            if (connection.ReconnectDelaySeconds < 1) connection.ReconnectDelaySeconds = 5;
            if (connection.CreatedUtc == default) connection.CreatedUtc = now;
            if (connection.ModifiedUtc == default) connection.ModifiedUtc = now;
        }

        foreach (var multi in bundle.MultiConfigs)
        {
            if (string.IsNullOrWhiteSpace(multi.Name)) multi.Name = Strings.Transfer_ImportedMultiConfig_Name;
            if (multi.InitialDelayMs < 0) multi.InitialDelayMs = 0;
            if (multi.CreatedUtc == default) multi.CreatedUtc = now;
            if (multi.ModifiedUtc == default) multi.ModifiedUtc = now;

            multi.Items = Compact(multi.Items);
            foreach (var item in multi.Items)
            {
                item.Display ??= new DisplayOverride();
                item.CustomPropertyOverrides = CaseInsensitive(item.CustomPropertyOverrides);
                if (item.DelayMs < 0) item.DelayMs = 0;
                NormalizeOverride(item.Display);
            }
        }

        foreach (var credential in bundle.Credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.Name)) credential.Name = UnnamedLabel;
            credential.Username ??= string.Empty;
        }
    }

    /// <summary>
    /// A hand-edited or truncated file can carry geometry no display can show. Out-of-range values
    /// are replaced with the defaults rather than written into an .rdp file mstsc has to argue with.
    /// </summary>
    private static void NormalizeDisplay(DisplaySettings display)
    {
        display.SelectedMonitors ??= new List<int>();

        // The bounds deliberately match the connection editor's own sanitiser, so exporting and
        // importing a connection again gives back exactly what was stored.
        if (display.DesktopWidth is <= 0 or > MaxPixels) display.DesktopWidth = 1920;
        if (display.DesktopHeight is <= 0 or > MaxPixels) display.DesktopHeight = 1080;
        if (display.CustomWidth is <= 0 or > MaxPixels) display.CustomWidth = 1280;
        if (display.CustomHeight is <= 0 or > MaxPixels) display.CustomHeight = 800;
        if (display.ColorDepth is not (8 or 15 or 16 or 24 or 32)) display.ColorDepth = 32;
        if (display.DesktopScaleFactor is < 100 or > 500) display.DesktopScaleFactor = 100;
        if (display.DeviceScaleFactor is not (100 or 140 or 180)) display.DeviceScaleFactor = 100;
        if (display.TargetMonitorIndex < 0) display.TargetMonitorIndex = 0;
        if (display.CustomLeft is < -MaxPixels or > MaxPixels) display.CustomLeft = 0;
        if (display.CustomTop is < -MaxPixels or > MaxPixels) display.CustomTop = 0;
    }

    /// <summary>The same for a multi-config item: a nonsense override falls back to "inherit".</summary>
    private static void NormalizeOverride(DisplayOverride display)
    {
        if (display.DesktopWidth is <= 0 or > MaxPixels) display.DesktopWidth = null;
        if (display.DesktopHeight is <= 0 or > MaxPixels) display.DesktopHeight = null;
        if (display.CustomWidth is <= 0 or > MaxPixels) display.CustomWidth = null;
        if (display.CustomHeight is <= 0 or > MaxPixels) display.CustomHeight = null;
        if (display.ColorDepth is not (null or 8 or 15 or 16 or 24 or 32)) display.ColorDepth = null;
        if (display.DesktopScaleFactor is < 100 or > 500) display.DesktopScaleFactor = null;
        if (display.DeviceScaleFactor is not (null or 100 or 140 or 180)) display.DeviceScaleFactor = null;
        if (display.TargetMonitorIndex is < 0) display.TargetMonitorIndex = null;
        if (display.CustomLeft is < -MaxPixels or > MaxPixels) display.CustomLeft = null;
        if (display.CustomTop is < -MaxPixels or > MaxPixels) display.CustomTop = null;
    }

    /// <summary>A drive or camera redirection list is never empty; "*" means "all of them".</summary>
    private static string RedirectionList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "*" : value;

    private static List<T> Compact<T>(List<T>? items) where T : class
    {
        if (items is null) return new List<T>();

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is null) items.RemoveAt(i);
        }

        return items;
    }

    private static Dictionary<string, string> CaseInsensitive(Dictionary<string, string>? source)
    {
        // The deserializer builds a case-sensitive dictionary; .rdp property names are not.
        var map = new Dictionary<string, string>(source?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
        if (source is null) return map;

        foreach (var pair in source)
        {
            if (pair.Key is { Length: > 0 } && pair.Value is not null) map[pair.Key] = pair.Value;
        }

        return map;
    }

    private static string ToFullPath(string path, string parameterName)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(UiLanguage.Format(Strings.Transfer_Error_InvalidPath, path), parameterName, ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not delete the temporary file '{path}'.", ex);
        }
    }

    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static string ResolveAppVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(ConfigTransfer).Assembly;
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

    // ------------------------------------------------------------------ internal types

    private enum SecretStatus
    {
        Decrypted = 0,
        WrongPassphrase = 1,
        Malformed = 2,
    }

    /// <summary>The kind of entry an import note is about.</summary>
    private enum EntryKind
    {
        Group = 0,
        Connection = 1,
        MultiConfig = 2,
        Credential = 3,
    }

    private sealed record Library(
        IReadOnlyList<ConnectionGroup> Groups,
        IReadOnlyList<RdpConnection> Connections,
        IReadOnlyList<MultiConfig> MultiConfigs,
        IReadOnlyList<CredentialSet> Credentials);

    private sealed record ExistingIds(
        HashSet<Guid> Groups,
        HashSet<Guid> Connections,
        HashSet<Guid> MultiConfigs,
        HashSet<Guid> Credentials)
    {
        public static ExistingIds From(Library library) => new(
            library.Groups.Select(g => g.Id).ToHashSet(),
            library.Connections.Select(c => c.Id).ToHashSet(),
            library.MultiConfigs.Select(m => m.Id).ToHashSet(),
            library.Credentials.Select(c => c.Id).ToHashSet());
    }

    private sealed record PendingCredential(CredentialSet Set, string? EncryptedPassword);

    private sealed class ImportPlan
    {
        public List<ConnectionGroup> Groups { get; } = new();
        public List<PendingCredential> Credentials { get; } = new();
        public List<RdpConnection> Connections { get; } = new();
        public List<MultiConfig> MultiConfigs { get; } = new();

        /// <summary>Entries the file carried that can never be written.</summary>
        public int Rejected { get; set; }
    }

    private sealed class Counters
    {
        public int GroupsAdded { get; set; }
        public int ConnectionsAdded { get; set; }
        public int MultiConfigsAdded { get; set; }
        public int CredentialsAdded { get; set; }
        public int Skipped { get; set; }
        public int Replaced { get; set; }
    }

    private sealed class WarningSink
    {
        private const int MaxWarnings = 50;

        private readonly List<string> _items = new();
        private int _suppressed;

        public void Add(string message)
        {
            if (_items.Count < MaxWarnings) _items.Add(message);
            else _suppressed++;
        }

        public IReadOnlyList<string> Build()
        {
            if (_suppressed > 0)
            {
                _items.Add(UiLanguage.Plural(
                    _suppressed, Strings.Transfer_Warning_MoreProblems_One, Strings.Transfer_Warning_MoreProblems_Many));
                _suppressed = 0;
            }

            return _items;
        }
    }
}
