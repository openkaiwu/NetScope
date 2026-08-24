using System.Windows;
using System.Windows.Controls;

namespace NetScope.App.Views;

public partial class NetworkDiagnosticView : UserControl
{
    public NetworkDiagnosticView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = ActualWidth < 820;
        EvidenceColumn.Width = compact ? new GridLength(0) : new GridLength(300);
        EvidencePanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
}
