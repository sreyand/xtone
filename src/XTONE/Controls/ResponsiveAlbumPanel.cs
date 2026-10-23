using System.Windows;
using System.Windows.Controls;

namespace XTONE.Controls;

/// <summary>Shares the available width between a small, adaptive number of album columns.</summary>
public sealed class ResponsiveAlbumPanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveAlbumPanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        IsNonNegativeFinite);

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(ResponsiveAlbumPanel),
        new FrameworkPropertyMetadata(30d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        IsNonNegativeFinite);

    public static readonly DependencyProperty TargetCardWidthProperty = DependencyProperty.Register(
        nameof(TargetCardWidth), typeof(double), typeof(ResponsiveAlbumPanel),
        new FrameworkPropertyMetadata(264d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number > 0);

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    public double TargetCardWidth
    {
        get => (double)GetValue(TargetCardWidthProperty);
        set => SetValue(TargetCardWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren();
        if (children.Length == 0)
            return new Size();

        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : Math.Min(4, children.Length) * (TargetCardWidth + Gap) - Gap;
        var columns = ColumnCount(width);
        var cardWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        foreach (var child in children)
            child.Measure(new Size(cardWidth, double.PositiveInfinity));

        return new Size(width, RowHeights(children, columns).Sum() + RowGap * ((children.Length - 1) / columns));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren();
        if (children.Length == 0)
            return finalSize;

        var columns = ColumnCount(finalSize.Width);
        var cardWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        // A parent can assign a different width during its arrange pass. Keep square
        // artwork and its card labels measured against that actual finite width.
        foreach (var child in children)
            child.Measure(new Size(cardWidth, double.PositiveInfinity));

        var rowHeights = RowHeights(children, columns);
        double y = 0;
        for (var row = 0; row < rowHeights.Length; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= children.Length)
                    break;

                var logicalColumn = FlowDirection == FlowDirection.RightToLeft ? columns - column - 1 : column;
                children[index].Arrange(new Rect(
                    logicalColumn * (cardWidth + Gap), y, cardWidth, rowHeights[row]));
            }
            y += rowHeights[row] + RowGap;
        }
        return finalSize;
    }

    private int ColumnCount(double width) => Math.Clamp(
        (int)Math.Round((Math.Max(0, width) + Gap) / (TargetCardWidth + Gap), MidpointRounding.AwayFromZero),
        1, 6);

    private UIElement[] VisibleChildren() => InternalChildren.Cast<UIElement>()
        .Where(child => child.Visibility != Visibility.Collapsed).ToArray();

    private static double[] RowHeights(UIElement[] children, int columns)
    {
        var heights = new double[(children.Length + columns - 1) / columns];
        for (var index = 0; index < children.Length; index++)
            heights[index / columns] = Math.Max(heights[index / columns], children[index].DesiredSize.Height);
        return heights;
    }

    private static bool IsNonNegativeFinite(object value) =>
        value is double number && double.IsFinite(number) && number >= 0;
}
