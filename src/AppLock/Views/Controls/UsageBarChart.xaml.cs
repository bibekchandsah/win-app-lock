using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AppLock.Views.Controls;

/// <summary>
/// Section 6: charts drawn with XAML primitives instead of a charting dependency. Each bar is a
/// focusable button with an automation name and a tooltip, so the chart works with a keyboard and
/// a screen reader as well as with a mouse.
/// </summary>
public sealed partial class UsageBarChart : UserControl
{
    private const double LabelRowHeight = 18;

    private IReadOnlyList<UsageBar> _bars = Array.Empty<UsageBar>();
    private double _max = 3600;
    private double? _average;

    public UsageBarChart()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PositionAverageLine();
    }

    /// <summary>Raised when a day bar is clicked so the Week view can filter its app list.</summary>
    public event EventHandler<DateOnly>? DayClicked;

    public void SetBars(IReadOnlyList<UsageBar> bars, double max, double? average)
    {
        _bars = bars ?? Array.Empty<UsageBar>();
        _max = max > 0 ? max : 1;
        _average = average;
        Rebuild();
    }

    private void Rebuild()
    {
        PlotHost.Children.Clear();
        Overlay.Children.Clear();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LabelRowHeight) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < _bars.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AxisTop.Text = UsageFormat.ShortDuration((long)_max);
        AxisMiddle.Text = UsageFormat.ShortDuration((long)(_max / 2));

        for (int i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];

            var cell = new Grid { Margin = new Thickness(1, 0, 1, 0), MinHeight = 0 };
            cell.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(Math.Max(0.001, _max - Math.Min(bar.Value, _max)), GridUnitType.Star)
            });
            cell.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(Math.Max(0.001, Math.Min(bar.Value, _max)), GridUnitType.Star)
            });

            var visual = BuildBarVisual(bar);
            Grid.SetRow(visual, 1);
            cell.Children.Add(visual);

            if (bar.Future)
                visual.Opacity = 0.25;

            var button = new Button
            {
                Content = cell,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Tag = bar
            };

            ToolTipService.SetToolTip(button, bar.Tooltip);
            AutomationProperties.SetName(button, AutomationText(bar));
            button.Click += Bar_Click;

            Grid.SetColumn(button, i + 1);
            grid.Children.Add(button);

            var label = new TextBlock
            {
                Text = bar.Label,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = UsagePalette.SecondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, i + 1);
            grid.Children.Add(label);
        }

        PlotHost.Children.Add(grid);
        PositionAverageLine();
    }

    private static Grid BuildBarVisual(UsageBar bar)
    {
        var host = new Grid();

        if (bar.Value <= 0 || bar.Segments.Count == 0)
        {
            host.Children.Add(new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = UsagePalette.Neutral,
                Opacity = 0.25
            });
            return host;
        }

        var stack = new Grid();
        foreach (var segment in bar.Segments)
        {
            stack.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(Math.Max(0.001, segment.Seconds), GridUnitType.Star)
            });
        }

        for (int i = 0; i < bar.Segments.Count; i++)
        {
            var segment = bar.Segments[i];
            var part = new Border
            {
                Background = segment.ColorIndex < 0 ? UsagePalette.Neutral : UsagePalette.BrushFor(segment.ColorIndex),
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 1, 0, 0)
            };

            ToolTipService.SetToolTip(part, segment.Tooltip);
            Grid.SetRow(part, i);
            stack.Children.Add(part);
        }

        host.Children.Add(stack);

        if (bar.Incomplete)
        {
            var coverageStrip = new Border
            {
                Height = 3,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = UsagePalette.Warning,
                Opacity = 0.7
            };
            ToolTipService.SetToolTip(coverageStrip, "AppLock was not running for part of this hour.");
            host.Children.Add(coverageStrip);
        }

        return host;
    }

    private static string AutomationText(UsageBar bar)
    {
        string duration = UsageFormat.Duration((long)bar.Value);
        if (bar.Future) return $"{bar.Label}, not yet";
        if (bar.Value <= 0) return $"{bar.Label}, no recorded time";

        string top = bar.Segments.Count > 0 ? $", mostly {bar.Segments[0].Label}" : string.Empty;
        return $"{bar.Label}, {duration}{top}";
    }

    private void Bar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UsageBar bar } && bar.Day.HasValue)
            DayClicked?.Invoke(this, bar.Day.Value);
    }

    private void ChartRoot_SizeChanged(object sender, SizeChangedEventArgs e) => PositionAverageLine();

    private void PositionAverageLine()
    {
        Overlay.Children.Clear();
        if (_average is not { } average || _max <= 0) return;

        double width = PlotHost.ActualWidth;
        double height = PlotHost.ActualHeight - LabelRowHeight;
        if (width <= 0 || height <= 0) return;

        double ratio = Math.Clamp(average / _max, 0, 1);
        double y = height - (ratio * height);

        Overlay.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = y,
            Y2 = y,
            Stroke = UsagePalette.SecondaryText,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            Opacity = 0.7
        });
    }
}
