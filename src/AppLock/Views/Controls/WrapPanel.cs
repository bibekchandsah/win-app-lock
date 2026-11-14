using System;
using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views.Controls;

/// <summary>
/// A responsive panel that arranges child elements sequentially from left to right,
/// wrapping to the next line when reaching the edge of the container.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0.0, OnSpacingChanged));

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0.0, OnSpacingChanged));

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0;
        double rowHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;

        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var size = child.DesiredSize;

            if (x > 0 && x + size.Width > availableSize.Width)
            {
                totalWidth = Math.Max(totalWidth, x - HorizontalSpacing);
                totalHeight += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        totalWidth = Math.Max(totalWidth, x > 0 ? x - HorizontalSpacing : 0);
        totalHeight += rowHeight;

        return new Size(
            double.IsInfinity(availableSize.Width) ? totalWidth : availableSize.Width,
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        var row = new List<UIElement>();

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y = ArrangeRow(row, y);
                x = 0;
                row.Clear();
            }

            row.Add(child);
            x += size.Width + HorizontalSpacing;
        }

        ArrangeRow(row, y);
        return finalSize;
    }

    /// <summary>
    /// Arranges one row and returns the Y where the next row starts. Every child in the row is
    /// given the row's tallest height, so cards whose content is shorter fill their cell instead
    /// of leaving a gap below the neighbouring card.
    /// </summary>
    private double ArrangeRow(List<UIElement> row, double y)
    {
        if (row.Count == 0) return y;

        double rowHeight = 0;
        foreach (var child in row)
        {
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        double x = 0;
        foreach (var child in row)
        {
            child.Arrange(new Rect(x, y, child.DesiredSize.Width, rowHeight));
            x += child.DesiredSize.Width + HorizontalSpacing;
        }

        return y + rowHeight + VerticalSpacing;
    }
}

