using System.Windows;
using DynatecRDM.Services;

namespace DynatecRDM.Views;

/// <summary>
/// Yes/no confirmation. DialogResult is true only when the confirm button was pressed;
/// Escape and Cancel both leave it false.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText = "Delete", bool destructive = true)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "Confirm" : title;
        HeadingText.Text = Title;
        MessageText.Text = message ?? string.Empty;
        ConfirmButton.Content = string.IsNullOrWhiteSpace(confirmText) ? "OK" : confirmText;

        if (destructive) return;

        try
        {
            // A non-destructive prompt is safe to accept with Enter; a destructive one is not,
            // so only this branch makes the confirm button the default.
            ConfirmButton.IsDefault = true;
            if (TryFindResource("PrimaryButtonStyle") is Style style) ConfirmButton.Style = style;
            if (TryFindResource("AccentBrush") is System.Windows.Media.Brush brush) AlertIcon.Stroke = brush;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Styling the confirmation dialog failed.", ex);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Closing the confirmation dialog failed.", ex);
            try
            {
                Close();
            }
            catch (Exception closeEx)
            {
                AppLog.Warn("Closing the confirmation dialog failed.", closeEx);
            }
        }
    }
}
