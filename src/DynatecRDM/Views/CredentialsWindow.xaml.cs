using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// Credential manager shell. Everything here is wiring: the PasswordBox cannot be bound, so
/// its edits are forwarded to the view model explicitly and programmatic clears are suppressed
/// so an untouched box never counts as "the user cleared the password".
/// </summary>
public partial class CredentialsWindow : Window
{
    private readonly CredentialsViewModel _vm;
    private bool _suppressPasswordChanged;

    public CredentialsWindow(CredentialsViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));

        InitializeComponent();
        DataContext = _vm;

        _vm.PasswordResetRequested += OnPasswordResetRequested;
        _vm.EditRequested += OnEditRequested;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm.LoadCommand.CanExecute(null)) _vm.LoadCommand.Execute(null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Loading the credential manager failed.", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _vm.PasswordResetRequested -= OnPasswordResetRequested;
            _vm.EditRequested -= OnEditRequested;
            Loaded -= OnLoaded;
            Closed -= OnClosed;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Detaching the credential manager handlers failed.", ex);
        }
    }

    /// <summary>
    /// Notes commits on lost focus, which Ctrl+S would skip. Pushing the focused box to its
    /// source first means the shortcut saves what is on screen.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.S || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            if (Keyboard.FocusedElement is not TextBox box) return;

            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Committing the focused field failed.", ex);
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordChanged) return;

        try
        {
            if (sender is PasswordBox box) _vm.NotePasswordEdited(box.Password);
        }
        catch (Exception ex)
        {
            AppLog.Error("Recording the password edit failed.", ex);
        }
    }

    /// <summary>Clears the box without the clear being read as an edit.</summary>
    private void OnPasswordResetRequested(object? sender, EventArgs e)
    {
        try
        {
            _suppressPasswordChanged = true;
            PasswordInput.Clear();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clearing the password box failed.", ex);
        }
        finally
        {
            _suppressPasswordChanged = false;
        }

        if (!_vm.IsEditingPassword) return;

        // The box may still be collapsed at this point; focus once layout has caught up.
        try
        {
            Dispatcher.BeginInvoke(new Action(FocusPasswordBox), DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Focusing the password box failed.", ex);
        }
    }

    private void FocusPasswordBox()
    {
        try
        {
            if (PasswordInput.IsVisible) PasswordInput.Focus();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Focusing the password box failed.", ex);
        }
    }

    private void OnEditRequested(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(FocusNameBox), DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Focusing the name box failed.", ex);
        }
    }

    private void FocusNameBox()
    {
        try
        {
            if (!NameBox.IsVisible) return;
            NameBox.Focus();
            NameBox.SelectAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Focusing the name box failed.", ex);
        }
    }
}
