using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.Models;

/// <summary>A single downloadable file attached to a release.</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size, string ContentType);

/// <summary>A published release that could replace the running build.</summary>
public sealed record UpdateInfo(
    string Version,
    string Tag,
    string Name,
    string? ReleaseNotes,
    DateTime PublishedUtc,
    bool IsPrerelease,
    string HtmlUrl,
    IReadOnlyList<UpdateAsset> Assets)
{
    /// <summary>
    /// The asset to install: the .msi when the release ships one, otherwise the .exe, otherwise
    /// the .zip. Null when the release carries nothing installable.
    /// </summary>
    public UpdateAsset? InstallerAsset { get; } = PickInstaller(Assets);

    /// <summary>Size of <see cref="InstallerAsset"/> in bytes; 0 when there is nothing to download.</summary>
    public long DownloadSize => InstallerAsset?.Size ?? 0;

    /// <summary>True when the release carries a file this app knows how to install.</summary>
    public bool HasInstaller => InstallerAsset is not null;

    /// <summary>"1.4.2" or "1.4.2 (pre-release)" - ready for a window title or a notification.</summary>
    public string DisplayVersion =>
        IsPrerelease ? UiLanguage.Format(Strings.Update_DisplayVersion_Prerelease, Version) : Version;

    private static UpdateAsset? PickInstaller(IReadOnlyList<UpdateAsset>? assets)
    {
        if (assets is null || assets.Count == 0) return null;

        return FirstWithExtension(assets, ".msi")
            ?? FirstWithExtension(assets, ".exe")
            ?? FirstWithExtension(assets, ".zip");
    }

    private static UpdateAsset? FirstWithExtension(IReadOnlyList<UpdateAsset> assets, string extension)
    {
        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            if (asset is not null &&
                !string.IsNullOrEmpty(asset.Name) &&
                asset.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        return null;
    }
}

/// <summary>Outcome of an update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>Nothing newer is published (or the newer version was skipped by the user).</summary>
    UpToDate,

    /// <summary>A newer release is available.</summary>
    UpdateAvailable,

    /// <summary>Update checking is switched off in settings.</summary>
    Disabled,

    /// <summary>No usable repository is configured, so there is nowhere to look.</summary>
    NotConfigured,

    /// <summary>The check could not be completed (network, HTTP or response problem).</summary>
    Failed,
}

/// <summary>The result of an update check, including a message suitable for showing to the user.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string? Message)
{
    /// <summary>True when there is a release the user could install.</summary>
    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable && Update is not null;

    public static UpdateCheckResult UpToDate(string? message = null) =>
        new(UpdateCheckStatus.UpToDate, null, message);

    public static UpdateCheckResult Available(UpdateInfo update, string? message = null) =>
        new(UpdateCheckStatus.UpdateAvailable, update, message);

    public static UpdateCheckResult Disabled(string? message = null) =>
        new(UpdateCheckStatus.Disabled, null, message ?? Strings.Update_Status_Disabled);

    public static UpdateCheckResult NotConfigured(string? message = null) =>
        new(UpdateCheckStatus.NotConfigured, null, message ?? Strings.Update_Status_NoRepository);

    public static UpdateCheckResult Failed(string message) =>
        new(UpdateCheckStatus.Failed, null, message);
}
