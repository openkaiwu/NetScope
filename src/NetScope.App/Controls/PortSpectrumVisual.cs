using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using NetScope.Core.Models;

namespace NetScope.App.Controls;

public sealed class PortSpectrumVisual : DrawingVisualHost
{
    private INotifyCollectionChanged? _observed;
    public static readonly DependencyProperty PortsProperty = DependencyProperty.Register(nameof(Ports), typeof(IEnumerable), typeof(PortSpectrumVisual),
        new FrameworkPropertyMetadata(null, OnPortsChanged));
    public IEnumerable? Ports { get => (IEnumerable?)GetValue(PortsProperty); set => SetValue(PortsProperty, value); }
    public static readonly DependencyProperty RecommendedPortsProperty = DependencyProperty.Register(nameof(RecommendedPorts), typeof(IEnumerable), typeof(PortSpectrumVisual),
        new FrameworkPropertyMetadata(null, OnPortsChanged));
    public IEnumerable? RecommendedPorts { get => (IEnumerable?)GetValue(RecommendedPortsProperty); set => SetValue(RecommendedPortsProperty, value); }
    public static readonly DependencyProperty SystemRangesProperty = DependencyProperty.Register(nameof(SystemRanges), typeof(SystemPortRangeSnapshot), typeof(PortSpectrumVisual),
        new FrameworkPropertyMetadata(null, OnRangesChanged));
    public SystemPortRangeSnapshot? SystemRanges { get => (SystemPortRangeSnapshot?)GetValue(SystemRangesProperty); set => SetValue(SystemRangesProperty, value); }
    public static readonly DependencyProperty ProtocolProperty = DependencyProperty.Register(nameof(Protocol), typeof(string), typeof(PortSpectrumVisual),
        new FrameworkPropertyMetadata("TCP", OnRangesChanged));
    public string Protocol { get => (string)GetValue(ProtocolProperty); set => SetValue(ProtocolProperty, value); }

    private static void OnPortsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PortSpectrumVisual)d;
        if (control._observed is not null) control._observed.CollectionChanged -= control.CollectionChanged;
        control._observed = e.NewValue as INotifyCollectionChanged;
        if (control._observed is not null) control._observed.CollectionChanged += control.CollectionChanged;
        control.Redraw();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();
    private static void OnRangesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PortSpectrumVisual)d).Redraw();

    protected override void Draw(DrawingContext context, Size size)
    {
        var bar = new Rect(4, 22, Math.Max(1, size.Width - 8), 30);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(239, 242, 246)), null, bar, 8, 8);
        var registeredWidth = bar.Width * 1024 / 65536d;
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(219, 234, 254)), null, new Rect(bar.X, bar.Y, registeredWidth, bar.Height), 8, 8);
        DrawRange(context, bar, 1024, 49151, new SolidColorBrush(Color.FromRgb(232, 246, 239)));

        var protocol = Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Udp : PortProtocol.Tcp;
        var ranges = SystemRanges?.Ranges.Where(x => x.Protocol == protocol).ToArray() ??
            [new PortRange(49_152, 65_535, protocol, PortRangeKind.Dynamic, "Windows 默认", "动态端口")];
        foreach (var range in ranges.Where(x => x.Kind == PortRangeKind.Dynamic))
            DrawRange(context, bar, range.Start, range.End, new SolidColorBrush(Color.FromRgb(253, 240, 205)));
        foreach (var range in ranges.Where(x => x.Kind == PortRangeKind.Excluded))
            DrawRange(context, bar, range.Start, range.End, new SolidColorBrush(Color.FromRgb(174, 183, 196)));
        foreach (var range in new[] { (1900, 1900), (4444, 4444), (5555, 5555), (6660, 7000), (31337, 31337) })
            DrawRange(context, bar, range.Item1, range.Item2, new SolidColorBrush(Color.FromRgb(126, 137, 153)));

        foreach (var port in Ports?.Cast<object>().Select(x => Convert.ToInt32(x)).Distinct() ?? [])
        {
            var x = bar.X + bar.Width * port / 65535d;
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(217, 74, 74)), port < 1024 ? 2.5 : 1.6), new Point(x, bar.Y + 3), new Point(x, bar.Bottom - 3));
        }

        foreach (var port in RecommendedPorts?.Cast<object>().Select(x => Convert.ToInt32(x)).Distinct() ?? [])
        {
            var x = bar.X + bar.Width * port / 65535d;
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(32, 166, 106)), 3.2), new Point(x, bar.Y + 2), new Point(x, bar.Bottom - 2));
        }

        DrawLabel(context, "0", bar.Left, 59, TextAlignment.Left);
        DrawLabel(context, "1024", Math.Max(bar.Left + 32, bar.Left + registeredWidth), 59, TextAlignment.Center);
        var dynamicStart = ranges.FirstOrDefault(x => x.Kind == PortRangeKind.Dynamic)?.Start ?? 49152;
        var dynamicX = bar.X + bar.Width * dynamicStart / 65536d;
        DrawLabel(context, dynamicStart.ToString(), dynamicX, 59, TextAlignment.Center);
        DrawLabel(context, "65535", bar.Right, 59, TextAlignment.Right);
    }

    private static void DrawRange(DrawingContext context, Rect bar, int start, int end, Brush brush)
    {
        var left = bar.X + bar.Width * Math.Clamp(start, 0, 65_535) / 65_536d;
        var right = bar.X + bar.Width * (Math.Clamp(end, 0, 65_535) + 1) / 65_536d;
        context.DrawRectangle(brush, null, new Rect(left, bar.Y, Math.Max(1, right - left), bar.Height));
    }

    private static void DrawLabel(DrawingContext context, string text, double x, double y, TextAlignment alignment)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text"), 10, new SolidColorBrush(Color.FromRgb(102, 112, 133)), 1.0) { TextAlignment = alignment };
        context.DrawText(formatted, new Point(x, y));
    }
}
