using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AppLock.Views.Controls;

/// <summary>Section 6.3: a simple calendar heat strip for the Month range.</summary>
public sealed partial class UsageCalendarHeat : UserControl
{
    private static readonly string[] WeekdayLetters = { "M", "T", "W", "T", "F", "S", "S" };

    public UsageCalendarHeat()
    {
        InitializeComponent();
    }

    public event EventHandler<DateOnly>? DaySelected;

    public void SetDays(IReadOnlyList<DayUsage> days, DateOnly today)
    {
        HostGrid.Children.Clear();
        HostGrid.RowDefinitions.Clear();
        HostGrid.ColumnDefinitions.Clear();

        for (int i = 0; i < 7; i++)
            HostGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        HostGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < 7; i++)
        {
            var header = new TextBlock
            {
                Text = WeekdayLetters[i],
                Style = (Style)Resources["HeatWeekdayLabel"]
            };
            Grid.SetColumn(header, i);
            HostGrid.Children.Add(header);
        }

        if (days.Count == 0) return;

        int max = Math.Max(1, days.Max(d => d.Seconds));
        int offset = ((int)days[0].Day.DayOfWeek + 6) % 7;
        int row = 1;
        int column = offset;
        HostGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        foreach (var day in days)
        {
            if (column > 6)
            {
                column = 0;
                row++;
                HostGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            double ratio = day.Seconds / (double)max;
            double opacity = day.Seconds <= 0 ? 0 : ratio < 0.25 ? 0.2 : ratio < 0.55 ? 0.4 : ratio < 0.8 ? 0.65 : 0.9;

            var content = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(new TextBlock
            {
                Text = day.Day.Day.ToString(),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = day.Seconds > 0 ? UsageFormat.ShortDuration(day.Seconds) : string.Empty,
                FontSize = 9,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var fill = new Border
            {
                Background = UsagePalette.BrushFor(0),
                Opacity = opacity,
                CornerRadius = new CornerRadius(4)
            };

            var cell = new Grid();
            cell.Children.Add(fill);
            cell.Children.Add(content);

            var button = new Button
            {
                Content = cell,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 34,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Tag = day.Day,
                BorderBrush = day.Day == today
                    ? UsagePalette.BrushFor(0)
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = day.Day == today ? new Thickness(1) : new Thickness(0)
            };

            string tooltip = $"{day.Day:ddd d MMM} · {(day.Seconds > 0 ? UsageFormat.Duration(day.Seconds) : "No data")}";
            ToolTipService.SetToolTip(button, tooltip);
            AutomationProperties.SetName(button, tooltip);
            button.Click += (_, _) => DaySelected?.Invoke(this, day.Day);

            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            HostGrid.Children.Add(button);

            column++;
        }
    }
}
