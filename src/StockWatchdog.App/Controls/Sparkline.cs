using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace StockWatchdog.App.Controls;

public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<decimal>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(
            Array.Empty<decimal>(),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(MediaBrush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(
            MediaBrushes.SlateGray,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(
            1.5d,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<decimal> Values
    {
        get => (IReadOnlyList<decimal>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public MediaBrush Stroke
    {
        get => (MediaBrush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 72d;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 18d;
        return new WpfSize(Math.Max(24d, width), Math.Max(12d, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var values = Values;
        if (values.Count == 0 || ActualWidth < 6 || ActualHeight < 6)
        {
            return;
        }

        const double padding = 2d;
        var width = ActualWidth - (padding * 2);
        var height = ActualHeight - (padding * 2);
        var minimum = values.Min();
        var maximum = values.Max();
        var range = maximum - minimum;
        var denominator = Math.Max(1, values.Count - 1);

        WpfPoint PointAt(int index)
        {
            var x = padding + (index * width / denominator);
            var y = range == 0
                ? padding + (height / 2)
                : padding + (double)((maximum - values[index]) / range) * height;
            return new WpfPoint(x, y);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointAt(0), false, false);
            if (values.Count == 1)
            {
                context.LineTo(
                    new WpfPoint(padding + width, padding + (height / 2)),
                    true,
                    false);
            }
            else
            {
                for (var index = 1; index < values.Count; index++)
                {
                    context.LineTo(PointAt(index), true, false);
                }
            }
        }

        geometry.Freeze();
        var pen = new MediaPen(Stroke, Math.Max(0.5d, StrokeThickness))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        drawingContext.DrawGeometry(null, pen, geometry);
    }
}
