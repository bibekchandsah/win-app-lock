using System;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.RingLight;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AppLock.Views;

public sealed partial class RingLightPage : Page
{
    private bool _loading = true;
    private bool _suppressToggle;
    private System.Threading.CancellationTokenSource? _previewCts;

    public RingLightPage()
    {
        this.InitializeComponent();
        this.Loaded   += OnLoaded;
        this.Unloaded += OnUnloaded;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.RingLightService.EnabledChanged += OnEnabledChanged;
        LoadSettingsIntoUI();
        RefreshStatusText(ServiceContainer.RingLightService.IsOn);
        _loading = false;
        UpdatePreview();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.RingLightService.EnabledChanged -= OnEnabledChanged;
        _previewCts?.Cancel();
    }

    // ── External event ────────────────────────────────────────────────────

    private void OnEnabledChanged(object? sender, bool enabled)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _suppressToggle = true;
            MasterToggle.IsOn = enabled;
            _suppressToggle = false;
            RefreshStatusText(enabled);
        });
    }

    // ── Control events ────────────────────────────────────────────────────

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _loading) return;
        ServiceContainer.SetRingLightEnabled(MasterToggle.IsOn);
        RefreshStatusText(MasterToggle.IsOn);
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _previewCts?.Cancel();
        _previewCts = new System.Threading.CancellationTokenSource();
        var ct = _previewCts.Token;

        var svc   = ServiceContainer.RingLightService;
        bool wasOn = svc.IsOn;
        if (!wasOn) svc.SetEnabled(true, "preview");

        PreviewButton.IsEnabled = false;
        PreviewButton.Content   = "Previewing…";

        try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { }

        if (!ct.IsCancellationRequested && !wasOn)
            svc.SetEnabled(false, "preview");

        if (!ct.IsCancellationRequested)
        {
            PreviewButton.IsEnabled = true;
            PreviewButton.Content   = "Preview on screen for 5 s";
        }
    }

    // ── Appearance controls ───────────────────────────────────────────────

    private void ColorMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool isTemp = TempModeRadio.IsChecked == true;
        TemperaturePanel.Visibility = isTemp ? Visibility.Visible : Visibility.Collapsed;
        CustomColorPanel.Visibility = isTemp ? Visibility.Collapsed : Visibility.Visible;
        SaveAndApply(s => s.ColorMode = isTemp ? "Temperature" : "Custom");
    }

    private void KelvinSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int k = (int)KelvinSlider.Value;
        KelvinLabel.Text = $"{k} K";
        SaveAndApply(s => s.Kelvin = k);
    }

    private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var hex   = HexInput.Text.Trim();
        var color = TryParseHex(hex);
        if (color.HasValue)
        {
            ColorSwatch.Background = new SolidColorBrush(color.Value);
            SaveAndApply(s => s.CustomColorHex = hex.StartsWith('#') ? hex : "#" + hex);
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int v = (int)BrightnessSlider.Value;
        BrightnessLabel.Text = $"{v}%";
        SaveAndApply(s => s.BrightnessPercent = v);
    }

    private void ThicknessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int v = (int)ThicknessSlider.Value;
        ThicknessLabel.Text = $"{v}%";
        SaveAndApply(s => s.ThicknessPercent = v);
    }

    private void SoftnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int v = (int)SoftnessSlider.Value;
        SoftnessLabel.Text = $"{v}%";
        SaveAndApply(s => s.SoftnessPercent = v);
    }

    private void CornerRadiusSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int v = (int)Math.Clamp(CornerRadiusSlider.Value, 0, 200);
        SaveAndApply(s => s.CornerRadiusPx = v);
    }

    private void EdgeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveAndApply(s =>
        {
            s.EdgeTop    = EdgeTopCheck.IsChecked    == true;
            s.EdgeBottom = EdgeBottomCheck.IsChecked == true;
            s.EdgeLeft   = EdgeLeftCheck.IsChecked   == true;
            s.EdgeRight  = EdgeRightCheck.IsChecked  == true;
        });
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool isGlow = StyleGlowRadio.IsChecked == true;
        StyleHint.Visibility = isGlow ? Visibility.Visible : Visibility.Collapsed;
        SaveAndApply(s => s.Style = isGlow ? "FullGlow" : "Frame");
    }

    private void TaskbarClearToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveAndApply(s => s.KeepTaskbarClear = TaskbarClearToggle.IsOn);
    }

    private void FadeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int v = (int)FadeSlider.Value;
        FadeLabel.Text = $"{v} ms";
        SaveAndApply(s => s.FadeMs = v);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SaveAndApply(Action<RingLightSettings> mutate)
    {
        var s = ServiceContainer.Settings.RingLight;
        mutate(s);
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.RingLightService.ApplySettingsLive(s);
        UpdatePreview();
    }

    private void LoadSettingsIntoUI()
    {
        _loading = true;
        var s = ServiceContainer.Settings.RingLight;

        _suppressToggle   = true;
        MasterToggle.IsOn = ServiceContainer.RingLightService.IsOn;
        _suppressToggle   = false;

        bool isTemp = s.ColorMode != "Custom";
        TempModeRadio.IsChecked   = isTemp;
        CustomModeRadio.IsChecked = !isTemp;
        TemperaturePanel.Visibility = isTemp ? Visibility.Visible : Visibility.Collapsed;
        CustomColorPanel.Visibility = isTemp ? Visibility.Collapsed : Visibility.Visible;

        KelvinSlider.Value = Math.Clamp(s.Kelvin, 2700, 6500);
        KelvinLabel.Text   = $"{s.Kelvin} K";

        HexInput.Text = s.CustomColorHex;
        var col = TryParseHex(s.CustomColorHex);
        if (col.HasValue) ColorSwatch.Background = new SolidColorBrush(col.Value);

        BrightnessSlider.Value = Math.Clamp(s.BrightnessPercent, 10, 100);
        BrightnessLabel.Text   = $"{s.BrightnessPercent}%";

        ThicknessSlider.Value = Math.Clamp(s.ThicknessPercent, 2, 30);
        ThicknessLabel.Text   = $"{s.ThicknessPercent}%";

        SoftnessSlider.Value = Math.Clamp(s.SoftnessPercent, 0, 100);
        SoftnessLabel.Text   = $"{s.SoftnessPercent}%";

        CornerRadiusSlider.Value = Math.Clamp(s.CornerRadiusPx, 0, 200);

        EdgeTopCheck.IsChecked    = s.EdgeTop;
        EdgeBottomCheck.IsChecked = s.EdgeBottom;
        EdgeLeftCheck.IsChecked   = s.EdgeLeft;
        EdgeRightCheck.IsChecked  = s.EdgeRight;

        bool isGlow = s.Style == "FullGlow";
        StyleFrameRadio.IsChecked = !isGlow;
        StyleGlowRadio.IsChecked  = isGlow;
        StyleHint.Visibility      = isGlow ? Visibility.Visible : Visibility.Collapsed;

        TaskbarClearToggle.IsOn = s.KeepTaskbarClear;

        FadeSlider.Value = Math.Clamp(s.FadeMs, 0, 1000);
        FadeLabel.Text   = $"{s.FadeMs} ms";

        // Populate and select display target combo
        LoadDisplayTargets(s.DisplayTarget);

        _loading = false;
    }

    /// <summary>
    /// Populates DisplayTargetCombo with the fixed options (Primary / Built-in / All)
    /// plus one item per currently connected display, then selects the saved target.
    /// </summary>
    private void LoadDisplayTargets(string currentTarget)
    {
        // Remove any dynamically-added specific-display items (beyond the first 3 static items)
        while (DisplayTargetCombo.Items.Count > 3)
            DisplayTargetCombo.Items.RemoveAt(3);

        // Add one item per real display
        try
        {
            var displays = DisplayArea.FindAll();
            int idx = 0;
            foreach (var d in displays)
            {
                idx++;
                var label   = d.IsPrimary ? $"Display {idx} (Primary)" : $"Display {idx}";
                var item    = new ComboBoxItem { Tag = RingOverlayManager.DisplayKey(d), Content = label };
                DisplayTargetCombo.Items.Add(item);
            }
        }
        catch { /* ignore if display enumeration fails */ }

        // Select the saved target
        bool found = false;
        foreach (var obj in DisplayTargetCombo.Items)
        {
            if (obj is ComboBoxItem cbi && cbi.Tag?.ToString() == currentTarget)
            {
                DisplayTargetCombo.SelectedItem = cbi;
                found = true;
                break;
            }
        }
        if (!found && DisplayTargetCombo.Items.Count > 0)
            DisplayTargetCombo.SelectedIndex = 0;
    }

    private void DisplayTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DisplayTargetCombo.SelectedItem is ComboBoxItem cbi)
        {
            var tag = cbi.Tag?.ToString() ?? "Primary";
            SaveAndApply(s => s.DisplayTarget = tag);
        }
    }

    private void RefreshStatusText(bool enabled)
    {
        var s = ServiceContainer.Settings.RingLight;
        StatusText.Text = enabled
            ? $"On · {s.Kelvin}K · {s.BrightnessPercent}%"
            : "Off";
    }

    // ── Live mini preview (Border-based) ──────────────────────────────────

    private void UpdatePreview()
    {
        try
        {
            var s = ServiceContainer.Settings.RingLight;

            Color ringColor = s.ColorMode == "Temperature"
                ? ColorTemperature.ToColor(s.Kelvin, s.BrightnessPercent)
                : ParseHexWithBrightness(s.CustomColorHex, s.BrightnessPercent);

            if (s.Style == "FullGlow")
                ringColor = ColorTemperature.ToColor(s.Kelvin, Math.Min(s.BrightnessPercent, 60));

            double softness = s.SoftnessPercent / 100.0;

            // Scale thickness to preview (preview height=140, preview width=224)
            double thickPx = Math.Max(2.0, 140 * s.ThicknessPercent / 100.0);

            SetPreviewBorder(PreviewTopBorder,    ringColor, softness, true,  s.EdgeTop,    thickPx, true);
            SetPreviewBorder(PreviewBottomBorder, ringColor, softness, true,  s.EdgeBottom, thickPx, false);
            SetPreviewBorder(PreviewLeftBorder,   ringColor, softness, false, s.EdgeLeft,   thickPx, true);
            SetPreviewBorder(PreviewRightBorder,  ringColor, softness, false, s.EdgeRight,  thickPx, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RingPreview] {ex.Message}");
        }
    }

    private static void SetPreviewBorder(Border border, Color color,
        double softness, bool horizontal, bool visible, double thickPx, bool fromStart)
    {
        border.Opacity = visible ? 1.0 : 0.0;

        if (horizontal)
            border.Height = thickPx;
        else
            border.Width = thickPx;

        var brush = new LinearGradientBrush();

        if (softness <= 0.01)
        {
            brush.StartPoint = new Windows.Foundation.Point(0, 0);
            brush.EndPoint   = new Windows.Foundation.Point(1, 0);
            brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = color, Offset = 1 });
        }
        else
        {
            var transparent = Color.FromArgb(0, color.R, color.G, color.B);
            double hardStop = 1.0 - softness;

            // gradient direction: opaque outer → transparent inner
            if (horizontal)
            {
                brush.StartPoint = new Windows.Foundation.Point(0, fromStart ? 0 : 1);
                brush.EndPoint   = new Windows.Foundation.Point(0, fromStart ? 1 : 0);
            }
            else
            {
                brush.StartPoint = new Windows.Foundation.Point(fromStart ? 0 : 1, 0);
                brush.EndPoint   = new Windows.Foundation.Point(fromStart ? 1 : 0, 0);
            }

            brush.GradientStops.Add(new GradientStop { Color = color,       Offset = 0 });
            if (hardStop > 0.01)
                brush.GradientStops.Add(new GradientStop { Color = color,   Offset = hardStop });
            brush.GradientStops.Add(new GradientStop { Color = transparent, Offset = 1.0 });
        }

        border.Background = brush;
    }

    // ── Color helpers ─────────────────────────────────────────────────────

    private static Color? TryParseHex(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return Color.FromArgb(255, r, g, b);
            }
        }
        catch { }
        return null;
    }

    private static Color ParseHexWithBrightness(string hex, int brightnessPercent)
    {
        var c     = TryParseHex(hex) ?? Colors.White;
        float scale = brightnessPercent / 100f;
        return Color.FromArgb(255,
            (byte)(c.R * scale),
            (byte)(c.G * scale),
            (byte)(c.B * scale));
    }
}
