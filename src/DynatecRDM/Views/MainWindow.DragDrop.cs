using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace DynatecRDM.Views;

/// <summary>
/// Drag and drop in the library tree. A connection or multi-config dropped on a group goes into
/// it; dropped on the upper or lower half of another row it goes before or after that row. Groups
/// reorder the same way on the top and bottom quarter of a group row, and nest on its middle.
/// Empty space below the tree is the end of the top level. The view model decides what is
/// allowed and does the move; this only reads the pointer and draws the insertion mark.
/// </summary>
public partial class MainWindow
{
    private const string DragFormat = "DynatecRDM.LibraryNode";

    /// <summary>The top and bottom quarter of a group row mean before and after; the rest means into.</summary>
    private const double GroupEdge = 0.25;

    private const double AutoScrollMargin = 24;
    private const int ExpandHoverMs = 700;

    private Point _dragStart;
    private TreeNodeViewModel? _dragCandidate;
    private bool _dragging;
    private DropMark? _dropMark;
    private TreeNodeViewModel? _expandTarget;
    private DispatcherTimer? _expandTimer;

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (e.OriginalSource is not DependencyObject source) return;

        // The expander arrow keeps its click.
        if (FindAncestor<ButtonBase>(source) is not null) return;
        if (FindAncestor<TreeViewItem>(source)?.DataContext is not TreeNodeViewModel node)
        {
            // Empty space below the rows goes back to the overview; the scroll bar is not empty space.
            if (FindAncestor<ScrollBar>(source) is null) _viewModel.ClearSelection();
            return;
        }

        _dragStart = e.GetPosition(LibraryTree);
        _dragCandidate = node;
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _dragCandidate is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = null;
            return;
        }

        var position = e.GetPosition(LibraryTree);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var node = _dragCandidate;
        _dragCandidate = null;
        _dragging = true;
        try
        {
            DragDrop.DoDragDrop(LibraryTree, new DataObject(DragFormat, node), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Dragging in the library failed.", ex);
        }
        finally
        {
            _dragging = false;
            ClearDropFeedback();
        }
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;

        try
        {
            AutoScroll(e);

            if (!TryReadDrop(e, out var dragged, out var target, out var placement, out var item)
                || !_viewModel.CanMove(dragged, target, placement))
            {
                ClearDropFeedback();
                return;
            }

            e.Effects = DragDropEffects.Move;
            ShowDropFeedback(item, placement);
            ArmExpand(target, placement);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tracking a drag over the library failed.", ex);
            ClearDropFeedback();
        }
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        // Also raised when the pointer crosses from one row to the next; only leaving the tree counts.
        var position = e.GetPosition(LibraryTree);
        if (position.X < 0 || position.Y < 0 ||
            position.X >= LibraryTree.ActualWidth || position.Y >= LibraryTree.ActualHeight)
            ClearDropFeedback();
    }

    private async void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        try
        {
            var readable = TryReadDrop(e, out var dragged, out var target, out var placement, out _);
            ClearDropFeedback();
            if (!readable || !_viewModel.CanMove(dragged, target, placement)) return;

            await _viewModel.MoveAsync(dragged, target, placement);
        }
        catch (Exception ex)
        {
            AppLog.Error("Moving an item in the library failed.", ex);
        }
    }

    /// <summary>
    /// What is being dragged, the row under the pointer and where on that row. A null target is
    /// the empty space below the tree.
    /// </summary>
    private bool TryReadDrop(
        DragEventArgs e,
        out TreeNodeViewModel dragged,
        out TreeNodeViewModel? target,
        out DropPlacement placement,
        out TreeViewItem? item)
    {
        dragged = null!;
        target = null;
        placement = DropPlacement.After;
        item = null;

        if (e.Data.GetData(DragFormat) is not TreeNodeViewModel node) return false;
        dragged = node;

        item = e.OriginalSource is DependencyObject source ? FindAncestor<TreeViewItem>(source) : null;
        if (item?.DataContext is not TreeNodeViewModel over)
        {
            item = null;
            return true;
        }

        target = over;
        var row = HeaderRow(item);
        var y = e.GetPosition(row).Y;
        var height = Math.Max(1, row.ActualHeight);

        if (!over.IsGroup)
        {
            placement = y < height / 2 ? DropPlacement.Before : DropPlacement.After;
        }
        else if (dragged.IsGroup && y < height * GroupEdge)
        {
            placement = DropPlacement.Before;
        }
        else if (dragged.IsGroup && y > height * (1 - GroupEdge) && !(over.IsExpanded && over.Children.Count > 0))
        {
            // Below an open group the next row is its first child, so the lower edge means "into".
            placement = DropPlacement.After;
        }
        else
        {
            placement = DropPlacement.Into;
        }

        return true;
    }

    /// <summary>The row itself, without the children an open group shows beneath it.</summary>
    private static FrameworkElement HeaderRow(TreeViewItem item) =>
        item.Template?.FindName("Bd", item) as FrameworkElement ?? item;

    // --------------------------------------------------------------- feedback

    private void ShowDropFeedback(TreeViewItem? item, DropPlacement placement)
    {
        FrameworkElement? adorned;
        if (item is not null)
        {
            adorned = HeaderRow(item);
        }
        else
        {
            // The end of the top level: under the last root row, children and all.
            adorned = LastVisibleRoot();
            placement = DropPlacement.After;
        }

        if (adorned is null)
        {
            ClearDropFeedback();
            return;
        }

        if (_dropMark is { } current && ReferenceEquals(current.AdornedElement, adorned) && current.Placement == placement)
            return;

        ClearDropFeedback(keepExpand: true);

        var layer = AdornerLayer.GetAdornerLayer(adorned);
        if (layer is null) return;

        var accent = TryFindResource("AccentBrush") as Brush ?? System.Windows.SystemColors.HighlightBrush;
        _dropMark = new DropMark(adorned, placement, accent);
        layer.Add(_dropMark);
    }

    private void ClearDropFeedback(bool keepExpand = false)
    {
        if (_dropMark is { } mark)
        {
            AdornerLayer.GetAdornerLayer(mark.AdornedElement)?.Remove(mark);
            _dropMark = null;
        }

        if (!keepExpand) ArmExpand(null, DropPlacement.After);
    }

    private FrameworkElement? LastVisibleRoot()
    {
        for (var i = LibraryTree.Items.Count - 1; i >= 0; i--)
        {
            if (LibraryTree.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem { IsVisible: true } root)
                return root;
        }
        return null;
    }

    /// <summary>Hovering over a closed group with something to drop opens it after a moment.</summary>
    private void ArmExpand(TreeNodeViewModel? target, DropPlacement placement)
    {
        var wanted = placement == DropPlacement.Into && target is { IsGroup: true, IsExpanded: false } ? target : null;
        if (ReferenceEquals(wanted, _expandTarget)) return;

        _expandTarget = wanted;
        _expandTimer?.Stop();
        if (wanted is null) return;

        if (_expandTimer is null)
        {
            _expandTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ExpandHoverMs),
            };
            _expandTimer.Tick += (_, _) =>
            {
                _expandTimer.Stop();
                if (_expandTarget is { } group) group.IsExpanded = true;
            };
        }
        _expandTimer.Start();
    }

    private void AutoScroll(DragEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(LibraryTree) is not { } viewer) return;

        var y = e.GetPosition(viewer).Y;
        if (y < AutoScrollMargin) viewer.LineUp();
        else if (y > viewer.ActualHeight - AutoScrollMargin) viewer.LineDown();
    }

    // ---------------------------------------------------------------- helpers

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>The insertion line above or below a row, or an outline around a group it will go into.</summary>
    private sealed class DropMark : Adorner
    {
        private readonly Pen _line;
        private readonly Pen _outline;
        private readonly Brush _fill;

        public DropMark(UIElement adorned, DropPlacement placement, Brush accent) : base(adorned)
        {
            Placement = placement;
            IsHitTestVisible = false;

            _line = new Pen(accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _line.Freeze();
            _outline = new Pen(accent, 1.5);
            _outline.Freeze();

            var fill = accent.Clone();
            fill.Opacity = 0.14;
            fill.Freeze();
            _fill = fill;
        }

        public DropPlacement Placement { get; }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            if (Placement == DropPlacement.Into)
            {
                dc.DrawRoundedRectangle(_fill, _outline, new Rect(0.75, 0.75, Math.Max(0, size.Width - 1.5), Math.Max(0, size.Height - 1.5)), 4, 4);
                return;
            }

            var y = Placement == DropPlacement.Before ? 1 : size.Height - 1;
            dc.DrawEllipse(_line.Brush, null, new Point(4, y), 3, 3);
            dc.DrawLine(_line, new Point(7, y), new Point(Math.Max(7, size.Width - 2), y));
        }
    }
}
