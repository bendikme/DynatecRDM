using System.Windows;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.Views;

/// <summary>
/// Yes/no confirmation. DialogResult is true only when the confirm button was pressed;
/// Escape and Cancel both leave it false.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string? confirmText = null, bool destructive = true)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? Strings.Dialog_Confirm_Title : title;
        HeadingText.Text = Title;
        MessageText.Text = message ?? string.Empty;
        ConfirmButton.Content = confirmText is null
            ? Strings.Common_Delete
            : string.IsNullOrWhiteSpace(confirmText) ? Strings.Common_OK : confirmText;

        if (destructive) return;

        try
        {
            // A non-destructive prompt is safe to accept with Enter; a destructive one is not,
            // so only this branch makes the confirm button the default.
            ConfirmButton.IsDefault = true;
            if (TryFindResource("PrimaryButtonStyle") is Style style) ConfirmButton.Style = style;
            // A reference rather than the brush itself, so the icon follows a theme change.
            AlertIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
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
