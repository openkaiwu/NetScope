using System.Windows;
using System.Windows.Controls;
using NetScope.App.ViewModels;

namespace NetScope.App.Views;

public partial class PortView : UserControl
{
    public PortView()
    {
        InitializeComponent();
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { if (DataContext is PortViewModel vm) vm.IsPaused = !vm.IsPaused; }
}
