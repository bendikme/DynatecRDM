using System.Windows;
using System.Windows.Controls;
using DynatecRDM.Services;

namespace DynatecRDM.Views;

/// <summary>
/// One-line prompt. <see cref="Value"/> stays null unless the user confirmed.
/// Enter accepts (via IsDefault), Escape cancels (via IsCancel).
/// </summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string? initialValue = null)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "Enter a value" : title;
        HeadingText.Text = Title;
        PromptText.Text = prompt ?? string.Empty;
        InputBox.Text = initialValue ?? string.Empty;

        UpdateOkState();
        Loaded += OnLoaded;
    }

    /// <summary>What the user typed, or null when the dialog was cancelled.</summary>
    public string? Value { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Loaded -= OnLoaded;
            InputBox.Focus();
            InputBox.SelectAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Focusing the input box failed.", ex);
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            UpdateOkState();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Updating the input dialog failed.", ex);
        }
    }

    private void UpdateOkState() => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = InputBox.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            Value = text.Trim();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Closing the input dialog failed.", ex);
            try
            {
                Close();
            }
            catch (Exception closeEx)
            {
                AppLog.Warn("Closing the input dialog failed.", closeEx);
            }
        }
    }
}
