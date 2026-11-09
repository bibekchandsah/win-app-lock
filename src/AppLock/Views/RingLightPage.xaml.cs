using System;
using System.Collections.Generic;
using System.Linq;
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
        // Clear active preset ID — settings are now "modified"
        s.ActivePresetId = null;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.RingLightService.ApplySettingsLive(s);
        UpdatePreview();
        RefreshActivePresetLabel();
    }

    // ── Preset helpers ────────────────────────────────────────────────────

    private void PresetChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string presetId)
            ApplyPresetById(presetId);
    }

    private void ApplyPresetById(string presetId)
    {
        // Check built-ins first
        foreach (var p in RingPresets.BuiltIn)
        {
            if (p.Id == presetId)
            {
                var s = ServiceContainer.Settings.RingLight;
                RingPresets.ApplyTo(p, s);
                ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                ServiceContainer.RingLightService.ApplySettingsLive(s);
                _loading = true;
                SyncAppearanceSliders(s);
                _loading = false;
                UpdatePreview();
                RefreshActivePresetLabel();
                return;
            }
        }
        // Check custom presets
        var cs = ServiceContainer.Settings.RingLight;
        foreach (var p in cs.CustomPresets)
        {
            if (p.Id == presetId)
            {
                RingPresets.ApplyTo(p, cs);
                ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                ServiceContainer.RingLightService.ApplySettingsLive(cs);
                _loading = true;
                SyncAppearanceSliders(cs);
                _loading = false;
                UpdatePreview();
                RefreshActivePresetLabel();
                return;
            }
        }
    }

    /// <summary>Syncs all appearance sliders/controls to the current settings without triggering save.</summary>
    private void SyncAppearanceSliders(RingLightSettings s)
    {
        bool isTemp = s.ColorMode != "Custom";
        TempModeRadio.IsChecked   = isTemp;
        CustomModeRadio.IsChecked = !isTemp;
        TemperaturePanel.Visibility = isTemp ? Visibility.Visible : Visibility.Collapsed;
        CustomColorPanel.Visibility = isTemp ? Visibility.Collapsed : Visibility.Visible;

        KelvinSlider.Value = Math.Clamp(s.Kelvin, 2700, 6500);
        KelvinLabel.Text   = $"{s.Kelvin} K";

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
    }

    private void RefreshActivePresetLabel()
    {
        var s = ServiceContainer.Settings.RingLight;
        var id = s.ActivePresetId;

        // Check built-ins
        foreach (var p in RingPresets.BuiltIn)
        {
            if (p.Id == id)
            {
                ActivePresetLabel.Text = $"Active: {p.Name}";
                UpdateChipHighlights(id);
                return;
            }
        }
        // Check custom
        foreach (var p in s.CustomPresets)
        {
            if (p.Id == id)
            {
                ActivePresetLabel.Text = $"Active: {p.Name} (custom)";
                UpdateChipHighlights(id);
                return;
            }
        }
        // Modified or none
        ActivePresetLabel.Text = string.IsNullOrEmpty(id) ? "No preset selected" : "Modified";
        UpdateChipHighlights(null);
    }

    private void UpdateChipHighlights(string? activeId)
    {
        // Built-in chips: use AccentButtonStyle when active, DefaultButtonStyle otherwise
        var accent  = (Style)Application.Current.Resources["AccentButtonStyle"];
        var defBtn  = (Style)Application.Current.Resources["DefaultButtonStyle"];
        SetChip(ChipWarm,    "builtin:warm",    activeId, accent, defBtn);
        SetChip(ChipNeutral, "builtin:neutral", activeId, accent, defBtn);
        SetChip(ChipCool,    "builtin:cool",    activeId, accent, defBtn);
        SetChip(ChipSoft,    "builtin:soft",    activeId, accent, defBtn);
        SetChip(ChipStudio,  "builtin:studio",  activeId, accent, defBtn);
        SetChip(ChipMax,     "builtin:maxlight",activeId, accent, defBtn);

        // Custom chips
        foreach (var child in CustomChipsPanel.Children)
        {
            if (child is Button cb && cb.Tag is string cid)
                cb.Style = cid == activeId ? accent : defBtn;
        }
    }

    private static void SetChip(Button chip, string id, string? activeId, Style accent, Style defBtn)
        => chip.Style = id == activeId ? accent : defBtn;

    private void RebuildCustomChips()
    {
        CustomChipsPanel.Children.Clear();
        var presets = ServiceContainer.Settings.RingLight.CustomPresets;

        bool any = presets.Count > 0;
        CustomPresetsLabel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        CustomChipsPanel.Visibility   = any ? Visibility.Visible : Visibility.Collapsed;

        var accent  = (Style)Application.Current.Resources["AccentButtonStyle"];
        var defBtn  = (Style)Application.Current.Resources["DefaultButtonStyle"];
        var activeId = ServiceContainer.Settings.RingLight.ActivePresetId;

        foreach (var preset in presets)
        {
            // Apply chip
            var applyBtn = new Button
            {
                Content = preset.Name,
                Tag     = preset.Id,
                Style   = preset.Id == activeId ? accent : defBtn,
                Margin  = new Thickness(0),
            };
            applyBtn.Click += PresetChip_Click;

            // Rename button (pencil icon)
            var renameBtn = new Button
            {
                Tag    = preset.Id,
                Padding = new Thickness(6, 4, 6, 4),
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 12 },
            };
            ToolTipService.SetToolTip(renameBtn, "Rename");
            renameBtn.Click += RenamePresetButton_Click;

            // Delete button (X)
            var deleteBtn = new Button
            {
                Tag    = preset.Id,
                Padding = new Thickness(6, 4, 6, 4),
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            };
            ToolTipService.SetToolTip(deleteBtn, "Delete");
            deleteBtn.Click += DeletePresetButton_Click;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            row.Children.Add(applyBtn);
            row.Children.Add(renameBtn);
            row.Children.Add(deleteBtn);
            CustomChipsPanel.Children.Add(row);
        }
    }

    private async void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "Preset name", MinWidth = 240 };
        var dialog  = new ContentDialog
        {
            Title             = "Save preset",
            Content           = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot,
            DefaultButton     = ContentDialogButton.Primary,
        };
        nameBox.KeyDown += (s, ke) =>
        {
            if (ke.Key == Windows.System.VirtualKey.Enter) dialog.Hide();
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var s      = ServiceContainer.Settings.RingLight;
        var preset = RingPresets.SnapshotFrom(s, name);
        s.CustomPresets.Add(preset);
        s.ActivePresetId = preset.Id;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

        RebuildCustomChips();
        RefreshActivePresetLabel();
    }

    private async void RenamePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var s = ServiceContainer.Settings.RingLight;
        var preset = s.CustomPresets.Find(p => p.Id == id);
        if (preset == null) return;

        var nameBox = new TextBox { Text = preset.Name, MinWidth = 240 };
        var dialog  = new ContentDialog
        {
            Title             = "Rename preset",
            Content           = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot,
            DefaultButton     = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        preset.Name = name;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        RebuildCustomChips();
        RefreshActivePresetLabel();
    }

    private async void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var s = ServiceContainer.Settings.RingLight;
        var preset = s.CustomPresets.Find(p => p.Id == id);
        if (preset == null) return;

        var dialog = new ContentDialog
        {
            Title             = "Delete preset",
            Content           = $"Delete \"{preset.Name}\"?",
            PrimaryButtonText = "Delete",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot,
            DefaultButton     = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        s.CustomPresets.Remove(preset);
        if (s.ActivePresetId == id) s.ActivePresetId = null;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        RebuildCustomChips();
        RefreshActivePresetLabel();
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

        // Populate preset chips and label
        RebuildCustomChips();
        RefreshActivePresetLabel();

        // Automatic mode settings
        bool cameraSupported = ServiceContainer.RingLightService.CameraMonitor.IsSupported;
        CameraConsentWarningBar.IsOpen = !cameraSupported;
        AutoCameraOnToggle.IsEnabled   = cameraSupported;
        AutoCameraOnToggle.IsOn        = cameraSupported && s.Auto.OnWhenCameraInUse;
        AutoCameraOffToggle.IsOn       = s.Auto.TurnOffWhenCameraStops;

        FilterAnyAppRadio.IsChecked   = !s.Auto.OnlySelectedApps;
        FilterSelectedRadio.IsChecked = s.Auto.OnlySelectedApps;
        AppFilterPanel.Visibility     = s.Auto.OnlySelectedApps ? Visibility.Visible : Visibility.Collapsed;

        RebuildAppFilterList();
        SelectAutoOffItem(s.Auto.AutoOffMinutes);

        // Brightness boost settings
        bool brightnessSupported = ServiceContainer.RingLightService.BrightnessController.IsSupported;
        BoostBrightnessToggle.IsEnabled = brightnessSupported;
        if (!brightnessSupported)
        {
            ToolTipService.SetToolTip(BoostBrightnessToggle, "Display brightness control is not available on this system.");
        }
        BoostBrightnessToggle.IsOn = brightnessSupported && s.BoostScreenBrightness;
        BoostBrightnessPanel.Visibility = BoostBrightnessToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        BoostBrightnessSlider.Value = Math.Clamp(s.BoostBrightnessPercent, 50, 100);
        BoostBrightnessLabel.Text   = $"{s.BoostBrightnessPercent}%";

        // Auto warmth settings
        AutoWarmthToggle.IsOn       = s.Auto.AutoWarmth;
        AutoWarmthPanel.Visibility = s.Auto.AutoWarmth ? Visibility.Visible : Visibility.Collapsed;
        DayKelvinSlider.Value       = Math.Clamp(s.Auto.DayKelvin, 2700, 6500);
        DayKelvinLabel.Text         = $"{s.Auto.DayKelvin} K";
        NightKelvinSlider.Value     = Math.Clamp(s.Auto.NightKelvin, 2700, 6500);
        NightKelvinLabel.Text       = $"{s.Auto.NightKelvin} K";
        NightStartBox.Text          = string.IsNullOrEmpty(s.Auto.NightStart) ? "20:00" : s.Auto.NightStart;
        NightEndBox.Text            = string.IsNullOrEmpty(s.Auto.NightEnd) ? "07:00" : s.Auto.NightEnd;

        _loading = false;
    }

    private void BoostBrightnessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        BoostBrightnessPanel.Visibility = BoostBrightnessToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        SaveAndApply(s => s.BoostScreenBrightness = BoostBrightnessToggle.IsOn);
    }

    private void BoostBrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int val = (int)BoostBrightnessSlider.Value;
        BoostBrightnessLabel.Text = $"{val}%";
        SaveAndApply(s => s.BoostBrightnessPercent = val);
    }

    private void AutoWarmthToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AutoWarmthPanel.Visibility = AutoWarmthToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        SaveAndApply(s => s.Auto.AutoWarmth = AutoWarmthToggle.IsOn);
    }

    private void DayKelvinSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int val = (int)DayKelvinSlider.Value;
        DayKelvinLabel.Text = $"{val} K";
        SaveAndApply(s => s.Auto.DayKelvin = val);
    }

    private void NightKelvinSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        int val = (int)NightKelvinSlider.Value;
        NightKelvinLabel.Text = $"{val} K";
        SaveAndApply(s => s.Auto.NightKelvin = val);
    }

    private void NightSchedule_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SaveAndApply(s =>
        {
            s.Auto.NightStart = NightStartBox.Text.Trim();
            s.Auto.NightEnd   = NightEndBox.Text.Trim();
        });
    }

    private void SelectAutoOffItem(int minutes)
    {
        string targetTag = minutes.ToString();
        bool found = false;
        foreach (var item in AutoOffCombo.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag?.ToString() == targetTag)
            {
                AutoOffCombo.SelectedItem = cbi;
                found = true;
                break;
            }
        }
        if (!found && AutoOffCombo.Items.Count > 0)
            AutoOffCombo.SelectedIndex = 0;
    }

    private void RebuildAppFilterList()
    {
        AppFilterListPanel.Children.Clear();
        var filter = ServiceContainer.Settings.RingLight.Auto.AppFilter;

        foreach (var app in filter)
        {
            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = app,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var deleteBtn = new Button
            {
                Tag = app,
                Padding = new Thickness(6, 4, 6, 4),
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            };
            ToolTipService.SetToolTip(deleteBtn, "Remove");
            deleteBtn.Click += DeleteAppFilter_Click;

            Grid.SetColumn(textBlock, 0);
            Grid.SetColumn(deleteBtn, 1);
            row.Children.Add(textBlock);
            row.Children.Add(deleteBtn);

            AppFilterListPanel.Children.Add(row);
        }
    }

    private void DeleteAppFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string app)
        {
            SaveAndApply(s => s.Auto.AppFilter.Remove(app));
            RebuildAppFilterList();
        }
    }

    private void AutoCameraOnToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveAndApply(s => s.Auto.OnWhenCameraInUse = AutoCameraOnToggle.IsOn);
    }

    private void AutoCameraOffToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveAndApply(s => s.Auto.TurnOffWhenCameraStops = AutoCameraOffToggle.IsOn);
    }

    private void AppFilterMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool selectedOnly = FilterSelectedRadio.IsChecked == true;
        AppFilterPanel.Visibility = selectedOnly ? Visibility.Visible : Visibility.Collapsed;
        SaveAndApply(s => s.Auto.OnlySelectedApps = selectedOnly);
    }

    private void AddAppFilterButton_Click(object sender, RoutedEventArgs e)
    {
        CommitAppFilter();
    }

    private void AddAppFilterBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitAppFilter();
            e.Handled = true;
        }
    }

    private void CommitAppFilter()
    {
        var text = AddAppFilterBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var s = ServiceContainer.Settings.RingLight;
        if (!s.Auto.AppFilter.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            SaveAndApply(set => set.Auto.AppFilter.Add(text));
            RebuildAppFilterList();
        }
        AddAppFilterBox.Text = "";
    }

    private void BrowseAppFilterButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var ofd = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select Application Executable",
                Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
                CheckFileExists = true,
            };

            if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var fileName = System.IO.Path.GetFileName(ofd.FileName);
                if (!string.IsNullOrEmpty(fileName))
                {
                    var s = ServiceContainer.Settings.RingLight;
                    if (!s.Auto.AppFilter.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    {
                        SaveAndApply(set => set.Auto.AppFilter.Add(fileName));
                        RebuildAppFilterList();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RingLightPage] Browse error: {ex.Message}");
        }
    }

    private void AutoOffCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AutoOffCombo.SelectedItem is ComboBoxItem cbi && int.TryParse(cbi.Tag?.ToString(), out int mins))
        {
            SaveAndApply(s => s.Auto.AutoOffMinutes = mins);
        }
    }

    private void TestCameraDetectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var apps = ServiceContainer.RingLightService.CameraMonitor.GetActiveCameraApps();
            if (apps.Count > 0)
            {
                TestCameraStatusText.Text = $"Active camera apps: {string.Join(", ", apps)}";
                TestCameraStatusText.Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            }
            else
            {
                TestCameraStatusText.Text = "No apps currently using camera.";
                TestCameraStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            }
        }
        catch (Exception ex)
        {
            TestCameraStatusText.Text = $"Check failed: {ex.Message}";
        }
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
