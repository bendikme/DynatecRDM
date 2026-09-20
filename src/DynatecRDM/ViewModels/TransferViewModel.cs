using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using DynatecRDM.Models;
using DynatecRDM.Services;
using Microsoft.Win32;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Backs the export / import dialog. Every long operation runs through an
/// <see cref="AsyncRelayCommand"/> with the busy flag raised, and every failure ends as a line
/// in the message bar rather than as an exception: this dialog is the last place a user wants
/// a crash, because it is where they move their library between machines.
/// </summary>
public sealed class TransferViewModel : ObservableObject
{
    private const int MinPassphraseLength = 8;
    private const int MaxMessageLength = 220;
    private const string UngroupedName = "Ungrouped";

    private readonly IDataStore _store;
    private readonly ConfigTransfer _transfer;
    private readonly ObservableCollection<TransferItemViewModel> _items = new();

    private bool _loaded;
    private bool _suspendSelectionUpdates;

    private string _search = string.Empty;
    private bool _exportEverything = true;
    private bool _includeCredentials;
    private bool _includeSettings;
    private string _passphrase = string.Empty;
    private string _passphraseConfirm = string.Empty;
    private string? _passphraseError;
    private int _selectedCount;
    private bool _isLoadingItems;
    private string _exportSummary = string.Empty;
    private string _exportPath = string.Empty;

    private TransferBundle? _bundle;
    private ImportPreview? _preview;
    private ImportResult? _result;
    private string _importFileName = string.Empty;
    private string _importPassphrase = string.Empty;
    private bool _needsPassphrase;
    private ImportMode _mode = ImportMode.Skip;

    private int _selectedTabIndex;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string _message = string.Empty;
    private bool _messageIsError;

    public TransferViewModel(AppServices services, bool startOnImportTab = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        _store = services.Store;

        // The container builds one transfer service; a second one would be identical but would
        // read the library a second time for no reason.
        ConfigTransfer? transfer = services.Transfer;
        _transfer = transfer ?? new ConfigTransfer(services.Store, services.Protector);

        _selectedTabIndex = startOnImportTab ? 1 : 0;

        Items = CollectionViewSource.GetDefaultView(_items);
        Items.SortDescriptions.Add(new SortDescription(nameof(TransferItemViewModel.GroupName), ListSortDirection.Ascending));
        Items.SortDescriptions.Add(new SortDescription(nameof(TransferItemViewModel.Name), ListSortDirection.Ascending));
        Items.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TransferItemViewModel.GroupName)));
        Items.Filter = MatchesSearch;

        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => !IsBusy);
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false), () => !IsBusy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, CanExport);
        BackupCommand = new AsyncRelayCommand(BackupAsync, () => !IsBusy);
        ChooseFileCommand = new AsyncRelayCommand(ChooseFileAsync, () => !IsBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, CanImport);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, false), () => !IsBusy);
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>Raised after a successful import, so the shell can reload what it shows.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Raised when the view must empty the export passphrase boxes; they cannot be bound.</summary>
    public event EventHandler? ClearExportPassphraseRequested;

    /// <summary>Raised when the view must empty the import passphrase box.</summary>
    public event EventHandler? ClearImportPassphraseRequested;

    /// <summary>Owner for the file dialogs. Set by the window that hosts this model.</summary>
    public System.Windows.Window? OwnerWindow { get; set; }

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand BackupCommand { get; }
    public AsyncRelayCommand ChooseFileCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand CloseCommand { get; }

    // ------------------------------------------------------------------ shared state

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCommands();
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => SetProperty(ref _messageIsError, value);
    }

    // ------------------------------------------------------------------ export

    /// <summary>Connections and multi-configs, grouped and filtered for the picker.</summary>
    public System.ComponentModel.ICollectionView Items { get; }

    public bool IsLoadingItems
    {
        get => _isLoadingItems;
        private set => SetProperty(ref _isLoadingItems, value);
    }

    public string Search
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value ?? string.Empty)) return;

            try
            {
                Items.Refresh();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Filtering the export list failed.", ex);
            }
        }
    }

    public bool ExportEverything
    {
        get => _exportEverything;
        set
        {
            if (!SetProperty(ref _exportEverything, value)) return;

            Raise(nameof(ExportChosenOnly), nameof(CanIncludeSettings));
            ExportCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The inverse of <see cref="ExportEverything"/>, for the second radio button.</summary>
    public bool ExportChosenOnly
    {
        get => !_exportEverything;
        set
        {
            if (value) ExportEverything = false;
        }
    }

    /// <summary>Application settings only travel with a full-library export.</summary>
    public bool CanIncludeSettings => _exportEverything;

    public bool IncludeCredentials
    {
        get => _includeCredentials;
        set
        {
            if (!SetProperty(ref _includeCredentials, value)) return;

            if (!value) ClearExportPassphrase();

            ValidatePassphrase();
            ExportCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IncludeSettings
    {
        get => _includeSettings;
        set => SetProperty(ref _includeSettings, value);
    }

    public string? PassphraseError
    {
        get => _passphraseError;
        private set => SetProperty(ref _passphraseError, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetProperty(ref _selectedCount, value)) OnPropertyChanged(nameof(SelectionText));
        }
    }

    public string SelectionText => _selectedCount == 1 ? "1 selected" : $"{_selectedCount} selected";

    /// <summary>What the last export produced, shown in the success card.</summary>
    public string ExportSummary
    {
        get => _exportSummary;
        private set
        {
            if (SetProperty(ref _exportSummary, value)) OnPropertyChanged(nameof(HasExportResult));
        }
    }

    public string ExportPath
    {
        get => _exportPath;
        private set => SetProperty(ref _exportPath, value);
    }

    public bool HasExportResult => _exportSummary.Length > 0;

    // ------------------------------------------------------------------ import

    public string ImportFileName
    {
        get => _importFileName;
        private set
        {
            if (SetProperty(ref _importFileName, value)) OnPropertyChanged(nameof(HasFile));
        }
    }

    public bool HasFile => _importFileName.Length > 0;

    /// <summary>What the chosen file contains; the view binds its counts directly.</summary>
    public ImportPreview? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value)) Raise(nameof(HasPreview), nameof(PreviewOriginText));
        }
    }

    public bool HasPreview => _preview is not null;

    /// <summary>"Exported by version 1.4.0 on 3 March 2026" - composed here so a file written by
    /// an older build, which may not name a version at all, still reads as a sentence.</summary>
    public string PreviewOriginText
    {
        get
        {
            if (_preview is not { } preview) return string.Empty;

            var version = string.IsNullOrWhiteSpace(preview.AppVersion) ? null : preview.AppVersion.Trim();
            var written = DescribeExportDate(preview.ExportedUtc);

            return (version, written) switch
            {
                (not null, not null) => $"Exported by version {version} on {written}",
                (not null, null) => $"Exported by version {version}",
                (null, not null) => $"Exported on {written}",
                _ => "This file does not say when it was written.",
            };
        }
    }

    public bool NeedsPassphrase
    {
        get => _needsPassphrase;
        private set
        {
            if (SetProperty(ref _needsPassphrase, value)) ImportCommand.RaiseCanExecuteChanged();
        }
    }

    public ImportMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    /// <summary>What the last import did; the view binds its counters and warnings directly.</summary>
    public ImportResult? Result
    {
        get => _result;
        private set
        {
            if (SetProperty(ref _result, value))
                Raise(nameof(HasResult), nameof(AddedCount), nameof(ResultDetailText));
        }
    }

    public bool HasResult => _result is not null;

    /// <summary>Everything the import wrote as new, across all four kinds of entity.</summary>
    public int AddedCount =>
        _result is { } r ? r.GroupsAdded + r.ConnectionsAdded + r.MultiConfigsAdded + r.CredentialsAdded : 0;

    /// <summary>The breakdown behind <see cref="AddedCount"/>, so the three big numbers are not the whole story.</summary>
    public string ResultDetailText
    {
        get
        {
            if (_result is not { } r) return string.Empty;

            var parts = new List<string>(4);
            if (r.ConnectionsAdded > 0) parts.Add(Plural(r.ConnectionsAdded, "connection"));
            if (r.MultiConfigsAdded > 0) parts.Add(Plural(r.MultiConfigsAdded, "multi-config"));
            if (r.GroupsAdded > 0) parts.Add(Plural(r.GroupsAdded, "folder"));
            if (r.CredentialsAdded > 0) parts.Add(Plural(r.CredentialsAdded, "credential"));

            return parts.Count == 0 ? "Nothing new was added." : "Added " + string.Join(", ", parts) + ".";
        }
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Fills the picker from the store. Runs once, when the window is first shown.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        IsLoadingItems = true;
        try
        {
            var connections = await _store.GetConnectionsAsync().ConfigureAwait(true);
            var multiConfigs = await _store.GetMultiConfigsAsync().ConfigureAwait(true);
            var groups = await _store.GetGroupsAsync().ConfigureAwait(true);
            var paths = BuildGroupPaths(groups);

            _suspendSelectionUpdates = true;
            _items.Clear();

            foreach (var connection in connections)
            {
                _items.Add(new TransferItemViewModel(
                    connection.Id,
                    string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name,
                    connection.FullAddress,
                    GroupNameFor(paths, connection.GroupId),
                    isMultiConfig: false,
                    OnItemSelectionChanged));
            }

            foreach (var config in multiConfigs)
            {
                var count = config.Items.Count;
                _items.Add(new TransferItemViewModel(
                    config.Id,
                    string.IsNullOrWhiteSpace(config.Name) ? "Multi-config" : config.Name,
                    count == 1 ? "1 connection" : $"{count} connections",
                    GroupNameFor(paths, config.GroupId),
                    isMultiConfig: true,
                    OnItemSelectionChanged));
            }

            _suspendSelectionUpdates = false;
            UpdateSelection();
            Items.Refresh();
        }
        catch (Exception ex)
        {
            _suspendSelectionUpdates = false;
            AppLog.Error("The export list could not be loaded.", ex);
            ShowMessage($"The library could not be read: {Concise(ex)}", true);
        }
        finally
        {
            IsLoadingItems = false;
        }
    }

    private static Dictionary<Guid, string> BuildGroupPaths(IReadOnlyList<ConnectionGroup> groups)
    {
        var byId = new Dictionary<Guid, ConnectionGroup>(groups.Count);
        foreach (var group in groups) byId[group.Id] = group;

        var paths = new Dictionary<Guid, string>(groups.Count);
        foreach (var group in groups) paths[group.Id] = BuildPath(byId, group);
        return paths;
    }

    private static string BuildPath(Dictionary<Guid, ConnectionGroup> byId, ConnectionGroup group)
    {
        var parts = new List<string>(4);
        var current = group;

        // The depth guard keeps a corrupt parent cycle from spinning here forever.
        for (var depth = 0; depth < 16; depth++)
        {
            parts.Add(string.IsNullOrWhiteSpace(current.Name) ? "Group" : current.Name.Trim());
            if (current.ParentId is not { } parentId || !byId.TryGetValue(parentId, out var parent)) break;
            current = parent;
        }

        parts.Reverse();
        return string.Join(" / ", parts);
    }

    private static string GroupNameFor(Dictionary<Guid, string> paths, Guid? groupId) =>
        groupId is { } id && paths.TryGetValue(id, out var path) ? path : UngroupedName;

    private bool MatchesSearch(object candidate) =>
        _search.Length == 0 || (candidate is TransferItemViewModel item && item.Matches(_search));

    private void OnItemSelectionChanged()
    {
        if (_suspendSelectionUpdates) return;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = 0;
        foreach (var item in _items)
        {
            if (item.IsSelected) count++;
        }

        SelectedCount = count;
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void SetAllSelected(bool selected)
    {
        try
        {
            _suspendSelectionUpdates = true;

            // Only what the search currently shows, so "select all" never reaches further
            // than the list the user is looking at.
            foreach (var item in Items.OfType<TransferItemViewModel>().ToList()) item.IsSelected = selected;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Changing the export selection failed.", ex);
        }
        finally
        {
            _suspendSelectionUpdates = false;
            UpdateSelection();
        }
    }

    // ------------------------------------------------------------------ passphrase

    /// <summary>Called by the view; a PasswordBox cannot be bound.</summary>
    public void SetExportPassphrase(string? value)
    {
        _passphrase = value ?? string.Empty;
        ValidatePassphrase();
        ExportCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by the view; a PasswordBox cannot be bound.</summary>
    public void SetExportPassphraseConfirmation(string? value)
    {
        _passphraseConfirm = value ?? string.Empty;
        ValidatePassphrase();
        ExportCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by the view; a PasswordBox cannot be bound.</summary>
    public void SetImportPassphrase(string? value)
    {
        _importPassphrase = value ?? string.Empty;
        ImportCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Drops the export passphrase from the model and from the two boxes in the view.</summary>
    private void ClearExportPassphrase()
    {
        _passphrase = string.Empty;
        _passphraseConfirm = string.Empty;
        PassphraseError = null;
        SafeRaise(ClearExportPassphraseRequested, "clear the export passphrase boxes");
        ExportCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Raises an event without letting a subscriber's failure escape into the operation that
    /// raised it - a finished export or import must never be reported as a failure.
    /// </summary>
    private void SafeRaise(EventHandler? handler, string what)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A listener failed to {what}.", ex);
        }
    }

    private bool PassphraseIsValid =>
        _passphrase.Length >= MinPassphraseLength &&
        !string.IsNullOrWhiteSpace(_passphrase) &&
        string.Equals(_passphrase, _passphraseConfirm, StringComparison.Ordinal);

    private void ValidatePassphrase()
    {
        if (!_includeCredentials || _passphrase.Length == 0)
        {
            PassphraseError = null;
            return;
        }

        if (_passphrase.Length < MinPassphraseLength)
        {
            PassphraseError = $"Use at least {MinPassphraseLength} characters.";
            return;
        }

        // Mirrors the rule the transfer service enforces, so the export button is simply not
        // offered rather than failing once the file dialog has already been answered.
        if (string.IsNullOrWhiteSpace(_passphrase))
        {
            PassphraseError = "Use something other than spaces.";
            return;
        }

        if (_passphraseConfirm.Length == 0)
        {
            PassphraseError = null;
            return;
        }

        PassphraseError = string.Equals(_passphrase, _passphraseConfirm, StringComparison.Ordinal)
            ? null
            : "The two passphrases do not match.";
    }

    // ------------------------------------------------------------------ export commands

    private bool CanExport() =>
        !IsBusy &&
        (_exportEverything || _selectedCount > 0) &&
        (!_includeCredentials || PassphraseIsValid);

    private async Task ExportAsync()
    {
        if (!CanExport()) return;

        var extension = FileExtension();
        var dialog = new SaveFileDialog
        {
            Title = "Export configurations",
            Filter = $"DYNATEC RDM library (*{extension})|*{extension}|All files (*.*)|*.*",
            FileName = $"DynatecRDM-library-{DateTime.Now:yyyy-MM-dd}{extension}",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (!ShowFileDialog(dialog)) return;

        var path = dialog.FileName;
        if (RefuseDataDirectory(path)) return;

        var everything = _exportEverything;
        var includeCredentials = _includeCredentials;
        var includeSettings = everything && _includeSettings;
        var passphrase = includeCredentials ? _passphrase : null;
        var connectionIds = SelectedIds(multiConfigs: false);
        var multiConfigIds = SelectedIds(multiConfigs: true);

        SetBusy("Building the export...");
        try
        {
            // Task.Run keeps the compression and the re-encryption off the UI thread even if
            // the service does part of its work before its first await.
            var bundle = await Task.Run(() => everything
                    ? _transfer.BuildFullLibraryAsync(includeCredentials, includeSettings, passphrase)
                    : _transfer.BuildBundleAsync(connectionIds, multiConfigIds, includeCredentials, passphrase))
                .ConfigureAwait(true);

            BusyText = "Writing the file...";
            await Task.Run(() => _transfer.ExportAsync(bundle, path)).ConfigureAwait(true);

            // The bundle is what actually reached the file, which is not always what was asked
            // for: a multi-config drags in the connections its entries launch.
            ExportSummary =
                $"Exported {Plural(bundle.Connections.Count, "connection")} and " +
                $"{Plural(bundle.MultiConfigs.Count, "multi-config")}" +
                (bundle.ContainsSecrets ? ", with saved passwords." : ".");
            ExportPath = path;
            ShowMessage($"The export finished: {Path.GetFileName(path)}", false);
            AppLog.Info($"Exported the library to '{path}'.");

            // The passphrase protected one file and has no further use here.
            if (includeCredentials) ClearExportPassphrase();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Exporting the library to '{path}' failed.", ex);
            ShowMessage($"The export failed: {Concise(ex)}", true);
        }
        finally
        {
            ClearBusy();
        }
    }

    private async Task BackupAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up the database",
            Filter = "Database files (*.db)|*.db|All files (*.*)|*.*",
            FileName = $"DynatecRDM-backup-{DateTime.Now:yyyy-MM-dd}.db",
            DefaultExt = ".db",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (!ShowFileDialog(dialog)) return;

        var target = dialog.FileName;
        if (RefuseDataDirectory(target)) return;

        SetBusy("Copying the database...");
        try
        {
            var written = await Task.Run(() => _transfer.BackupDatabaseAsync(target)).ConfigureAwait(true);

            ExportSummary = "The whole database was copied, including everything this dialog can export.";
            ExportPath = string.IsNullOrWhiteSpace(written) ? dialog.FileName : written;
            ShowMessage($"The backup finished: {Path.GetFileName(ExportPath)}", false);
            AppLog.Info($"Backed the database up to '{ExportPath}'.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Backing the database up failed.", ex);
            ShowMessage($"The backup failed: {Concise(ex)}", true);
        }
        finally
        {
            ClearBusy();
        }
    }

    private List<Guid> SelectedIds(bool multiConfigs)
    {
        var ids = new List<Guid>();
        foreach (var item in _items)
        {
            if (item.IsSelected && item.IsMultiConfig == multiConfigs) ids.Add(item.Id);
        }
        return ids;
    }

    // ------------------------------------------------------------------ import commands

    private bool CanImport() => !IsBusy && _bundle is not null && (!_needsPassphrase || _importPassphrase.Length > 0);

    private async Task ChooseFileAsync()
    {
        var extension = FileExtension();
        var dialog = new OpenFileDialog
        {
            Title = "Choose a configuration file",
            Filter = $"DYNATEC RDM library (*{extension})|*{extension}|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!ShowFileDialog(dialog)) return;

        var path = dialog.FileName;

        SetBusy("Reading the file...");
        try
        {
            Result = null;

            var bundle = await Task.Run(() => _transfer.ReadAsync(path)).ConfigureAwait(true);
            var preview = await Task.Run(() => _transfer.PreviewAsync(bundle)).ConfigureAwait(true);

            _bundle = bundle;
            Preview = preview;
            NeedsPassphrase = preview.NeedsPassphrase;
            ImportFileName = Path.GetFileName(path);

            _importPassphrase = string.Empty;
            SafeRaise(ClearImportPassphraseRequested, "clear the import passphrase box");

            if (NeedsPassphrase)
                ShowMessage("This file carries saved passwords. Enter the passphrase it was exported with.", false);
            else
                ClearMessage();
        }
        catch (Exception ex)
        {
            _bundle = null;
            Preview = null;
            NeedsPassphrase = false;
            ImportFileName = string.Empty;

            AppLog.Error($"Reading the configuration file '{path}' failed.", ex);
            ShowMessage($"That file could not be read: {Concise(ex)}", true);
        }
        finally
        {
            ImportCommand.RaiseCanExecuteChanged();
            ClearBusy();
        }
    }

    private async Task ImportAsync()
    {
        if (_bundle is not { } bundle) return;

        var passphrase = _needsPassphrase ? _importPassphrase : null;

        SetBusy("Importing...");
        try
        {
            var mode = _mode;
            var result = await Task.Run(() => _transfer.ImportAsync(bundle, mode, passphrase)).ConfigureAwait(true);

            Result = result;

            var notes = result.Warnings?.Count ?? 0;
            ShowMessage(
                notes == 0 ? "The import finished." : $"The import finished, with {Plural(notes, "note")} below.",
                false);
            AppLog.Info($"Imported '{_importFileName}' using mode {_mode}.");

            // The file has been used. Letting go of it clears the passphrase from memory and makes
            // importing the same file twice by accident impossible; the result card stays on show.
            // A failed import keeps everything, so a mistyped passphrase can simply be corrected.
            _bundle = null;
            _importPassphrase = string.Empty;
            NeedsPassphrase = false;
            Preview = null;
            ImportFileName = string.Empty;

            // Past this point the library has changed. A listener that throws is its own problem
            // and must not turn a finished import into "the import failed".
            SafeRaise(ClearImportPassphraseRequested, "clear the import passphrase box");
            SafeRaise(LibraryChanged, "reload the library");
        }
        catch (Exception ex)
        {
            Result = null;
            AppLog.Error($"Importing '{_importFileName}' failed.", ex);
            ShowMessage($"The import failed: {Concise(ex)}", true);
        }
        finally
        {
            ClearBusy();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string FileExtension()
    {
        var extension = ConfigTransfer.FileExtension;
        if (string.IsNullOrWhiteSpace(extension)) return ".drdm";

        extension = extension.Trim();
        return extension[0] == '.' ? extension : "." + extension;
    }

    /// <summary>
    /// Refuses a target inside the application's own data folder and says why. That folder holds
    /// the live database, and an export written over it would destroy the very library the user
    /// is trying to copy. Returns true when the caller must stop.
    /// </summary>
    private bool RefuseDataDirectory(string path)
    {
        if (!IsInsideDataDirectory(path)) return false;

        ShowMessage(
            "Choose a folder outside DYNATEC RDM's own data folder - that folder holds the live database.",
            true);
        return true;
    }

    private static bool IsInsideDataDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppLog.DataDirectory));
            var full = Path.GetFullPath(path);

            return string.Equals(full, data, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // An unusable path fails on its own in a moment; this check must not be what breaks.
            AppLog.Warn("Comparing the chosen path with the data folder failed.", ex);
            return false;
        }
    }

    private bool ShowFileDialog(FileDialog dialog)
    {
        try
        {
            var owner = OwnerWindow;
            var result = owner is { IsVisible: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return result == true;
        }
        catch (Exception ex)
        {
            AppLog.Error("The file dialog could not be opened.", ex);
            ShowMessage($"The file dialog could not be opened: {Concise(ex)}", true);
            return false;
        }
    }

    private void SetBusy(string text)
    {
        BusyText = text;
        IsBusy = true;
    }

    private void ClearBusy()
    {
        IsBusy = false;
        BusyText = string.Empty;
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }

    private void ClearMessage()
    {
        MessageIsError = false;
        Message = string.Empty;
    }

    private void RaiseCommands()
    {
        SelectAllCommand.RaiseCanExecuteChanged();
        SelectNoneCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        BackupCommand.RaiseCanExecuteChanged();
        ChooseFileCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The export stamp as a local date, or null when the file carries no usable one.</summary>
    private static string? DescribeExportDate(DateTime exportedUtc)
    {
        if (exportedUtc == default || exportedUtc.Year < 2000) return null;

        var utc = exportedUtc.Kind == DateTimeKind.Utc
            ? exportedUtc
            : DateTime.SpecifyKind(exportedUtc, DateTimeKind.Utc);

        return utc.ToLocalTime().ToString("d MMMM yyyy");
    }

    private static string Concise(Exception ex)
    {
        var text = ex.Message?.Trim();
        if (string.IsNullOrEmpty(text)) return "the reason is in the application log";

        text = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength].TrimEnd() + "...";
    }
}

/// <summary>One selectable row in the export picker.</summary>
public sealed class TransferItemViewModel : ObservableObject
{
    private readonly Action? _selectionChanged;
    private bool _isSelected;

    public TransferItemViewModel(
        Guid id,
        string? name,
        string? detail,
        string? groupName,
        bool isMultiConfig,
        Action? selectionChanged = null)
    {
        Id = id;
        Name = name ?? string.Empty;
        Detail = detail ?? string.Empty;
        GroupName = string.IsNullOrWhiteSpace(groupName) ? "Ungrouped" : groupName;
        IsMultiConfig = isMultiConfig;
        _selectionChanged = selectionChanged;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public string GroupName { get; }
    public bool IsMultiConfig { get; }

    public string Kind => IsMultiConfig ? "Multi-config" : "Connection";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) _selectionChanged?.Invoke();
        }
    }

    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
        Detail.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
        GroupName.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}
