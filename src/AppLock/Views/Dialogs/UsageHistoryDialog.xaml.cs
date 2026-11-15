using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AppLock.Services;
using AppLock.Services.SpeedMeter;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace AppLock.Views.Dialogs;

/// <summary>
/// Daily / weekly / monthly usage chart with totals and CSV export. Reads only the local usage store.
/// </summary>
public sealed partial class UsageHistoryDialog : ContentDialog
{
    private static readonly Color DownColor = Color.FromArgb(255, 74, 222, 128);
    private static readonly Color UpColor = Color.FromArgb(255, 96, 165, 250);

    private readonly UsageStore _store;
    private List<UsagePoint> _points = new();

    public UsageHistoryDialog(UsageStore store)
    {
        InitializeComponent();

        // Follow the app's light/dark choice instead of the system default. The XamlRoot only
        // exists once the dialog is shown, so the final value is taken from the page it opens over.
        RequestedTheme = ServiceContainer.CurrentTheme;

        Loaded += (_, _) =>
        {
            if (XamlRoot?.Content is FrameworkElement root)
            {
                RequestedTheme = root.ActualTheme;
            }
        };

        _store = store;

        AddRange("Last 7 days", "7d");
        AddRange("Last 30 days (by week)", "30d");
        AddRange("Last 12 months", "12m");

        RangeCombo.SelectedIndex = 0;
        Loaded += (_, _) => DrawChart();
    }

    private void AddRange(string label, string tag)
        => RangeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

    private void RangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (RangeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "7d";

        _points = tag switch
        {
            "30d" => _store.Weekly(5),
            "12m" => _store.Monthly(12),
            _ => _store.Daily(7)
        };

        DrawChart();
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();

        if (_points.Count == 0) return;

        var totalDown = _points.Sum(p => p.BytesDown);
        var totalUp = _points.Sum(p => p.BytesUp);
        var total = totalDown + totalUp;

        TotalsText.Text = total == 0
            ? "No traffic recorded in this period."
            : $"Total ↓ {FormatBytes(totalDown)} · ↑ {FormatBytes(totalUp)} · {FormatBytes(total)} combined";

        EmptyText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;

        var width = ChartCanvas.ActualWidth > 0 ? ChartCanvas.ActualWidth : 440;
        var height = ChartCanvas.ActualHeight > 0 ? ChartCanvas.ActualHeight : 170;

        // Leave room for the value scale on the left and labels underneath.
        const double leftPad = 44;
        const double bottomPad = 22;
        var plotWidth = Math.Max(10, width - leftPad);
        var plotHeight = Math.Max(10, height - bottomPad);

        var max = _points.Max(p => p.Total);
        if (max <= 0) max = 1;

        var slot = plotWidth / _points.Count;
        var barWidth = Math.Max(4, Math.Min(38, slot * 0.62));

        // Three guide lines make the bars readable without a full charting dependency.
        for (var line = 0; line <= 2; line++)
        {
            var y = line * plotHeight / 2;
            var guide = new Line
            {
                X1 = leftPad,
                X2 = leftPad + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)),
                StrokeThickness = 1
            };

            ChartCanvas.Children.Add(guide);

            var label = new TextBlock
            {
                Text = FormatBytes((long)(max * (1 - line / 2.0))),
                FontSize = 10,
                Foreground = TotalsText.Foreground
            };

            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, Math.Max(0, y - 7));
            ChartCanvas.Children.Add(label);
        }

        var labelEvery = Math.Max(1, (int)Math.Ceiling(_points.Count / 8.0));

        for (var index = 0; index < _points.Count; index++)
        {
            var point = _points[index];

            var downHeight = plotHeight * (point.BytesDown / (double)max);
            var upHeight = plotHeight * (point.BytesUp / (double)max);
            var x = leftPad + slot * index + (slot - barWidth) / 2;

            var downBar = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(0, downHeight),
                Fill = new SolidColorBrush(DownColor),
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(downBar, x);
            Canvas.SetTop(downBar, plotHeight - downHeight);
            ChartCanvas.Children.Add(downBar);

            var upBar = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(0, upHeight),
                Fill = new SolidColorBrush(UpColor),
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(upBar, x);
            Canvas.SetTop(upBar, plotHeight - downHeight - upHeight);
            ChartCanvas.Children.Add(upBar);

            // A transparent hit area per column: hovering (or tapping) a bar shows its own values.
            var hit = new Rectangle
            {
                Width = slot,
                Height = height,
                Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
            };
            Canvas.SetLeft(hit, leftPad + slot * index);
            Canvas.SetTop(hit, 0);

            ToolTipService.SetToolTip(hit, new ToolTip
            {
                Content = $"{point.Label}\n↓ {FormatBytes(point.BytesDown)}   ↑ {FormatBytes(point.BytesUp)}"
            });

            ChartCanvas.Children.Add(hit);

            if (index % labelEvery != 0) continue;

            var caption = new TextBlock
            {
                Text = point.Label,
                FontSize = 10,
                Foreground = TotalsText.Foreground
            };

            Canvas.SetLeft(caption, Math.Max(0, x - 4));
            Canvas.SetTop(caption, plotHeight + 4);
            ChartCanvas.Children.Add(caption);
        }
    }

    private void Export_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open so the user can pick another range afterwards.
        args.Cancel = true;

        try
        {
            // Native dialog: the WinRT picker needs package identity and silently failed here.
            var path = NativeFileDialog.Save(
                "Export data usage",
                "CSV file (*.csv)\0*.csv\0All files (*.*)\0*.*\0",
                "csv",
                $"applock-data-usage-{DateTime.Today:yyyy-MM-dd}.csv");

            if (path is null) return;

            System.IO.File.WriteAllText(path, _store.BuildCsv());
            TotalsText.Text = $"Exported to {path}";
        }
        catch (Exception ex)
        {
            TotalsText.Text = $"Export failed: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value.ToString(index == 0 ? "F0" : "F1", CultureInfo.CurrentCulture)} {units[index]}";
    }
}
