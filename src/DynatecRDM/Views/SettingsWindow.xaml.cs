using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// Settings dialog shell. The view model owns every decision; this only starts the initial
/// load and turns its close request into a dialog result.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));

        InitializeComponent();
        DataContext = _vm;

        _vm.CloseRequested += OnCloseRequested;

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
            AppLog.Error("Loading the settings dialog failed.", ex);
        }
    }

    /// <summary>
    /// The number fields commit on lost focus, which a keyboard save would skip. Pushing the
    /// focused box to its source first means Enter and Ctrl+S store what is on screen.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            var isSaveKey = e.Key == Key.Enter
                || (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
            if (!isSaveKey) return;

            if (Keyboard.FocusedElement is not TextBox box) return;
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Committing the focused field failed.", ex);
        }
    }

    private void OnCloseRequested(object? sender, bool saved)
    {
        try
        {
            DialogResult = saved;
            return;
        }
        catch (Exception ex)
        {
            // Only reachable when the window was not shown with ShowDialog.
            AppLog.Debug_($"The settings dialog result could not be set: {ex.Message}");
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Closing the settings window failed.", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _vm.CloseRequested -= OnCloseRequested;
            Loaded -= OnLoaded;
            Closed -= OnClosed;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Detaching the settings handlers failed.", ex);
        }
    }
}
