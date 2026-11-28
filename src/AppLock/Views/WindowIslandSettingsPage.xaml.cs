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
    private bool _updatingCustomColor;
    private string _customIslandHex = "#1E293B";
    private string _mediaPillMode = "Usual";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _diskSaveTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _worldClockTimer;
    private readonly System.Collections.Generic.List<(TextBlock LiveBlock, CustomWorldClock Clock)> _settingsClockBindings = new();

    public WindowIslandSettingsPage()
    {
        InitializeComponent();
        SelectCategoryByTag(ServiceContainer.Settings.WindowIsland.LastSettingsTab);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        FlushPendingDiskSave();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WindowIslandHost.Instance.ThroughModeChanged -= OnExternalThroughModeChanged;
        WindowIslandHost.Instance.LyricsEnabledChanged -= OnExternalLyricsEnabledChanged;
        WindowIslandHost.Instance.CustomClocksChanged -= OnExternalCustomClocksChanged;
        WindowIslandHost.Instance.ChatSitesChanged -= OnExternalChatSitesChanged;
        AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.WeatherCitiesChanged -= OnExternalWeatherCitiesChanged;
        _worldClockTimer?.Stop();
        FlushPendingDiskSave();
    }

    private void FlushPendingDiskSave()
    {
        if (_diskSaveTimer != null && _diskSaveTimer.IsRunning)
        {
            _diskSaveTimer.Stop();
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }

    private void SelectCategoryByTag(string? tag)
    {
        if (CategoryList == null) return;
        string target = string.IsNullOrWhiteSpace(tag) ? "general" : tag;
        for (int i = 0; i < CategoryList.Items.Count; i++)
        {
            if (CategoryList.Items[i] is ListViewItem item &&
                string.Equals(item.Tag as string, target, StringComparison.OrdinalIgnoreCase))
            {
                CategoryList.SelectedIndex = i;
                return;
            }
        }
        if (CategoryList.Items.Count > 0) CategoryList.SelectedIndex = 0;
    }

    private void OnExternalThroughModeChanged(bool enabled)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ThroughModeToggle == null || ThroughModeToggle.IsOn == enabled) return;
            bool prevLoading = _loading;
            _loading = true;
            try
            {
                ThroughModeToggle.IsOn = enabled;
            }
            finally
            {
                _loading = prevLoading;
            }
        });
    }

    private void OnExternalLyricsEnabledChanged(bool enabled)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (LyricsToggle == null || LyricsToggle.IsOn == enabled) return;
            bool prevLoading = _loading;
            _loading = true;
            try
            {
                LyricsToggle.IsOn = enabled;
            }
            finally
            {
                _loading = prevLoading;
            }
        });
    }

    private void OnExternalCustomClocksChanged()
    {
        DispatcherQueue?.TryEnqueue(RefreshSettingsWorldClocksList);
    }

    private void InitSettingsWorldClocks()
    {
        if (SettingsClockTzBox != null && SettingsClockTzBox.Items.Count == 0)
        {
            var zones = TimeZoneInfo.GetSystemTimeZones();
            SettingsClockTzBox.ItemsSource = zones.Select(tz => new ComboBoxItem
            {
                Content = tz.DisplayName,
                Tag = tz.Id
            }).ToList();
            if (zones.Count > 0) SettingsClockTzBox.SelectedIndex = 0;
        }
        RefreshSettingsWorldClocksList();
    }

    private void RefreshSettingsWorldClocksList()
    {
        if (SettingsWorldClocksList == null) return;
        SettingsWorldClocksList.Children.Clear();
        _settingsClockBindings.Clear();

        var clocks = ServiceContainer.Settings.WindowIsland.CustomClocks;
        if (clocks == null || clocks.Count == 0)
        {
            _worldClockTimer?.Stop();
            SettingsWorldClocksList.Children.Add(new TextBlock
            {
                Text = "No custom world clocks added yet.",
                FontSize = 12,
                Opacity = 0.6
            });
            return;
        }

        var utcNow = DateTime.UtcNow;
        for (int i = 0; i < clocks.Count; i++)
        {
            int idx = i;
            var wc = clocks[i];

            string tzLabel = wc.TimeZoneId;
            try { tzLabel = TimeZoneInfo.FindSystemTimeZoneById(wc.TimeZoneId).DisplayName; } catch { }

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 8, 8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1)
            };

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = wc.PlaceName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
            info.Children.Add(new TextBlock { Text = tzLabel, FontSize = 11.5, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var liveTb = new TextBlock
            {
                Text = FormatSettingsWorldClockLive(utcNow, wc.TimeZoneId),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(liveTb, 1);
            row.Children.Add(liveTb);
            _settingsClockBindings.Add((liveTb, wc));

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (clocks.Count > 1)
            {
                var upBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70E", FontSize = 11 },
                    Padding = new Thickness(6),
                    IsEnabled = idx > 0
                };
                ToolTipService.SetToolTip(upBtn, "Move up");
                upBtn.Click += (_, _) => MoveSettingsWorldClock(idx, -1);
                btns.Children.Add(upBtn);

                var downBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70D", FontSize = 11 },
                    Padding = new Thickness(6),
                    IsEnabled = idx < clocks.Count - 1
                };
                ToolTipService.SetToolTip(downBtn, "Move down");
                downBtn.Click += (_, _) => MoveSettingsWorldClock(idx, 1);
                btns.Children.Add(downBtn);
            }

            var delBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
                Padding = new Thickness(6)
            };
            ToolTipService.SetToolTip(delBtn, "Remove clock");
            delBtn.Click += (_, _) => RemoveSettingsWorldClock(idx);
            btns.Children.Add(delBtn);

            Grid.SetColumn(btns, 2);
            row.Children.Add(btns);
            card.Child = row;
            SettingsWorldClocksList.Children.Add(card);
        }

        if (_worldClockTimer == null)
        {
            _worldClockTimer = DispatcherQueue.CreateTimer();
            _worldClockTimer.Interval = TimeSpan.FromSeconds(1);
            _worldClockTimer.IsRepeating = true;
            _worldClockTimer.Tick += (_, _) => UpdateSettingsWorldClockTimes();
        }
        if (!_worldClockTimer.IsRunning) _worldClockTimer.Start();
    }

    private void UpdateSettingsWorldClockTimes()
    {
        if (_settingsClockBindings.Count == 0) return;
        var utcNow = DateTime.UtcNow;
        foreach (var (tb, wc) in _settingsClockBindings)
        {
            tb.Text = FormatSettingsWorldClockLive(utcNow, wc.TimeZoneId);
        }
    }

    private static string FormatSettingsWorldClockLive(DateTime utcNow, string timeZoneId)
    {
        DateTime local = utcNow;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        }
        catch { }

        var s = ServiceContainer.Settings.WindowIsland;
        bool showSec = string.Equals(s.CalendarClockStyle, "DigitalSeconds", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.CalendarClockStyle, "AnalogSeconds", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.CalendarClockStyle, "AnalogClassic", StringComparison.OrdinalIgnoreCase);
        string pattern = s.TimeFormat24H
            ? (showSec ? "HH:mm:ss" : "HH:mm")
            : (showSec ? "h:mm:ss tt" : "h:mm tt");
        return $"{local.ToString(pattern)} · {local:ddd, MMM d}";
    }

    private void SettingsAddClockBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsClockTzBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string tzId) return;
        string place = SettingsClockPlaceBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(place))
        {
            string disp = item.Content as string ?? tzId;
            int paren = disp.LastIndexOf(')');
            place = paren >= 0 && paren + 1 < disp.Length ? disp[(paren + 1)..].Trim() : tzId;
        }

        var s = ServiceContainer.Settings.WindowIsland;
        s.CustomClocks.Add(new CustomWorldClock(place, tzId));
        if (SettingsClockPlaceBox != null) SettingsClockPlaceBox.Text = string.Empty;
        PersistAndSyncWorldClocks();
    }

    private void MoveSettingsWorldClock(int index, int delta)
    {
        var s = ServiceContainer.Settings.WindowIsland;
        int target = index + delta;
        if (index < 0 || index >= s.CustomClocks.Count || target < 0 || target >= s.CustomClocks.Count) return;
        (s.CustomClocks[index], s.CustomClocks[target]) = (s.CustomClocks[target], s.CustomClocks[index]);
        PersistAndSyncWorldClocks();
    }

    private void RemoveSettingsWorldClock(int index)
    {
        var s = ServiceContainer.Settings.WindowIsland;
        if (index < 0 || index >= s.CustomClocks.Count) return;
        s.CustomClocks.RemoveAt(index);
        PersistAndSyncWorldClocks();
    }

    private void PersistAndSyncWorldClocks()
    {
        var s = ServiceContainer.Settings.WindowIsland;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        WindowIslandHost.Instance.ApplySettings(s);
        RefreshSettingsWorldClocksList();
    }

    /// <summary>Shows the settings section belonging to the selected rail category.</summary>
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionGeneral is null) return;

        string category = (CategoryList.SelectedItem as ListViewItem)?.Tag as string ?? "general";

        if (!_loading)
        {
            var s = ServiceContainer.Settings.WindowIsland;
            if (!string.Equals(s.LastSettingsTab, category, StringComparison.OrdinalIgnoreCase))
            {
                s.LastSettingsTab = category;
                _diskSaveTimer ??= CreateDiskSaveTimer();
                _diskSaveTimer.Stop();
                _diskSaveTimer.Start();
            }
        }

        SectionGeneral.Visibility = ShowSection(category, "general");
        SectionAppearance.Visibility = ShowSection(category, "appearance");
        SectionVisibility.Visibility = ShowSection(category, "visibility");
        SectionIndicators.Visibility = ShowSection(category, "indicators");
        SectionClock.Visibility = ShowSection(category, "clock");
        SectionMedia.Visibility = ShowSection(category, "media");
        SectionCards.Visibility = ShowSection(category, "cards");
        SectionControls.Visibility = ShowSection(category, "controls");
        SectionWeather.Visibility = ShowSection(category, "weather");
        SectionChat.Visibility = ShowSection(category, "chat");
        SectionSearch.Visibility = ShowSection(category, "search");
        SectionPerformance.Visibility = ShowSection(category, "performance");
        SectionAbout.Visibility = ShowSection(category, "about");

        ContentScroller.ChangeView(null, 0, null, true);
    }

    private static Visibility ShowSection(string category, string wanted) =>
        string.Equals(category, wanted, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
        var s = ServiceContainer.Settings.WindowIsland;
        SelectCategoryByTag(s.LastSettingsTab);

        EnabledToggle.IsOn = s.Enabled;

        SelectComboItem(PositionBox, s.Position);
        SelectComboItem(ShapeBox, s.Shape);
        SelectComboItem(ThemeBox, s.Theme);
        if (TryParseHex6(s.LastCustomIslandColor, out var savedCustomColor))
        {
            _customIslandHex = FormatHex(savedCustomColor);
        }

        if (!string.IsNullOrWhiteSpace(s.IslandColor) && s.IslandColor.StartsWith('#'))
        {
            if (TryParseHex6(s.IslandColor, out var customColor))
            {
                _customIslandHex = FormatHex(customColor);
                s.LastCustomIslandColor = _customIslandHex;
            }
            SelectComboItem(IslandColorBox, "Custom");
        }
        else
        {
            SelectComboItem(IslandColorBox, s.IslandColor);
        }
        SyncCustomColorControls();

        SizeSlider.Minimum    = 60;
        SizeSlider.Maximum    = 150;
        SizeSlider.StepFrequency = 10;
        SizeSlider.TickFrequency = 10;
        SizeSlider.Value      = s.SizePercent;

        OpacitySlider.Minimum    = 10;
        OpacitySlider.Maximum    = 100;
        OpacitySlider.StepFrequency = 5;
        OpacitySlider.TickFrequency = 10;
        OpacitySlider.Value      = Math.Clamp(s.OpacityPercent, 10, 100);

        YOffsetSlider.Minimum    = -10;
        YOffsetSlider.Maximum    = 50;
        YOffsetSlider.StepFrequency = 2;
        YOffsetSlider.TickFrequency = 2;
        YOffsetSlider.Value      = s.YOffsetDip;

        XOffsetSlider.Minimum    = -200;
        XOffsetSlider.Maximum    = 200;
        XOffsetSlider.StepFrequency = 2;
        XOffsetSlider.TickFrequency = 2;
        XOffsetSlider.Value      = s.XOffsetDip;

        UpdateSizeLabel();
        UpdateOpacityLabel();
        UpdateYOffsetLabel();
        UpdateXOffsetLabel();

        ShowClockToggle.IsOn   = s.ShowClock;
        TimeFormat24HCheck.IsChecked = s.TimeFormat24H;
        ShowSecondsCheck.IsChecked = s.ShowSeconds;
        SelectComboItem(CalendarClockStyleBox, string.IsNullOrWhiteSpace(s.CalendarClockStyle) ? "DigitalSeconds" : s.CalendarClockStyle);
        SelectComboItem(CalendarClockFaceBox, string.IsNullOrWhiteSpace(s.CalendarClockFace) ? "Auto" : s.CalendarClockFace);
        SelectComboItem(CalendarDigitalFaceBox, string.IsNullOrWhiteSpace(s.CalendarDigitalFace) ? "Classic" : s.CalendarDigitalFace);
        ShowBatteryToggle.IsOn = s.ShowBattery;

        CardCapsLockToggle.IsOn   = s.CardCapsLockEnabled;
        CardNumLockToggle.IsOn    = s.CardNumLockEnabled;
        CardDeleteKeyWarningToggle.IsOn = s.CardDeleteKeyWarningEnabled;
        CardCustomKeyWarningsBox.Text   = string.Join(", ", s.CustomKeyWarnings ?? new());
        CardVolumeToggle.IsOn     = s.CardVolumeEnabled;
        CardClipboardToggle.IsOn  = s.CardClipboardEnabled;
        CardScreenshotToggle.IsOn = s.CardScreenshotEnabled;
        CardDownloadsToggle.IsOn  = s.CardDownloadsEnabled;
        CardFileCopyToggle.IsOn   = s.CardFileCopyEnabled;
        CardVpnToggle.IsOn        = s.CardVpnEnabled;
        CardBatteryLowToggle.IsOn = s.CardBatteryLowEnabled;
        RefreshBatteryThresholdsUI();
        CardChargingToggle.IsOn   = s.CardChargingEnabled;
        CardMicToggle.IsOn        = s.CardMicEnabled;
        CardCamToggle.IsOn        = s.CardCamEnabled;
        MicDotToggle.IsOn         = s.MicDotEnabled;
        CamDotToggle.IsOn         = s.CamDotEnabled;
        ShowCpuNotchToggle.IsOn   = s.ShowCpuInNotch;
        ShowRamNotchToggle.IsOn   = s.ShowRamInNotch;
        ShowDiskNotchToggle.IsOn  = s.ShowDiskInNotch;
        ShowGpuNotchToggle.IsOn   = s.ShowGpuInNotch;
        ShowDownSpeedNotchToggle.IsOn = s.ShowDownSpeedInNotch;
        ShowUpSpeedNotchToggle.IsOn   = s.ShowUpSpeedInNotch;
        SelectComboItem(MediaPlayerStyleBox, s.MediaPlayerStyle);
        _mediaPillMode = string.IsNullOrWhiteSpace(s.MediaPillMode) ? "Usual" : s.MediaPillMode;
        UpdateMediaPillModeButtons();
        LyricsToggle.IsOn         = s.LyricsEnabled;
        SelectComboItem(CardStayBox, s.CardStayDuration);
        double customSec = Math.Clamp((s.CardStayCustomMs > 0 ? s.CardStayCustomMs : 4000) / 1000.0, 0.5, 30.0);
        CardStayCustomSlider.Value = customSec;
        CardStayCustomLabel.Text = $"Custom duration: {customSec:0.0} s";
        CardStayCustomPanel.Visibility = string.Equals(s.CardStayDuration, "Custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var activeCity = s.GetActiveCity();
        WeatherCityBox.Text = activeCity?.Name ?? s.WeatherCity;
        WeatherCityStatus.Text = activeCity != null
            ? $"Active: {activeCity.Name} ({activeCity.Latitude:F2}, {activeCity.Longitude:F2})"
            : (string.IsNullOrEmpty(s.WeatherCity) ? "No city configured" : $"Saved: {s.WeatherCity} ({s.WeatherLatitude:F2}, {s.WeatherLongitude:F2})");
        RefreshSettingsWeatherCitiesList();
        AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.WeatherCitiesChanged -= OnExternalWeatherCitiesChanged;
        AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.WeatherCitiesChanged += OnExternalWeatherCitiesChanged;
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
        string defaultShotsFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        ScreenshotsFolderBox.PlaceholderText = defaultShotsFolder;
        ScreenshotsFolderBox.Text = string.IsNullOrWhiteSpace(s.ScreenshotsFolder)
            ? defaultShotsFolder
            : s.ScreenshotsFolder;
        ScreenshotsDefaultPathText.Text = $"Default: {defaultShotsFolder}";

        // Controls Tab Add-on
        ControlsCloseAfterCardToggle.IsOn       = s.Controls.CloseIslandAfterCard;
        ControlsShowBtBatteryCardToggle.IsOn    = s.Controls.ShowBluetoothBatteryCard;
        ControlsShowBtBatteryOnIslandToggle.IsOn = s.Controls.ShowBtBatteryOnIsland;
        ControlsShowMultipleBtOnIslandToggle.IsOn = s.Controls.ShowMultipleBtBatteriesOnIsland;
        SelectComboItem(ControlsBtBatteryIslandStyleBox, s.Controls.BtBatteryIslandStyle);
        ControlsBtLowBatteryAlertToggle.IsOn    = s.Controls.BtLowBatteryAlert;
        ControlsBtWarningLevelsBox.Text         = string.Join(", ", s.Controls.GetBtWarningThresholds());
        ControlsBtWarningWindowsNotificationToggle.IsOn = s.Controls.BtWarningWindowsNotification;
        ControlsShowBrightnessToggle.IsOn       = s.Controls.ShowBrightness;
        ControlsShowMicrophoneToggle.IsOn       = s.Controls.ShowMicrophone;
        ControlsShowPowerRowToggle.IsOn         = s.Controls.ShowPowerRow;

        // Milestone 15: Visibility rules
        HideInFullScreenToggle.IsOn             = s.HideInFullScreen;
        EdgeDwellToggle.IsOn                    = s.EdgeDwellEnabled;
        AutoHideToggle.IsOn                     = s.AutoHide;
        AutoHideStayOnDesktopToggle.IsOn        = s.AutoHideStayOnDesktop;
        AutoHideOnlyWhenOverlappedToggle.IsOn   = s.AutoHideOnlyWhenOverlapped;
        UnderTopTaskbarToggle.IsOn              = s.UnderTopTaskbar;
        ThroughModeToggle.IsOn                  = s.ThroughMode;
        ThroughModeHotkeyBox.Text               = s.ThroughModeHotkey ?? string.Empty;
        CommandBarHotkeyBox.Text                = s.CommandBarHotkey ?? "Alt+Space";
        WindowIslandHost.Instance.ThroughModeChanged -= OnExternalThroughModeChanged;
        WindowIslandHost.Instance.ThroughModeChanged += OnExternalThroughModeChanged;
        WindowIslandHost.Instance.LyricsEnabledChanged -= OnExternalLyricsEnabledChanged;
        WindowIslandHost.Instance.LyricsEnabledChanged += OnExternalLyricsEnabledChanged;
        WindowIslandHost.Instance.CustomClocksChanged -= OnExternalCustomClocksChanged;
        WindowIslandHost.Instance.ChatSitesChanged -= OnExternalChatSitesChanged;
        WindowIslandHost.Instance.CustomClocksChanged += OnExternalCustomClocksChanged;
        WindowIslandHost.Instance.ChatSitesChanged += OnExternalChatSitesChanged;
        InitSettingsWorldClocks();
        InitSettingsChatSites();
        SelectComboItem(TargetMonitorBox,  s.TargetMonitorMode);
        SelectComboItem(ChosenAppsModeBox, s.ChosenAppsMode);
        ChosenAppsBox.Text = string.Join(", ", s.ChosenApps ?? new());

        // Milestone 16: Polish, Lite mode, Delays, Frame Stats
        LiteModeToggle.IsOn           = s.LiteMode;
        ReducedMotionToggle.IsOn      = s.ReducedMotion;
        ExcludeFromCaptureToggle.IsOn = s.ExcludeFromCapture;
        OpenDelaySlider.Value         = s.OpenDelayMs;
        CloseDelaySlider.Value        = s.CloseDelayMs;
        AnimationDurationSlider.Value      = Math.Clamp(s.AnimationDurationMs > 0 ? s.AnimationDurationMs : 240, 80, 1000);
        CloseAnimationDurationSlider.Value = Math.Clamp(s.CloseAnimationDurationMs > 0 ? s.CloseAnimationDurationMs : 220, 80, 1000);
        FrameStatsToggle.IsOn         = s.ShowFrameStats;
        UpdateDelayLabels();
        SelectComboItem(DefaultSearchEngineBox, s.DefaultSearchEngine);
        CustomDefaultSearchUrlBox.Text = s.CustomDefaultSearchUrl ?? string.Empty;
        CustomDefaultSearchUrlBox.Visibility = string.Equals(s.DefaultSearchEngine, "custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshSearchCommandsList();
        }
        finally
        {
            _loading = false;
        }
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

    private void OnIslandColorChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncCustomColorControls();
        Save();
    }

    private void IslandCustomColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading || _updatingCustomColor) return;
        _updatingCustomColor = true;
        try
        {
            _customIslandHex = FormatHex(args.NewColor);
            if (IslandCustomColorSwatch != null)
            {
                IslandCustomColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
            }
            if (IslandCustomColorHexBox != null && IslandCustomColorHexBox.Text != _customIslandHex)
            {
                IslandCustomColorHexBox.Text = _customIslandHex;
            }
        }
        finally
        {
            _updatingCustomColor = false;
        }
        Save(debounceDisk: true);
    }

    private void IslandCustomColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _updatingCustomColor || IslandCustomColorHexBox == null) return;
        if (!TryParseHex6(IslandCustomColorHexBox.Text, out var parsed)) return;

        _updatingCustomColor = true;
        try
        {
            _customIslandHex = FormatHex(parsed);
            if (IslandCustomColorPicker != null) IslandCustomColorPicker.Color = parsed;
            if (IslandCustomColorSwatch != null)
            {
                IslandCustomColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(parsed);
            }
        }
        finally
        {
            _updatingCustomColor = false;
        }
        Save(debounceDisk: true);
    }

    private void SyncCustomColorControls()
    {
        bool isCustom = string.Equals(TagOf(IslandColorBox), "Custom", StringComparison.OrdinalIgnoreCase);
        if (IslandCustomColorPanel != null)
        {
            IslandCustomColorPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!TryParseHex6(_customIslandHex, out var color))
        {
            color = Windows.UI.Color.FromArgb(255, 0x1E, 0x29, 0x3B);
            _customIslandHex = FormatHex(color);
        }

        _updatingCustomColor = true;
        try
        {
            if (IslandCustomColorPicker != null) IslandCustomColorPicker.Color = color;
            if (IslandCustomColorSwatch != null)
            {
                IslandCustomColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }
            if (IslandCustomColorHexBox != null) IslandCustomColorHexBox.Text = _customIslandHex;
        }
        finally
        {
            _updatingCustomColor = false;
        }
    }

    private static string FormatHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseHex6(string? text, out Windows.UI.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string raw = text.Trim().TrimStart('#');
        if (raw.Length != 6) return false;
        if (byte.TryParse(raw[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(raw[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(raw[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            color = Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }
        return false;
    }

    private void OnDefaultSearchEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomDefaultSearchUrlBox != null)
        {
            CustomDefaultSearchUrlBox.Visibility = string.Equals(TagOf(DefaultSearchEngineBox), "custom", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        Save();
    }

    private void OnCustomDefaultSearchUrlChanged(object sender, TextChangedEventArgs e) => Save();

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Save();

    private void OnCardStaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardStayCustomPanel != null)
        {
            CardStayCustomPanel.Visibility = string.Equals(TagOf(CardStayBox), "Custom", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        Save();
    }

    private void OnCardStayCustomSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (CardStayCustomLabel != null)
        {
            CardStayCustomLabel.Text = $"Custom duration: {e.NewValue:0.0} s";
        }
        Save(debounceDisk: true);
    }

    private void OnMediaPillModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _mediaPillMode = tag;
            UpdateMediaPillModeButtons();
            Save();
        }
    }

    private void UpdateMediaPillModeButtons()
    {
        var activeBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(46, 255, 255, 255));
        var clearBg  = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        Button?[] btns = { MediaPillUsualBtn, MediaPillWithNameBtn, MediaPillMiniPlayerBtn, MediaPillWithWordsBtn };
        foreach (var b in btns)
        {
            if (b == null) continue;
            bool selected = string.Equals(b.Tag as string, _mediaPillMode, StringComparison.OrdinalIgnoreCase);
            b.Background = selected ? activeBg : clearBg;
            b.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void OnHotkeyChanged(object sender, TextChangedEventArgs e) => Save();

    private void OnScreenshotsFolderChanged(object sender, TextChangedEventArgs e) => Save(debounceDisk: true);

    private async void ScreenshotsFolderBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (App.MainWindowInstance != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && ScreenshotsFolderBox != null)
            {
                ScreenshotsFolderBox.Text = folder.Path;
                Save();
            }
        }
        catch { }
    }

    private void ScreenshotsFolderResetBtn_Click(object sender, RoutedEventArgs e)
    {
        string defaultShotsFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        if (ScreenshotsFolderBox != null)
        {
            ScreenshotsFolderBox.Text = defaultShotsFolder;
        }
        Save();
    }

    private void OnChosenAppsChanged(object sender, TextChangedEventArgs e) => Save();

    private void OnBtWarningLevelsChanged(object sender, TextChangedEventArgs e) => Save();

    private void OnCustomKeyWarningsChanged(object sender, TextChangedEventArgs e) => Save();

    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSizeLabel();
        UpdateOpacityLabel();
        UpdateYOffsetLabel();
        UpdateXOffsetLabel();
        Save(debounceDisk: true);
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private void Save() => Save(debounceDisk: false);

    private void Save(bool debounceDisk)
    {
        if (_loading) return;

        var s = ServiceContainer.Settings.WindowIsland;

        bool prevEnabled = s.Enabled;
        s.Enabled       = EnabledToggle.IsOn;
        s.Position      = TagOf(PositionBox) ?? s.Position;
        s.Shape         = TagOf(ShapeBox)    ?? s.Shape;
        s.Theme         = TagOf(ThemeBox)    ?? s.Theme;
        var colorTag    = TagOf(IslandColorBox);
        s.LastCustomIslandColor = _customIslandHex;
        s.IslandColor   = string.Equals(colorTag, "Custom", StringComparison.OrdinalIgnoreCase)
            ? _customIslandHex
            : (colorTag ?? s.IslandColor);
        s.SizePercent   = (int)SizeSlider.Value;
        s.OpacityPercent = (int)OpacitySlider.Value;
        s.YOffsetDip    = (int)YOffsetSlider.Value;
        s.XOffsetDip    = (int)XOffsetSlider.Value;
        s.ShowClock     = ShowClockToggle.IsOn;
        s.TimeFormat24H = TimeFormat24HCheck.IsChecked ?? true;
        s.ShowSeconds   = ShowSecondsCheck.IsChecked ?? false;
        s.CalendarClockStyle = TagOf(CalendarClockStyleBox) ?? s.CalendarClockStyle;
        s.CalendarClockFace  = TagOf(CalendarClockFaceBox) ?? s.CalendarClockFace;
        s.CalendarDigitalFace = TagOf(CalendarDigitalFaceBox) ?? s.CalendarDigitalFace;
        s.ShowBattery   = ShowBatteryToggle.IsOn;
        s.CardCapsLockEnabled   = CardCapsLockToggle.IsOn;
        s.CardNumLockEnabled    = CardNumLockToggle.IsOn;
        s.CardDeleteKeyWarningEnabled = CardDeleteKeyWarningToggle.IsOn;
        s.CustomKeyWarnings     = WindowIslandSettings.ParseCustomKeyWarnings(CardCustomKeyWarningsBox.Text);
        s.CardVolumeEnabled     = CardVolumeToggle.IsOn;
        s.CardClipboardEnabled  = CardClipboardToggle.IsOn;
        s.CardScreenshotEnabled = CardScreenshotToggle.IsOn;
        s.CardDownloadsEnabled  = CardDownloadsToggle.IsOn;
        s.CardFileCopyEnabled   = CardFileCopyToggle.IsOn;
        s.CardStayDuration      = TagOf(CardStayBox) ?? s.CardStayDuration;
        s.CardStayCustomMs      = (int)Math.Round(Math.Clamp(CardStayCustomSlider?.Value ?? 4.0, 0.5, 30.0) * 1000.0);
        s.CardVpnEnabled        = CardVpnToggle.IsOn;
        s.CardBatteryLowEnabled = CardBatteryLowToggle.IsOn;
        s.CardChargingEnabled   = CardChargingToggle.IsOn;
        s.CardMicEnabled        = CardMicToggle.IsOn;
        s.CardCamEnabled        = CardCamToggle.IsOn;
        s.MicDotEnabled         = MicDotToggle.IsOn;
        s.CamDotEnabled         = CamDotToggle.IsOn;
        s.ShowCpuInNotch        = ShowCpuNotchToggle.IsOn;
        s.ShowRamInNotch        = ShowRamNotchToggle.IsOn;
        s.ShowDiskInNotch       = ShowDiskNotchToggle.IsOn;
        s.ShowGpuInNotch        = ShowGpuNotchToggle.IsOn;
        s.ShowDownSpeedInNotch  = ShowDownSpeedNotchToggle.IsOn;
        s.ShowUpSpeedInNotch    = ShowUpSpeedNotchToggle.IsOn;
        s.MicCamDotsEnabled     = s.MicDotEnabled || s.CamDotEnabled;
        s.MediaPlayerStyle      = TagOf(MediaPlayerStyleBox) ?? s.MediaPlayerStyle;
        s.MediaPillMode         = _mediaPillMode;
        s.LyricsEnabled         = LyricsToggle.IsOn;
        s.WeatherUnits          = TagOf(WeatherUnitsBox) ?? s.WeatherUnits;
        s.WeatherTempOnIsland   = WeatherTempOnIslandToggle.IsOn;
        s.WeatherRainWarning    = WeatherRainWarningToggle.IsOn;
        if (int.TryParse(TagOf(ChatIdleBox), out int idleMin))
        {
            s.ChatIdleMinutes = AppLock.WindowIsland.Modules.Chat.WindowIslandChatManager.ClampIdleMinutes(idleMin);
        }
        s.Hotkey        = HotkeyBox.Text.Trim();
        string defaultShotsFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        string enteredShots = ScreenshotsFolderBox?.Text?.Trim() ?? string.Empty;
        s.ScreenshotsFolder = string.Equals(enteredShots, defaultShotsFolder, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : enteredShots;
        s.DefaultSearchEngine    = TagOf(DefaultSearchEngineBox) ?? "google";
        s.CustomDefaultSearchUrl = CustomDefaultSearchUrlBox.Text.Trim();

        // Controls Tab Add-on
        s.Controls.CloseIslandAfterCard            = ControlsCloseAfterCardToggle.IsOn;
        s.Controls.ShowBluetoothBatteryCard        = ControlsShowBtBatteryCardToggle.IsOn;
        s.Controls.ShowBtBatteryOnIsland           = ControlsShowBtBatteryOnIslandToggle.IsOn;
        s.Controls.ShowMultipleBtBatteriesOnIsland = ControlsShowMultipleBtOnIslandToggle.IsOn;
        s.Controls.BtBatteryIslandStyle            = TagOf(ControlsBtBatteryIslandStyleBox) ?? s.Controls.BtBatteryIslandStyle;
        s.Controls.BtLowBatteryAlert               = ControlsBtLowBatteryAlertToggle.IsOn;
        s.Controls.BtWarningWindowsNotification    = ControlsBtWarningWindowsNotificationToggle.IsOn;
        var parsedLevels = ControlsSettings.ParseThresholds(ControlsBtWarningLevelsBox.Text);
        if (parsedLevels.Count > 0)
        {
            s.Controls.BtWarningThresholds = parsedLevels;
            s.Controls.BtLowBatteryPercent = parsedLevels[^1];
        }
        s.Controls.ShowBrightness                  = ControlsShowBrightnessToggle.IsOn;
        s.Controls.ShowMicrophone                  = ControlsShowMicrophoneToggle.IsOn;
        s.Controls.ShowPowerRow                    = ControlsShowPowerRowToggle.IsOn;

        // Milestone 15: Visibility rules
        s.HideInFullScreen           = HideInFullScreenToggle.IsOn;
        s.EdgeDwellEnabled           = EdgeDwellToggle.IsOn;
        s.AutoHide                   = AutoHideToggle.IsOn;
        s.AutoHideStayOnDesktop      = AutoHideStayOnDesktopToggle.IsOn;
        s.AutoHideOnlyWhenOverlapped = AutoHideOnlyWhenOverlappedToggle.IsOn;
        s.UnderTopTaskbar            = UnderTopTaskbarToggle.IsOn;
        s.ThroughMode                = ThroughModeToggle.IsOn;
        s.ThroughModeHotkey          = ThroughModeHotkeyBox?.Text?.Trim() ?? string.Empty;
        s.CommandBarHotkey           = CommandBarHotkeyBox?.Text?.Trim() ?? "Alt+Space";
        s.TargetMonitorMode          = TagOf(TargetMonitorBox)  ?? s.TargetMonitorMode;
        s.ChosenAppsMode             = TagOf(ChosenAppsModeBox) ?? s.ChosenAppsMode;
        s.ChosenApps                 = ChosenAppsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Milestone 16: Polish, Lite mode, Delays, Frame Stats
        s.LiteMode            = LiteModeToggle.IsOn;
        s.ReducedMotion       = ReducedMotionToggle.IsOn;
        s.AnimationDurationMs      = (int)AnimationDurationSlider.Value;
        s.CloseAnimationDurationMs = (int)CloseAnimationDurationSlider.Value;
        s.ExcludeFromCapture  = ExcludeFromCaptureToggle.IsOn;
        s.OpenDelayMs         = (int)OpenDelaySlider.Value;
        s.CloseDelayMs        = (int)CloseDelaySlider.Value;
        s.ShowFrameStats      = FrameStatsToggle.IsOn;

        if (debounceDisk)
        {
            _diskSaveTimer ??= CreateDiskSaveTimer();
            _diskSaveTimer.Stop();
            _diskSaveTimer.Start();
        }
        else
        {
            _diskSaveTimer?.Stop();
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }

        if (s.Enabled != prevEnabled)
        {
            ServiceContainer.SetWindowIslandEnabled(s.Enabled);
        }

        if (s.Enabled)
        {
            WindowIslandHost.Instance.ApplySettings(s);
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateDiskSaveTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(150);
        t.IsRepeating = false;
        t.Tick += (_, _) => ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        return t;
    }

    private void OnExternalWeatherCitiesChanged(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            var s = ServiceContainer.Settings.WindowIsland;
            var active = s.GetActiveCity();
            if (active != null && WeatherCityStatus != null)
            {
                WeatherCityStatus.Text = $"Active: {active.Name} ({active.Latitude:F2}, {active.Longitude:F2})";
            }
            RefreshSettingsWeatherCitiesList();
        });
    }

    private void RefreshSettingsWeatherCitiesList()
    {
        if (SettingsWeatherCitiesList == null) return;
        SettingsWeatherCitiesList.Children.Clear();

        var s = ServiceContainer.Settings.WindowIsland;
        var cities = s.GetWeatherCities();
        int activeIdx = Math.Clamp(s.ActiveWeatherCityIndex, 0, Math.Max(0, cities.Count - 1));

        for (int i = 0; i < cities.Count; i++)
        {
            int idx = i;
            var city = cities[i];
            bool isActive = (i == activeIdx);

            var row = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = isActive
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8)
            };

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            string title = string.IsNullOrWhiteSpace(city.Country) ? city.Name : $"{city.Name}, {city.Country}";
            info.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = isActive
                    ? $"Active · {city.Latitude:F2}, {city.Longitude:F2}"
                    : $"{city.Latitude:F2}, {city.Longitude:F2}",
                FontSize = 11,
                Opacity = 0.65
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            if (isActive)
            {
                var badge = new Border
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 3, 8, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "Active",
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                    }
                };
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);
            }
            else
            {
                var selectBtn = new Button
                {
                    Content = "Set Active",
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                selectBtn.Click += async (_, _) =>
                {
                    selectBtn.IsEnabled = false;
                    await AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.SelectCityAsync(idx);
                };
                Grid.SetColumn(selectBtn, 1);
                grid.Children.Add(selectBtn);
            }

            if (cities.Count > 1)
            {
                var removeBtn = new Button
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 }
                };
                ToolTipService.SetToolTip(removeBtn, "Remove city");
                removeBtn.Click += async (_, _) =>
                {
                    removeBtn.IsEnabled = false;
                    await AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.RemoveCityAsync(idx);
                };
                Grid.SetColumn(removeBtn, 2);
                grid.Children.Add(removeBtn);
            }

            row.Child = grid;
            SettingsWeatherCitiesList.Children.Add(row);
        }
    }

    private void WeatherCityBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            WeatherSearchBtn_Click(sender, e);
        }
    }

    private async void WeatherSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        string query = WeatherCityBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        WeatherSearchBtn.IsEnabled = false;
        WeatherCityStatus.Text = "Searching...";
        if (WeatherSearchResultsList != null)
        {
            WeatherSearchResultsList.Children.Clear();
            WeatherSearchResultsList.Visibility = Visibility.Collapsed;
        }

        try
        {
            var results = await AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.SearchCitiesAsync(query);
            if (results != null && results.Count > 0)
            {
                WeatherCityStatus.Text = $"Found {results.Count} match(es) — select one to add & activate:";
                if (WeatherSearchResultsList != null)
                {
                    foreach (var match in results.Take(5))
                    {
                        var r = match;
                        var itemBorder = new Border
                        {
                            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(12, 8, 12, 8)
                        };

                        var rowGrid = new Grid { ColumnSpacing = 12 };
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
                        txt.Children.Add(new TextBlock { Text = r.DisplayName, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                        txt.Children.Add(new TextBlock { Text = $"{r.Latitude:F2}, {r.Longitude:F2}", FontSize = 11, Opacity = 0.65 });
                        Grid.SetColumn(txt, 0);
                        rowGrid.Children.Add(txt);

                        var addBtn = new Button
                        {
                            Content = "Set & Add",
                            Padding = new Thickness(10, 4, 10, 4),
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        addBtn.Click += async (_, _) =>
                        {
                            addBtn.IsEnabled = false;
                            await AppLock.WindowIsland.Modules.Weather.WindowIslandWeatherService.Instance.AddCityAsync(
                                r.DisplayName, r.Latitude, r.Longitude, r.Country ?? string.Empty);
                            WeatherCityBox.Text = r.DisplayName;
                            WeatherCityStatus.Text = $"Active: {r.DisplayName} ({r.Latitude:F2}, {r.Longitude:F2})";
                            if (WeatherSearchResultsList != null)
                            {
                                WeatherSearchResultsList.Children.Clear();
                                WeatherSearchResultsList.Visibility = Visibility.Collapsed;
                            }
                        };
                        Grid.SetColumn(addBtn, 1);
                        rowGrid.Children.Add(addBtn);

                        itemBorder.Child = rowGrid;
                        WeatherSearchResultsList.Children.Add(itemBorder);
                    }
                    WeatherSearchResultsList.Visibility = Visibility.Visible;
                }
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

    // ── Custom AI Assistants (Chat Tab) ────────────────────────────────────────

    private int _editingChatSiteIndex = -1;

    private void OnExternalChatSitesChanged()
    {
        DispatcherQueue.TryEnqueue(InitSettingsChatSites);
    }

    private void InitSettingsChatSites()
    {
        if (SettingsChatSitesPanel == null) return;
        SettingsChatSitesPanel.Children.Clear();

        var s = ServiceContainer.Settings.WindowIsland;
        s.ChatSites ??= WindowIslandSettings.DefaultChatSites();
        var sites = s.ChatSites;

        for (int i = 0; i < sites.Count; i++)
        {
            int index = i;
            var site = sites[i];
            bool isBuiltIn = i < 3;

            var row = new Grid
            {
                ColumnSpacing = 10,
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(6)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = string.IsNullOrWhiteSpace(site.Icon) ? "\uE8BD" : site.Icon,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            textStack.Children.Add(new TextBlock
            {
                Text = site.Name,
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            textStack.Children.Add(new TextBlock
            {
                Text = site.Url,
                FontSize = 11,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(textStack, 1);
            row.Children.Add(textStack);

            if (isBuiltIn)
            {
                var badge = new TextBlock
                {
                    Text = "Default",
                    FontSize = 11,
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
            }
            else
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

                var editBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 12 },
                    Padding = new Thickness(6),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };
                ToolTipService.SetToolTip(editBtn, $"Edit {site.Name}");
                editBtn.Click += (_, _) =>
                {
                    _editingChatSiteIndex = index;
                    if (NewChatNameBox != null) NewChatNameBox.Text = site.Name;
                    if (NewChatUrlBox != null) NewChatUrlBox.Text = site.Url;
                    if (AddChatSiteBtnIcon != null) AddChatSiteBtnIcon.Glyph = "\uE73E";
                    if (AddChatSiteBtnText != null) AddChatSiteBtnText.Text = "Save";
                    if (CancelEditChatSiteBtn != null) CancelEditChatSiteBtn.Visibility = Visibility.Visible;
                    if (ChatSiteValidationText != null) ChatSiteValidationText.Visibility = Visibility.Collapsed;
                    NewChatNameBox?.Focus(FocusState.Programmatic);
                };
                actions.Children.Add(editBtn);

                var removeBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
                    Padding = new Thickness(6),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };
                ToolTipService.SetToolTip(removeBtn, $"Remove {site.Name}");
                removeBtn.Click += (_, _) =>
                {
                    var settings = ServiceContainer.Settings.WindowIsland;
                    if (AppLock.WindowIsland.Modules.Chat.WindowIslandChatManager.RemoveChatSite(settings, index))
                    {
                        if (_editingChatSiteIndex == index) ResetChatSiteEditState();
                        else if (_editingChatSiteIndex > index) _editingChatSiteIndex--;
                        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                        WindowIslandHost.Instance.ApplySettings(settings);
                        InitSettingsChatSites();
                    }
                };
                actions.Children.Add(removeBtn);

                Grid.SetColumn(actions, 2);
                row.Children.Add(actions);
            }

            SettingsChatSitesPanel.Children.Add(row);
        }
    }

    private void ResetChatSiteEditState()
    {
        _editingChatSiteIndex = -1;
        if (NewChatNameBox != null) NewChatNameBox.Text = string.Empty;
        if (NewChatUrlBox != null) NewChatUrlBox.Text = string.Empty;
        if (AddChatSiteBtnIcon != null) AddChatSiteBtnIcon.Glyph = "\uE710";
        if (AddChatSiteBtnText != null) AddChatSiteBtnText.Text = "Add";
        if (CancelEditChatSiteBtn != null) CancelEditChatSiteBtn.Visibility = Visibility.Collapsed;
        if (ChatSiteValidationText != null) ChatSiteValidationText.Visibility = Visibility.Collapsed;
    }

    private void CancelEditChatSiteBtn_Click(object sender, RoutedEventArgs e)
        => ResetChatSiteEditState();

    private void AddChatSiteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ChatSiteValidationText != null) ChatSiteValidationText.Visibility = Visibility.Collapsed;

        string name = NewChatNameBox?.Text?.Trim() ?? string.Empty;
        string url = NewChatUrlBox?.Text?.Trim() ?? string.Empty;
        var s = ServiceContainer.Settings.WindowIsland;

        if (_editingChatSiteIndex >= 0)
        {
            if (!AppLock.WindowIsland.Modules.Chat.WindowIslandChatManager.UpdateChatSite(s, _editingChatSiteIndex, name, url))
            {
                if (ChatSiteValidationText != null)
                {
                    ChatSiteValidationText.Text = "Enter a valid assistant name and http/https URL.";
                    ChatSiteValidationText.Visibility = Visibility.Visible;
                }
                return;
            }
            ResetChatSiteEditState();
        }
        else
        {
            if (!AppLock.WindowIsland.Modules.Chat.WindowIslandChatManager.AddChatSite(s, name, url))
            {
                if (ChatSiteValidationText != null)
                {
                    ChatSiteValidationText.Text = (s.ChatSites?.Count ?? 0) >= AppLock.WindowIsland.Modules.Chat.WindowIslandChatManager.MaxChatSites
                        ? $"Maximum of {AppLock.WindowIsland.Modules.Chat.WindowIslandChatManager.MaxChatSites} AI assistants reached."
                        : "Enter a valid assistant name and http/https URL.";
                    ChatSiteValidationText.Visibility = Visibility.Visible;
                }
                return;
            }
            if (NewChatNameBox != null) NewChatNameBox.Text = string.Empty;
            if (NewChatUrlBox != null) NewChatUrlBox.Text = string.Empty;
        }

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        WindowIslandHost.Instance.ApplySettings(s);
        InitSettingsChatSites();
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
    {
        int val = (int)OpacitySlider.Value;
        OpacityLabelText.Text = val >= 100
            ? "100% (Solid)"
            : $"{val}% ({100 - val}% transparent)";
    }

    private void UpdateYOffsetLabel()
        => YOffsetLabelText.Text = $"{(int)YOffsetSlider.Value} DIPs";

    private void UpdateXOffsetLabel()
        => XOffsetLabelText.Text = $"{(int)XOffsetSlider.Value} DIPs";

    private void RefreshBatteryThresholdsUI()
    {
        if (BatteryThresholdsPanel == null) return;
        BatteryThresholdsPanel.Children.Clear();

        var s = ServiceContainer.Settings.WindowIsland;
        var list = s.GetEffectiveLowBatteryThresholds();

        foreach (int pct in list)
        {
            int captured = pct;
            var pill = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 6, 4)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = $"{pct}%",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (list.Count > 1)
            {
                var delBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
                    Padding = new Thickness(3),
                    MinWidth = 0,
                    MinHeight = 0,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(delBtn, $"Remove {pct}% alert");
                delBtn.Click += (_, _) =>
                {
                    var settings = ServiceContainer.Settings.WindowIsland;
                    var updated = settings.GetEffectiveLowBatteryThresholds();
                    updated.Remove(captured);
                    if (updated.Count == 0) return;
                    settings.LowBatteryThresholds = updated;
                    ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                    WindowIslandHost.Instance.ApplySettings(settings);
                    RefreshBatteryThresholdsUI();
                };
                sp.Children.Add(delBtn);
            }

            pill.Child = sp;
            BatteryThresholdsPanel.Children.Add(pill);
        }
    }

    private void NewBatteryThresholdBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddBatteryThresholdBtn_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AddBatteryThresholdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (BatteryThresholdValidationText != null) BatteryThresholdValidationText.Visibility = Visibility.Collapsed;
        string raw = (NewBatteryThresholdBox?.Text ?? string.Empty).Trim().TrimEnd('%');
        if (!int.TryParse(raw, out int pct) || pct < 1 || pct > 99)
        {
            if (BatteryThresholdValidationText != null)
            {
                BatteryThresholdValidationText.Text = "Enter a number from 1 to 99.";
                BatteryThresholdValidationText.Visibility = Visibility.Visible;
            }
            return;
        }

        var s = ServiceContainer.Settings.WindowIsland;
        var list = s.GetEffectiveLowBatteryThresholds();
        if (!list.Contains(pct))
        {
            list.Add(pct);
            s.LowBatteryThresholds = list.OrderByDescending(x => x).ToList();
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            WindowIslandHost.Instance.ApplySettings(s);
        }

        if (NewBatteryThresholdBox != null) NewBatteryThresholdBox.Text = string.Empty;
        RefreshBatteryThresholdsUI();
    }

    private void ControlsResetCardsBtn_Click(object sender, RoutedEventArgs e)
    {
        var s = ServiceContainer.Settings.WindowIsland;
        s.Controls.Cards = QuickCard.Defaults();
        Save();
    }

    private void OnDelaySliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateDelayLabels();
        Save(debounceDisk: true);
    }

    private void UpdateDelayLabels()
    {
        if (AnimationDurationLabelText != null && AnimationDurationSlider != null)
            AnimationDurationLabelText.Text = $"{(int)AnimationDurationSlider.Value} ms";
        if (CloseAnimationDurationLabelText != null && CloseAnimationDurationSlider != null)
            CloseAnimationDurationLabelText.Text = $"{(int)CloseAnimationDurationSlider.Value} ms";
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
