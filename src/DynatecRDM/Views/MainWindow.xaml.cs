using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The main shell. Pure wiring: the window hands itself to the view model so dialogs get an
/// owner and the saved placement can be applied, and forwards the two view-only requests
/// (focus the search box, persist the window rectangle).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.FocusSearchRequested += OnFocusSearchRequested;
        _viewModel.PropertyChanged += OnHistoryViewModelChanged;

        try
        {
            _viewModel.Attach(this);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The main window could not be attached to its view model.", ex);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewModel.RefreshSnapshotsIfStale();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try
        {
            _viewModel.SaveWindowPlacement();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The window placement could not be saved.", ex);
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Only reached when the close was not cancelled (the app cancels it while "close to tray"
    /// is on), so this is the one place where the window is really going away. Everything the
    /// window or its view model hung off a long-lived service is released here.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _viewModel.FocusSearchRequested -= OnFocusSearchRequested;
            _viewModel.PropertyChanged -= OnHistoryViewModelChanged;
            _viewModel.Detach();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The main window could not be torn down cleanly.", ex);
        }

        base.OnClosed(e);
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusSearchBox));
        }
        catch (Exception ex)
        {
            AppLog.Warn("The search box could not be focused.", ex);
        }
    }

    private void FocusSearchBox()
    {
        try
        {
            TextBox box = SearchBox;
            if (box is null) return;

            box.Focus();
            box.SelectAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The search box could not take focus.", ex);
        }
    }
}
