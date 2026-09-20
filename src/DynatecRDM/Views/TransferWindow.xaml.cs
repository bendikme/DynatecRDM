using System.Windows;
using System.Windows.Controls;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The export / import dialog. The view model does the work; this class owns the three
/// password boxes, whose contents cannot be data-bound, and hands them over on every keystroke.
/// </summary>
public partial class TransferWindow : Window
{
    private readonly TransferViewModel _vm;
    private bool _closing;

    public TransferWindow(TransferViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));

        InitializeComponent();

        DataContext = _vm;
        _vm.OwnerWindow = this;
        _vm.RequestClose += OnRequestClose;
        _vm.ClearExportPassphraseRequested += OnClearExportPassphrase;
        _vm.ClearImportPassphraseRequested += OnClearImportPassphrase;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _vm.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("The export list could not be loaded.", ex);
        }
    }

    /// <summary>
    /// Refuses to close while a file is being written or a library imported. The busy overlay is
    /// on screen saying what is happening, and tearing the dialog down mid-write helps nobody.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (!_vm.IsBusy) return;

            e.Cancel = true;
            AppLog.Debug_("The transfer dialog stayed open: an export or import is still running.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Deciding whether the transfer dialog may close failed.", ex);
        }
    }

    private void OnExportPassphraseChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is PasswordBox box) _vm.SetExportPassphrase(box.Password);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the export passphrase failed.", ex);
        }
    }

    private void OnExportPassphraseConfirmChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is PasswordBox box) _vm.SetExportPassphraseConfirmation(box.Password);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the repeated export passphrase failed.", ex);
        }
    }

    private void OnImportPassphraseChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is PasswordBox box) _vm.SetImportPassphrase(box.Password);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the import passphrase failed.", ex);
        }
    }

    private void OnClearExportPassphrase(object? sender, EventArgs e)
    {
        try
        {
            ExportPassphraseBox.Clear();
            ExportPassphraseConfirmBox.Clear();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clearing the export passphrase boxes failed.", ex);
        }
    }

    private void OnClearImportPassphrase(object? sender, EventArgs e)
    {
        try
        {
            ImportPassphraseBox.Clear();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clearing the import passphrase box failed.", ex);
        }
    }

    private void OnRequestClose(object? sender, bool accepted)
    {
        if (_closing) return;
        _closing = true;

        try
        {
            // On a modal dialog this closes the window; a modeless one throws instead.
            DialogResult = accepted;
        }
        catch (InvalidOperationException)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn("The transfer dialog could not be closed.", ex);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing the transfer dialog failed.", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _vm.RequestClose -= OnRequestClose;
            _vm.ClearExportPassphraseRequested -= OnClearExportPassphrase;
            _vm.ClearImportPassphraseRequested -= OnClearImportPassphrase;
            _vm.OwnerWindow = null;
            Closing -= OnClosing;
            Closed -= OnClosed;

            // The passphrases were only ever needed while the dialog was open.
            ExportPassphraseBox.Clear();
            ExportPassphraseConfirmBox.Clear();
            ImportPassphraseBox.Clear();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tearing down the transfer dialog failed.", ex);
        }
    }
}
