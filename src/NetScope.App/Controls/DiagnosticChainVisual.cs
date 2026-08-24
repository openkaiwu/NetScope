using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetScope.App.ViewModels;
using NetScope.Core.Models;

namespace NetScope.App.Controls;

public sealed class DiagnosticChainVisual : DrawingVisualHost
{
    private INotifyCollectionChanged? _observed;
    private readonly List<INotifyPropertyChanged> _observedItems = [];
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(DiagnosticChainVisual),
        new FrameworkPropertyMetadata(null, OnItemsChanged));

    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    public DiagnosticChainVisual() { Cursor = Cursors.Hand; MouseLeftButtonUp += SelectNode; }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DiagnosticChainVisual)d;
        if (control._observed is not null) control._observed.CollectionChanged -= control.CollectionChanged;
        control._observed = e.NewValue as INotifyCollectionChanged;
        if (control._observed is not null) control._observed.CollectionChanged += control.CollectionChanged;
        control.SubscribeItems();
        control.Redraw();
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { SubscribeItems(); Redraw(); }

    private void SubscribeItems()
    {
        foreach (var item in _observedItems) item.PropertyChanged -= ItemPropertyChanged;
        _observedItems.Clear();
        foreach (var item in Items?.Cast<object>().OfType<INotifyPropertyChanged>() ?? [])
        {
            item.PropertyChanged += ItemPropertyChanged;
            _observedItems.Add(item);
        }
    }

    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Redraw();

    protected override void Draw(DrawingContext context, Size size)
    {
        var items = Items?.Cast<DiagnosticStageItemViewModel>().ToArray() ?? [];
        if (items.Length == 0) return;
        var margin = 34d;
        var centerY = 34d;
        var width = Math.Max(1, size.Width - margin * 2);
        var step = width / (items.Length - 1);
        var muted = new SolidColorBrush(Color.FromRgb(217, 225, 234)); muted.Freeze();
        context.DrawLine(new Pen(muted, 4), new Point(margin, centerY), new Point(size.Width - margin, centerY));

        for (var i = 0; i < items.Length; i++)
        {
            var x = margin + step * i;
            var color = items[i].IsRunning ? new SolidColorBrush(Color.FromRgb(59, 130, 246)) : StatusColor(items[i].Status);
            context.DrawEllipse(Brushes.White, new Pen(color, 3), new Point(x, centerY), 13, 13);
            if (items[i].Status != DiagnosticStatus.NotTested || items[i].IsRunning)
                context.DrawEllipse(color, null, new Point(x, centerY), 6, 6);
            var title = new FormattedText(items[i].Title, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text"), 11, new SolidColorBrush(Color.FromRgb(75, 85, 99)), 1.0);
            context.DrawText(title, new Point(x - title.Width / 2, centerY + 23));
            var caption = new FormattedText(items[i].IsRunning ? "检测中" : StatusText(items[i].Status), System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text"), 10, color, 1.0);
            context.DrawText(caption, new Point(x - caption.Width / 2, centerY - 28));
        }
    }

    private void SelectNode(object sender, MouseButtonEventArgs e)
    {
        var items = Items?.Cast<DiagnosticStageItemViewModel>().ToArray() ?? [];
        if (items.Length == 0 || DataContext is not DiagnosticViewModel vm) return;
        var margin = 34d;
        var step = Math.Max(1, (ActualWidth - margin * 2) / (items.Length - 1));
        var index = Math.Clamp((int)Math.Round((e.GetPosition(this).X - margin) / step), 0, items.Length - 1);
        vm.SelectStageCommand.Execute(items[index]);
    }

    private static Brush StatusColor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => new SolidColorBrush(Color.FromRgb(32, 166, 106)),
        DiagnosticStatus.Degraded => new SolidColorBrush(Color.FromRgb(230, 162, 60)),
        DiagnosticStatus.Fault => new SolidColorBrush(Color.FromRgb(217, 74, 74)),
        _ => new SolidColorBrush(Color.FromRgb(152, 162, 179))
    };
    private static string StatusText(DiagnosticStatus status) => status switch
    { DiagnosticStatus.Healthy => "正常", DiagnosticStatus.Degraded => "退化", DiagnosticStatus.Fault => "故障", _ => "未检测" };
}
