using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The multi-config editor. Everything it does lives in
/// <see cref="MultiConfigEditorViewModel"/>; this class only wires the window up to it.
/// </summary>
public partial class MultiConfigEditorWindow : Window
{
    private readonly MultiConfigEditorViewModel _viewModel;
    private bool _closing;

    public MultiConfigEditorWindow(MultiConfigEditorViewModel vm)
    {
        _viewModel = vm ?? throw new ArgumentNullException(nameof(vm));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("The multi-config editor could not finish loading.", ex);
        }
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        if (_closing) return;
        _closing = true;

        try
        {
            // Set on a modal dialog this closes the window; a modeless one throws instead.
            DialogResult = saved;
        }
        catch (InvalidOperationException)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn("The multi-config editor could not be closed.", ex);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing the multi-config editor failed.", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _viewModel.RequestClose -= OnRequestClose;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Detach();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tidying up the multi-config editor failed.", ex);
        }
    }

    /// <summary>Another item's properties start at the top, not wherever the last one was scrolled to.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MultiConfigEditorViewModel.SelectedItem)) return;
        try
        {
            PropertiesScroller.ScrollToTop();
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Scrolling the item properties to the top failed: {ex.Message}");
        }
    }

    private void OnPickerOpened(object? sender, EventArgs e)
    {
        try
        {
            PickerSearchBox.Focus();
            Keyboard.Focus(PickerSearchBox);
            PickerSearchBox.SelectAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The connection picker could not take focus.", ex);
        }
    }

    private void OnPickerItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_viewModel.AddSelectedCommand.CanExecute(null))
                _viewModel.AddSelectedCommand.Execute(null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Adding the picked connection failed.", ex);
        }
    }
}
