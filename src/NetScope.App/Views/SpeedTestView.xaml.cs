using System.Windows;
using System.Windows.Controls;
using NetScope.App.ViewModels;

namespace NetScope.App.Views;

public partial class SpeedTestView : UserControl
{
    public SpeedTestView() => InitializeComponent();

    private async void StartPerformanceTest_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticViewModel viewModel || viewModel.IsBusy) return;
        var result = MessageBox.Show(
            "真实测速将连接 Cloudflare 测速节点，并产生最多约 62 MB 的下载和上传流量。\n\n测速仅在本次确认后运行，结果不会由 NetScope 上传或持久化。是否继续？",
            "开始真实网速测试",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
            await viewModel.RunPerformanceTestCommand.ExecuteAsync(null);
    }
}
