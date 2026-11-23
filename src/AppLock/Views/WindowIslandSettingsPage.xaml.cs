using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.Models;
using AppLock.Services;
using AppLock.WindowIsland.Host;

namespace AppLock.Views;

public sealed partial class WindowIslandSettingsPage : Page
{
    private bool _loading = true;

    public WindowIslandSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var s = ServiceContainer.Settings.WindowIsland;

        EnabledToggle.IsOn = s.Enabled;

        SelectComboItem(PositionBox, s.Position);
        SelectComboItem(ShapeBox, s.Shape);
        SelectComboItem(ThemeBox, s.Theme);
        SelectComboItem(IslandColorBox, s.IslandColor);

        SizeSlider.Minimum    = 60;
        SizeSlider.Maximum    = 150;
        SizeSlider.StepFrequency = 10;
        SizeSlider.TickFrequency = 10;
        SizeSlider.Value      = s.SizePercent;

        OpacitySlider.Minimum    = 40;
        OpacitySlider.Maximum    = 100;
        OpacitySlider.StepFrequency = 5;
        OpacitySlider.TickFrequency = 5;
        OpacitySlider.Value      = s.OpacityPercent;

        YOffsetSlider.Minimum    = -10;
        YOffsetSlider.Maximum    = 50;
        YOffsetSlider.StepFrequency = 2;
        YOffsetSlider.TickFrequency = 2;
        YOffsetSlider.Value      = s.YOffsetDip;

        UpdateSizeLabel();
        UpdateOpacityLabel();
        UpdateYOffsetLabel();

        ShowClockToggle.IsOn   = s.ShowClock;
        TimeFormat24HCheck.IsChecked = s.TimeFormat24H;
        ShowSecondsCheck.IsChecked = s.ShowSeconds;
        ShowBatteryToggle.IsOn = s.ShowBattery;

        CardCapsLockToggle.IsOn   = s.CardCapsLockEnabled;
        CardVolumeToggle.IsOn     = s.CardVolumeEnabled;
        CardClipboardToggle.IsOn  = s.CardClipboardEnabled;
        CardScreenshotToggle.IsOn = s.CardScreenshotEnabled;
        CardVpnToggle.IsOn        = s.CardVpnEnabled;
        CardBatteryLowToggle.IsOn = s.CardBatteryLowEnabled;
        CardChargingToggle.IsOn   = s.CardChargingEnabled;
        MicCamDotsToggle.IsOn     = s.MicCamDotsEnabled;
        SelectComboItem(CardStayBox, s.CardStayDuration);

        WeatherCityBox.Text = s.WeatherCity;
        WeatherCityStatus.Text = string.IsNullOrEmpty(s.WeatherCity) ? "No city configured (Weather tab is dormant)" : $"Saved: {s.WeatherCity} ({s.WeatherLatitude:F2}, {s.WeatherLongitude:F2})";
        SelectComboItem(WeatherUnitsBox, s.WeatherUnits);
        WeatherTempOnIslandToggle.IsOn = s.WeatherTempOnIsland;
        WeatherRainWarningToggle.IsOn  = s.WeatherRainWarning;

        SelectComboItem(ChatIdleBox, s.ChatIdleMinutes.ToString());
        try
        {
            string rtVer = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            ChatRuntimeStatusText.Text = $"WebView2 runtime installed (v{rtVer})";
        }
        catch
        {
            ChatRuntimeStatusText.Text = "WebView2 runtime not detected on this system.";
        }

        HotkeyBox.Text = string.IsNullOrWhiteSpace(s.Hotkey) ? "Ctrl+Shift+W" : s.Hotkey;

        // Controls Tab Add-on
        ControlsCloseAfterCardToggle.IsOn    = s.Controls.CloseIslandAfterCard;
        ControlsShowBtBatteryCardToggle.IsOn = s.Controls.ShowBluetoothBatteryCard;
        ControlsShowBrightnessToggle.IsOn    = s.Controls.ShowBrightness;
        ControlsShowPowerRowToggle.IsOn      = s.Controls.ShowPowerRow;

        // Milestone 15: Visibility rules
        HideInFullScreenToggle.IsOn             = s.HideInFullScreen;
        EdgeDwellToggle.IsOn                    = s.EdgeDwellEnabled;
        AutoHideToggle.IsOn                     = s.AutoHide;
        AutoHideStayOnDesktopToggle.IsOn        = s.AutoHideStayOnDesktop;
        AutoHideOnlyWhenOverlappedToggle.IsOn   = s.AutoHideOnlyWhenOverlapped;
        UnderTopTaskbarToggle.IsOn              = s.UnderTopTaskbar;
        SelectComboItem(TargetMonitorBox,  s.TargetMonitorMode);
        SelectComboItem(ChosenAppsModeBox, s.ChosenAppsMode);
        ChosenAppsBox.Text = string.Join(", ", s.ChosenApps ?? new());

        // Milestone 16: Polish, Lite mode, Delays, Frame Stats
        LiteModeToggle.IsOn           = s.LiteMode;
        ReducedMotionToggle.IsOn      = s.ReducedMotion;
        ExcludeFromCaptureToggle.IsOn = s.ExcludeFromCapture;
        OpenDelaySlider.Value         = s.OpenDelayMs;
        CloseDelaySlider.Value        = s.CloseDelayMs;
        FrameStatsToggle.IsOn         = s.ShowFrameStats;
        UpdateDelayLabels();

        _loading = false;
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

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

    private void OnSettingChanged(object sender, RoutedEventArgs e) => Save();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Save();

    private void OnHotkeyChanged(object sender, TextChangedEventArgs e) => Save();

    private void OnChosenAppsChanged(object sender, TextChangedEventArgs e) => Save();

    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSizeLabel();
        UpdateOpacityLabel();
        UpdateYOffsetLabel();
        Save();
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private void Save()
    {
        if (_loading) return;

        var s = ServiceContainer.Settings.WindowIsland;

        bool prevEnabled = s.Enabled;
        s.Enabled       = EnabledToggle.IsOn;
        s.Position      = TagOf(PositionBox) ?? s.Position;
        s.Shape         = TagOf(ShapeBox)    ?? s.Shape;
        s.Theme         = TagOf(ThemeBox)    ?? s.Theme;
        s.IslandColor   = TagOf(IslandColorBox) ?? s.IslandColor;
        s.SizePercent   = (int)SizeSlider.Value;
        s.OpacityPercent = (int)OpacitySlider.Value;
        s.YOffsetDip    = (int)YOffsetSlider.Value;
        s.ShowClock     = ShowClockToggle.IsOn;
        s.TimeFormat24H = TimeFormat24HCheck.IsChecked ?? true;
        s.ShowSeconds   = ShowSecondsCheck.IsChecked ?? false;
        s.ShowBattery   = ShowBatteryToggle.IsOn;
        s.CardCapsLockEnabled   = CardCapsLockToggle.IsOn;
        s.CardVolumeEnabled     = CardVolumeToggle.IsOn;
        s.CardClipboardEnabled  = CardClipboardToggle.IsOn;
        s.CardScreenshotEnabled = CardScreenshotToggle.IsOn;
        s.CardStayDuration      = TagOf(CardStayBox) ?? s.CardStayDuration;
        s.CardVpnEnabled        = CardVpnToggle.IsOn;
        s.CardBatteryLowEnabled = CardBatteryLowToggle.IsOn;
        s.CardChargingEnabled   = CardChargingToggle.IsOn;
        s.MicCamDotsEnabled     = MicCamDotsToggle.IsOn;
        s.WeatherUnits          = TagOf(WeatherUnitsBox) ?? s.WeatherUnits;
        s.WeatherTempOnIsland   = WeatherTempOnIslandToggle.IsOn;
        s.WeatherRainWarning    = WeatherRainWarningToggle.IsOn;
        if (int.TryParse(TagOf(ChatIdleBox), out int idleMin))
        {
            s.ChatIdleMinutes = AppLock.WindowIsland.Modules.Chat.WindowIslandChatManager.ClampIdleMinutes(idleMin);
        }
        s.Hotkey        = HotkeyBox.Text.Trim();

        // Controls Tab Add-on
        s.Controls.CloseIslandAfterCard     = ControlsCloseAfterCardToggle.IsOn;
        s.Controls.ShowBluetoothBatteryCard = ControlsShowBtBatteryCardToggle.IsOn;
        s.Controls.ShowBrightness           = ControlsShowBrightnessToggle.IsOn;
        s.Controls.ShowPowerRow             = ControlsShowPowerRowToggle.IsOn;

        // Milestone 15: Visibility rules
        s.HideInFullScreen           = HideInFullScreenToggle.IsOn;
        s.EdgeDwellEnabled           = EdgeDwellToggle.IsOn;
        s.AutoHide                   = AutoHideToggle.IsOn;
        s.AutoHideStayOnDesktop      = AutoHideStayOnDesktopToggle.IsOn;
        s.AutoHideOnlyWhenOverlapped = AutoHideOnlyWhenOverlappedToggle.IsOn;
        s.UnderTopTaskbar            = UnderTopTaskbarToggle.IsOn;
        s.TargetMonitorMode          = TagOf(TargetMonitorBox)  ?? s.TargetMonitorMode;
        s.ChosenAppsMode             = TagOf(ChosenAppsModeBox) ?? s.ChosenAppsMode;
        s.ChosenApps                 = ChosenAppsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Milestone 16: Polish, Lite mode, Delays, Frame Stats
        s.LiteMode           = LiteModeToggle.IsOn;
        s.ReducedMotion      = ReducedMotionToggle.IsOn;
        s.ExcludeFromCapture = ExcludeFromCaptureToggle.IsOn;
        s.OpenDelayMs        = (int)OpenDelaySlider.Value;
        s.CloseDelayMs       = (int)CloseDelaySlider.Value;
        s.ShowFrameStats     = FrameStatsToggle.IsOn;

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SetWindowIslandEnabled(s.Enabled);

        if (s.Enabled)
        {
            WindowIslandHost.Instance.ApplySettings(s);
        }
    }

    private async void WeatherSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        string query = WeatherCityBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        WeatherSearchBtn.IsEnabled = false;
        WeatherCityStatus.Text = "Searching...";

        try
        {
            var results = await AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.SearchCitiesAsync(query);
            if (results != null && results.Count > 0)
            {
                var first = results[0];
                await AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.SetLocationAsync(first.DisplayName, first.Latitude, first.Longitude);
                WeatherCityStatus.Text = $"Saved: {first.DisplayName} ({first.Latitude:F2}, {first.Longitude:F2})";
                WeatherCityBox.Text = first.DisplayName;
            }
            else
            {
                WeatherCityStatus.Text = "No locations found. Please check spelling.";
            }
        }
        catch (Exception ex)
        {
            WeatherCityStatus.Text = $"Search failed: {ex.Message}";
        }
        finally
        {
            WeatherSearchBtn.IsEnabled = true;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void SelectComboItem(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void UpdateSizeLabel()
        => SizeLabelText.Text = $"{(int)SizeSlider.Value}%";

    private void UpdateOpacityLabel()
        => OpacityLabelText.Text = $"{(int)OpacitySlider.Value}%";

    private void UpdateYOffsetLabel()
        => YOffsetLabelText.Text = $"{(int)YOffsetSlider.Value} DIPs";

    private void ControlsResetCardsBtn_Click(object sender, RoutedEventArgs e)
    {
        var s = ServiceContainer.Settings.WindowIsland;
        s.Controls.Cards = QuickCard.Defaults();
        Save();
    }

    private void OnDelaySliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateDelayLabels();
        Save();
    }

    private void UpdateDelayLabels()
    {
        if (OpenDelayLabelText != null && OpenDelaySlider != null)
            OpenDelayLabelText.Text = $"{(int)OpenDelaySlider.Value} ms";
        if (CloseDelayLabelText != null && CloseDelaySlider != null)
            CloseDelayLabelText.Text = $"{(int)CloseDelaySlider.Value} ms";
    }

    private async void RunBenchmarkBtn_Click(object sender, RoutedEventArgs e)
    {
        RunBenchmarkBtn.IsEnabled = false;
        BenchmarkResultText.Text = "Running benchmark cycles (opening/closing/measuring p95)…";

        try
        {
            var report = await WindowIslandHost.Instance.RunBenchmarkAsync(50);
            BenchmarkResultText.Text = report;
        }
        catch (Exception ex)
        {
            BenchmarkResultText.Text = $"Benchmark failed: {ex.Message}";
        }
        finally
        {
            RunBenchmarkBtn.IsEnabled = true;
        }
    }
}
