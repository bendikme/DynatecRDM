namespace DynatecRDM.Models;

/// <summary>
/// The on-disk shape of an exported configuration file (".drdm", JSON inside).
/// <see cref="FormatVersion"/> is written with every file so a future release can still read the
/// files this build produces, and can tell the user plainly when a file is too new to read.
/// </summary>
public sealed class TransferBundle
{
    public int FormatVersion { get; set; } = 1;

    public string Application { get; set; } = "DYNATEC Remote Desktop Manager";

    /// <summary>Machine the file was written on, for provenance.</summary>
    public string? ExportedBy { get; set; }

    public DateTime ExportedUtc { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>True when at least one credential carries an encrypted password.</summary>
    public bool ContainsSecrets { get; set; }

    public List<ConnectionGroup> Groups { get; set; } = new();
    public List<RdpConnection> Connections { get; set; } = new();
    public List<MultiConfig> MultiConfigs { get; set; } = new();
    public List<TransferCredential> Credentials { get; set; } = new();

    /// <summary>Only present in a full-library export, and only when the user asked for it.</summary>
    public AppSettings? Settings { get; set; }
}

/// <summary>
/// A credential as it travels between machines. The database keeps passwords as DPAPI blobs bound
/// to one user on one machine, which are meaningless anywhere else, so a transferred password is
/// re-encrypted under a passphrase the person importing the file has to know.
/// </summary>
public sealed class TransferCredential
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>
    /// Base64 of the passphrase-encrypted password. NEVER a raw DPAPI blob - those are bound to the
    /// exporting user and machine and are meaningless (and needlessly sensitive) elsewhere.
    /// </summary>
    public string? EncryptedPassword { get; set; }
}

/// <summary>What to do with an imported entity whose id already exists in the library.</summary>
public enum ImportMode
{
    /// <summary>Leave the existing entity alone.</summary>
    Skip = 0,
    /// <summary>Overwrite the existing entity with the one from the file.</summary>
    Replace = 1,
    /// <summary>Import everything as a fresh copy, with new ids and remapped references.</summary>
    DuplicateWithNewId = 2,
}

/// <summary>What an import would do, worked out without changing anything.</summary>
public sealed record ImportPreview(
    int Groups,
    int Connections,
    int MultiConfigs,
    int Credentials,
    int Conflicts,
    bool ContainsSecrets,
    bool NeedsPassphrase,
    string? AppVersion,
    DateTime ExportedUtc);

/// <summary>
/// What an import actually did. <see cref="Skipped"/> counts every entity in the file that was not
/// written - existing ones left alone, and malformed ones reported in <see cref="Warnings"/>.
/// </summary>
public sealed record ImportResult(
    int GroupsAdded,
    int ConnectionsAdded,
    int MultiConfigsAdded,
    int CredentialsAdded,
    int Skipped,
    int Replaced,
    IReadOnlyList<string> Warnings);
