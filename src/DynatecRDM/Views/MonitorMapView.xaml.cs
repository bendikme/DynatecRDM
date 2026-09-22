using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The monitor map for a <see cref="DisplayEditorViewModel"/>. Everything here is wiring: the map
/// reports the size it has to draw into, and pointer and key input on the window rectangle go to
/// the view model, which does the geometry.
/// </summary>
public partial class MonitorMapView : UserControl
{
    public static readonly DependencyProperty CompanionsProperty = DependencyProperty.Register(
        nameof(Companions), typeof(IEnumerable), typeof(MonitorMapView), new PropertyMetadata(null));

    public static readonly DependencyProperty CompanionCommandProperty = DependencyProperty.Register(
        nameof(CompanionCommand), typeof(ICommand), typeof(MonitorMapView), new PropertyMetadata(null));

    /// <summary>What the drag in progress moves, and where on the map it started.</summary>
    private RectEdges _dragEdges;
    private System.Windows.Point _dragOrigin;

    public MonitorMapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary><see cref="MapShape"/>s drawn over the monitors.</summary>
    public IEnumerable? Companions
    {
        get => (IEnumerable?)GetValue(CompanionsProperty);
        set => SetValue(CompanionsProperty, value);
    }

    /// <summary>Run with a shape's Item when its chip is clicked.</summary>
    public ICommand? CompanionCommand
    {
        get => (ICommand?)GetValue(CompanionCommandProperty);
        set => SetValue(CompanionCommandProperty, value);
    }

    private DisplayEditorViewModel? Editor => DataContext as DisplayEditorViewModel;

    /// <summary>The map scales to fit, so the view model needs the host's live size.</summary>
    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e) =>
        Report(e.NewSize.Width, e.NewSize.Height);

    /// <summary>A different editor behind the same map - another item of a set - is drawn to the same size.</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Report(MonitorMapHost.ActualWidth, MonitorMapHost.ActualHeight);

    private void Report(double width, double height)
    {
        try
        {
            if (width > 0 && height > 0) Editor?.SetMapViewport(width, height);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Resizing the monitor map failed.", ex);
        }
    }

    /// <summary>
    /// A press on the rectangle starts a drag. The part pressed says what moves: the middle moves
    /// the whole window, an edge or corner (tagged with the edges it moves) resizes it.
    /// </summary>
    private void OnRectangleMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement body || Editor is not { CanEditRectangle: true } editor) return;

            var part = e.OriginalSource as FrameworkElement;
            _dragEdges = part?.Tag is string tag && Enum.TryParse<RectEdges>(tag, ignoreCase: true, out var edges)
                ? edges
                : RectEdges.None;
            _dragOrigin = e.GetPosition(MonitorMapHost);

            body.Focus();
            if (!body.CaptureMouse()) return;

            // Under capture the cursor would fall back to the rectangle's own; keep the handle's.
            Mouse.OverrideCursor = part?.Cursor ?? Cursors.SizeAll;
            editor.BeginRectangleDrag();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Starting to drag the window rectangle failed.", ex);
        }
    }

    private void OnRectangleMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement body || !body.IsMouseCaptured) return;

        try
        {
            var point = e.GetPosition(MonitorMapHost);
            // Ctrl places the window exactly under the pointer, as it makes a nudge exact.
            Editor?.DragRectangle(_dragEdges, point.X - _dragOrigin.X, point.Y - _dragOrigin.Y,
                snap: !Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Dragging the window rectangle failed.", ex);
        }
    }

    private void OnRectangleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement body || !body.IsMouseCaptured) return;

        body.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Every drag ends here, including one cut short by Alt+Tab or another window.</summary>
    private void OnRectangleLostCapture(object sender, MouseEventArgs e)
    {
        Mouse.OverrideCursor = null;
        try
        {
            Editor?.EndRectangleDrag();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Finishing the window rectangle drag failed.", ex);
        }
    }

    /// <summary>Arrow keys move the focused rectangle, Shift resizes it, Ctrl makes the step one pixel.</summary>
    private void OnRectangleKeyDown(object sender, KeyEventArgs e)
    {
        int dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left: dx = -1; break;
            case Key.Right: dx = 1; break;
            case Key.Up: dy = -1; break;
            case Key.Down: dy = 1; break;
            default: return;
        }

        try
        {
            var modifiers = Keyboard.Modifiers;
            Editor?.NudgeRectangle(dx, dy,
                resize: modifiers.HasFlag(ModifierKeys.Shift),
                fine: modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Nudging the window rectangle failed.", ex);
        }
    }
}
