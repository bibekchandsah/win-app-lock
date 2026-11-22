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
using Microsoft.UI.Xaml.Documents;
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

    private long _todayDown;
    private long _todayUp;
    private bool _metered;
    private bool _vpn;

    /// <summary>True when the host window actually paints a Mica/acrylic backdrop behind us.</summary>
    private bool _backdropAvailable = true;

    private readonly Queue<double> _downHistory = new();
    private readonly Queue<double> _upHistory = new();
    private readonly Dictionary<string, TextBlock> _rowValues = new();

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _clockTimer;

    private SystemStats? _lastStats;
    private TopAppsStats? _topApps;
    private double _downBytesPerSec;
    private double _upBytesPerSec;

    private string _downFormatted = "0 B/s";
    private string _upFormatted = "0 B/s";

    private Brush _downBrush = new SolidColorBrush(DefaultDown);
    private Brush _upBrush = new SolidColorBrush(DefaultUp);
    private Brush _textBrush = new SolidColorBrush(Colors.White);
    private FontFamily _fontFamily = new("Segoe UI Variable");
    private Windows.UI.Text.FontWeight _fontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
    private double _scale = 1.0;
    private double _textScale = 1.0;

    public SpeedWidgetControl()
    {
        InitializeComponent();

        SparkCanvas.SizeChanged += (_, _) => DrawSpark();

        // The clock has its own one-second timer; it only runs while the control is in the tree.
        Loaded += (_, _) =>
        {
            UpdateClock();
            if (ServiceContainer.Settings.SpeedMeter.ShowClock) StartClock();
        };
        Unloaded += (_, _) => _clockTimer?.Stop();

        ApplyAppearance(ServiceContainer.Settings.SpeedMeter);
    }

    /// <summary>
    /// Told by the host window whether a system backdrop is really present, so "Follow Windows" can
    /// stay translucent over Mica and fall back to a solid card when it cannot.
    /// </summary>
    public void SetBackdropAvailable(bool available)
    {
        if (_backdropAvailable == available) return;

        _backdropAvailable = available;
        ApplyAppearance(ServiceContainer.Settings.SpeedMeter);
    }

    /// <summary>Re-applies every appearance setting: preset, colors, opacity, scale, layout, font.</summary>
    public void ApplyAppearance(SpeedMeterSettings settings)
    {
        var preset = settings.ThemePreset ?? "Glass";
        var isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;

        var scale = Math.Clamp(settings.ScalePercent, 50, 250) / 100.0;

        // Text size is its own setting, so a user can enlarge the numbers without enlarging the card.
        var textScale = scale * Math.Clamp(settings.FontSizePercent, 50, 250) / 100.0;
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
                // Follow Windows is a Mica surface: the window paints the backdrop and the card only
                // adds a faint theme tint. Filling it with #202020 is what made Mica look black.
                if (_backdropAvailable)
                {
                    background = isDark ? Colors.White : Colors.Black;
                    opacity = opacity * 0.2;
                }
                else
                {
                    // No backdrop on this system: keep the readable theme card instead of nothing.
                    background = isDark ? Color.FromArgb(255, 0x20, 0x20, 0x20) : Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
                }
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

        _downBrush = downBrush;
        _upBrush = upBrush;
        _textBrush = textBrush;

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
        DownBlock.Spacing = 6 * textScale;
        UpBlock.Spacing = 6 * textScale;

        var font = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI Variable" : settings.FontFamily);
        var weight = settings.FontWeight switch
        {
            "Bold" => Microsoft.UI.Text.FontWeights.Bold,
            "Normal" => Microsoft.UI.Text.FontWeights.Normal,
            _ => Microsoft.UI.Text.FontWeights.SemiBold
        };

        var fontSize = 14 * textScale;
        var iconSize = 12 * textScale;
        var numberWidth = 84 * textScale;

        foreach (var block in new[] { DownText, UpText })
        {
            block.FontFamily = font;
            block.FontWeight = weight;
            block.FontSize = fontSize;
            block.Width = numberWidth;
        }

        // The unit is part of the reading, so it is re-split whenever the size changes.
        SetRateText(DownText, _downFormatted);
        SetRateText(UpText, _upFormatted);

        foreach (var label in new[] { DownLabel, UpLabel })
        {
            label.FontFamily = font;
            label.FontWeight = weight;
            label.FontSize = 11 * textScale;
            label.Visibility = settings.UseIcons ? Visibility.Collapsed : Visibility.Visible;
            label.Foreground = textBrush;
        }

        ApplyRateColors();

        DownIcon.FontSize = iconSize;
        UpIcon.FontSize = iconSize;
        DownIcon.Foreground = downBrush;
        UpIcon.Foreground = upBrush;
        DownIcon.Visibility = settings.UseIcons ? Visibility.Visible : Visibility.Collapsed;
        UpIcon.Visibility = settings.UseIcons ? Visibility.Visible : Visibility.Collapsed;

        // Sparkline and extra rows scale with the widget too. The canvas stretches to the card
        // width (it used to be pinned to a fixed width, which cut the graph short in horizontal
        // layout); DrawSpark reads its actual width.
        SparkCanvas.Visibility = settings.ShowSparkline ? Visibility.Visible : Visibility.Collapsed;
        SparkCanvas.HorizontalAlignment = HorizontalAlignment.Stretch;
        SparkCanvas.Height = SparkHeight * scale;
        DownSpark.Stroke = new SolidColorBrush(ParseColor(settings.DownloadColor, DefaultDown));
        UpSpark.Stroke = new SolidColorBrush(ParseColor(settings.UploadColor, DefaultUp));

        MeteredBadge.FontFamily = font;
        MeteredBadge.FontWeight = weight;
        MeteredBadge.FontSize = 9 * textScale;
        MeteredBadge.Foreground = textBrush;

        VpnBadge.Visibility = _vpn ? Visibility.Visible : Visibility.Collapsed;

        // The badge's icon and caption are plain children, so the weight is set on each of them.
        foreach (var child in VpnBadge.Children)
        {
            switch (child)
            {
                case TextBlock text:
                    text.FontWeight = weight;
                    break;
                case FontIcon icon:
                    icon.FontWeight = weight;
                    break;
            }
        }

        _fontFamily = font;
        _fontWeight = weight;
        _scale = scale;
        _textScale = textScale;

        // Date and time under the speeds. Horizontal layout keeps one line; vertical stacks the
        // date over the time, so a tall narrow widget does not get wider every second.
        var vertical = string.Equals(settings.Layout, "Vertical", StringComparison.OrdinalIgnoreCase);

        ClockPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ClockPanel.Spacing = vertical ? 0 : 8 * textScale;
        ClockPanel.HorizontalAlignment = HorizontalAlignment.Center;

        ClockDateText.FontFamily = font;
        ClockDateText.FontWeight = weight;
        ClockDateText.FontSize = 10.5 * textScale;
        ClockDateText.Foreground = textBrush;

        ClockTimeText.FontFamily = font;
        ClockTimeText.FontWeight = weight;
        ClockTimeText.FontSize = (settings.Compact ? 11 : 12) * textScale;
        ClockTimeText.Foreground = textBrush;

        HeaderDivider.Background = new SolidColorBrush(Muted(textColor, 0x24));
        UpdateHeaderDivider();

        UpdateClock();

        if (settings.ShowClock)
        {
            StartClock();
        }
        else
        {
            _clockTimer?.Stop();
        }

        UpdateTicker();

        BuildRows(settings, textBrush, 11 * textScale, font, textBrush);
        ApplyStatsValues();
    }

    // ── Readable numbers ────────────────────────────────────────────────────────

    /// <summary>
    /// Splits "129.7 KB/s" into a number and a smaller unit, so the reading reads as a value
    /// rather than one long coloured string. The unit follows the number's colour (and its warning
    /// colour), because it only changes size.
    /// </summary>
    private static void SetRateText(TextBlock block, string text)
    {
        block.Inlines.Clear();

        var split = text.LastIndexOf(' ');
        if (split <= 0)
        {
            block.Inlines.Add(new Run { Text = text });
            return;
        }

        block.Inlines.Add(new Run { Text = text[..split] });
        block.Inlines.Add(new Run
        {
            Text = " " + text[(split + 1)..],
            FontSize = Math.Max(8, block.FontSize * 0.7)
        });
    }

    /// <summary>Today's totals keep the header's colour language: download and upload tinted.</summary>
    private void SetTodayRow()
    {
        if (!_rowValues.TryGetValue("today", out var block)) return;

        block.Inlines.Clear();
        block.Inlines.Add(new Run { Text = "↓" + FormatBytes(_todayDown), Foreground = _downBrush });
        block.Inlines.Add(new Run { Text = "   " });
        block.Inlines.Add(new Run { Text = "↑" + FormatBytes(_todayUp), Foreground = _upBrush });
    }

    /// <summary>The hairline only separates the header when there is something under it.</summary>
    private void UpdateHeaderDivider()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;

        var hasRows = settings.ShowCpu || settings.ShowRam || settings.ShowDisk
                      || settings.ShowGpu || settings.ShowBattery || settings.ShowTodayTotal;

        var needed = settings.ShowSparkline || settings.ShowTopApps || hasRows || _metered || _vpn;
        HeaderDivider.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The same text colour at a lower alpha, for labels and hairlines.</summary>
    private static Color Muted(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    // ── Date and time ───────────────────────────────────────────────────────────

    /// <summary>
    /// Shows the local date and time (with seconds) under the speeds. The date and the time are
    /// separate blocks so each layout can place them without a second line breaking the measure.
    /// </summary>
    private void UpdateClock()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;

        ClockPanel.Visibility = settings.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.ShowClock) return;

        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        var format = culture.DateTimeFormat;

        // Follow the system clock: 'H' in the short time pattern means a 24-hour clock.
        var hour24 = format.ShortTimePattern.Contains('H');
        var timePattern = settings.ShowClockSeconds
            ? (hour24 ? "HH:mm:ss" : "h:mm:ss tt")
            : (hour24 ? "HH:mm" : "h:mm tt");

        var date = settings.ShowClockDate ? now.ToString("d", culture) : string.Empty;

        ClockDateText.Text = date;
        ClockDateText.Visibility = string.IsNullOrEmpty(date) ? Visibility.Collapsed : Visibility.Visible;
        ClockTimeText.Text = now.ToString(timePattern, culture);
    }

    private void StartClock()
    {
        if (_clockTimer is null)
        {
            _clockTimer = DispatcherQueue.CreateTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.IsRepeating = true;
            _clockTimer.Tick += (_, _) => UpdateClock();
        }

        if (!_clockTimer.IsRunning) _clockTimer.Start();
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
        if (settings.ShowTodayTotal) enabled.Add("today");

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
                FontWeight = _fontWeight,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);

            var value = new TextBlock
            {
                Text = "--",
                FontSize = fontSize,
                FontFamily = font,
                FontWeight = _fontWeight,
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
        "today" => "Today",
        _ => id
    };

    /// <summary>Today's totals and the metered flag, fed by the service.</summary>
    public void SetUsage(long todayDown, long todayUp, bool metered, bool vpn)
    {
        _todayDown = todayDown;
        _todayUp = todayUp;
        _metered = metered;
        _vpn = vpn;

        MeteredBadge.Visibility = metered ? Visibility.Visible : Visibility.Collapsed;
        VpnBadge.Visibility = vpn ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaderDivider();
        ApplyStatsValues();
    }

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

        SetRow("disk", stats?.DiskPercent, settings.WarnDiskPercent, $"{stats?.DiskPercent ?? 0:F0}%");
        SetRow("gpu", stats?.GpuPercent, 101, $"{stats?.GpuPercent ?? 0:F0}%");
        SetRow("battery", stats?.BatteryPercent, 101, $"{stats?.BatteryPercent ?? 0}%");

        SetTodayRow();
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

        return $"{value.ToString(index == 0 ? "F0" : "F1")} {units[index]}";
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
        => SetRates(down, up, _downBytesPerSec, _upBytesPerSec);

    /// <summary>Sets the labels plus the raw rates, so the warning thresholds can tint them.</summary>
    public void SetRates(string down, string up, double downBytesPerSec, double upBytesPerSec)
    {
        _downFormatted = down;
        _upFormatted = up;
        SetRateText(DownText, down);
        SetRateText(UpText, up);

        _downBytesPerSec = downBytesPerSec;
        _upBytesPerSec = upBytesPerSec;

        ApplyRateColors();
    }

    /// <summary>Red when the configured download/upload warning value is crossed.</summary>
    private void ApplyRateColors()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;

        DownText.Foreground = Crosses(settings.WarnDownMbps, _downBytesPerSec)
            ? new SolidColorBrush(WarnColor)
            : _downBrush;

        UpText.Foreground = Crosses(settings.WarnUpMbps, _upBytesPerSec)
            ? new SolidColorBrush(WarnColor)
            : _upBrush;
    }

    private static bool Crosses(int warnMbps, double bytesPerSec)
        => warnMbps > 0 && bytesPerSec >= warnMbps * 1_000_000d / 8d;

    // ── Top app ticker ───────────────────────────────────────────────────────

    /// <summary>Feeds the busiest processes; the line cycles through the enabled metrics.</summary>
    public void SetTopApps(TopAppsStats stats)
    {
        _topApps = stats;
        UpdateTicker();
    }

    private void UpdateTicker()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;

        TopAppsPanel.Children.Clear();

        if (!settings.ShowTopApps || _topApps is null)
        {
            TopAppsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Every enabled metric gets its own line, so all four top apps are readable at once.
        var lines = new List<(string Label, string Value)>();
        if (settings.TopAppsCpu && _topApps.Cpu is { } cpu) lines.Add(("CPU", cpu.Text));
        if (settings.TopAppsRam && _topApps.Ram is { } ram) lines.Add(("RAM", ram.Text));
        if (settings.TopAppsDisk && _topApps.Disk is { } disk) lines.Add(("Disk", disk.Text));
        if (settings.TopAppsGpu && _topApps.Gpu is { } gpu) lines.Add(("GPU", gpu.Text));

        foreach (var line in lines)
        {
            var block = new TextBlock
            {
                FontSize = 10 * _textScale,
                FontFamily = _fontFamily,
                FontWeight = _fontWeight,
                Foreground = _textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            block.Inlines.Add(new Run { Text = line.Label + " · " });
            block.Inlines.Add(new Run { Text = line.Value });

            TopAppsPanel.Children.Add(block);
        }

        TopAppsPanel.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaderDivider();
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
        var width = SparkCanvas.ActualWidth > 0
            ? SparkCanvas.ActualWidth
            : (SparkCanvas.Width > 0 ? SparkCanvas.Width : SparkWidth);
        var height = SparkCanvas.ActualHeight > 0
            ? SparkCanvas.ActualHeight
            : (SparkCanvas.Height > 0 ? SparkCanvas.Height : SparkHeight);

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
