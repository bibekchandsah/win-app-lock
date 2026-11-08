using System;
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
        double rowHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }
}

