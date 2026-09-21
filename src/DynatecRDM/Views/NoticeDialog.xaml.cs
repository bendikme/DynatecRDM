using System.Windows;

namespace DynatecRDM.Views;

/// <summary>Tells the user about something they have to act on, with a single OK.</summary>
public partial class NoticeDialog : Window
{
    public NoticeDialog(string heading, string message)
    {
        InitializeComponent();
        Title = Services.AppIdentity.Name;
        HeadingText.Text = heading;
        MessageText.Text = message;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }   // shown modeless
    }
}
