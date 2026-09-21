using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DynatecRDM.Services;

namespace DynatecRDM.Views;

/// <summary>
/// The connection detail's layout. The last snapshot sits at the top right, beside the name: it
/// takes up to two fifths of the pane, so the connection's own details keep the larger share when
/// the pane is narrow, and never more than its full size when the pane is wide. Next to Connect is
/// a menu with everything else that can be done to the connection.
/// </summary>
public partial class MainWindow
{
    private const double DetailSnapshotMaxWidth = 320;
    private const double DetailSnapshotMinWidth = 200;
    private const double DetailSnapshotShare = 0.4;

    private void OnDetailSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged) FitHistoryList();
        if (!e.WidthChanged) return;

        try
        {
            var padding = DetailScroller.Padding;
            var inner = DetailScroller.ActualWidth - padding.Left - padding.Right;
            if (inner <= 0) return;

            var width = Math.Round(Math.Clamp(inner * DetailSnapshotShare, DetailSnapshotMinWidth, DetailSnapshotMaxWidth));
            if (Math.Abs(DetailSnapshotBox.Width - width) >= 1) DetailSnapshotBox.Width = width;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Sizing the detail snapshot failed: {ex.Message}");
        }
    }

    /// <summary>The menu opens under the button on a click, as it does by itself on a right-click.</summary>
    private void OnDetailActionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button { ContextMenu: { } menu } button) return;

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The connection's menu could not be opened.", ex);
        }
    }
}
