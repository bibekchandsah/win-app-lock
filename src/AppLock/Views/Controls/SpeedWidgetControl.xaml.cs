using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AppLock.Models;
using AppLock.Services.SpeedMeter;
using AppLock.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AppLock.Views.Controls;

/// <summary>
/// The speed widget's content. Hosted by the floating window and by the settings preview,
/// so every appearance change is visible instantly without the widget being enabled.
/// </summary>
public sealed partial class SpeedWidgetControl : UserControl
{
    private static readonly Color DefaultBackground = Color.FromArgb(255, 0x20, 0x20, 0x20);
    private static readonly Color DefaultText = Colors.White;
    private static readonly Color DefaultDown = Color.FromArgb(255, 0x4A, 0xDE, 0x80);
    private static readonly Color DefaultUp = Color.FromArgb(255, 0x60, 0xA5, 0xFA);
    private static readonly Color WarnColor = Color.FromArgb(255, 0xE8, 0x11, 0x23);

    private const int SparkSamples = 60;
    private const double SparkWidth = 140;
    private const double SparkHeight = 22;

    /// <summary>Row order used when the user has not rearranged anything.</summary>
    private static readonly string[] DefaultRowOrder = { "cpu", "ram", "disk", "gpu", "battery" };

    private readonly Queue<double> _downHistory = new();
    private readonly Queue<double> _upHistory = new();
    private readonly Dictionary<string, TextBlock> _rowValues = new();

    private SystemStats? _lastStats;

    public SpeedWidgetControl()
    {
        InitializeComponent();
        ApplyAppearance(ServiceContainer.Settings.SpeedMeter);
    }

    /// <summary>Re-applies every appearance setting: preset, colors, opacity, scale, layout, font.</summary>
    public void ApplyAppearance(SpeedMeterSettings settings)
    {
        var preset = settings.ThemePreset ?? "Glass";
        var isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;

        var scale = Math.Clamp(settings.ScalePercent, 50, 250) / 100.0;
        var opacity = Math.Clamp(settings.BackgroundOpacityPercent, 0, 100) / 100.0;
        var radius = Math.Clamp(settings.CornerRadiusPx, 0, 40);

        // Background: the preset decides the base color, the opacity setting decides its alpha.
        var background = ParseColor(settings.BackgroundColor, DefaultBackground);
        var transparent = false;

        switch (preset)
        {
            case "Transparent":
                transparent = true;
                break;
            case "FollowWindows":
                background = isDark ? Color.FromArgb(255, 0x20, 0x20, 0x20) : Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
                break;
            case "Solid":
                opacity = 1.0;
                break;
        }

        Card.Background = transparent
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), background.R, background.G, background.B));

        var textColor = preset == "FollowWindows"
            ? (isDark ? Colors.White : Color.FromArgb(255, 0x1A, 0x1A, 0x1A))
            : ParseColor(settings.TextColor, DefaultText);

        var textBrush = new SolidColorBrush(textColor);
        var downBrush = new SolidColorBrush(ParseColor(settings.DownloadColor, DefaultDown));
        var upBrush = new SolidColorBrush(ParseColor(settings.UploadColor, DefaultUp));

        // Border follows the appearance toggle, tinted from the text color so it stays visible.
        var showBorder = settings.ShowBorder && !transparent;
        Card.BorderThickness = showBorder ? new Thickness(1) : new Thickness(0);
        Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, textColor.R, textColor.G, textColor.B));

        Card.CornerRadius = new CornerRadius(radius);
        Card.Padding = settings.Compact
            ? new Thickness(10 * scale, 6 * scale, 10 * scale, 6 * scale)
            : new Thickness(16 * scale, 10 * scale, 16 * scale, 10 * scale);

        Row.Orientation = string.Equals(settings.Layout, "Vertical", StringComparison.OrdinalIgnoreCase)
            ? Orientation.Vertical
            : Orientation.Horizontal;
        Row.Spacing = 16 * scale;
        DownBlock.Spacing = 6 * scale;
        UpBlock.Spacing = 6 * scale;

        var font = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI Variable" : settings.FontFamily);
        var weight = settings.FontWeight switch
        {
            "Bold" => Microsoft.UI.Text.FontWeights.Bold,
            "Normal" => Microsoft.UI.Text.FontWeights.Normal,
            _ => Microsoft.UI.Text.FontWeights.SemiBold
        };

        var fontSize = 14 * scale;
        var iconSize = 12 * scale;
        var numberWidth = 84 * scale;

        foreach (var block in new[] { DownText, UpText })
        {
            block.FontFamily = font;
            block.FontWeight = weight;
            block.FontSize = fontSize;
            block.Width = numberWidth;
            block.Foreground = textBrush;
        }

        foreach (var label in new[] { DownLabel, UpLabel })
        {
            label.FontFamily = font;
            label.FontWeight = weight;
            label.FontSize = 11 * scale;
            label.Visibility = settings.UseIcons ? Visibility.Collapsed : Visibility.Visible;
            label.Foreground = textBrush;
        }

        DownText.Foreground = textBrush;
        UpText.Foreground = textBrush;

        DownIcon.FontSize = iconSize;
        UpIcon.FontSize = iconSize;
        DownIcon.Foreground = downBrush;
        UpIcon.Foreground = upBrush;
        DownIcon.Visibility = settings.UseIcons ? Visibility.Visible : Visibility.Collapsed;
        UpIcon.Visibility = settings.UseIcons ? Visibility.Visible : Visibility.Collapsed;

        // Sparkline and extra rows scale with the widget too.
        SparkCanvas.Visibility = settings.ShowSparkline ? Visibility.Visible : Visibility.Collapsed;
        SparkCanvas.Width = SparkWidth * scale;
        SparkCanvas.Height = SparkHeight * scale;
        DownSpark.Stroke = new SolidColorBrush(ParseColor(settings.DownloadColor, DefaultDown));
        UpSpark.Stroke = new SolidColorBrush(ParseColor(settings.UploadColor, DefaultUp));

        BuildRows(settings, textBrush, 11 * scale, font, textBrush);
        ApplyStatsValues();
    }

    /// <summary>Creates (or recreates) one row per enabled stat, in the configured order.</summary>
    private void BuildRows(SpeedMeterSettings settings, Brush textBrush, double fontSize, FontFamily font, Brush valueBrush)
    {
        ExtraPanel.Children.Clear();
        _rowValues.Clear();

        var enabled = new HashSet<string>();
        if (settings.ShowCpu) enabled.Add("cpu");
        if (settings.ShowRam) enabled.Add("ram");
        if (settings.ShowDisk) enabled.Add("disk");
        if (settings.ShowGpu) enabled.Add("gpu");
        if (settings.ShowBattery) enabled.Add("battery");

        var order = (settings.RowOrder is { Count: > 0 } ? settings.RowOrder : DefaultRowOrder.ToList())
            .Where(enabled.Contains)
            .Distinct()
            .ToList();

        // Anything enabled but missing from the stored order still gets a row.
        order.AddRange(enabled.Where(id => !order.Contains(id)));

        foreach (var id in order)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = RowLabel(id),
                FontSize = fontSize,
                FontFamily = font,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);

            var value = new TextBlock
            {
                Text = "--",
                FontSize = fontSize,
                FontFamily = font,
                Foreground = valueBrush,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(value, 1);

            grid.Children.Add(label);
            grid.Children.Add(value);

            _rowValues[id] = value;
            ExtraPanel.Children.Add(grid);
        }
    }

    private static string RowLabel(string id) => id switch
    {
        "cpu" => "CPU",
        "ram" => "RAM",
        "disk" => "Disk",
        "gpu" => "GPU",
        "battery" => "Battery",
        _ => id
    };

    /// <summary>Shows the latest CPU/RAM/disk/GPU/battery values with warning colors.</summary>
    public void SetStats(SystemStats? stats)
    {
        _lastStats = stats;
        ApplyStatsValues();
    }

    private void ApplyStatsValues()
    {
        if (_rowValues.Count == 0) return;

        var settings = ServiceContainer.Settings.SpeedMeter;
        var stats = _lastStats;

        SetRow("cpu", stats?.CpuPercent, settings.WarnCpuPercent, $"{stats?.CpuPercent ?? 0:F0}%");

        if (stats?.RamPercent is { } ram)
        {
            var ramText = string.Equals(settings.RamFormat, "UsedTotal", StringComparison.OrdinalIgnoreCase)
                ? $"{stats.RamUsedGb ?? 0:F1}/{stats.RamTotalGb ?? 0:F1} GB"
                : $"{ram:F0}%";

            SetRow("ram", ram, settings.WarnRamPercent, ramText);
        }
        else
        {
            SetRow("ram", null, settings.WarnRamPercent, string.Empty);
        }

        SetRow("disk", stats?.DiskPercent, 101, $"{stats?.DiskPercent ?? 0:F0}%");
        SetRow("gpu", stats?.GpuPercent, 101, $"{stats?.GpuPercent ?? 0:F0}%");
        SetRow("battery", stats?.BatteryPercent, 101, $"{stats?.BatteryPercent ?? 0}%");
    }

    private void SetRow(string id, double? value, int warnAbove, string text)
    {
        if (!_rowValues.TryGetValue(id, out var block)) return;

        var settings = ServiceContainer.Settings.SpeedMeter;
        var normalBrush = new SolidColorBrush(ParseColor(settings.TextColor, DefaultText));

        block.Text = text;
        block.Foreground = value.HasValue && value.Value >= warnAbove && warnAbove <= 100
            ? new SolidColorBrush(WarnColor)
            : normalBrush;
    }

    /// <summary>Sets the two rate labels; formatting is done by the caller.</summary>
    public void SetValues(string down, string up)
    {
        DownText.Text = down;
        UpText.Text = up;
    }

    /// <summary>Adds one sample to the mini graph and redraws it.</summary>
    public void AddSparkSample(double downBytesPerSec, double upBytesPerSec)
    {
        if (!ServiceContainer.Settings.SpeedMeter.ShowSparkline) return;

        _downHistory.Enqueue(downBytesPerSec);
        _upHistory.Enqueue(upBytesPerSec);
        while (_downHistory.Count > SparkSamples) _downHistory.Dequeue();
        while (_upHistory.Count > SparkSamples) _upHistory.Dequeue();

        DrawSpark();
    }

    private void DrawSpark()
    {
        var max = Math.Max(1, Math.Max(_downHistory.DefaultIfEmpty().Max(), _upHistory.DefaultIfEmpty().Max()));
        var width = SparkCanvas.Width > 0 ? SparkCanvas.Width : SparkWidth;
        var height = SparkCanvas.Height > 0 ? SparkCanvas.Height : SparkHeight;

        DownSpark.Points = BuildPoints(_downHistory, max, width, height);
        UpSpark.Points = BuildPoints(_upHistory, max, width, height);
    }

    private static PointCollection BuildPoints(IEnumerable<double> values, double max, double width, double height)
    {
        var list = values.ToList();
        var points = new PointCollection();
        if (list.Count < 2) return points;

        var step = width / (SparkSamples - 1);

        for (var i = 0; i < list.Count; i++)
        {
            var x = width - (list.Count - 1 - i) * step;
            var y = height - Math.Clamp(list[i] / max, 0, 1) * (height - 2) - 1;
            points.Add(new Windows.Foundation.Point(x, y));
        }

        return points;
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            var text = value.TrimStart('#');
            if (text.Length == 6)
            {
                return Color.FromArgb(
                    255,
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16));
            }

            if (text.Length == 8)
            {
                return Color.FromArgb(
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16),
                    Convert.ToByte(text.Substring(6, 2), 16));
            }
        }
        catch (Exception)
        {
            // Malformed hex: fall through to the default.
        }

        return fallback;
    }
}
