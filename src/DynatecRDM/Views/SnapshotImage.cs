using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynatecRDM.Converters;
using DynatecRDM.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;

namespace DynatecRDM.Views;

/// <summary>
/// A session picture wherever the app shows one: the tree, the detail pane, the session cards and
/// the quick-launch list. With no picture - never captured, or the file gone - it shows a dark grey
/// question mark on light grey instead, the same everywhere.
///
/// The file keeps its name when a capture replaces it, so <see cref="Stamp"/> is what says "load it
/// again"; bind it to the capture time.
/// </summary>
public sealed class SnapshotImage : Border
{
    private static readonly Brush PlaceholderBackground = Frozen(Color.FromRgb(0xD9, 0xD9, 0xDC));
    private static readonly Brush PlaceholderMark = Frozen(Color.FromRgb(0x5F, 0x60, 0x66));

    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(SnapshotImage), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty StampProperty = DependencyProperty.Register(
        nameof(Stamp), typeof(object), typeof(SnapshotImage), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth), typeof(int), typeof(SnapshotImage), new PropertyMetadata(0, OnSourceChanged));

    private readonly Grid _host = new();
    private readonly Image _image = new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _mark = new()
    {
        Text = "?",
        FontWeight = FontWeights.SemiBold,
        Foreground = PlaceholderMark,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public SnapshotImage()
    {
        CornerRadius = new CornerRadius(3);
        Background = PlaceholderBackground;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _host.Children.Add(_mark);
        _host.Children.Add(_image);
        Child = _host;

        SizeChanged += (_, _) => FitToSize();
        Show(null);
    }

    /// <summary>The picture file; null or a missing file shows the question mark.</summary>
    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>When the picture was taken. A new value reloads the file.</summary>
    public object? Stamp
    {
        get => GetValue(StampProperty);
        set => SetValue(StampProperty, value);
    }

    /// <summary>Pixel width to decode at; 0 decodes the file at its own size.</summary>
    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    /// <summary>True while a picture is shown rather than the question mark.</summary>
    public bool HasPicture => _image.Source is not null;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SnapshotImage)d).Reload();

    private void Reload()
    {
        ImageSource? source = null;
        var path = Path;
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                source = FileToImageConverter.Instance.Convert(
                    path, typeof(ImageSource), DecodeWidth, CultureInfo.InvariantCulture) as ImageSource;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Snapshot '{path}' could not be shown: {ex.Message}");
        }

        Show(source);
    }

    private void Show(ImageSource? source)
    {
        _image.Source = source;
        _image.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        _mark.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rounds the picture's corners with the border's, and sizes the mark to the box.</summary>
    private void FitToSize()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var radius = CornerRadius.TopLeft;
        _host.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        _mark.FontSize = Math.Max(8, Math.Min(width, height) * 0.5);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
