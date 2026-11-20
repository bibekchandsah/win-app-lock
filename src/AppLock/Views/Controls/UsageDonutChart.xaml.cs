using System;
using System.Collections.Generic;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace AppLock.Views.Controls;

/// <summary>
/// The "App share" donut. Segments are stroked arcs drawn from pixel geometry, so the ring scales
/// with its card instead of relying on a charting dependency. Colors come from the shared palette.
/// </summary>
public sealed partial class UsageDonutChart : UserControl
{
    private const double GapDegrees = 2.0;

    private IReadOnlyList<DonutSlice> _slices = Array.Empty<DonutSlice>();

    public UsageDonutChart()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild();
    }

    public void SetSlices(IReadOnlyList<DonutSlice> slices, string centerValue, string centerLabel)
    {
        _slices = slices ?? Array.Empty<DonutSlice>();
        CenterValue.Text = centerValue;
        CenterLabel.Text = centerLabel;
        Rebuild();
    }

    private void Rebuild()
    {
        SliceHost.Children.Clear();

        double width = RootGrid.ActualWidth;
        double height = RootGrid.ActualHeight;
        double size = Math.Min(width, height);

        // The first layout pass has no size yet; SizeChanged calls back once it does.
        if (size <= 20) return;

        double thickness = Math.Clamp(size * 0.15, 12, 28);
        double radius = size / 2 - 2;
        double cx = width / 2;
        double cy = height / 2;

        long total = 0;
        foreach (var slice in _slices) total += Math.Max(0, slice.Seconds);

        if (total <= 0 || _slices.Count == 0)
        {
            SliceHost.Children.Add(new ShapePath
            {
                Stroke = UsagePalette.Neutral,
                StrokeThickness = thickness,
                Opacity = 0.35,
                Data = ArcGeometry(cx, cy, radius - thickness / 2, 0, 359.9)
            });
            return;
        }

        double ringRadius = radius - thickness / 2;
        double cursor = 0;

        foreach (var slice in _slices)
        {
            if (slice.Seconds <= 0) continue;

            double sweep = slice.Seconds / (double)total * 360.0;
            if (sweep <= 0) continue;

            double gap = Math.Min(GapDegrees, sweep * 0.3);
            double start = cursor + gap / 2;
            double end = Math.Min(cursor + sweep - gap / 2, cursor + 359.5);

            var path = new ShapePath
            {
                Stroke = slice.Brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
                Data = ArcGeometry(cx, cy, ringRadius, start, end)
            };

            ToolTipService.SetToolTip(path, $"{slice.Label} · {UsageFormat.Duration(slice.Seconds)}");
            SliceHost.Children.Add(path);

            cursor += sweep;
        }
    }

    /// <summary>Clockwise arc from the top of the circle; 0° is 12 o'clock.</summary>
    private static PathGeometry ArcGeometry(double cx, double cy, double radius, double startDegrees, double endDegrees)
    {
        var figure = new PathFigure
        {
            StartPoint = PointOn(cx, cy, radius, startDegrees),
            IsClosed = false,
            IsFilled = false
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = PointOn(cx, cy, radius, endDegrees),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = endDegrees - startDegrees > 180
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointOn(double cx, double cy, double radius, double degrees)
    {
        double radians = (degrees - 90) * Math.PI / 180.0;
        return new Point(cx + radius * Math.Cos(radians), cy + radius * Math.Sin(radians));
    }
}
