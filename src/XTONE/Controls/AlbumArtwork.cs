using System.Windows;
using System.Windows.Media;

namespace XTONE.Controls;

/// <summary>Square album art with rounded image clipping and a quiet cover fallback.</summary>
public sealed class AlbumArtwork : FrameworkElement
{
    private static readonly Brush DefaultFallback = Frozen(new SolidColorBrush(Color.FromRgb(72, 88, 109)));
    private static readonly Brush FallbackLight = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(55, 255, 255, 255), 0),
            new(Color.FromArgb(0, 255, 255, 255), 0.48),
            new(Color.FromArgb(74, 0, 0, 0), 1)
        }, new Point(0, 0), new Point(1, 1)));
    private static readonly Brush SoftShape = Frozen(new SolidColorBrush(Color.FromArgb(21, 255, 255, 255)));
    private static readonly Pen FineLine = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(29, 255, 255, 255)), 1));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(AlbumArtwork),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FallbackBrushProperty = DependencyProperty.Register(
        nameof(FallbackBrush), typeof(Brush), typeof(AlbumArtwork),
        new FrameworkPropertyMetadata(DefaultFallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Brush? FallbackBrush
    {
        get => (Brush?)GetValue(FallbackBrushProperty);
        set => SetValue(FallbackBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : 264;
        if (double.IsFinite(availableSize.Height))
            side = Math.Min(side, Math.Max(0, availableSize.Height));
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        drawingContext.PushClip(new RectangleGeometry(bounds, 6, 6));
        if (Source is { } image && image.Width > 0 && image.Height > 0)
        {
            var scale = Math.Max(bounds.Width / image.Width, bounds.Height / image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            drawingContext.DrawImage(image, new Rect(
                (bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height));
        }
        else
        {
            drawingContext.DrawRectangle(FallbackBrush ?? DefaultFallback, null, bounds);
            drawingContext.DrawRectangle(FallbackLight, null, bounds);
            drawingContext.DrawEllipse(SoftShape, null,
                new Point(bounds.Width * 0.19, bounds.Height * 0.98),
                bounds.Width * 0.7, bounds.Height * 0.7);
            drawingContext.DrawEllipse(null, FineLine,
                new Point(bounds.Width * 0.79, bounds.Height * 0.25),
                bounds.Width * 0.47, bounds.Height * 0.47);
            drawingContext.DrawLine(FineLine,
                new Point(0, bounds.Height * 0.66), new Point(bounds.Width, bounds.Height * 0.66));
        }
        drawingContext.Pop();
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
