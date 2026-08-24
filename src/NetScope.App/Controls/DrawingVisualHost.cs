using System.Windows;
using System.Windows.Media;

namespace NetScope.App.Controls;

public abstract class DrawingVisualHost : FrameworkElement
{
    private readonly VisualCollection _visuals;
    protected readonly DrawingVisual Visual = new();

    protected DrawingVisualHost()
    {
        _visuals = new VisualCollection(this) { Visual };
        SizeChanged += (_, _) => Redraw();
        IsVisibleChanged += (_, _) => Redraw();
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];
    protected abstract void Draw(DrawingContext context, Size size);

    protected void Redraw()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        using var context = Visual.RenderOpen();
        Draw(context, new Size(ActualWidth, ActualHeight));
    }
}
