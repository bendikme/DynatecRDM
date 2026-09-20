using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The update dialog. All of the behaviour lives in <see cref="UpdateViewModel"/>; this class
/// starts the first check and turns the view model's close request into a dialog result.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateViewModel _vm;
    private bool _closing;

    public UpdateWindow(UpdateViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));

        InitializeComponent();

        DataContext = _vm;
        _vm.RequestClose += OnRequestClose;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("The update check could not be started.", ex);
        }
    }

    private void OnRequestClose(object? sender, bool installing)
    {
        if (_closing) return;
        _closing = true;

        try
        {
            // On a modal dialog this closes the window; a modeless one throws instead.
            DialogResult = installing;
        }
        catch (InvalidOperationException)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn("The update dialog could not be closed.", ex);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing the update dialog failed.", ex);
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
            AppLog.Warn("Tearing down the update dialog failed.", ex);
        }
    }
}

/// <summary>
/// Byte count to "18.4 MB". Kept next to the dialog that needs it, and tolerant about the
/// numeric type it is handed so it can format an asset size straight off the model.
/// </summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public static readonly ByteSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = ToBytes(value);
        return bytes is null or < 0 ? string.Empty : Describe(bytes.Value, culture ?? CultureInfo.CurrentCulture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static double? ToBytes(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        ulong ul => ul,
        uint ui => ui,
        short s => s,
        double d => d,
        float f => f,
        decimal m => (double)m,
        string text when double.TryParse(
            text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static string Describe(double bytes, CultureInfo culture)
    {
        if (bytes < 1024d) return bytes.ToString("0", culture) + " B";

        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024d) return kilobytes.ToString("0.#", culture) + " KB";

        var megabytes = kilobytes / 1024d;
        return megabytes < 1024d
            ? megabytes.ToString("0.#", culture) + " MB"
            : (megabytes / 1024d).ToString("0.##", culture) + " GB";
    }
}
