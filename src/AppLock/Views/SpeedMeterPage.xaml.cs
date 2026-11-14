using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.Brightness;
using AppLock.Services.SpeedMeter;
using AppLock.Views.Dialogs;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.UI;

namespace AppLock.Views;

/// <summary>
/// Settings page for the floating network-speed widget (Others → Speed Meter).
/// Appearance changes apply to the embedded preview immediately and to the widget if it is on.
/// </summary>
public sealed partial class SpeedMeterPage : Page
{
    // Setting controls from code fires their change events; this keeps those out of the handlers.
    private bool _loading;

    private NetworkSpeedSample? _lastSample;

    private static SpeedMeterSettings Settings => ServiceContainer.Settings.SpeedMeter;

    private static readonly string[] Presets = { "Transparent", "Glass", "Solid", "FollowWindows", "Custom" };

    public SpeedMeterPage()
    {
        InitializeComponent();

        // Slider/NumberBox ranges are set here rather than in XAML: assigning Minimum in the XBF
        // failed at load time with "Failed to assign to property 'RangeBase.Minimum'".
        OpacitySlider.Minimum = 0;
        OpacitySlider.Maximum = 100;
        OpacitySlider.StepFrequency = 1;

        ScaleSlider.Minimum = 50;
        ScaleSlider.Maximum = 250;
        ScaleSlider.StepFrequency = 5;

        RadiusSlider.Minimum = 0;
        RadiusSlider.Maximum = 40;
        RadiusSlider.StepFrequency = 1;

        DecimalsBox.Minimum = 0;
        DecimalsBox.Maximum = 2;

        WarnCpuBox.Minimum = 0;
        WarnCpuBox.Maximum = 100;
        WarnRamBox.Minimum = 0;
        WarnRamBox.Maximum = 100;

        DurationBox.Minimum = 2;
        DurationBox.Maximum = 15;
        MarginBox.Minimum = 0;
        MarginBox.Maximum = 200;
        DebounceBox.Minimum = 1;
        DebounceBox.Maximum = 30;
        MinOutageBox.Minimum = 1;
        MinOutageBox.Maximum = 300;

        IdleThresholdBox.Minimum = 0;
        IdleThresholdBox.Maximum = 100_000;
        IdleSecondsBox.Minimum = 1;
        IdleSecondsBox.Maximum = 3600;

        DailyCapBox.Minimum = 0;
        DailyCapBox.Maximum = 10_000;
        MonthlyCapBox.Minimum = 0;
        MonthlyCapBox.Maximum = 100_000;
        ResetDayBox.Minimum = 1;
        ResetDayBox.Maximum = 28;

        BuildComboItems();
        LoadFromSettings();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ServiceContainer.SpeedMeterService is { } service)
        {
            service.SampleUpdated -= OnSampleUpdated;
            service.SampleUpdated += OnSampleUpdated;
            service.StatsUpdated -= OnStatsUpdated;
            service.StatsUpdated += OnStatsUpdated;
        }

        UpdateStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ServiceContainer.SpeedMeterService is { } service)
        {
            service.SampleUpdated -= OnSampleUpdated;
            service.StatsUpdated -= OnStatsUpdated;
        }
    }

    private void OnSampleUpdated(object? sender, NetworkSpeedSample sample)
    {
        _lastSample = sample;
        UpdateStatus(sample);
        UpdatePreview();
    }

    private void OnStatsUpdated(object? sender, SystemStats stats)
    {
        PreviewWidget.SetStats(stats);
    }

    private void UpdatePreview()
    {
        var units = Settings.Units;
        var decimals = Settings.Decimals;
        var sample = _lastSample;

        PreviewWidget.SetValues(
            SpeedFormatter.Format(sample?.BytesDownPerSec ?? 0, units, decimals),
            SpeedFormatter.Format(sample?.BytesUpPerSec ?? 0, units, decimals));
    }

    private void LoadFromSettings()
    {
        _loading = true;

        MasterToggle.IsOn = Settings.Enabled;
        StartWithAppLockToggle.IsOn = Settings.StartWithAppLock;

        OpacitySlider.Value = Math.Clamp(Settings.BackgroundOpacityPercent, 0, 100);
        ScaleSlider.Value = Math.Clamp(Settings.ScalePercent, 50, 250);
        RadiusSlider.Value = Math.Clamp(Settings.CornerRadiusPx, 0, 40);

        BackgroundColorPicker.Color = ParseColor(Settings.BackgroundColor, Color.FromArgb(255, 0x20, 0x20, 0x20));
        TextColorPicker.Color = ParseColor(Settings.TextColor, Colors.White);
        DownColorPicker.Color = ParseColor(Settings.DownloadColor, Color.FromArgb(255, 0x4A, 0xDE, 0x80));
        UpColorPicker.Color = ParseColor(Settings.UploadColor, Color.FromArgb(255, 0x60, 0xA5, 0xFA));
        BackgroundHexBox.Text = ToHex(BackgroundColorPicker.Color);

        ShowIconsToggle.IsOn = Settings.UseIcons;
        BorderToggle.IsOn = Settings.ShowBorder;
        CompactToggle.IsOn = Settings.Compact;

        ShowCpuToggle.IsOn = Settings.ShowCpu;
        ShowRamToggle.IsOn = Settings.ShowRam;
        ShowDiskToggle.IsOn = Settings.ShowDisk;
        ShowGpuToggle.IsOn = Settings.ShowGpu;
        ShowBatteryToggle.IsOn = Settings.ShowBattery;
        ShowSparklineToggle.IsOn = Settings.ShowSparkline;
        ShowTodayTotalToggle.IsOn = Settings.ShowTodayTotal;
        SelectComboByTag(RamFormatCombo, Settings.RamFormat);
        WarnCpuBox.Value = Math.Clamp(Settings.WarnCpuPercent, 0, 100);
        WarnRamBox.Value = Math.Clamp(Settings.WarnRamPercent, 0, 100);
        BuildRowOrderPanel();

        var alerts = Settings.Alerts;
        NotifyLostToggle.IsOn = alerts.NotifyLost;
        NotifyRestoredToggle.IsOn = alerts.NotifyRestored;
        AlertSoundToggle.IsOn = alerts.SoundEnabled;
        SuppressFullscreenToggle.IsOn = alerts.SuppressInFullscreen;
        RealInternetToggle.IsOn = alerts.CheckRealInternet;
        ActiveProbeToggle.IsOn = alerts.ActiveProbe;
        SelectComboByTag(AlertStyleCombo, alerts.Style);
        SelectComboByTag(AnchorCombo, alerts.Anchor);
        SelectComboByTag(ToastThemeCombo, alerts.ToastTheme);
        SelectComboByTag(AnimationCombo, alerts.Animation);
        DurationBox.Value = Math.Clamp(alerts.DurationSeconds, 2, 15);
        MarginBox.Value = Math.Clamp(alerts.MarginPx, 0, 200);
        DebounceBox.Value = Math.Clamp(alerts.DebounceSeconds, 1, 30);
        MinOutageBox.Value = Math.Clamp(alerts.MinOutageToAnnounceSeconds, 1, 300);

        TrackUsageToggle.IsOn = Settings.TrackUsage;
        DailyCapBox.Value = Settings.DailyCapGb is { } dailyCap && dailyCap > 0 ? dailyCap : double.NaN;
        MonthlyCapBox.Value = Settings.MonthlyCapGb is { } monthlyCap && monthlyCap > 0 ? monthlyCap : double.NaN;
        ResetDayBox.Value = Math.Clamp(Settings.MonthlyResetDay, 1, 28);
        CapActiveToggle.IsOn = !string.IsNullOrEmpty(Settings.CapAdapterId);
        UsageSummaryText.Text = BuildUsageSummary();

        LockPositionToggle.IsOn = Settings.LockPosition;
        SnapToggle.IsOn = Settings.SnapToEdges;
        ClickThroughToggle.IsOn = Settings.ClickThrough;
        HideFullscreenToggle.IsOn = Settings.HideInFullscreen;
        HideFromCaptureToggle.IsOn = Settings.HideFromCapture;
        HideIdleToggle.IsOn = Settings.HideWhenIdle;
        HideDisconnectedToggle.IsOn = Settings.HideWhenDisconnected;
        IdleThresholdBox.Value = Math.Clamp(Settings.IdleThresholdKBps, 0, 100_000);
        IdleSecondsBox.Value = Math.Clamp(Settings.IdleSeconds, 1, 3600);
        UpdateHotkeyText();
        BuildPresetCombo();

        SelectComboByTag(FontCombo, Settings.FontFamily);
        SelectComboByTag(WeightCombo, Settings.FontWeight);
        SelectComboByTag(LayoutCombo, Settings.Layout);
        SelectComboByTag(UnitsCombo, Settings.Units);
        SelectComboByTag(IntervalCombo, Settings.IntervalSeconds.ToString(CultureInfo.InvariantCulture));

        DecimalsBox.Value = Math.Clamp(Settings.Decimals, 0, 2);

        UpdateValueLabels();
        _loading = false;

        ApplyAppearance();
        UpdateStatus();
    }

    private void BuildComboItems()
    {
        foreach (var weight in new[] { "Normal", "SemiBold", "Bold" })
        {
            WeightCombo.Items.Add(new ComboBoxItem { Content = weight, Tag = weight });
        }

        foreach (var layout in new[] { "Horizontal", "Vertical" })
        {
            LayoutCombo.Items.Add(new ComboBoxItem { Content = layout, Tag = layout });
        }

        foreach (var units in new[] { "Auto", "Bytes", "Bits" })
        {
            UnitsCombo.Items.Add(new ComboBoxItem { Content = units, Tag = units });
        }

        foreach (var ramFormat in new[] { "Percent", "UsedTotal" })
        {
            RamFormatCombo.Items.Add(new ComboBoxItem { Content = ramFormat, Tag = ramFormat });
        }

        AddComboItem(AlertStyleCombo, "AppLockToast", "AppLock toast");
        AddComboItem(AlertStyleCombo, "Windows", "Windows notification");

        AddComboItem(AnchorCombo, "TopLeft", "Top left");
        AddComboItem(AnchorCombo, "TopCenter", "Top center");
        AddComboItem(AnchorCombo, "TopRight", "Top right");
        AddComboItem(AnchorCombo, "MiddleLeft", "Middle left");
        AddComboItem(AnchorCombo, "MiddleCenter", "Middle center");
        AddComboItem(AnchorCombo, "MiddleRight", "Middle right");
        AddComboItem(AnchorCombo, "BottomLeft", "Bottom left");
        AddComboItem(AnchorCombo, "BottomCenter", "Bottom center");
        AddComboItem(AnchorCombo, "BottomRight", "Bottom right");

        AddComboItem(ToastThemeCombo, "FollowSystem", "Follow system");
        AddComboItem(ToastThemeCombo, "Dark", "Dark");
        AddComboItem(ToastThemeCombo, "Light", "Light");
        AddComboItem(ToastThemeCombo, "Custom", "Custom");

        AddComboItem(AnimationCombo, "SlideTop", "Slide from top");
        AddComboItem(AnimationCombo, "Fade", "Fade");
        AddComboItem(AnimationCombo, "None", "None");

        foreach (var interval in new[] { "0.5", "1", "2" })
        {
            IntervalCombo.Items.Add(new ComboBoxItem
            {
                Content = interval == "0.5" ? "0.5 s" : $"{interval} s",
                Tag = interval
            });
        }

        foreach (var font in GetInstalledFonts())
        {
            FontCombo.Items.Add(new ComboBoxItem { Content = font, Tag = font });
        }
    }

    private static IEnumerable<string> GetInstalledFonts()
    {
        try
        {
            var families = Microsoft.Graphics.Canvas.Text.CanvasTextFormat.GetSystemFontFamilies();
            if (families is { Length: > 0 })
            {
                return families.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.CurrentCulture);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Font enumeration failed: {ex.Message}");
        }

        return new[] { "Segoe UI Variable", "Segoe UI", "Consolas", "Cascadia Mono", "Bahnschrift", "Arial", "Verdana" };
    }

    private static void AddComboItem(ComboBox combo, string tag, string label)
        => combo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

    private static void SelectComboByTag(ComboBox combo, string? tag)
    {
        var match = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            combo.SelectedItem = match;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static string? GetComboTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void UpdateValueLabels()
    {
        OpacityValueText.Text = $"{(int)Math.Round(OpacitySlider.Value)}%";
        ScaleValueText.Text = $"{(int)Math.Round(ScaleSlider.Value)}%";
        RadiusValueText.Text = $"{(int)Math.Round(RadiusSlider.Value)} px";

        foreach (var button in PresetPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = string.Equals(button.Tag as string, Settings.ThemePreset, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Pushes the current settings into the preview and the floating widget, then saves.</summary>
    private void ApplyAppearance()
    {
        PreviewWidget.ApplyAppearance(Settings);
        UpdatePreview();

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SpeedMeterService?.ApplySettings();
    }

    private void UpdateStatus(NetworkSpeedSample? sample = null)
    {
        if (!Settings.Enabled)
        {
            StatusText.Text = "Off · turn this on to show the widget on your desktop.";
            return;
        }

        if (sample is null)
        {
            StatusText.Text = "On · waiting for the first reading…";
            return;
        }

        var units = Settings.Units;
        var decimals = Settings.Decimals;
        var down = SpeedFormatter.Format(sample.BytesDownPerSec, units, decimals);
        var up = SpeedFormatter.Format(sample.BytesUpPerSec, units, decimals);

        var adapter = sample.HasAdapter ? sample.AdapterName : "No active adapter";
        var link = sample.LinkSpeedBitsPerSec > 0
            ? $" · link {SpeedFormatter.Format(sample.LinkSpeedBitsPerSec / 8.0, "Bits", 0)}"
            : string.Empty;

        StatusText.Text = $"On · {adapter}{link} · ↓ {down} ↑ {up}";
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        ServiceContainer.SetSpeedMeterEnabled(MasterToggle.IsOn);
        UpdateStatus();
    }

    private void StartWithAppLockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.StartWithAppLock = StartWithAppLockToggle.IsOn;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleButton button) return;

        var preset = button.Tag as string;
        if (string.IsNullOrEmpty(preset)) return;

        Settings.ThemePreset = Array.Find(Presets, p => string.Equals(p, preset, StringComparison.OrdinalIgnoreCase)) ?? "Custom";

        _loading = true;
        UpdateValueLabels();
        _loading = false;

        ApplyAppearance();
    }

    private void BackgroundColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;

        Settings.BackgroundColor = ToHex(args.NewColor);

        _loading = true;
        BackgroundHexBox.Text = Settings.BackgroundColor;
        _loading = false;

        ApplyAppearance();
    }

    private void BackgroundHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        var text = BackgroundHexBox.Text?.Trim() ?? string.Empty;
        if (!IsValidHex(text)) return;

        Settings.BackgroundColor = text.ToUpperInvariant();

        _loading = true;
        BackgroundColorPicker.Color = ParseColor(text, Color.FromArgb(255, 0x20, 0x20, 0x20));
        _loading = false;

        ApplyAppearance();
    }

    private void TextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;
        Settings.TextColor = ToHex(args.NewColor);
        ApplyAppearance();
    }

    private void DownColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;
        Settings.DownloadColor = ToHex(args.NewColor);
        ApplyAppearance();
    }

    private void UpColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;
        Settings.UploadColor = ToHex(args.NewColor);
        ApplyAppearance();
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Settings.BackgroundOpacityPercent = (int)Math.Round(e.NewValue);
        UpdateValueLabels();
        ApplyAppearance();
    }

    private void ScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Settings.ScalePercent = (int)Math.Round(e.NewValue);
        UpdateValueLabels();
        ApplyAppearance();
    }

    private void RadiusSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Settings.CornerRadiusPx = (int)Math.Round(e.NewValue);
        UpdateValueLabels();
        ApplyAppearance();
    }

    private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GetComboTag(FontCombo) is { } font) Settings.FontFamily = font;
        ApplyAppearance();
    }

    private void WeightCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GetComboTag(WeightCombo) is { } weight) Settings.FontWeight = weight;
        ApplyAppearance();
    }

    private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GetComboTag(LayoutCombo) is { } layout) Settings.Layout = layout;
        ApplyAppearance();
    }

    private void UnitsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GetComboTag(UnitsCombo) is { } units) Settings.Units = units;
        ApplyAppearance();
    }

    private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (GetComboTag(IntervalCombo) is { } interval &&
            double.TryParse(interval, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            Settings.IntervalSeconds = seconds;
        }

        ApplyAppearance();
    }

    private void DecimalsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        Settings.Decimals = (int)Math.Clamp(args.NewValue, 0, 2);
        ApplyAppearance();
    }

    private void ShowIconsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.UseIcons = ShowIconsToggle.IsOn;
        ApplyAppearance();
    }

    private void BorderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.ShowBorder = BorderToggle.IsOn;
        ApplyAppearance();
    }

    private void CompactToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Compact = CompactToggle.IsOn;
        ApplyAppearance();
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new SpeedMeterSettings();

        Settings.ThemePreset = defaults.ThemePreset;
        Settings.BackgroundColor = defaults.BackgroundColor;
        Settings.BackgroundOpacityPercent = defaults.BackgroundOpacityPercent;
        Settings.TextColor = defaults.TextColor;
        Settings.DownloadColor = defaults.DownloadColor;
        Settings.UploadColor = defaults.UploadColor;
        Settings.ScalePercent = defaults.ScalePercent;
        Settings.FontFamily = defaults.FontFamily;
        Settings.FontWeight = defaults.FontWeight;
        Settings.CornerRadiusPx = defaults.CornerRadiusPx;
        Settings.ShowBorder = defaults.ShowBorder;
        Settings.Layout = defaults.Layout;
        Settings.Compact = defaults.Compact;
        Settings.Units = defaults.Units;
        Settings.Decimals = defaults.Decimals;
        Settings.IntervalSeconds = defaults.IntervalSeconds;
        Settings.UseIcons = defaults.UseIcons;

        LoadFromSettings();
        ApplyAppearance();
    }

    /// <summary>Row ids in their effective order (stored order first, then defaults).</summary>
    private static List<string> EffectiveRowOrder()
    {
        var order = new List<string>(Settings.RowOrder);
        foreach (var id in new[] { "cpu", "ram", "disk", "gpu", "battery", "today" })
        {
            if (!order.Contains(id)) order.Add(id);
        }

        return order;
    }

    private void BuildRowOrderPanel()
    {
        RowOrderPanel.Children.Clear();

        var order = EffectiveRowOrder();
        var labels = new Dictionary<string, string>
        {
            ["cpu"] = "CPU usage",
            ["ram"] = "RAM usage",
            ["disk"] = "Disk activity",
            ["gpu"] = "GPU usage",
            ["battery"] = "Battery",
            ["today"] = "Total data today"
        };

        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var index = i;

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = labels.TryGetValue(id, out var text) ? text : id,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);

            var up = new Button { Content = "\uE74A", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), Padding = new Thickness(8, 2, 8, 2), IsEnabled = index > 0 };
            up.Click += (_, _) => MoveRow(index, -1);
            Grid.SetColumn(up, 1);

            var down = new Button { Content = "\uE74B", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), Padding = new Thickness(8, 2, 8, 2), IsEnabled = index < order.Count - 1 };
            down.Click += (_, _) => MoveRow(index, 1);
            Grid.SetColumn(down, 2);

            grid.Children.Add(label);
            grid.Children.Add(up);
            grid.Children.Add(down);

            RowOrderPanel.Children.Add(grid);
        }
    }

    private void MoveRow(int index, int delta)
    {
        var order = EffectiveRowOrder();
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count) return;

        (order[index], order[target]) = (order[target], order[index]);
        Settings.RowOrder = order;

        BuildRowOrderPanel();
        SaveExtraStats();
    }

    private void SaveExtraStats()
    {
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SpeedMeterService?.ApplySettings();
        PreviewWidget.ApplyAppearance(Settings);
    }

    private void ExtraStatToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.ShowCpu = ShowCpuToggle.IsOn;
        Settings.ShowRam = ShowRamToggle.IsOn;
        Settings.ShowDisk = ShowDiskToggle.IsOn;
        Settings.ShowGpu = ShowGpuToggle.IsOn;
        Settings.ShowBattery = ShowBatteryToggle.IsOn;
        Settings.ShowSparkline = ShowSparklineToggle.IsOn;
        Settings.ShowTodayTotal = ShowTodayTotalToggle.IsOn;

        if (Settings.RowOrder.Count == 0) Settings.RowOrder = EffectiveRowOrder();

        SaveExtraStats();
    }

    private void RamFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GetComboTag(RamFormatCombo) is { } format) Settings.RamFormat = format;
        SaveExtraStats();
    }

    private void WarnBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;

        if (!double.IsNaN(WarnCpuBox.Value)) Settings.WarnCpuPercent = (int)Math.Clamp(WarnCpuBox.Value, 0, 100);
        if (!double.IsNaN(WarnRamBox.Value)) Settings.WarnRamPercent = (int)Math.Clamp(WarnRamBox.Value, 0, 100);

        SaveExtraStats();
    }

    private void SaveUsageSettings()
    {
        Settings.TrackUsage = TrackUsageToggle.IsOn;

        Settings.DailyCapGb = double.IsNaN(DailyCapBox.Value) || DailyCapBox.Value <= 0
            ? null
            : Math.Clamp(DailyCapBox.Value, 0, 10_000);

        Settings.MonthlyCapGb = double.IsNaN(MonthlyCapBox.Value) || MonthlyCapBox.Value <= 0
            ? null
            : Math.Clamp(MonthlyCapBox.Value, 0, 100_000);

        if (!double.IsNaN(ResetDayBox.Value))
        {
            Settings.MonthlyResetDay = (int)Math.Clamp(ResetDayBox.Value, 1, 28);
        }

        Settings.CapAdapterId = CapActiveToggle.IsOn
            ? Settings.CapAdapterId ?? NetworkDetailsProvider.Read().AdapterId
            : null;

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SpeedMeterService?.ApplySettings();

        UsageSummaryText.Text = BuildUsageSummary();
    }

    private void UsageSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveUsageSettings();
    }

    private void UsageNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        SaveUsageSettings();
    }

    private string BuildUsageSummary()
    {
        var store = ServiceContainer.SpeedMeterService?.Usage;
        if (store is null) return "Today —";

        var (down, up) = store.Today();
        return $"Today ↓ {FormatBytes(down)} · ↑ {FormatBytes(up)}";
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

    private async void ViewUsageHistory_Click(object sender, RoutedEventArgs e)
    {
        var store = ServiceContainer.SpeedMeterService?.Usage;
        if (store is null) return;

        var dialog = new UsageHistoryDialog(store) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();

        UsageSummaryText.Text = BuildUsageSummary();
    }

    private async void ResetUsage_Click(object sender, RoutedEventArgs e)
    {
        var store = ServiceContainer.SpeedMeterService?.Usage;
        if (store is null) return;

        var confirm = new ContentDialog
        {
            Title = "Reset data usage?",
            Content = "All stored daily totals are deleted. This cannot be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        store.Reset();
        UsageSummaryText.Text = BuildUsageSummary();
    }

    private void SaveAlerts()
    {
        var alerts = Settings.Alerts;

        alerts.NotifyLost = NotifyLostToggle.IsOn;
        alerts.NotifyRestored = NotifyRestoredToggle.IsOn;
        alerts.SoundEnabled = AlertSoundToggle.IsOn;
        alerts.SuppressInFullscreen = SuppressFullscreenToggle.IsOn;
        alerts.CheckRealInternet = RealInternetToggle.IsOn;
        alerts.ActiveProbe = ActiveProbeToggle.IsOn;

        if (GetComboTag(AlertStyleCombo) is { } style) alerts.Style = style;
        if (GetComboTag(AnchorCombo) is { } anchor) alerts.Anchor = anchor;
        if (GetComboTag(ToastThemeCombo) is { } theme) alerts.ToastTheme = theme;
        if (GetComboTag(AnimationCombo) is { } animation) alerts.Animation = animation;

        if (!double.IsNaN(DurationBox.Value)) alerts.DurationSeconds = (int)Math.Clamp(DurationBox.Value, 2, 15);
        if (!double.IsNaN(MarginBox.Value)) alerts.MarginPx = (int)Math.Clamp(MarginBox.Value, 0, 200);
        if (!double.IsNaN(DebounceBox.Value)) alerts.DebounceSeconds = (int)Math.Clamp(DebounceBox.Value, 1, 30);
        if (!double.IsNaN(MinOutageBox.Value)) alerts.MinOutageToAnnounceSeconds = (int)Math.Clamp(MinOutageBox.Value, 1, 300);

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SpeedMeterService?.ApplySettings();
    }

    private void AlertToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveAlerts();
    }

    private void AlertCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SaveAlerts();
    }

    private void AlertNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        SaveAlerts();
    }

    private void PreviewLost_Click(object sender, RoutedEventArgs e)
        => ServiceContainer.SpeedMeterService?.ToastService.ShowPreview(lost: true);

    private void PreviewRestored_Click(object sender, RoutedEventArgs e)
        => ServiceContainer.SpeedMeterService?.ToastService.ShowPreview(lost: false);

    private void UpdateHotkeyText()
    {
        ToggleHotkeyText.Text = Settings.ToggleHotkey is null
            ? "Not set"
            : HotkeyValidator.FormatHotkey(Settings.ToggleHotkey);
    }

    /// <summary>Saves a behavior flag and pushes it to the floating widget.</summary>
    private void SaveBehavior()
    {
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SpeedMeterService?.ApplySettings();
    }

    private void LockPositionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.LockPosition = LockPositionToggle.IsOn;
        SaveBehavior();
    }

    private void SnapToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.SnapToEdges = SnapToggle.IsOn;
        SaveBehavior();
    }

    private void ClickThroughToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.ClickThrough = ClickThroughToggle.IsOn;
        SaveBehavior();
    }

    private void HideFullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.HideInFullscreen = HideFullscreenToggle.IsOn;
        SaveBehavior();
    }

    private void HideIdleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.HideWhenIdle = HideIdleToggle.IsOn;
        if (!HideIdleToggle.IsOn) ServiceContainer.SpeedMeterService?.RevealWidget();
        SaveBehavior();
    }

    private void HideDisconnectedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.HideWhenDisconnected = HideDisconnectedToggle.IsOn;
        if (!HideDisconnectedToggle.IsOn) ServiceContainer.SpeedMeterService?.RevealWidget();
        SaveBehavior();
    }

    private void IdleNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;

        if (!double.IsNaN(IdleThresholdBox.Value))
        {
            Settings.IdleThresholdKBps = Math.Clamp(IdleThresholdBox.Value, 0, 100_000);
        }

        if (!double.IsNaN(IdleSecondsBox.Value))
        {
            Settings.IdleSeconds = (int)Math.Clamp(IdleSecondsBox.Value, 1, 3600);
        }

        SaveBehavior();
    }

    // ── Presets and settings file ────────────────────────────────────────────

    private void BuildPresetCombo()
    {
        PresetCombo.Items.Clear();

        foreach (var (id, name) in SpeedMeterPresets.BuiltIn)
        {
            PresetCombo.Items.Add(new ComboBoxItem { Content = name, Tag = $"builtin:{id}" });
        }

        foreach (var preset in Settings.CustomPresets)
        {
            PresetCombo.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = $"custom:{preset.Name}" });
        }

        if (PresetCombo.Items.Count > 0) PresetCombo.SelectedIndex = 0;
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        var tag = (PresetCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag is null) return;

        if (tag.StartsWith("builtin:", StringComparison.Ordinal))
        {
            SpeedMeterPresets.Apply(Settings, tag["builtin:".Length..]);
        }
        else if (tag.StartsWith("custom:", StringComparison.Ordinal))
        {
            var name = tag["custom:".Length..];
            var preset = Settings.CustomPresets.FirstOrDefault(p => p.Name == name);
            if (preset is null) return;

            SpeedMeterPresets.ApplyAppearanceJson(Settings, preset.SettingsJson);
        }

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SpeedMeterService?.ApplySettings();

        LoadFromSettings();
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = NewPresetNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var json = SpeedMeterPresets.CaptureAppearance(Settings);
        var existing = Settings.CustomPresets.FirstOrDefault(p => p.Name == name);

        if (existing is null)
        {
            Settings.CustomPresets.Add(new SpeedMeterPreset { Name = name, SettingsJson = json });
        }
        else
        {
            existing.SettingsJson = json;
        }

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        NewPresetNameBox.Text = string.Empty;
        BuildPresetCombo();
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        var tag = (PresetCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag is null || !tag.StartsWith("custom:", StringComparison.Ordinal)) return;

        var name = tag["custom:".Length..];
        Settings.CustomPresets.RemoveAll(p => p.Name == name);

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        BuildPresetCombo();
    }

    private async void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "applock-speed-meter"
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });

            if (App.MainWindowInstance is { } window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await Windows.Storage.FileIO.WriteTextAsync(
                file, SpeedMeterPresets.CaptureAppearance(Settings));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Export error: {ex.Message}");
        }
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".json");

            if (App.MainWindowInstance is { } window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            SpeedMeterPresets.ApplyAppearanceJson(Settings, json);

            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            ServiceContainer.SpeedMeterService?.ApplySettings();

            LoadFromSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Import error: {ex.Message}");
        }
    }

    private void HideFromCaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.HideFromCapture = HideFromCaptureToggle.IsOn;
        SaveBehavior();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        Settings.PositionPercentX = 98;
        Settings.PositionPercentY = 90;
        Settings.MonitorId = null;

        ServiceContainer.SpeedMeterService?.ResetWidgetPosition();
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private async void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        var validator = new Func<HotkeyBinding?, ValidationResult>(binding =>
        {
            if (binding is null) return ValidationResult.Success();
            if (binding.Modifiers == 0)
                return ValidationResult.Failure("Use at least one modifier (Ctrl, Alt, Shift or Win)");

            if (string.Equals(HotkeyValidator.FormatHotkey(binding),
                    ServiceContainer.Settings.EmergencyHotkey, StringComparison.OrdinalIgnoreCase))
                return ValidationResult.Failure("Conflicts with the Emergency Lock shortcut");

            return ValidationResult.Success();
        });

        var dialog = new HotkeyCaptureDialog(Settings.ToggleHotkey, validator)
        {
            XamlRoot = XamlRoot,
            Title = "Change Speed Meter Shortcut"
        };

        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
        {
            Settings.ToggleHotkey = dialog.CapturedHotkey;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            ServiceContainer.SpeedMeterService?.RegisterHotkey();
            UpdateHotkeyText();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(OthersPage));
        }
    }

    private static bool IsValidHex(string text)
    {
        var trimmed = text.TrimStart('#');
        return (trimmed.Length == 6 || trimmed.Length == 8) && trimmed.All(Uri.IsHexDigit);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var text = value.TrimStart('#');
        try
        {
            if (text.Length == 6)
            {
                return Color.FromArgb(
                    255,
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16));
            }
        }
        catch (Exception)
        {
            // Fall through to the default.
        }

        return fallback;
    }
}
