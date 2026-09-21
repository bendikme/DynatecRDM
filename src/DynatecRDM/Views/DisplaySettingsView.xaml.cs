using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The display settings form for a <see cref="DisplayEditorViewModel"/>. The host decides where the
/// monitor map goes: inside the form through <see cref="Map"/>, or somewhere of its own.
/// </summary>
public partial class DisplaySettingsView : UserControl
{
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(object), typeof(DisplaySettingsView),
        new PropertyMetadata(null, OnMapChanged));

    public DisplaySettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Shown under the screen-mode choices, with the map hint below it; nothing when null.</summary>
    public object? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    private static void OnMapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DisplaySettingsView view) return;
        view.MapPresenter.Content = e.NewValue;
        view.MapSlot.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A typed rectangle is put back on the screens once its field is left.</summary>
    private void OnCustomRectFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        try
        {
            (DataContext as DisplayEditorViewModel)?.CommitCustomRectangle();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Fitting the typed window rectangle failed.", ex);
        }
    }
}
