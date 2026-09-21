namespace DynatecRDM.ViewModels;

/// <summary>
/// Something drawn over the monitor map besides the monitors themselves - in the multi-config
/// editor, where each item of the set will appear. Coordinates are map pixels.
/// </summary>
public sealed class MapShape : ObservableObject
{
    private double _x;
    private double _y;
    private double _w = 1;
    private double _h = 1;
    private double _chipOffset;
    private string _number = string.Empty;
    private string _label = string.Empty;
    private bool _isSelected;
    private bool _isDimmed;

    public MapShape(object item, string? colorHex)
    {
        Item = item;
        ColorHex = colorHex;
    }

    /// <summary>What the shape stands for; handed to the map's command when its chip is clicked.</summary>
    public object Item { get; }

    /// <summary>The item's own colour, or null for the accent.</summary>
    public string? ColorHex { get; }

    /// <summary>The item's position in its list, as shown on its chip.</summary>
    public string Number { get => _number; set => SetProperty(ref _number, value); }

    public string Label { get => _label; set => SetProperty(ref _label, value); }

    public double X { get => _x; set => SetProperty(ref _x, value); }
    public double Y { get => _y; set => SetProperty(ref _y, value); }
    public double W { get => _w; set => SetProperty(ref _w, value); }
    public double H { get => _h; set => SetProperty(ref _h, value); }

    /// <summary>
    /// How far down its chip sits, so shapes on the same monitor do not hide each other's names.
    /// </summary>
    public double ChipOffset { get => _chipOffset; set => SetProperty(ref _chipOffset, value); }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>Drawn faintly: the item stays in the set but does not start with it.</summary>
    public bool IsDimmed { get => _isDimmed; set => SetProperty(ref _isDimmed, value); }
}
