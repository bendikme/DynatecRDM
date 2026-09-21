using System.Collections.ObjectModel;
using System.Text;
using DynatecRDM.Models;
using DynatecRDM.Services;
using DynatecRDM.Views;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Backs the credential manager: the application's own credential sets on top, and the
/// Windows Credential Vault entries (TERMSRV/&lt;host&gt;) that mstsc actually reads underneath.
/// Plain passwords never leave this class - they go straight into ISecretProtector or into a
/// vault write and are dropped as soon as the operation finishes.
/// </summary>
public sealed class CredentialsViewModel : ObservableObject
{
    private readonly AppServices _services;

    private CredentialSet? _selected;
    private bool _loading;

    private string _editName = string.Empty;
    private string _editDomain = string.Empty;
    private string _editUsername = string.Empty;
    private string _editNotes = string.Empty;
    private bool _editIsDefault;

    private bool _hasStoredPassword;
    private bool _isEditingPassword;
    private bool _passwordEdited;
    private string? _pendingPassword;

    private bool _isDirty;
    private bool _isBusy;
    private string _status = string.Empty;
    private bool _statusIsError;

    private string _vaultHost = string.Empty;
    private StoredCredentialInfo? _selectedVaultEntry;
    private bool _showCommands;
    private bool _useCmdKey;

    public CredentialsViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _useCmdKey = services.Settings.VaultWriteMethod == VaultWriteMethod.CmdKey;

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        NewCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync, () => !IsBusy && Selected is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !IsBusy && Selected is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        RevertCommand = new RelayCommand(Revert, () => Selected is not null && IsDirty);
        ChangePasswordCommand = new RelayCommand(BeginPasswordChange, () => Selected is not null && !IsEditingPassword);
        KeepPasswordCommand = new RelayCommand(CancelPasswordChange, () => IsEditingPassword && HasStoredPassword);
        RefreshVaultCommand = new AsyncRelayCommand(RefreshVaultAsync, () => !IsBusy);
        DeleteVaultEntryCommand = new AsyncRelayCommand(DeleteVaultEntryAsync, p => !IsBusy && p is StoredCredentialInfo);
        ApplyToHostCommand = new AsyncRelayCommand(ApplyToHostAsync, () => !IsBusy && Selected is not null);
        CopyCommandsCommand = new RelayCommand(CopyCommands);
        ToggleCommandsCommand = new RelayCommand(() => ShowCommands = !ShowCommands);
    }

    // ------------------------------------------------------------------ events

    /// <summary>Asks the view to clear its PasswordBox without counting it as an edit.</summary>
    public event EventHandler? PasswordResetRequested;

    /// <summary>Asks the view to put the caret in the name field.</summary>
    public event EventHandler? EditRequested;

    // ------------------------------------------------------------- collections

    public ObservableCollection<CredentialSet> Items { get; } = new();

    public ObservableCollection<StoredCredentialInfo> VaultEntries { get; } = new();

    // ---------------------------------------------------------------- commands

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand NewCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand ChangePasswordCommand { get; }
    public RelayCommand KeepPasswordCommand { get; }
    public AsyncRelayCommand RefreshVaultCommand { get; }
    public AsyncRelayCommand DeleteVaultEntryCommand { get; }
    public AsyncRelayCommand ApplyToHostCommand { get; }
    public RelayCommand CopyCommandsCommand { get; }
    public RelayCommand ToggleCommandsCommand { get; }

    // --------------------------------------------------------------- selection

    public CredentialSet? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            LoadEditor(value);
            Raise(nameof(HasSelection), nameof(SelectedSummary), nameof(GenericCommandText));
            RefreshCommands();
        }
    }

    public bool HasSelection => _selected is not null;

    public string SelectedSummary => _selected is null ? "Nothing selected" : _selected.Name;

    // ------------------------------------------------------------------ editor

    public string EditName
    {
        get => _editName;
        set { if (SetProperty(ref _editName, value)) MarkDirty(); }
    }

    public string EditDomain
    {
        get => _editDomain;
        set { if (SetProperty(ref _editDomain, value)) MarkDirty(); }
    }

    public string EditUsername
    {
        get => _editUsername;
        set { if (SetProperty(ref _editUsername, value)) MarkDirty(); }
    }

    public string EditNotes
    {
        get => _editNotes;
        set { if (SetProperty(ref _editNotes, value)) MarkDirty(); }
    }

    public bool EditIsDefault
    {
        get => _editIsDefault;
        set { if (SetProperty(ref _editIsDefault, value)) MarkDirty(); }
    }

    public bool HasStoredPassword
    {
        get => _hasStoredPassword;
        private set
        {
            if (!SetProperty(ref _hasStoredPassword, value)) return;
            Raise(nameof(ShowStoredPasswordHint), nameof(ShowPasswordBox));
            RefreshCommands();
        }
    }

    public bool IsEditingPassword
    {
        get => _isEditingPassword;
        private set
        {
            if (!SetProperty(ref _isEditingPassword, value)) return;
            Raise(nameof(ShowStoredPasswordHint), nameof(ShowPasswordBox));
            RefreshCommands();
        }
    }

    /// <summary>A password is on file and the user has not asked to replace it.</summary>
    public bool ShowStoredPasswordHint => _hasStoredPassword && !_isEditingPassword;

    /// <summary>The PasswordBox is the live control right now.</summary>
    public bool ShowPasswordBox => !_hasStoredPassword || _isEditingPassword;

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (SetProperty(ref _isDirty, value)) RefreshCommands(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); }
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

    // ------------------------------------------------------------------- vault

    public string VaultHost
    {
        get => _vaultHost;
        set
        {
            if (!SetProperty(ref _vaultHost, value)) return;
            Raise(nameof(DeleteCommandText), nameof(GenericCommandText));
        }
    }

    public StoredCredentialInfo? SelectedVaultEntry
    {
        get => _selectedVaultEntry;
        set
        {
            if (!SetProperty(ref _selectedVaultEntry, value)) return;
            if (value is not null && !string.IsNullOrWhiteSpace(value.Host)) VaultHost = value.Host;
            RefreshCommands();
        }
    }

    public bool ShowCommands
    {
        get => _showCommands;
        set => SetProperty(ref _showCommands, value);
    }

    public bool HasVaultEntries => VaultEntries.Count > 0;

    public string VaultSummary => VaultEntries.Count switch
    {
        0 => "No cached remote-desktop logins found.",
        1 => "1 cached host.",
        _ => $"{VaultEntries.Count} cached hosts.",
    };

    /// <summary>True when the vault write goes through cmdkey.exe instead of the credential API.</summary>
    public bool UseCmdKey
    {
        get => _useCmdKey;
        set
        {
            if (!SetProperty(ref _useCmdKey, value)) return;
            OnPropertyChanged(nameof(WriteMethodExplanation));
            PersistWriteMethod();
        }
    }

    public VaultWriteMethod WriteMethod =>
        _useCmdKey ? VaultWriteMethod.CmdKey : VaultWriteMethod.NativeCredentialApi;

    public string WriteMethodExplanation => _useCmdKey
        ? "cmdkey.exe is the documented manual route, but the password is visible on its command line while it runs."
        : "The Windows credential API writes in-process, so the password never reaches a command line.";

    // -------------------------------------------------------- command previews

    public string ListCommandText => "cmdkey /list:TERMSRV/*";

    public string DeleteCommandText => $"cmdkey /delete:TERMSRV/{HostToken}";

    public string GenericCommandText =>
        $"cmdkey /generic:TERMSRV/{HostToken} /user:{UserToken} /pass:<password>";

    private string HostToken => string.IsNullOrWhiteSpace(_vaultHost) ? "<host>" : _vaultHost.Trim();

    private string UserToken
    {
        get
        {
            var set = _selected;
            return set is null || string.IsNullOrWhiteSpace(set.Username) ? "<username>" : set.GetLogonName(_vaultHost);
        }
    }

    // -------------------------------------------------------------- operations

    /// <summary>Fills both lists. Never throws.</summary>
    public async Task LoadAsync()
    {
        SyncWriteMethodFromSettings();
        await ReloadSetsAsync(_selected?.Id).ConfigureAwait(true);
        await ReloadVaultAsync().ConfigureAwait(true);
    }

    /// <summary>Called by the view whenever the user types in the PasswordBox.</summary>
    public void NotePasswordEdited(string? password)
    {
        _passwordEdited = true;
        _pendingPassword = password;
        if (!IsEditingPassword) IsEditingPassword = true;
        MarkDirty();
    }

    private async Task CreateAsync()
    {
        IsBusy = true;
        try
        {
            var set = new CredentialSet { Name = UniqueName("New credential") };
            await _services.Store.UpsertCredentialSetAsync(set).ConfigureAwait(true);
            await ReloadSetsAsync(set.Id).ConfigureAwait(true);
            SetStatus("New credential set created. Fill in the details and save.", false);
            EditRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("Creating a credential set failed.", ex);
            SetStatus("The credential set could not be created. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DuplicateAsync()
    {
        var source = Selected;
        if (source is null) return;

        IsBusy = true;
        try
        {
            var copy = source.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = UniqueName(source.Name + " copy");
            copy.IsDefault = false;
            copy.CreatedUtc = DateTime.UtcNow;
            copy.ModifiedUtc = DateTime.UtcNow;

            await _services.Store.UpsertCredentialSetAsync(copy).ConfigureAwait(true);
            await ReloadSetsAsync(copy.Id).ConfigureAwait(true);
            SetStatus($"Duplicated as '{copy.Name}'.", false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Duplicating the credential set failed.", ex);
            SetStatus("The credential set could not be duplicated. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        var set = Selected;
        if (set is null) return;

        var confirmed = Confirm(
            "Delete credential set",
            $"Delete '{set.Name}'? Connections that use it will fall back to prompting for a password.",
            "Delete");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await _services.Store.DeleteCredentialSetAsync(set.Id).ConfigureAwait(true);
            await ReloadSetsAsync(null).ConfigureAwait(true);
            SetStatus($"Deleted '{set.Name}'.", false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Deleting the credential set failed.", ex);
            SetStatus("The credential set could not be deleted. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() =>
        !IsBusy && Selected is not null && IsDirty && !string.IsNullOrWhiteSpace(EditName);

    private async Task SaveAsync()
    {
        var current = Selected;
        if (current is null) return;

        var name = EditName.Trim();
        if (name.Length == 0)
        {
            SetStatus("Give the credential set a name.", true);
            return;
        }

        IsBusy = true;
        try
        {
            var set = current.Clone();
            set.Name = name;
            set.Domain = Blank(EditDomain);
            set.Username = EditUsername.Trim();
            set.Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes;
            set.IsDefault = EditIsDefault;
            set.ModifiedUtc = DateTime.UtcNow;

            if (_passwordEdited)
            {
                var plain = _pendingPassword ?? string.Empty;
                var protector = _services.Protector;
                set.ProtectedPassword = plain.Length == 0
                    ? null
                    : await Task.Run(() => protector.Protect(plain)).ConfigureAwait(true);
            }

            await _services.Store.UpsertCredentialSetAsync(set).ConfigureAwait(true);

            if (set.IsDefault) await ClearOtherDefaultsAsync(set.Id).ConfigureAwait(true);

            _pendingPassword = null;
            _passwordEdited = false;

            await ReloadSetsAsync(set.Id).ConfigureAwait(true);
            SetStatus($"Saved '{set.Name}'.", false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving the credential set failed.", ex);
            SetStatus("The credential set could not be saved. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearOtherDefaultsAsync(Guid keepId)
    {
        var others = Items.Where(i => i.Id != keepId && i.IsDefault).ToList();
        foreach (var other in others)
        {
            var cleared = other.Clone();
            cleared.IsDefault = false;
            cleared.ModifiedUtc = DateTime.UtcNow;
            await _services.Store.UpsertCredentialSetAsync(cleared).ConfigureAwait(true);
        }
    }

    private void Revert()
    {
        LoadEditor(Selected);
        SetStatus("Changes discarded.", false);
    }

    private void BeginPasswordChange()
    {
        _passwordEdited = false;
        _pendingPassword = null;
        IsEditingPassword = true;
        PasswordResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelPasswordChange()
    {
        _passwordEdited = false;
        _pendingPassword = null;
        IsEditingPassword = false;
        PasswordResetRequested?.Invoke(this, EventArgs.Empty);
        RefreshCommands();
    }

    // -------------------------------------------------------------- vault work

    private async Task RefreshVaultAsync()
    {
        IsBusy = true;
        try
        {
            await ReloadVaultAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadVaultAsync()
    {
        try
        {
            var vault = _services.Credentials;
            var keep = _selectedVaultEntry?.TargetName;

            var entries = await Task.Run(() => vault.ListTermsrvCredentials()).ConfigureAwait(true);

            VaultEntries.Clear();
            for (var i = 0; i < entries.Count; i++) VaultEntries.Add(entries[i]);

            Raise(nameof(HasVaultEntries), nameof(VaultSummary));

            if (keep is not null)
            {
                SelectedVaultEntry = VaultEntries.FirstOrDefault(
                    e => string.Equals(e.TargetName, keep, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading the Windows credential vault failed.", ex);
            SetStatus("The Windows credential vault could not be read. See the log for details.", true);
        }
    }

    private async Task DeleteVaultEntryAsync(object? parameter)
    {
        if (parameter is not StoredCredentialInfo entry) return;

        var confirmed = Confirm(
            "Delete cached login",
            $"Remove the cached Windows login for {entry.TargetName}? The next connection to {entry.Host} will ask for a password.",
            "Delete");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var vault = _services.Credentials;
            var method = WriteMethod;
            var host = entry.Host;

            var removed = await Task.Run(() => vault.DeleteCredential(host, method)).ConfigureAwait(true);

            SetStatus(
                removed ? $"Removed {entry.TargetName}." : $"Nothing was stored for {entry.TargetName}.",
                !removed);

            await ReloadVaultAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Deleting the vault entry for {entry.TargetName} failed.", ex);
            SetStatus("The cached login could not be removed. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyToHostAsync()
    {
        var set = Selected;
        if (set is null)
        {
            SetStatus("Select a credential set first.", true);
            return;
        }

        if (IsDirty)
        {
            SetStatus("Save the credential set before applying it to a host.", true);
            return;
        }

        var host = VaultHost.Trim();
        if (host.Length == 0)
        {
            var typed = Prompt(
                "Apply to host",
                "Which host should this login be cached for? Enter the name exactly as the connection uses it.");
            host = typed?.Trim() ?? string.Empty;
            if (host.Length == 0) return;
            VaultHost = host;
        }

        if (string.IsNullOrWhiteSpace(set.Username))
        {
            SetStatus("The selected set has no user name.", true);
            return;
        }

        if (!set.HasPassword)
        {
            SetStatus("The selected set has no stored password. Add one and save first.", true);
            return;
        }

        IsBusy = true;
        try
        {
            var protector = _services.Protector;
            var vault = _services.Credentials;
            var method = WriteMethod;
            var blob = set.ProtectedPassword;
            var user = set.GetLogonName(host);

            var written = await Task.Run(() =>
            {
                var plain = blob is null ? null : protector.Unprotect(blob);
                return plain is not null && vault.SaveCredential(host, user, plain, method);
            }).ConfigureAwait(true);

            SetStatus(
                written
                    ? $"Stored TERMSRV/{host} for {user}."
                    : $"Writing TERMSRV/{host} failed. See the log for details.",
                !written);

            await ReloadVaultAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Applying the credential to {host} failed.", ex);
            SetStatus("The cached login could not be written. See the log for details.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyCommands()
    {
        var text = new StringBuilder()
            .AppendLine(ListCommandText)
            .AppendLine(DeleteCommandText)
            .Append(GenericCommandText)
            .ToString();

        var copied = TryCopy(text);
        SetStatus(
            copied
                ? "Commands copied. The password stays a placeholder - no secret was copied."
                : "The clipboard is not available right now.",
            !copied);
    }

    private static bool TryCopy(string text)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Writing to the clipboard failed.", ex);
            }
        }

        return false;
    }

    // ----------------------------------------------------------------- helpers

    private async Task ReloadSetsAsync(Guid? selectId)
    {
        try
        {
            var sets = await _services.Store.GetCredentialSetsAsync().ConfigureAwait(true);

            Items.Clear();
            foreach (var set in sets.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                Items.Add(set);

            var target = selectId is Guid id ? Items.FirstOrDefault(s => s.Id == id) : null;
            Selected = target ?? Items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading the credential sets failed.", ex);
            SetStatus("The credential sets could not be read. See the log for details.", true);
        }
    }

    private void LoadEditor(CredentialSet? set)
    {
        _loading = true;
        try
        {
            EditName = set?.Name ?? string.Empty;
            EditDomain = set?.Domain ?? string.Empty;
            EditUsername = set?.Username ?? string.Empty;
            EditNotes = set?.Notes ?? string.Empty;
            EditIsDefault = set?.IsDefault ?? false;

            HasStoredPassword = set?.HasPassword ?? false;
            IsEditingPassword = set is not null && !set.HasPassword;

            _passwordEdited = false;
            _pendingPassword = null;
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
        PasswordResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MarkDirty()
    {
        if (_loading) return;
        if (_isDirty)
        {
            // Still re-evaluate: Save also depends on the name being non-blank.
            RefreshCommands();
            return;
        }

        IsDirty = true;
    }

    private string UniqueName(string preferred)
    {
        var baseName = string.IsNullOrWhiteSpace(preferred) ? "New credential" : preferred.Trim();
        if (!Items.Any(i => string.Equals(i.Name, baseName, StringComparison.CurrentCultureIgnoreCase)))
            return baseName;

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!Items.Any(i => string.Equals(i.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
                return candidate;
        }

        return baseName;
    }

    private static string? Blank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetStatus(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }

    private void SyncWriteMethodFromSettings()
    {
        var fromSettings = _services.Settings.VaultWriteMethod == VaultWriteMethod.CmdKey;
        if (fromSettings == _useCmdKey) return;

        _useCmdKey = fromSettings;
        Raise(nameof(UseCmdKey), nameof(WriteMethodExplanation));
    }

    private void PersistWriteMethod()
    {
        try
        {
            _services.Settings.VaultWriteMethod = WriteMethod;
            _ = PersistSettingsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Storing the vault write method failed.", ex);
        }
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _services.SaveSettingsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Saving settings failed.", ex);
        }
    }

    private void RefreshCommands()
    {
        NewCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        ChangePasswordCommand.RaiseCanExecuteChanged();
        KeepPasswordCommand.RaiseCanExecuteChanged();
        RefreshVaultCommand.RaiseCanExecuteChanged();
        DeleteVaultEntryCommand.RaiseCanExecuteChanged();
        ApplyToHostCommand.RaiseCanExecuteChanged();
    }

    private static bool Confirm(string title, string message, string confirmText)
    {
        try
        {
            var dialog = new ConfirmDialog(title, message, confirmText) { Owner = ActiveWindow() };
            if (dialog.Owner is null)
                dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            AppLog.Error("The confirmation dialog could not be shown.", ex);
            return false;
        }
    }

    private static string? Prompt(string title, string message, string? initial = null)
    {
        try
        {
            var dialog = new InputDialog(title, message, initial) { Owner = ActiveWindow() };
            if (dialog.Owner is null)
                dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            return dialog.ShowDialog() == true ? dialog.Value : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("The input dialog could not be shown.", ex);
            return null;
        }
    }

    private static System.Windows.Window? ActiveWindow()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return null;

            foreach (System.Windows.Window window in app.Windows)
            {
                if (window.IsActive) return window;
            }

            return app.MainWindow is { IsVisible: true } main ? main : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Resolving the active window failed.", ex);
            return null;
        }
    }
}
