using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NetScope.Core.Models;

namespace NetScope.App.Converters;

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiagnosticStatus.Healthy => new SolidColorBrush(Color.FromRgb(32, 166, 106)),
        DiagnosticStatus.Degraded => new SolidColorBrush(Color.FromRgb(230, 162, 60)),
        DiagnosticStatus.Fault => new SolidColorBrush(Color.FromRgb(217, 74, 74)),
        _ => new SolidColorBrush(Color.FromRgb(152, 162, 179))
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiagnosticStatus.Healthy => "正常", DiagnosticStatus.Degraded => "退化", DiagnosticStatus.Fault => "故障", _ => "未检测"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
