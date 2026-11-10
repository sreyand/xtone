using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using XTONE.Models;
using XTONE.Services;

namespace XTONE.Controls;

public sealed class EqualizerGraph : FrameworkElement
{
    private EqualizerSettings _settings = EqualizerSettings.Flat;
    private int _sampleRate = 48000;
    private int _selected;
    private int _dragging = -1;
    private double _range = 12;
    private Rect Plot => new(42, 12, Math.Max(1, ActualWidth - 57), Math.Max(1, ActualHeight - 39));
    private double UpperFrequency => Math.Min(20000, _sampleRate * .475);
    private static readonly double[] Ticks = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];

    public event Action<int>? BandSelected;
    public event Action<int, double, double>? BandEdited;
    public event Action? EditFinished;

    public void Show(EqualizerSettings settings, int sampleRate, int selected)
    {
        _settings = settings;
        _sampleRate = sampleRate;
        _selected = selected;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var plot = Plot;
        var line = new Pen((Brush)FindResource("LineBrush"), 1);
        var ink = (Brush)FindResource("MutedBrush");
        var blue = (Brush)FindResource("BlueBrush");
        var paper = (Brush)FindResource("PaperBrush");
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var response = new EqualizerResponse(_settings, _sampleRate);
        var samples = new double[181];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = response.GainDbAt(FrequencyAt(i / 180.0));
        var peak = samples.Select(Math.Abs).DefaultIfEmpty().Max();
        if (_dragging < 0) _range = Math.Max(18, Math.Ceiling(peak / 6) * 6);
        foreach (var db in new[] { -_range, -_range / 2, 0, _range / 2, _range })
        {
            var y = Y(db);
            context.DrawLine(line, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(context, $"{db:+0;-0;0}", new Point(plot.Left - 7, y - 7), ink, right: true);
        }
        foreach (var frequency in Ticks.Where(frequency => frequency <= UpperFrequency))
        {
            var x = X(frequency);
            context.DrawLine(line, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Label(context, frequency >= 1000 ? $"{frequency / 1000:g}k" : $"{frequency:g}",
                new Point(x, plot.Bottom + 7), ink);
        }
        context.PushClip(new RectangleGeometry(plot));
        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(new Point(plot.Left, Y(samples[0])), false, false);
            for (var i = 1; i < samples.Length; i++)
                path.LineTo(new Point(plot.Left + plot.Width * i / 180, Y(samples[i])), true, false);
        }
        context.DrawGeometry(null, new Pen(blue, 2), curve);
        context.Pop();

        if (!IsEnabled) return;
        for (var i = 0; i < _settings.Bands.Count; i++)
            if (i != _selected) DrawBand(context, i, paper, blue);
        if (_selected >= 0 && _selected < _settings.Bands.Count)
            DrawBand(context, _selected, blue, blue);
    }

    private void DrawBand(DrawingContext context, int index, Brush fill, Brush stroke)
    {
        var point = BandPoint(index);
        context.DrawEllipse(fill, new Pen(stroke, 1.5), point, index == _selected ? 6 : 4.5, index == _selected ? 6 : 4.5);
    }

    private void Label(DrawingContext context, string value, Point point, Brush ink, bool right = false)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("AppFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            11, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(text, new Point(point.X - (right ? text.Width : text.Width / 2), point.Y));
    }

    private Point BandPoint(int index) => new(X(_settings.Bands[index].FrequencyHz), Y(_settings.Bands[index].GainDb));
    private double X(double frequency) => Plot.Left + Plot.Width *
        Math.Log(Math.Clamp(frequency, 20, UpperFrequency) / 20) / Math.Log(UpperFrequency / 20);
    private double Y(double db) => Plot.Top + Plot.Height * (1 - db / _range) / 2;
    private double FrequencyAt(double fraction) => 20 * Math.Pow(UpperFrequency / 20, fraction);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        var index = Enumerable.Range(0, _settings.Bands.Count)
            .OrderBy(i => (BandPoint(i) - point).LengthSquared)
            .ThenBy(i => i == _selected ? 0 : 1)
            .FirstOrDefault(-1);
        if (index < 0 || (BandPoint(index) - point).Length > 14) return;
        if (CaptureMouse()) _dragging = index;
        BandSelected?.Invoke(index);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging < 0) return;
        var point = e.GetPosition(this);
        var frequency = FrequencyAt(Math.Clamp((point.X - Plot.Left) / Plot.Width, 0, 1));
        var gain = Math.Clamp(_range * (1 - 2 * (point.Y - Plot.Top) / Plot.Height), -18, 18);
        BandEdited?.Invoke(_dragging, Math.Round(frequency), Math.Round(gain, 1));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging < 0) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging < 0) return;
        _dragging = -1;
        InvalidateVisual();
        EditFinished?.Invoke();
    }
}
