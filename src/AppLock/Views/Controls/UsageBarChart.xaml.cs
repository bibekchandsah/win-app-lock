using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AppLock.Views.Controls;

/// <summary>
/// Section 6: charts drawn with XAML primitives instead of a charting dependency. Bar heights are
/// computed in pixels from the measured plot area, because star-sized rows inside a button's
/// content measured to zero on first layout and made the bars disappear.
/// </summary>
public sealed partial class UsageBarChart : UserControl
{
    private const double LabelRowHeight = 18;
    private const double MinimumPlotHeight = 120;

    private IReadOnlyList<UsageBar> _bars = Array.Empty<UsageBar>();
    private double _max = 3600;
    private double? _average;
    private double _lastPlotHeight = -1;

    public UsageBarChart()
    {
        InitializeComponent();
        SizeChanged += ChartRoot_SizeChanged;
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

    private double PlotHeight => Math.Max(MinimumPlotHeight, PlotHost.ActualHeight - LabelRowHeight);

    private void Rebuild()
    {
        PlotHost.Children.Clear();
        Overlay.Children.Clear();
        _lastPlotHeight = PlotHost.ActualHeight;

        double plotHeight = PlotHeight;

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

            var cell = new Grid
            {
                Height = plotHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };
            cell.Children.Add(BuildBarVisual(bar, plotHeight));

            if (bar.Incomplete)
            {
                var coverageStrip = new Border
                {
                    Height = 3,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = UsagePalette.Warning,
                    Opacity = 0.8
                };
                ToolTipService.SetToolTip(coverageStrip, "AppLock was not running for part of this hour.");
                cell.Children.Add(coverageStrip);
            }

            if (bar.Future)
                cell.Opacity = 0.3;

            Grid.SetColumn(cell, i + 1);
            grid.Children.Add(cell);

            // Transparent hit target above the bar: Button content does not layout reliably here.
            var button = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
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
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            };
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, i + 1);
            grid.Children.Add(label);
        }

        PlotHost.Children.Add(grid);
        PositionAverageLine();
    }

    private Grid BuildBarVisual(UsageBar bar, double plotHeight)
    {
        var host = new Grid();

        if (bar.Value <= 0 || bar.Segments.Count == 0)
        {
            host.Children.Add(new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = UsagePalette.Neutral,
                Opacity = 0.35,
                CornerRadius = new CornerRadius(1)
            });
            return host;
        }

        double barHeight = Math.Max(3, plotHeight * (Math.Min(bar.Value, _max) / _max));
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Spacing = 0
        };

        foreach (var segment in bar.Segments)
        {
            double share = bar.Value > 0 ? segment.Seconds / bar.Value : 0;
            double height = Math.Max(1, barHeight * share);

            var part = new Border
            {
                Height = height,
                Background = segment.ColorIndex < 0
                    ? UsagePalette.Neutral
                    : UsagePalette.BrushFor(segment.ColorIndex),
                CornerRadius = new CornerRadius(1)
            };

            ToolTipService.SetToolTip(part, segment.Tooltip);
            stack.Children.Add(part);
        }

        host.Children.Add(stack);
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

    private void ChartRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The first layout gives the plot its real height; rebuild once it is known, and on resize.
        if (Math.Abs(PlotHost.ActualHeight - _lastPlotHeight) > 2 && _bars.Count > 0)
        {
            Rebuild();
            return;
        }

        PositionAverageLine();
    }

    private void PositionAverageLine()
    {
        Overlay.Children.Clear();
        if (_average is not { } average || _max <= 0) return;

        double width = PlotHost.ActualWidth;
        double height = PlotHeight;
        if (width <= 0) return;

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
