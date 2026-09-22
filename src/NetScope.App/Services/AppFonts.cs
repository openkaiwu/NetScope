using System.Windows.Media;

namespace NetScope.App.Services;

/// <summary>
/// 统一界面字体栈（与 App.xaml 的 AppFont 保持一致）：
/// 拉丁字形用 Segoe 系，中日韩回退到微软雅黑 UI，避免系统兜底到宋体。
/// 自绘控件（Sparkline/DiagnosticChain/PortSpectrum）必须复用同一栈，
/// 否则其中文占位文本会与其他界面字体不一致。
/// </summary>
public static class AppFonts
{
    public const string InterfaceStack = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei";

    private static readonly Typeface InterfaceTypeface = new(InterfaceStack);

    public static Typeface Interface => InterfaceTypeface;
}
