using System.Windows;
using System.Windows.Controls;

namespace NetScope.App.Views;

public partial class PortCatalogView : UserControl
{
    public PortCatalogView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = ActualWidth < 760;
        CatalogDetailsColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CatalogGap.Width = compact ? new GridLength(0) : new GridLength(12);
        CatalogDetails.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
}
