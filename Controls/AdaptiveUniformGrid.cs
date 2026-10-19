using System.Windows;
using System.Windows.Controls.Primitives;

namespace Chistara.Controls;

/// <summary>
/// A UniformGrid that chooses the largest useful column count for the available
/// width. Album and artist cards reach four columns on wide layouts without
/// becoming unreadably narrow on smaller windows.
/// </summary>
public sealed class AdaptiveUniformGrid : UniformGrid
{
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(
        nameof(MinimumItemWidth), typeof(double), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(158d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumItemWidth
    {
        get => (double)GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public int MaximumColumns
    {
        get => (int)GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsFinite(constraint.Width) ? constraint.Width : ActualWidth;
        var minimum = Math.Max(96, MinimumItemWidth);
        var maximum = Math.Max(1, MaximumColumns);
        var columns = width > 0 ? Math.Clamp((int)Math.Floor(width / minimum), 1, maximum) : 1;
        if (Columns != columns) Columns = columns;
        return base.MeasureOverride(constraint);
    }
}
