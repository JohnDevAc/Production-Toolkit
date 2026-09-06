using System.Windows;
using System.Windows.Controls;

namespace ToolkitLauncher;

// Equal columns and equal card heights within each row. A detailed environment
// card should not add empty space to every other row on the dashboard.
public sealed class CardGrid : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(CardGrid),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is int columns && columns > 0);
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    private double[] rowHeights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 1120 : availableSize.Width;
        var cellWidth = width / Columns;
        rowHeights = new double[(InternalChildren.Count + Columns - 1) / Columns];
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var child = InternalChildren[index];
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeights[index / Columns] = Math.Max(rowHeights[index / Columns], child.DesiredSize.Height);
        }
        return new(width, rowHeights.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0d;
        var cellWidth = finalSize.Width / Columns;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var row = index / Columns;
            if (index > 0 && index % Columns == 0) top += rowHeights[row - 1];
            InternalChildren[index].Arrange(new Rect(index % Columns * cellWidth, top, cellWidth, rowHeights[row]));
        }
        return finalSize;
    }
}
