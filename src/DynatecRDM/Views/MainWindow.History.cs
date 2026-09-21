using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The history card reaches down to the bottom of the detail pane, and the sessions scroll inside
/// it. Only a window too short for a usable list lets the pane itself scroll; then the wheel moves
/// the list first and the pane once the list has reached its end. While the list has more than
/// it shows, it is a tab stop, so the keyboard can scroll it too.
/// </summary>
public partial class MainWindow
{
    /// <summary>The list keeps at least this much height - about five sessions - however short the window.</summary>
    private const double MinHistoryListHeight = 160;

    private void OnDetailContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged) FitHistoryList();
    }

    /// <summary>
    /// Gives the list whatever height the rest of the detail leaves in the pane. Everything else in
    /// the detail is whatever of its height the list does not take, so this follows the real
    /// layout - wrapped text, a hidden snapshot, either language - and settles at once: setting
    /// the height changes the detail by exactly the difference, and the next pass finds nothing
    /// to change. That needs the detail's own height, which is why it is top-aligned: a ScrollViewer
    /// stretches its content to at least the visible height, and a stretched detail would hide the
    /// room a list could grow into. The pane's visible height is worked out from its size, not from
    /// ViewportHeight, which a ScrollViewer updates only after layout.
    /// </summary>
    private void FitHistoryList()
    {
        try
        {
            // No sessions, no list: the card stays as small as its "nothing yet" line.
            if (!_viewModel.HasHistory) return;

            var padding = DetailScroller.Padding;
            var viewport = DetailScroller.ActualHeight - padding.Top - padding.Bottom;
            if (viewport <= 0 || DetailContent.ActualHeight <= 0) return;

            var others = DetailContent.ActualHeight - HistoryScroller.ActualHeight;
            var height = Math.Max(MinHistoryListHeight, Math.Floor(viewport - others));
            if (double.IsNaN(HistoryScroller.Height) || Math.Abs(HistoryScroller.Height - height) >= 1)
                HistoryScroller.Height = height;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Sizing the history list failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A ScrollViewer keeps every wheel turn it gets, even at its end. Once the list cannot go any
    /// further that way, the turn goes to the detail pane instead, so the page keeps scrolling.
    /// </summary>
    private void OnHistoryPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer list) return;

        try
        {
            var up = e.Delta > 0;
            var canMove = up ? list.VerticalOffset > 0 : list.VerticalOffset < list.ScrollableHeight;
            if (canMove) return;

            e.Handled = true;
            DetailScroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = list,
            });
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Passing the wheel on from the history list failed: {ex.Message}");
        }
    }

    /// <summary>
    /// An emptied list - another connection selected, or everything cleared - starts again at the
    /// newest session. The list is one ScrollViewer for every connection, and it would otherwise
    /// open the next one wherever the last was left, hiding its newest sessions above the top.
    /// </summary>
    private void OnHistoryViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.HasHistory) || _viewModel.HasHistory) return;

        try
        {
            HistoryScroller.ScrollToTop();
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Resetting the history list failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A focused ScrollViewer takes Home and End as the horizontal ends, and this list does not
    /// scroll sideways; they mean its first and last session here.
    /// </summary>
    private void OnHistoryPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ScrollViewer list || !ReferenceEquals(e.OriginalSource, list)) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Home:
                list.ScrollToTop();
                e.Handled = true;
                break;
            case Key.End:
                list.ScrollToBottom();
                e.Handled = true;
                break;
        }
    }
}
