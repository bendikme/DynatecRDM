using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using DynatecRDM.Services;
using Path = System.Windows.Shapes.Path;
using Size = System.Windows.Size;

namespace DynatecRDM.Views;

/// <summary>
/// Fits the header to the window. The search box takes the free width; as the window narrows the
/// header gives way in steps, keeping the search box usable at each one:
///
///   1. everything, with labels on the three "new" buttons;
///   2. those buttons as icons only;
///   3. the app icon alone, without the title beside it;
///   4. buttons moving, rightmost first, into the "more" menu - "new connection" last.
///
/// Each step is tried in turn and the first that leaves the search box enough room wins, so the
/// layout follows the real widths of the text in whichever language is showing.
/// </summary>
public partial class MainWindow
{
    /// <summary>Room the search box must keep at each step before the next one is tried.</summary>
    private const double SearchRoomWithLabels = 320;
    private const double SearchRoomIconsOnly = 280;
    private const double SearchRoomIconAlone = 280;
    private const double SearchRoomMinimum = 260;

    /// <summary>The search box's own margins in the header.</summary>
    private const double SearchMargins = 24 + 16;

    private List<Button>? _overflowOrder;
    private readonly HashSet<Button> _overflowed = new();

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) FitHeader();
    }

    private void FitHeader()
    {
        try
        {
            var total = HeaderGrid.ActualWidth;
            if (total <= 0) return;

            // Rightmost first; the one everybody needs goes last.
            _overflowOrder ??=
            [
                SettingsButton, TransferButton, CredentialsButton, ReloadButton, ImportButton,
                NewGroupButton, NewMultiConfigButton, NewConnectionButton,
            ];

            var fixedWidth = MeasuredWidth(CaptionButtons) + SearchMargins;

            if (Fits(labels: true, title: true, overflow: 0, SearchRoomWithLabels)) return;
            if (Fits(labels: false, title: true, overflow: 0, SearchRoomIconsOnly)) return;
            if (Fits(labels: false, title: false, overflow: 0, SearchRoomIconAlone)) return;
            // One button alone gains nothing: the "more" button takes its place. So two at a time first.
            for (var overflow = 2; overflow <= _overflowOrder.Count; overflow++)
            {
                if (Fits(labels: false, title: false, overflow, SearchRoomMinimum)) return;
            }

            bool Fits(bool labels, bool title, int overflow, double searchRoom)
            {
                Apply(labels, title, overflow);
                var room = total - fixedWidth - MeasuredWidth(BrandPanel) - MeasuredWidth(ToolbarPanel);
                return room >= searchRoom;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Fitting the header failed: {ex.Message}");
        }
    }

    private void Apply(bool labels, bool title, int overflow)
    {
        foreach (var (button, label) in new[]
                 {
                     (NewConnectionButton, NewConnectionLabel),
                     (NewMultiConfigButton, NewMultiConfigLabel),
                     (NewGroupButton, NewGroupLabel),
                 })
        {
            label.Visibility = labels ? Visibility.Visible : Visibility.Collapsed;
            if (IconOf(button)?.Parent is FrameworkElement icon) icon.Margin = labels ? new Thickness(0, 0, 6, 0) : new Thickness(0);

            // As icons they match the icon-only buttons beside them; with labels, the style decides.
            if (labels)
            {
                button.ClearValue(PaddingProperty);
                button.ClearValue(HorizontalContentAlignmentProperty);
            }
            else
            {
                button.Padding = new Thickness(7, 0, 7, 0);
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
            }
        }

        BrandTitle.Visibility = title ? Visibility.Visible : Visibility.Collapsed;

        _overflowed.Clear();
        for (var i = 0; i < _overflowOrder!.Count; i++)
        {
            var button = _overflowOrder[i];
            var hidden = i < overflow;
            if (hidden) _overflowed.Add(button);
            button.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        }

        OverflowButton.Visibility = overflow > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The line between the "new" buttons and the rest only when there is something on both sides.
        var left = NewConnectionButton.Visibility == Visibility.Visible
                   || NewMultiConfigButton.Visibility == Visibility.Visible
                   || NewGroupButton.Visibility == Visibility.Visible;
        var right = ImportButton.Visibility == Visibility.Visible || OverflowButton.Visibility == Visibility.Visible;
        ToolbarSeparator.Visibility = left && right ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The width the element wants right now. Measure returns its cached answer unless the element
    /// is marked dirty, and a label switched off deep inside a button only marks its own panel -
    /// so everything underneath is marked first, or the result would describe the previous step.
    /// </summary>
    private static double MeasuredWidth(FrameworkElement element)
    {
        InvalidateMeasureDown(element);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private static void InvalidateMeasureDown(DependencyObject node)
    {
        if (node is UIElement element) element.InvalidateMeasure();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            InvalidateMeasureDown(VisualTreeHelper.GetChild(node, i));
    }

    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        try
        {
            BuildOverflowMenu().IsOpen = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The toolbar's more menu could not be opened.", ex);
        }
    }

    /// <summary>The buttons the toolbar had no room for, in toolbar order, as a menu under the "more" button.</summary>
    private ContextMenu BuildOverflowMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = OverflowButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = -6, // 4 below the button, less the shadow's room above the menu
        };

        foreach (var button in _overflowOrder!.AsEnumerable().Reverse())
        {
            if (!_overflowed.Contains(button)) continue;

            var item = new MenuItem
            {
                Header = AutomationProperties.GetName(button),
                Command = button.Command,
                CommandParameter = button.CommandParameter,
            };

            if (IconOf(button) is { } source)
            {
                var glyph = new Path
                {
                    Style = (Style)FindResource("ToolbarIconStyle"),
                    Data = source.Data,
                };
                glyph.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
                item.Icon = new Viewbox { Width = 14, Height = 14, Child = glyph };
            }

            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>The icon path inside a toolbar button.</summary>
    private static Path? IconOf(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Path path) return path;
            if (child is DependencyObject inner && IconOf(inner) is { } found) return found;
        }
        return null;
    }
}
