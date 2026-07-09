using System.Windows;
using System.Windows.Media;

namespace RhythmLume.App;

public sealed class SpectrumVisualizer : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(float[]),
        typeof(SpectrumVisualizer),
        new FrameworkPropertyMetadata(
            Array.Empty<float>(),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public float[] Values
    {
        get => (float[])GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(32, 148, 163, 184)), 1);
        gridPen.Freeze();
        for (var line = 1; line < 4; line++)
        {
            var y = height * line / 4;
            drawingContext.DrawLine(gridPen, new(0, y), new(width, y));
        }

        var values = Values ?? [];
        if (values.Length == 0)
        {
            return;
        }

        var gap = Math.Max(1, width / values.Length * 0.22);
        var barWidth = Math.Max(1, (width - (gap * (values.Length - 1))) / values.Length);
        var brush = new LinearGradientBrush(
            Color.FromRgb(34, 211, 238),
            Color.FromRgb(168, 85, 247),
            new(0.5, 1),
            new(0.5, 0));
        brush.Freeze();
        for (var index = 0; index < values.Length; index++)
        {
            var value = Math.Clamp(values[index], 0, 1);
            var barHeight = Math.Max(2, value * height);
            var x = index * (barWidth + gap);
            drawingContext.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, height - barHeight, barWidth, barHeight),
                Math.Min(3, barWidth / 2),
                Math.Min(3, barWidth / 2));
        }
    }
}

public sealed class BeatOrb : FrameworkElement
{
    public static readonly DependencyProperty ActivityProperty = DependencyProperty.Register(
        nameof(Activity),
        typeof(double),
        typeof(BeatOrb),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public double Activity
    {
        get => (double)GetValue(ActivityProperty);
        set => SetValue(ActivityProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var activity = Math.Clamp(Activity / 100d, 0, 1);
        var size = Math.Min(ActualWidth, ActualHeight);
        var radius = size * (0.27 + (activity * 0.20));
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new(Color.FromArgb((byte)(170 + (activity * 85)), 236, 72, 153), 0),
                new(Color.FromArgb((byte)(90 + (activity * 80)), 124, 58, 237), 0.55),
                new(Color.FromArgb(0, 34, 211, 238), 1)
            }
        };
        glow.Freeze();
        drawingContext.DrawEllipse(glow, null, center, radius * 1.65, radius * 1.65);
        var core = new SolidColorBrush(Color.FromRgb(244, 114, 182));
        core.Freeze();
        drawingContext.DrawEllipse(core, null, center, radius, radius);
    }
}
