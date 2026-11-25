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
        ControlsCloseAfterCardToggle.IsOn       = s.Controls.CloseIslandAfterCard;
        ControlsShowBtBatteryCardToggle.IsOn    = s.Controls.ShowBluetoothBatteryCard;
        ControlsShowBtBatteryOnIslandToggle.IsOn = s.Controls.ShowBtBatteryOnIsland;
        ControlsShowMultipleBtOnIslandToggle.IsOn = s.Controls.ShowMultipleBtBatteriesOnIsland;
        SelectComboItem(ControlsBtBatteryIslandStyleBox, s.Controls.BtBatteryIslandStyle);
        ControlsShowBrightnessToggle.IsOn       = s.Controls.ShowBrightness;
        ControlsShowPowerRowToggle.IsOn         = s.Controls.ShowPowerRow;

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
        RefreshSearchCommandsList();

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
        s.Controls.CloseIslandAfterCard            = ControlsCloseAfterCardToggle.IsOn;
        s.Controls.ShowBluetoothBatteryCard        = ControlsShowBtBatteryCardToggle.IsOn;
        s.Controls.ShowBtBatteryOnIsland           = ControlsShowBtBatteryOnIslandToggle.IsOn;
        s.Controls.ShowMultipleBtBatteriesOnIsland = ControlsShowMultipleBtOnIslandToggle.IsOn;
        s.Controls.BtBatteryIslandStyle            = TagOf(ControlsBtBatteryIslandStyleBox) ?? s.Controls.BtBatteryIslandStyle;
        s.Controls.ShowBrightness                  = ControlsShowBrightnessToggle.IsOn;
        s.Controls.ShowPowerRow                    = ControlsShowPowerRowToggle.IsOn;

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

    // ── Web Search Commands (@) ────────────────────────────────────────────────

    private void RefreshSearchCommandsList()
    {
        if (SearchCommandsListPanel == null) return;
        SearchCommandsListPanel.Children.Clear();

        var searches = ServiceContainer.Settings.WindowIsland.GetCustomSearches();
        for (int i = 0; i < searches.Count; i++)
        {
            var item = searches[i];
            int index = i;

            var border = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(16, 255, 255, 255)),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Tag badge
            var tagBadge = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(35, 96, 205, 255)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            var tagText = new TextBlock
            {
                Text = item.Command,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 205, 255))
            };
            tagBadge.Child = tagText;
            Grid.SetColumn(tagBadge, 0);
            grid.Children.Add(tagBadge);

            // Name & URL
            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
            var nameText = new TextBlock
            {
                Text = item.Name,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var urlText = new TextBlock
            {
                Text = item.QueryUrl,
                FontSize = 10,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            infoStack.Children.Add(nameText);
            infoStack.Children.Add(urlText);
            Grid.SetColumn(infoStack, 1);
            grid.Children.Add(infoStack);

            // Delete button
            var delBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 11 },
                Padding = new Thickness(6, 4, 6, 4),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(delBtn, "Delete command");
            delBtn.Click += (_, _) =>
            {
                if (index >= 0 && index < searches.Count)
                {
                    searches.RemoveAt(index);
                    ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                    WindowIslandHost.Instance.ApplySettings(ServiceContainer.Settings.WindowIsland);
                    RefreshSearchCommandsList();
                }
            };
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(delBtn);

            border.Child = grid;
            SearchCommandsListPanel.Children.Add(border);
        }
    }

    private void AddSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        string cmd = NewSearchCommandBox.Text.Trim();
        string name = NewSearchNameBox.Text.Trim();
        string url = NewSearchUrlBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(cmd) || string.IsNullOrWhiteSpace(url))
        {
            SearchCommandValidationText.Text = "Please enter both a command (e.g. @reddit) and a search URL.";
            SearchCommandValidationText.Visibility = Visibility.Visible;
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            SearchCommandValidationText.Text = "URL must start with http:// or https://";
            SearchCommandValidationText.Visibility = Visibility.Visible;
            return;
        }

        string normCmd = CustomSearchCommand.NormalizeCommand(cmd);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = normCmd.TrimStart('@');
            if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
        }

        SearchCommandValidationText.Visibility = Visibility.Collapsed;

        var searches = ServiceContainer.Settings.WindowIsland.GetCustomSearches();
        var existing = searches.FirstOrDefault(s => string.Equals(s.Command, normCmd, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Name = name;
            existing.QueryUrl = url;
            existing.BaseUrl = CustomSearchCommand.ExtractBaseUrl(url);
        }
        else
        {
            searches.Add(new CustomSearchCommand(normCmd, name, url));
        }

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        WindowIslandHost.Instance.ApplySettings(ServiceContainer.Settings.WindowIsland);

        NewSearchCommandBox.Text = string.Empty;
        NewSearchNameBox.Text = string.Empty;
        NewSearchUrlBox.Text = string.Empty;

        RefreshSearchCommandsList();
    }

    private void ResetSearchesBtn_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.Settings.WindowIsland.CustomSearches = WindowIslandSettings.DefaultCustomSearches();
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        WindowIslandHost.Instance.ApplySettings(ServiceContainer.Settings.WindowIsland);
        RefreshSearchCommandsList();
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
