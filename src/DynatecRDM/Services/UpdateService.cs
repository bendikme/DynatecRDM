using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynatecRDM.Models;
using DynatecRDM.Resources;

namespace DynatecRDM.Services;

/// <summary>
/// Checks GitHub releases for a newer build, downloads the installer and hands it to Windows.
/// <para>
/// The user's database, logs and snapshots live directly in <see cref="AppLog.DataDirectory"/>.
/// This service only ever creates, writes and prunes files inside the "updates" sub-folder, and
/// only files it recognises as its own downloads, so an update can never disturb user data.
/// </para>
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string ApiRoot = "https://api.github.com/repos/";
    private const int DownloadBufferSize = 81920;
    private const int MinIntervalHours = 1;
    private const int MaxIntervalHours = 24 * 30;

    /// <summary>Hard ceiling for a downloaded installer; anything larger is treated as bogus.</summary>
    private const long MaxDownloadBytes = 1024L * 1024L * 1024L;

    /// <summary>Head-room left on the disk after a download, so the volume is never filled.</summary>
    private const long FreeSpaceSlack = 64L * 1024L * 1024L;

    private const int MaxTagLength = 100;
    private const int MaxNameLength = 200;
    private const int MaxUrlLength = 2048;
    private const int MaxNotesLength = 32768;
    private const int MaxTokenLength = 512;

    private const int ErrorElevationRequired = 740;
    private const int ErrorCancelled = 1223;

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinLoopDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DownloadRetention = TimeSpan.FromDays(7);

    private static readonly char[] ArgumentQuoteChars = { ' ', '\t', '"' };

    private static readonly string[] InstallerExtensions = { ".msi", ".exe", ".zip" };

    // Declared before the client: the user agent is built from it.
    private static readonly string VersionString = ResolveCurrentVersion();

    // One client for the whole process. A client per call leaks sockets until they time out.
    private static readonly HttpClient Http = CreateHttpClient();

    private static int _badTokenLogged;

    private readonly Func<AppSettings> _settings;
    private readonly Func<Task> _saveSettings;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _loopSync = new();
    private readonly AppSettings _fallbackSettings = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _disposed;

    public UpdateService(Func<AppSettings> settings, Func<Task> saveSettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(saveSettings);

        _settings = settings;
        _saveSettings = saveSettings;
    }

    /// <summary>
    /// Raised on a background thread when a periodic check finds a release newer than this build.
    /// Handlers must marshal to the UI thread themselves.
    /// </summary>
    public event EventHandler<UpdateInfo>? UpdateAvailable;

    /// <summary>The running build's version, without any "+build" metadata.</summary>
    public static string CurrentVersion => VersionString;

    /// <summary>
    /// Folder downloaded installers are kept in. It is a sub-folder of the data directory and is
    /// the only place this service ever writes.
    /// </summary>
    public static string UpdatesDirectory { get; } = Path.Combine(AppLog.DataDirectory, "updates");

    // ------------------------------------------------------------------ checking

    /// <summary>
    /// Asks GitHub for the newest release. Never throws: every failure comes back as
    /// <see cref="UpdateCheckStatus.Failed"/> with a short message.
    /// </summary>
    /// <param name="force">
    /// Runs the check even when checking is switched off or the interval has not elapsed, and waits
    /// briefly for a background check that is already running instead of reporting its own guess.
    /// </param>
    public async Task<UpdateCheckResult> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        var settings = Cfg;

        if (!force && !settings.UpdateCheckEnabled)
            return UpdateCheckResult.Disabled();

        if (!TryParseRepository(settings.UpdateRepository, out var owner, out var repo, out var problem))
            return UpdateCheckResult.NotConfigured(problem);

        if (!force && IsWithinInterval(settings))
            return UpdateCheckResult.UpToDate();

        var acquired = false;

        try
        {
            // A user-triggered check queues behind a running one rather than reporting a result it
            // never produced; a background check just stands down.
            acquired = force
                ? await _checkGate.WaitAsync(GateWait, ct).ConfigureAwait(false)
                : _checkGate.Wait(0);

            if (!acquired)
            {
                return force
                    ? UpdateCheckResult.Failed(Strings.Update_Status_CheckRunning)
                    : UpdateCheckResult.UpToDate(Strings.Update_Status_AlreadyRunning);
            }

            // Settings may have been replaced while this call waited for its turn.
            settings = Cfg;

            // The check queued ahead of this one may already have done the work.
            if (!force && IsWithinInterval(settings))
                return UpdateCheckResult.UpToDate();

            var release = await FetchReleaseAsync(owner, repo, settings, ct).ConfigureAwait(false);

            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            await SaveSettingsSafeAsync().ConfigureAwait(false);

            if (release is null)
                return UpdateCheckResult.UpToDate(UiLanguage.Format(Strings.Update_Status_NoRelease, $"{owner}/{repo}"));

            var update = ToUpdateInfo(release, owner, repo);
            if (update is null)
                return UpdateCheckResult.UpToDate(UiLanguage.Format(Strings.Update_Status_NoVersionNumber, $"{owner}/{repo}"));

            if (CompareVersions(update.Version, CurrentVersion) <= 0)
                return UpdateCheckResult.UpToDate(UiLanguage.Format(Strings.Update_Status_Newest, CurrentVersion));

            var skipped = settings.SkippedUpdateVersion;
            if (!string.IsNullOrWhiteSpace(skipped) && CompareVersions(update.Version, skipped) == 0)
                return UpdateCheckResult.UpToDate(UiLanguage.Format(Strings.Update_Status_Skipped, update.Version));

            AppLog.Info($"Update available: {update.Version} (running {CurrentVersion}).");
            return UpdateCheckResult.Available(update);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(Strings.Update_Status_Cancelled);
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn($"The update check against {owner}/{repo} timed out.");
            return UpdateCheckResult.Failed(Strings.Update_Status_Timeout);
        }
        catch (UpdateApiException ex)
        {
            AppLog.Warn($"Update check against {owner}/{repo} failed: {ex.Message}");
            return UpdateCheckResult.Failed(ex.UserMessage);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warn($"Update check against {owner}/{repo} could not reach GitHub.", ex);
            return UpdateCheckResult.Failed(Strings.Update_Status_Unreachable);
        }
        catch (JsonException ex)
        {
            AppLog.Warn($"Update check against {owner}/{repo} returned unreadable JSON.", ex);
            return UpdateCheckResult.Failed(Strings.Update_Status_BadResponse);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update check against {owner}/{repo} failed.", ex);
            return UpdateCheckResult.Failed(Strings.Update_Status_Failed);
        }
        finally
        {
            if (acquired) _checkGate.Release();
        }
    }

    /// <summary>Runs a check and raises <see cref="UpdateAvailable"/> when something newer exists.</summary>
    public async Task<UpdateCheckResult> CheckInBackgroundAsync(CancellationToken ct = default)
    {
        var result = await CheckAsync(force: false, ct).ConfigureAwait(false);

        if (result.HasUpdate && result.Update is not null)
            RaiseUpdateAvailable(result.Update);

        return result;
    }

    /// <summary>Starts the periodic background check. Calling it twice is a no-op.</summary>
    public void StartBackgroundChecks(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        lock (_loopSync)
        {
            if (_loop is not null) return;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loopCts = cts;
            _loop = Task.Run(() => BackgroundLoopAsync(cts.Token), CancellationToken.None);
        }
    }

    /// <summary>Records a version the user never wants to be offered again.</summary>
    public void Skip(string version)
    {
        try
        {
            var normalised = NormalizeVersion(version);
            var settings = Cfg;
            settings.SkippedUpdateVersion = normalised.Length == 0 ? null : normalised;

            AppLog.Info(normalised.Length == 0
                ? "Cleared the skipped update version."
                : $"Version {normalised} will not be offered again.");

            _ = SaveSettingsSafeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not record the skipped update version.", ex);
        }
    }

    // ------------------------------------------------------------------ downloading

    /// <summary>
    /// Streams the release's installer into the updates folder and returns its path, or null when
    /// the download failed. The file is only moved into place once it is complete and the right size.
    /// </summary>
    /// <param name="progress">
    /// Reports completion as a fraction between 0 and 1 (never a percentage), at most once per
    /// whole percent. Callbacks arrive on a background thread.
    /// </param>
    public async Task<string?> DownloadAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (update is null)
        {
            AppLog.Warn("Update download requested without a release.");
            return null;
        }

        var asset = update.InstallerAsset;
        if (asset is null)
        {
            AppLog.Warn($"Release {update.Version} carries no .msi, .exe or .zip asset.");
            return null;
        }

        var fileName = SanitiseAssetName(asset.Name);
        if (fileName is null)
        {
            AppLog.Warn("The release asset was refused: unsupported or unsafe file name.");
            return null;
        }

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || !IsTrustedGitHubUri(uri))
        {
            AppLog.Warn($"The download URL of asset '{fileName}' was refused; it is not an https GitHub address.");
            return null;
        }

        if (asset.Size > MaxDownloadBytes)
        {
            AppLog.Warn($"Release {update.Version} declares an installer of {asset.Size} bytes, which is larger than this app will download.");
            return null;
        }

        if (asset.Size > 0 && !HasRoomFor(asset.Size))
        {
            AppLog.Warn($"There is not enough free disk space to download update {update.Version}.");
            return null;
        }

        string target;
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);

            var root = Path.GetFullPath(UpdatesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            target = Path.GetFullPath(Path.Combine(root, fileName));

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("The release asset name resolved outside the updates folder; refusing it.");
                return null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not prepare the updates folder.", ex);
            return null;
        }

        CleanupOldDownloads();

        var partialPath = target + ".part";
        var limit = asset.Size > 0 ? asset.Size : MaxDownloadBytes;
        var oversize = false;

        try
        {
            using var request = CreateRequest(uri, TokenFor(uri), "application/octet-stream");
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"Downloading update {update.Version} failed with HTTP {(int)response.StatusCode}.");
                return null;
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared > limit)
            {
                AppLog.Warn($"Downloading update {update.Version} was refused: the server announced {declared} bytes.");
                return null;
            }

            var total = declared ?? asset.Size;
            Report(progress, 0d);

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DownloadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[DownloadBufferSize];
                var lastReported = 0d;
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    if (written + read > limit)
                    {
                        oversize = true;
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;

                    if (progress is not null && total > 0)
                    {
                        var fraction = Math.Clamp((double)written / total, 0d, 1d);
                        if (fraction - lastReported >= 0.01d)
                        {
                            lastReported = fraction;
                            Report(progress, fraction);
                        }
                    }
                }

                if (!oversize) await destination.FlushAsync(ct).ConfigureAwait(false);
            }

            if (oversize)
            {
                AppLog.Error($"Update {update.Version} sent more data than the release declares; discarding it.");
                TryDelete(partialPath);
                return null;
            }

            var info = new FileInfo(partialPath);
            if (!info.Exists)
            {
                AppLog.Error($"The download of update {update.Version} produced no file.");
                return null;
            }

            if (asset.Size > 0 && info.Length != asset.Size)
            {
                AppLog.Error($"Update {update.Version} downloaded {info.Length} bytes but the release lists {asset.Size}; discarding it.");
                TryDelete(partialPath);
                return null;
            }

            File.Move(partialPath, target, overwrite: true);
            Report(progress, 1d);
            AppLog.Info($"Update {update.Version} downloaded to {target}.");
            return target;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"The download of update {update.Version} was cancelled.");
            TryDelete(partialPath);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Downloading update {update.Version} failed.", ex);
            TryDelete(partialPath);
            return null;
        }
    }

    /// <summary>
    /// Starts the downloaded installer. Returns whether the process was started - the app usually
    /// exits straight afterwards so Windows can replace its files.
    /// </summary>
    public bool LaunchInstaller(string installerPath, bool silent = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                AppLog.Warn("Cannot start the update installer: the file no longer exists.");
                return false;
            }

            var fullPath = Path.GetFullPath(installerPath);
            var extension = Path.GetExtension(fullPath);
            var arguments = new List<string>(4);

            string fileName;

            if (string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase))
            {
                fileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                if (!File.Exists(fileName))
                {
                    AppLog.Error("Cannot start the update: msiexec.exe was not found in the system directory.");
                    return false;
                }

                // Only /i and the package: no property here can reach the data directory.
                arguments.Add("/i");
                arguments.Add(fullPath);
                if (silent)
                {
                    arguments.Add("/qn");
                    arguments.Add("/norestart");
                }
            }
            else if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                fileName = fullPath;
                if (silent)
                {
                    arguments.Add("/SILENT");
                    arguments.Add("/NORESTART");
                }
            }
            else if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Unpacking an archive over the running executable cannot work; the user installs it.
                AppLog.Warn($"The update is a .zip archive and cannot be installed automatically: {fullPath}");
                return false;
            }
            else
            {
                AppLog.Warn($"Refusing to start an update file of type '{extension}'.");
                return false;
            }

            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? UpdatesDirectory,
            };

            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = StartInstaller(info, arguments);
            if (process is null)
            {
                AppLog.Error("The update installer did not start.");
                return false;
            }

            AppLog.Info($"Update installer started{(silent ? " silently" : string.Empty)}: {fullPath}");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            AppLog.Info("The update installer was not started: the Windows security prompt was declined.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not start the update installer.", ex);
            return false;
        }
    }

    /// <summary>Starts the installer, retrying through ShellExecute when it needs elevation.</summary>
    private static Process? StartInstaller(ProcessStartInfo info, IReadOnlyList<string> arguments)
    {
        try
        {
            return Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            // CreateProcess cannot elevate; ShellExecute shows the consent prompt instead.
            var elevated = new ProcessStartInfo(info.FileName)
            {
                UseShellExecute = true,
                WorkingDirectory = info.WorkingDirectory,
                Arguments = BuildArguments(arguments),
            };

            return Process.Start(elevated);
        }
    }

    private static string BuildArguments(IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();

        foreach (var argument in arguments)
        {
            if (builder.Length > 0) builder.Append(' ');

            if (argument.Length > 0 && argument.IndexOfAny(ArgumentQuoteChars) < 0)
            {
                builder.Append(argument);
                continue;
            }

            builder.Append('"').Append(argument.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------ version comparison

    /// <summary>
    /// Compares two version strings ("v1.2.3", "1.2.3.4", "1.2.3-rc.1"). A pre-release sorts before
    /// the same version without a suffix; anything unparsable sorts lowest.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var left = ParseVersion(a);
        var right = ParseVersion(b);

        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;

        var count = Math.Max(left.Numbers.Length, right.Numbers.Length);
        for (var i = 0; i < count; i++)
        {
            var x = i < left.Numbers.Length ? left.Numbers[i] : 0L;
            var y = i < right.Numbers.Length ? right.Numbers[i] : 0L;
            if (x != y) return x < y ? -1 : 1;
        }

        return ComparePreRelease(left.PreRelease, right.PreRelease);
    }

    private static ParsedVersion? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1]))
            text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        var preRelease = string.Empty;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..].Trim();
            text = text[..dash];
        }

        text = text.Trim();
        if (text.Length == 0) return null;

        var parts = text.Split('.');
        if (parts.Length > 8) return null;

        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.Length == 0 ||
                part.Length > 18 ||
                !long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers[i] = number;
        }

        return new ParsedVersion(numbers, preRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;
        if (b.Length == 0) return -1;

        var left = a.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var right = b.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(left.Length, right.Length);

        for (var i = 0; i < count; i++)
        {
            if (i >= left.Length) return -1;
            if (i >= right.Length) return 1;

            var leftIsNumber = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumber = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            if (leftIsNumber && rightIsNumber)
            {
                if (leftNumber != rightNumber) return leftNumber < rightNumber ? -1 : 1;
                continue;
            }

            if (leftIsNumber) return -1;
            if (rightIsNumber) return 1;

            var comparison = string.Compare(left[i], right[i], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison < 0 ? -1 : 1;
        }

        return 0;
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = CleanText(value, MaxTagLength);
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1]))
            text = text[1..];

        return text.Trim();
    }

    // ------------------------------------------------------------------ GitHub plumbing

    private static async Task<UpdateReleaseJson?> FetchReleaseAsync(
        string owner,
        string repo,
        AppSettings settings,
        CancellationToken ct)
    {
        var releases = ApiRoot + Uri.EscapeDataString(owner) + "/" + Uri.EscapeDataString(repo) + "/releases";
        var includePrereleases = settings.UpdateIncludePrereleases;

        // /releases/latest never returns a pre-release, so the list endpoint is the only way to see one.
        var uri = new Uri(includePrereleases ? releases + "?per_page=20" : releases + "/latest");

        using var timeout = new CancellationTokenSource(ApiTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var request = CreateRequest(uri, settings.UpdateAccessToken, "application/vnd.github+json");
        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // A 404 here means either the repository cannot be reached or it simply has no
            // published release yet. Those need different advice, and asking the repository
            // endpoint is the only way to tell them apart. It costs one extra request on a path
            // that is already failing.
            if (response.StatusCode == HttpStatusCode.NotFound &&
                await RepositoryExistsAsync(owner, repo, settings, linked.Token).ConfigureAwait(false))
            {
                throw new UpdateApiException(
                    $"{owner}/{repo} has no published releases yet, so there is nothing to update to.",
                    UiLanguage.Format(Strings.Update_Status_NoReleasesYet, $"{owner}/{repo}"));
            }

            throw DescribeFailure(response, owner, repo);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);

        if (!includePrereleases)
        {
            var single = await JsonSerializer
                .DeserializeAsync(stream, UpdateJsonContext.Default.Release, linked.Token)
                .ConfigureAwait(false);

            return single is null || single.Draft ? null : single;
        }

        var list = await JsonSerializer
            .DeserializeAsync(stream, UpdateJsonContext.Default.ReleaseList, linked.Token)
            .ConfigureAwait(false);

        if (list is null) return null;

        UpdateReleaseJson? newest = null;
        foreach (var candidate in list)
        {
            if (candidate is null || candidate.Draft) continue;
            if (newest is null || IsNewerRelease(candidate, newest)) newest = candidate;
        }

        return newest;
    }

    /// <summary>
    /// The highest version wins, not the most recently published one: a hotfix published today for
    /// an older branch must not hide a newer release.
    /// </summary>
    private static bool IsNewerRelease(UpdateReleaseJson candidate, UpdateReleaseJson current)
    {
        var byVersion = CompareVersions(NormalizeVersion(candidate.TagName), NormalizeVersion(current.TagName));
        if (byVersion != 0) return byVersion > 0;

        var a = candidate.PublishedAt?.UtcDateTime ?? DateTime.MinValue;
        var b = current.PublishedAt?.UtcDateTime ?? DateTime.MinValue;
        return a > b;
    }

    private static UpdateInfo? ToUpdateInfo(UpdateReleaseJson release, string owner, string repo)
    {
        var tag = CleanText(release.TagName, MaxTagLength);
        var releaseName = CleanText(release.Name, MaxNameLength);

        var version = NormalizeVersion(tag.Length > 0 ? tag : releaseName);
        if (version.Length == 0 || ParseVersion(version) is null) return null;

        var assets = new List<UpdateAsset>();
        if (release.Assets is not null)
        {
            foreach (var asset in release.Assets)
            {
                if (asset is null) continue;

                var name = CleanText(asset.Name, MaxNameLength);
                var url = CleanText(asset.BrowserDownloadUrl, MaxUrlLength);
                if (name.Length == 0 || url.Length == 0) continue;

                // Anything that is not an https GitHub address is dropped here, so nothing further
                // up can be tempted to open or download it.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var assetUri) || !IsTrustedGitHubUri(assetUri)) continue;

                assets.Add(new UpdateAsset(
                    name,
                    url,
                    asset.Size < 0 ? 0 : asset.Size,
                    CleanText(asset.ContentType, MaxNameLength)));
            }
        }

        var title = releaseName.Length > 0 ? releaseName : (tag.Length > 0 ? tag : version);
        var body = CleanText(release.Body, MaxNotesLength, allowNewLines: true);
        var notes = body.Length == 0 ? null : body;

        var htmlUrl = CleanText(release.HtmlUrl, MaxUrlLength);
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var pageUri) || !IsTrustedGitHubUri(pageUri))
            htmlUrl = $"https://github.com/{owner}/{repo}/releases";

        return new UpdateInfo(
            version,
            tag.Length > 0 ? tag : version,
            title,
            notes,
            release.PublishedAt?.UtcDateTime ?? DateTime.MinValue,
            release.Prerelease,
            htmlUrl,
            assets);
    }

    /// <summary>
    /// True when the repository itself is reachable. Used only to turn a 404 on the releases
    /// endpoint into a message that says which of the two problems it actually is.
    /// </summary>
    private static async Task<bool> RepositoryExistsAsync(
        string owner,
        string repo,
        AppSettings settings,
        CancellationToken ct)
    {
        try
        {
            var uri = new Uri(ApiRoot + Uri.EscapeDataString(owner) + "/" + Uri.EscapeDataString(repo));
            using var request = CreateRequest(uri, settings.UpdateAccessToken, "application/vnd.github+json");
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Repository probe for {owner}/{repo} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The failure in English for the log, and in the UI language for the dialog.</summary>
    private static UpdateApiException DescribeFailure(HttpResponseMessage response, string owner, string repo)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                return new UpdateApiException(
                    "GitHub rate limit reached. The next check will run later.",
                    Strings.Update_Status_RateLimited);

            case HttpStatusCode.Forbidden:
                return IsRateLimited(response)
                    ? new UpdateApiException(
                        "GitHub rate limit reached. The next check will run later.",
                        Strings.Update_Status_RateLimited)
                    : new UpdateApiException(
                        "GitHub refused the update check (403). Check the update access token.",
                        Strings.Update_Status_Forbidden);

            case HttpStatusCode.Unauthorized:
                return new UpdateApiException(
                    "GitHub rejected the update access token.",
                    Strings.Update_Status_Unauthorized);

            case HttpStatusCode.NotFound:
                return new UpdateApiException(
                    $"No release was found for {owner}/{repo}. Check the repository name.",
                    UiLanguage.Format(Strings.Update_Status_NotFound, $"{owner}/{repo}"));

            default:
                return new UpdateApiException(
                    $"GitHub returned HTTP {(int)response.StatusCode}.",
                    UiLanguage.Format(Strings.Update_Status_HttpError, (int)response.StatusCode));
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining))
        {
            foreach (var value in remaining)
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var left))
                    return left <= 0;
            }
        }

        return response.Headers.TryGetValues("retry-after", out _);
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string? token, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", accept);

        var safe = SafeToken(token);
        if (safe is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + safe);

        return request;
    }

    /// <summary>
    /// Accepts a token only when it is printable ASCII of a plausible length. A value with control
    /// characters would break the header framing, and the token itself is never logged or echoed,
    /// so a rejected one simply goes unused and the check runs anonymously.
    /// </summary>
    private static string? SafeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var trimmed = token.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxTokenLength) return null;

        foreach (var c in trimmed)
        {
            if (c < '!' || c > '~')
            {
                if (Interlocked.Exchange(ref _badTokenLogged, 1) == 0)
                    AppLog.Warn("The update access token contains unsupported characters and was ignored.");

                return null;
            }
        }

        return trimmed;
    }

    private string? TokenFor(Uri uri)
    {
        // Only GitHub itself ever sees the token; asset downloads redirect to storage hosts and
        // .NET drops the Authorization header across that hop.
        var host = uri.Host;
        var isGitHub = host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

        if (!isGitHub) return null;

        var token = Cfg.UpdateAccessToken;
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static bool IsTrustedGitHubUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits "owner/repo" (or a github.com URL) into its two segments.</summary>
    private static bool TryParseRepository(string? value, out string owner, out string repo, out string problem)
    {
        owner = string.Empty;
        repo = string.Empty;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            problem = Strings.Update_Status_NoRepository;
            return false;
        }

        var text = value.Trim();

        const string HttpsPrefix = "https://github.com/";
        const string HttpPrefix = "http://github.com/";
        if (text.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase)) text = text[HttpsPrefix.Length..];
        else if (text.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase)) text = text[HttpPrefix.Length..];

        text = text.Trim('/', ' ');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IsValidSegment(parts[0]) || !IsValidSegment(parts[1]))
        {
            problem = Strings.Update_Status_BadRepository;
            return false;
        }

        owner = parts[0];
        repo = parts[1];
        return true;
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > 100) return false;
        if (segment == "." || segment == "..") return false;

        foreach (var c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.') return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ background loop

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        try
        {
            // Let the app finish starting before adding network traffic.
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var settings = Cfg;
                    if (settings.UpdateCheckEnabled && !string.IsNullOrWhiteSpace(settings.UpdateRepository))
                        await CheckInBackgroundAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("A background update check failed.", ex);
                }

                await Task.Delay(NextDelay(Cfg), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            AppLog.Error("The background update loop stopped unexpectedly.", ex);
        }
    }

    private static TimeSpan IntervalOf(AppSettings settings) =>
        TimeSpan.FromHours(Math.Clamp(settings.UpdateCheckIntervalHours, MinIntervalHours, MaxIntervalHours));

    private static bool IsWithinInterval(AppSettings settings)
    {
        if (settings.LastUpdateCheckUtc is not { } last) return false;

        var age = DateTime.UtcNow - last;
        return age >= TimeSpan.Zero && age < IntervalOf(settings);
    }

    /// <summary>Time until the next check is due, with a floor so a failing check cannot spin.</summary>
    private static TimeSpan NextDelay(AppSettings settings)
    {
        var interval = IntervalOf(settings);
        if (settings.LastUpdateCheckUtc is not { } last) return interval;

        var wait = last + interval - DateTime.UtcNow;
        if (wait < MinLoopDelay) wait = MinLoopDelay;
        if (wait > interval) wait = interval;
        return wait;
    }

    private void RaiseUpdateAvailable(UpdateInfo update)
    {
        var handler = UpdateAvailable;
        if (handler is null) return;

        try
        {
            handler(this, update);
        }
        catch (Exception ex)
        {
            AppLog.Error("An update notification handler threw.", ex);
        }
    }

    // ------------------------------------------------------------------ helpers

    private AppSettings Cfg
    {
        get
        {
            try { return _settings() ?? _fallbackSettings; }
            catch (Exception ex)
            {
                AppLog.Warn("Update settings lookup failed; using defaults.", ex);
                return _fallbackSettings;
            }
        }
    }

    private async Task SaveSettingsSafeAsync()
    {
        try
        {
            var task = _saveSettings();
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not persist the update settings.", ex);
        }
    }

    private static void Report(IProgress<double>? progress, double value)
    {
        if (progress is null) return;

        try
        {
            progress.Report(value);
        }
        catch (Exception ex)
        {
            AppLog.Warn("An update progress callback threw.", ex);
        }
    }

    /// <summary>
    /// Reduces text that arrived from GitHub to something safe to log and to show: control
    /// characters are dropped (they would let a crafted release forge log lines) and the length
    /// is capped.
    /// </summary>
    private static string CleanText(string? value, int maxLength, bool allowNewLines = false)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0) return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));

        foreach (var c in value)
        {
            if (builder.Length >= maxLength) break;

            if (c == '\r') continue;

            if (c == '\n' || c == '\t')
            {
                builder.Append(allowNewLines ? c : ' ');
                continue;
            }

            if (char.IsControl(c)) continue;

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>Reduces a release asset name to a safe file name, or null when it is not installable.</summary>
    private static string? SanitiseAssetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string candidate;
        try
        {
            candidate = Path.GetFileName(CleanText(name, MaxNameLength));
        }
        catch (Exception)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..") return null;

        var extension = Path.GetExtension(cleaned);
        if (!IsInstallerExtension(extension)) return null;

        const int MaxLength = 120;
        if (cleaned.Length > MaxLength)
        {
            var stem = Path.GetFileNameWithoutExtension(cleaned);
            stem = stem[..Math.Max(1, MaxLength - extension.Length)];
            cleaned = stem + extension;
        }

        return cleaned;
    }

    private static bool IsInstallerExtension(string extension)
    {
        foreach (var known in InstallerExtensions)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Removes stale installers from the updates folder. It never recurses and only removes files
    /// this service itself could have written, so the database and the logs one level up are out
    /// of reach even if the folder ever held something else.
    /// </summary>
    private static void CleanupOldDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdatesDirectory)) return;

            var cutoff = DateTime.UtcNow - DownloadRetention;

            foreach (var file in Directory.EnumerateFiles(UpdatesDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var extension = Path.GetExtension(name);

                    var isOurs = IsInstallerExtension(extension) ||
                                 string.Equals(extension, ".part", StringComparison.OrdinalIgnoreCase);

                    if (!isOurs) continue;

                    var info = new FileInfo(file);
                    if (info.Exists && info.LastWriteTimeUtc < cutoff) info.Delete();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not remove the old update file '{file}'.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not clean the updates folder.", ex);
        }
    }

    /// <summary>True unless the volume is known to be too full to hold the download.</summary>
    private static bool HasRoomFor(long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(UpdatesDirectory));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            return !drive.IsReady || drive.AvailableFreeSpace > bytes + FreeSpaceSlack;
        }
        catch (Exception)
        {
            // A failed capacity check must not block a download that might well succeed.
            return true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not delete the partial update file '{path}'.", ex);
        }
    }

    private static string ResolveCurrentVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;

            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                var trimmed = (plus >= 0 ? informational[..plus] : informational).Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            var file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            if (!string.IsNullOrWhiteSpace(file)) return file.Trim();

            var version = assembly.GetName().Version;
            if (version is not null) return version.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not determine the running application version.", ex);
        }

        return "0.0.0";
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };

        // No client-wide timeout: the API calls use their own 30s token and downloads must be able
        // to take as long as they take.
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        // GitHub answers 403 to requests without a User-Agent.
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DynatecRDM/" + SanitiseForHeader(VersionString));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }

    private static string SanitiseForHeader(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_') builder.Append(c);
        }

        return builder.Length == 0 ? "0.0.0" : builder.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        CancellationTokenSource? cts;
        lock (_loopSync)
        {
            cts = _loopCts;
            _loopCts = null;
            _loop = null;
        }

        // Cancel before disposing: a token that is already cancelled stays usable to the loop.
        try { cts?.Cancel(); } catch (Exception ex) { AppLog.Warn("Cancelling the update loop failed.", ex); }
        try { cts?.Dispose(); } catch (Exception ex) { AppLog.Warn("Disposing the update loop token failed.", ex); }

        // The semaphore has no unmanaged resource and a check may still be unwinding, so it is left
        // alone; the static HttpClient lives for the process and is never disposed here.
    }

    private sealed class ParsedVersion
    {
        public ParsedVersion(long[] numbers, string preRelease)
        {
            Numbers = numbers;
            PreRelease = preRelease;
        }

        public long[] Numbers { get; }

        public string PreRelease { get; }
    }

    /// <summary>A GitHub response that the user needs to hear about in plain words.</summary>
    private sealed class UpdateApiException : Exception
    {
        /// <param name="message">English, for the log.</param>
        /// <param name="userMessage">The same sentence in the UI language, for the dialog.</param>
        public UpdateApiException(string message, string userMessage) : base(message)
        {
            UserMessage = userMessage;
        }

        public string UserMessage { get; }
    }
}

// ---------------------------------------------------------------------- JSON contract

internal sealed class UpdateReleaseJson
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<UpdateReleaseAssetJson>? Assets { get; set; }
}

internal sealed class UpdateReleaseAssetJson
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }
}

/// <summary>Source-generated metadata: no reflection and no per-call serializer options.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(UpdateReleaseJson), TypeInfoPropertyName = "Release")]
[JsonSerializable(typeof(List<UpdateReleaseJson>), TypeInfoPropertyName = "ReleaseList")]
internal partial class UpdateJsonContext : JsonSerializerContext
{
}
