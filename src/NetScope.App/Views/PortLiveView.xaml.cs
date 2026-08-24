using System.Windows;
using System.Windows.Controls;

namespace NetScope.App.Views;

public partial class PortLiveView : UserControl
{
    public PortLiveView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = ActualWidth < 760;
        DetailsColumn.Width = compact ? new GridLength(0) : new GridLength(1.1, GridUnitType.Star);
        DetailsGap.Width = compact ? new GridLength(0) : new GridLength(12);
        DetailsPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
}
