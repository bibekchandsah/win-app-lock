using System;
using System.Collections.Generic;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
    private ToolTip? _hoverTip;
    private double _cx;
    private double _cy;
    private double _outerRadius;
    private double _innerRadius;
    private string _centerValue = "0m";
    private string _centerLabel = "screen time";

    public UsageDonutChart()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild();
        RootGrid.PointerMoved += RootGrid_PointerMoved;
        RootGrid.PointerExited += (_, _) => CloseHoverTip();
    }

    public void SetSlices(IReadOnlyList<DonutSlice> slices, string centerValue, string centerLabel)
    {
        _slices = slices ?? Array.Empty<DonutSlice>();
        _centerValue = centerValue;
        _centerLabel = centerLabel;
        ShowCenterTotal();
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
            _cx = cx;
            _cy = cy;
            _outerRadius = radius;
            _innerRadius = Math.Max(0, radius - thickness);

            SliceHost.Children.Add(new ShapePath
            {
                StrokeThickness = thickness,
                Opacity = 0.35,
                Style = (Style)Resources["DonutNeutralSlice"],
                Data = ArcGeometry(cx, cy, radius - thickness / 2, 0, 359.9)
            });
            return;
        }

        double ringRadius = radius - thickness / 2;
        double cursor = 0;
        _cx = cx;
        _cy = cy;
        _outerRadius = ringRadius + thickness / 2;
        _innerRadius = Math.Max(0, ringRadius - thickness / 2);

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
                StrokeThickness = thickness,
                Data = ArcGeometry(cx, cy, ringRadius, start, end)
            };

            if (slice.Brush is { } brush)
            {
                path.Stroke = brush;
                path.StrokeStartLineCap = PenLineCap.Flat;
                path.StrokeEndLineCap = PenLineCap.Flat;
            }
            else
            {
                path.Style = (Style)Resources["DonutNeutralSlice"];
            }

            AutomationProperties.SetName(path, $"{slice.Label}, {UsageFormat.Duration(slice.Seconds)}");
            SliceHost.Children.Add(path);

            cursor += sweep;
        }
    }

    /// <summary>
    /// Reports the slice under the pointer as "app · duration". Shape tooltips did not fire on the
    /// stroked arcs, so the hit test is done from the pointer angle instead.
    /// </summary>
    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        long total = 0;
        foreach (var slice in _slices) total += Math.Max(0, slice.Seconds);
        if (total <= 0 || _outerRadius <= 0)
        {
            CloseHoverTip();
            return;
        }

        var point = e.GetCurrentPoint(RootGrid).Position;
        double dx = point.X - _cx;
        double dy = point.Y - _cy;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < _innerRadius - 2 || distance > _outerRadius + 2)
        {
            CloseHoverTip();
            return;
        }

        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90.0;
        if (angle < 0) angle += 360.0;

        double cursor = 0;
        DonutSlice? hit = null;
        foreach (var slice in _slices)
        {
            if (slice.Seconds <= 0) continue;

            double sweep = slice.Seconds / (double)total * 360.0;
            if (angle >= cursor && angle < cursor + sweep)
            {
                hit = slice;
                break;
            }

            cursor += sweep;
        }

        if (hit is not { } value)
        {
            CloseHoverTip();
            return;
        }

        // The centre follows the pointer, so the hovered app and its duration are readable at a glance.
        CenterValue.Text = UsageFormat.Duration(value.Seconds);
        CenterLabel.Text = value.Label;

        string text = $"{value.Label} · {UsageFormat.Duration(value.Seconds)}";
        if (_hoverTip is { IsOpen: true } && Equals(_hoverTip.Content, text)) return;

        try
        {
            _hoverTip ??= new ToolTip { Placement = PlacementMode.Mouse, PlacementTarget = RootGrid };
            _hoverTip.XamlRoot = RootGrid.XamlRoot;
            _hoverTip.Content = text;
            _hoverTip.IsOpen = true;
        }
        catch
        {
            // Ignore any transient layout/visual-tree attachment issues
        }
    }

    private void CloseHoverTip()
    {
        ShowCenterTotal();

        try
        {
            if (_hoverTip is { IsOpen: true })
                _hoverTip.IsOpen = false;
        }
        catch
        {
            // Ignore
        }
    }

    private void ShowCenterTotal()
    {
        CenterValue.Text = _centerValue;
        CenterLabel.Text = _centerLabel;
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
