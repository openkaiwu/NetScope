using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace NetScope.App.Controls;

public sealed class SparklineVisual : DrawingVisualHost
{
    private INotifyCollectionChanged? _observed;
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable), typeof(SparklineVisual),
        new FrameworkPropertyMetadata(null, OnValuesChanged));
    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SparklineVisual)d;
        if (control._observed is not null) control._observed.CollectionChanged -= control.CollectionChanged;
        control._observed = e.NewValue as INotifyCollectionChanged;
        if (control._observed is not null) control._observed.CollectionChanged += control.CollectionChanged;
        control.Redraw();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    protected override void Draw(DrawingContext context, Size size)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 210, 220, 232)), 1);
        for (var i = 1; i < 4; i++) context.DrawLine(gridPen, new Point(0, size.Height * i / 4), new Point(size.Width, size.Height * i / 4));
        var values = Values?.Cast<object>().Select(Convert.ToDouble).ToArray() ?? [];
        if (values.Length < 2)
        {
            var wait = new FormattedText("运行快速诊断后显示网关延迟趋势", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Variable Text"), 11, new SolidColorBrush(Color.FromRgb(152, 162, 179)), 1.0);
            context.DrawText(wait, new Point((size.Width - wait.Width) / 2, (size.Height - wait.Height) / 2));
            return;
        }
        var max = Math.Max(1, values.Max());
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var point = new Point(i * size.Width / (values.Length - 1), size.Height - 6 - values[i] / max * (size.Height - 12));
                if (i == 0) g.BeginFigure(point, false, false); else g.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(59, 130, 246)), 2.2), geometry);
    }
}
