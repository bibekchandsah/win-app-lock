using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.SpeedMeter;
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
        }

        UpdateStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ServiceContainer.SpeedMeterService is { } service)
        {
            service.SampleUpdated -= OnSampleUpdated;
        }
    }

    private void OnSampleUpdated(object? sender, NetworkSpeedSample sample)
    {
        _lastSample = sample;
        UpdateStatus(sample);
        UpdatePreview();
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
