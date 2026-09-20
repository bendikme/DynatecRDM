using System.Windows;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// Shell for the connection editor. Everything here is wiring: the view model owns the
/// behaviour, the window only turns its close request into a dialog result and feeds the
/// monitor map the size it has to draw into.
/// </summary>
public partial class ConnectionEditorWindow : Window
{
    private readonly ConnectionEditorViewModel _vm;

    public ConnectionEditorWindow(ConnectionEditorViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));

        InitializeComponent();
        DataContext = _vm;

        _vm.RequestClose += OnRequestClose;
        Closed += OnClosed;
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        try
        {
            DialogResult = saved;
        }
        catch (InvalidOperationException)
        {
            // Opened with Show() instead of ShowDialog(), so there is no dialog result to set.
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Closing the connection editor failed.", ex);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing the connection editor failed.", ex);
        }
    }

    /// <summary>The map scales to fit, so the view model needs the host's live size.</summary>
    private void OnMonitorMapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            _vm.SetMapViewport(e.NewSize.Width, e.NewSize.Height);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Resizing the monitor map failed.", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _vm.RequestClose -= OnRequestClose;
            Closed -= OnClosed;
            _vm.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tearing down the connection editor failed.", ex);
        }
    }
}
