using System.Windows.Controls;

namespace DynatecRDM.Views;

/// <summary>
/// The connecting screen shown inside a session window. All of it is binding; see
/// <see cref="ViewModels.ConnectionProgressViewModel"/>.
/// </summary>
public partial class ConnectionProgressView : UserControl
{
    public ConnectionProgressView()
    {
        InitializeComponent();
    }
}
