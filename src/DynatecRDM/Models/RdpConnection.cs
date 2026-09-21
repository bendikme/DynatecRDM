namespace DynatecRDM.Models;

/// <summary>A single stored remote-desktop configuration.</summary>
public sealed class RdpConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Group this connection belongs to; null means the root.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Host name or IP. Port is kept separately and appended on write.</summary>
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3389;

    /// <summary>Credential set reused across connections; null means prompt.</summary>
    public Guid? CredentialSetId { get; set; }

    public CredentialDelivery CredentialDelivery { get; set; } = CredentialDelivery.Both;

    public DisplaySettings Display { get; set; } = new();
    public ExperienceSettings Experience { get; set; } = new();
    public RedirectionSettings Redirection { get; set; } = new();
    public GatewaySettings Gateway { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();

    /// <summary>
    /// Raw extra .rdp lines, keyed by property name (for example "kdcproxyname:s").
    /// These are written verbatim and win over generated values.
    /// </summary>
    public Dictionary<string, string> CustomProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Watch the session and bring it back if it drops.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Maximum watchdog reconnect attempts; 0 means unlimited.</summary>
    public int MaxReconnectAttempts { get; set; } = 10;

    /// <summary>Seconds to wait between watchdog reconnect attempts.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>Accent colour shown in the UI and the tray menu, as #RRGGBB.</summary>
    public string? Color { get; set; }

    /// <summary>Free-text tags, comma separated.</summary>
    public string? Tags { get; set; }

    public bool Favorite { get; set; }
    public int SortOrder { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedUtc { get; set; }
    public int LaunchCount { get; set; }

    /// <summary>Host with the port appended when it is not the default.</summary>
    public string FullAddress => Port == 3389 || Port <= 0 ? Host : $"{Host}:{Port}";

    /// <summary>Target name used for the Windows Credential Vault entry.</summary>
    public string TermsrvTarget => $"TERMSRV/{Host}";

    public RdpConnection Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        GroupId = GroupId,
        Host = Host,
        Port = Port,
        CredentialSetId = CredentialSetId,
        CredentialDelivery = CredentialDelivery,
        Display = Display.Clone(),
        Experience = Experience.Clone(),
        Redirection = Redirection.Clone(),
        Gateway = Gateway.Clone(),
        Security = Security.Clone(),
        CustomProperties = new Dictionary<string, string>(CustomProperties, StringComparer.OrdinalIgnoreCase),
        AutoReconnect = AutoReconnect,
        MaxReconnectAttempts = MaxReconnectAttempts,
        ReconnectDelaySeconds = ReconnectDelaySeconds,
        Color = Color,
        Tags = Tags,
        Favorite = Favorite,
        SortOrder = SortOrder,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
        LastConnectedUtc = LastConnectedUtc,
        LaunchCount = LaunchCount,
    };
}

/// <summary>A folder in the connection tree. Groups may nest.</summary>
public sealed class ConnectionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public string? Color { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsExpanded { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public ConnectionGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        ParentId = ParentId,
        Color = Color,
        Description = Description,
        SortOrder = SortOrder,
        IsExpanded = IsExpanded,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
    };
}

/// <summary>
/// A reusable login. The password is stored DPAPI-protected (CurrentUser scope) and is
/// never held in plain text on disk.
/// </summary>
public sealed class CredentialSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>DPAPI blob produced by CredentialProtector; null when no password is stored.</summary>
    public byte[]? ProtectedPassword { get; set; }

    public string? Notes { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The logon name Windows will actually accept.
    ///
    /// DOMAIN\user only works with the short NetBIOS domain name. People naturally type the DNS
    /// name they know - "contoso.local" - and "contoso.local\user" is not a valid logon name at
    /// all: the server rejects it and Remote Desktop falls back to asking for the password. A
    /// dotted domain therefore becomes the user principal form, user@contoso.local, which is valid.
    /// </summary>
    public string QualifiedUsername
    {
        get
        {
            var user = (Username ?? string.Empty).Trim();
            var domain = (Domain ?? string.Empty).Trim();

            if (user.Length == 0) return string.Empty;
            if (domain.Length == 0) return user;

            // Already qualified by the user, either way round: leave it alone.
            if (user.Contains('\\') || user.Contains('@')) return user;

            return domain.Contains('.')
                ? $"{user}@{domain.TrimStart('@')}"
                : $"{domain}\\{user}";
        }
    }

    public bool HasPassword => ProtectedPassword is { Length: > 0 };

    public CredentialSet Clone() => new()
    {
        Id = Id,
        Name = Name,
        Domain = Domain,
        Username = Username,
        ProtectedPassword = ProtectedPassword?.ToArray(),
        Notes = Notes,
        IsDefault = IsDefault,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
    };
}
