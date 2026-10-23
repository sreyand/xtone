using System.Windows;
using System.Windows.Media;

namespace XTONE.Controls;

/// <summary>Draws restrained ribbons from the supplied FFT magnitudes.</summary>
public sealed class SignalVisualizer : FrameworkElement
{
    private const int SampleCount = 64;
    private readonly double[] _spectrum = new double[SampleCount];
    private bool _active;

    private static readonly Pen QuietPen = MakePen(31, 1);
    private static readonly Pen BarPen = MakePen(185, 1.25);
    private static readonly Pen[] RibbonPens =
    [
        MakePen(105, 0.9), MakePen(88, 0.85), MakePen(69, 0.8), MakePen(52, 0.8)
    ];

    /// <summary>
    /// Supplies normalized FFT magnitudes. All visible energy comes from these values;
    /// inactive playback settles to a quiet baseline rather than inventing a spectrum.
    /// </summary>
    public void SetSpectrum(float[] values, bool active)
    {
        if (!Dispatcher.CheckAccess())
        {
            float[] copy = values is null ? [] : (float[])values.Clone();
            Dispatcher.BeginInvoke(new Action(() => SetSpectrum(copy, active)));
            return;
        }

        _active = active;
        for (var index = 0; index < SampleCount; index++)
        {
            var target = active && values is { Length: > 0 }
                ? Interpolate(values, index / (double)(SampleCount - 1))
                : 0;
            var speed = target > _spectrum[index] ? 0.48 : 0.18;
            _spectrum[index] += (target - _spectrum[index]) * speed;
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 850,
        double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : 58);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var baseline = height / 2;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        drawingContext.DrawLine(QuietPen, new Point(0, baseline), new Point(width, baseline));
        if (_active)
        {
            var amplitude = Math.Max(0, height * 0.44);
            // Related contours and their reflection give the fine signal-ribbon shape.
            // Their amplitudes share the same measured bands; there is no idle oscillator.
            for (var layer = RibbonPens.Length - 1; layer >= 0; layer--)
            {
                var scale = 1 - layer * 0.18;
                drawingContext.DrawGeometry(null, RibbonPens[layer], Ribbon(width, baseline, amplitude * scale, -1));
                drawingContext.DrawGeometry(null, RibbonPens[layer], Ribbon(width, baseline, amplitude * scale, 1));
            }

            var barCount = Math.Clamp((int)(width / 5.2), 24, 180);
            for (var bar = 0; bar < barCount; bar++)
            {
                var fraction = bar / (double)(barCount - 1);
                var energy = SmoothedEnergy(fraction);
                var fade = EdgeEnvelope(fraction);
                var barHeight = energy * amplitude * fade;
                if (barHeight < 0.2)
                    continue;

                var x = width * fraction;
                drawingContext.PushOpacity(0.3 + 0.7 * fade);
                drawingContext.DrawLine(BarPen,
                    new Point(x, baseline - barHeight), new Point(x, baseline + barHeight * 0.13));
                drawingContext.Pop();
            }
        }
        drawingContext.Pop();
    }

    private StreamGeometry Ribbon(double width, double baseline, double amplitude, double direction)
    {
        var points = new Point[SampleCount];
        for (var index = 0; index < points.Length; index++)
        {
            var fraction = index / (double)(points.Length - 1);
            points[index] = new Point(width * fraction,
                baseline + direction * SmoothedEnergy(fraction) * EdgeEnvelope(fraction) * amplitude);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (var index = 0; index < points.Length - 1; index++)
            {
                var before = points[Math.Max(0, index - 1)];
                var current = points[index];
                var next = points[index + 1];
                var after = points[Math.Min(points.Length - 1, index + 2)];
                var control1 = new Point(current.X + (next.X - before.X) / 6, current.Y + (next.Y - before.Y) / 6);
                var control2 = new Point(next.X - (after.X - current.X) / 6, next.Y - (after.Y - current.Y) / 6);
                context.BezierTo(control1, control2, next, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private double SmoothedEnergy(double fraction)
    {
        var position = fraction * (SampleCount - 1);
        var low = (int)position;
        var high = Math.Min(SampleCount - 1, low + 1);
        var mix = position - low;
        var center = _spectrum[low] * (1 - mix) + _spectrum[high] * mix;
        var left = _spectrum[Math.Max(0, low - 1)];
        var right = _spectrum[Math.Min(SampleCount - 1, high + 1)];
        return center * 0.64 + (left + right) * 0.18;
    }

    private static double Interpolate(float[] values, double fraction)
    {
        var position = fraction * (values.Length - 1);
        var low = (int)position;
        var high = Math.Min(values.Length - 1, low + 1);
        var mix = position - low;
        var value = values[low] * (1 - mix) + values[high] * mix;
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    private static double EdgeEnvelope(double fraction) => Math.Pow(Math.Sin(Math.PI * fraction), 0.8);

    private static Pen MakePen(byte alpha, double thickness)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 37, 91, 255));
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}
