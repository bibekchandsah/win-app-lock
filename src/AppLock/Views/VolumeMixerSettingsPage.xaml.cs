using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AppLock.Services;
using AppLock.Services.VolumeMixer;
using AppLock.Models;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;

namespace AppLock.Views;

public sealed partial class VolumeMixerSettingsPage : Page, INotifyPropertyChanged
{
    private string _idleOpacityText = "50%";
    private string _hoverOpacityText = "100%";
    private bool _isLoading; // Guard: prevents change handlers from saving during LoadSettings()
    private readonly ObservableCollection<ViewModels.MuteWindowItem> _muteWindowItems = new();

    public string IdleOpacityText
    {
        get => _idleOpacityText;
        set { if (_idleOpacityText != value) { _idleOpacityText = value; OnPropertyChanged(); } }
    }

    public string HoverOpacityText
    {
        get => _hoverOpacityText;
        set { if (_hoverOpacityText != value) { _hoverOpacityText = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public VolumeMixerSettingsPage()
    {
        _isLoading = true;
        this.InitializeComponent();
        LoadSettings(); // sets _isLoading = false in its finally block
    }

    private void VolumeMixerSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to real-time updates
        ServiceContainer.VolumeMixerEnabledChanged -= OnVolumeMixerEnabledChanged;
        ServiceContainer.VolumeMixerEnabledChanged += OnVolumeMixerEnabledChanged;

        ServiceContainer.VolumeMixerPositionChanged -= OnVolumeMixerPositionChanged;
        ServiceContainer.VolumeMixerPositionChanged += OnVolumeMixerPositionChanged;

        if (ServiceContainer.MuteScheduleService is { } muteSchedule)
        {
            muteSchedule.StateChanged -= OnMuteScheduleStateChanged;
            muteSchedule.StateChanged += OnMuteScheduleStateChanged;
        }

        UpdateMuteScheduleStatus();
    }

    private void VolumeMixerSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Unsubscribe from events
        ServiceContainer.VolumeMixerEnabledChanged -= OnVolumeMixerEnabledChanged;
        ServiceContainer.VolumeMixerPositionChanged -= OnVolumeMixerPositionChanged;

        if (ServiceContainer.MuteScheduleService is { } muteSchedule)
        {
            muteSchedule.StateChanged -= OnMuteScheduleStateChanged;
        }
    }

    private void OnMuteScheduleStateChanged(object? sender, string? activeWindow)
    {
        DispatcherQueue?.TryEnqueue(UpdateMuteScheduleStatus);
    }

    private void OnVolumeMixerEnabledChanged(object? sender, bool enabled)
    {
        // Update UI on dispatcher queue when toggled from tray/hotkey
        DispatcherQueue?.TryEnqueue(() =>
        {
            EnabledToggle.IsOn = enabled;
            System.Diagnostics.Debug.WriteLine($"[Settings] Volume Mixer UI updated: {enabled}");
        });
    }

    private void OnVolumeMixerPositionChanged(object? sender, string positionId)
    {
        // Update the combo box when position is changed from the tray menu
        DispatcherQueue?.TryEnqueue(() =>
        {
            // Guard against re-entrant saves: set _isLoading while we update the UI
            _isLoading = true;
            try
            {
                IconPositionComboBox.SelectedIndex = positionId switch
                {
                    "top-left"      => 0,
                    "top-right"     => 1,
                    "top-center"    => 2,
                    "bottom-left"   => 3,
                    "bottom-right"  => 4,
                    "bottom-center" => 5,
                    _               => 2
                };
                System.Diagnostics.Debug.WriteLine($"[Settings] Icon position combo synced to: {positionId}");
            }
            finally
            {
                _isLoading = false;
            }
        });
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void LoadSettings()
    {
        _isLoading = true;
        try
        {
        var s = ServiceContainer.Settings.VolumeMixer;

        // Enable/Hotkey
        EnabledToggle.IsOn = s.Enabled;
        HotkeyEnabledToggle.IsOn = s.HotkeyEnabled;
        HotkeyTextBox.Text = s.Hotkey;

        // Appearance
        IconPositionComboBox.SelectedIndex = s.IconPosition switch
        {
            "top-left"      => 0,
            "top-right"     => 1,
            "top-center"    => 2,
            "bottom-left"   => 3,
            "bottom-right"  => 4,
            "bottom-center" => 5,
            _ => 2
        };
        IconSizeNumberBox.Value    = s.IconSize;
        IdleOpacitySlider.Value    = s.IdleOpacity;
        IdleOpacityText            = $"{s.IdleOpacity}%";
        HoverOpacitySlider.Value   = s.HoverOpacity;
        HoverOpacityText           = $"{s.HoverOpacity}%";
        SliderWidthNumberBox.Value = s.SliderWidth;
        ShowMicrophoneVolumeToggle.IsOn = s.ShowMicrophoneVolume;
        LockMicrophoneVolumeToggle.IsOn = s.LockMicrophoneVolume;
        ShowCameraBrightnessToggle.IsOn = s.ShowCameraBrightness;
        ShowScreenBrightnessToggle.IsOn = s.ShowScreenBrightness;

        // Auto mute schedule
        MuteScheduleToggle.IsOn = s.MuteScheduleEnabled;
        MuteSpeakersCheckBox.IsChecked = s.MuteSpeakersOnSchedule;
        MuteMicrophoneCheckBox.IsChecked = s.MuteMicrophoneOnSchedule;
        BuildMuteWindowList();
        UpdateMuteScheduleStatus();

        // Behavior
        HoverDelayNumberBox.Value       = s.HoverDelayMs;
        MixerHideDelayNumberBox.Value   = s.MixerHideDelayMs;
        AnimationSpeedNumberBox.Value   = s.AnimationMs;
        PollingIntervalNumberBox.Value  = s.PollingMs;
        DragModeToggle.IsOn             = s.DragMode;
        OnlyActiveAppsToggle.IsOn       = s.OnlyActiveApps;
        StartWithWindowsToggle.IsOn     = s.StartWithWindows;

        // Blacklist
        BlacklistListView.ItemsSource = new ObservableCollection<string>(s.Blacklist);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void SaveSettings()
    {
        if (_isLoading) return;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private void LockMicrophoneVolumeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        bool enabled = LockMicrophoneVolumeToggle.IsOn;
        ServiceContainer.Settings.VolumeMixer.LockMicrophoneVolume = enabled;

        // Whatever the microphones sit at right now is the level that should stick.
        if (enabled) ServiceContainer.MicrophoneVolumeLockService?.CaptureCurrentVolumes();

        SaveSettings();
        ServiceContainer.RefreshMicrophoneVolumeLock();
        System.Diagnostics.Debug.WriteLine($"[Settings] Microphone volume lock {(enabled ? "enabled" : "disabled")}");
    }

    // ── Appearance ────────────────────────────────────────────────────────────

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        bool enabled = EnabledToggle.IsOn;
        ServiceContainer.Settings.VolumeMixer.Enabled = enabled;
        SaveSettings();
        ServiceContainer.SetVolumeMixerEnabled(enabled);
        System.Diagnostics.Debug.WriteLine($"[Settings] Volume Mixer {(enabled ? "enabled" : "disabled")}");
    }

    private void HotkeyEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ServiceContainer.Settings.VolumeMixer.HotkeyEnabled = HotkeyEnabledToggle.IsOn;
        SaveSettings();
        ServiceContainer.VolumeMixerHotkeyManager?.RegisterHotkey();
        System.Diagnostics.Debug.WriteLine($"[Settings] Volume Mixer hotkey {(HotkeyEnabledToggle.IsOn ? "enabled" : "disabled")}");
    }

    private async void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var currentHotkey = ServiceContainer.Settings.VolumeMixer.Hotkey;
        var dialog = new ChangeHotkeyDialog(currentHotkey) { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();

        if (dialog.HotkeyChanged && !string.IsNullOrEmpty(dialog.NewHotkey))
        {
            var oldHotkey = ServiceContainer.Settings.VolumeMixer.Hotkey;
            ServiceContainer.Settings.VolumeMixer.Hotkey = dialog.NewHotkey;
            HotkeyTextBox.Text = dialog.NewHotkey;
            SaveSettings();
            ServiceContainer.VolumeMixerHotkeyManager?.RegisterHotkey();
            System.Diagnostics.Debug.WriteLine($"[Settings] Volume Mixer hotkey changed from {oldHotkey} to {dialog.NewHotkey}");
            ServiceContainer.NotificationService?.ShowSuccess("Hotkey Changed", $"Volume Mixer hotkey changed to: {dialog.NewHotkey}");
        }
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.Settings.VolumeMixer.Hotkey = "Alt+V";
        HotkeyTextBox.Text = "Alt+V";
        SaveSettings();
        ServiceContainer.VolumeMixerHotkeyManager?.RegisterHotkey();
        System.Diagnostics.Debug.WriteLine("[Settings] Volume Mixer hotkey reset to Alt+V");
    }

    private void IconPositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (IconPositionComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            // Use the central helper so the tray menu checkmarks are synced via
            // the VolumeMixerPositionChanged event (custom drag position is cleared).
            ServiceContainer.SetVolumeMixerPosition(tag);
            System.Diagnostics.Debug.WriteLine($"[Settings] Icon position changed to: {tag}");
        }
    }

    private void IconSizeNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading) return;
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.IconSize = (int)sender.Value;
            SaveSettings();
            ServiceContainer.VolumeOverlayService?.RefreshSettings();
        }
    }

    private void IdleOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading) return;
        var value = (int)e.NewValue;
        IdleOpacityText = $"{value}%";
        ServiceContainer.Settings.VolumeMixer.IdleOpacity = value;
        SaveSettings();
        ServiceContainer.VolumeOverlayService?.RefreshSettings();
    }

    private void HoverOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading) return;
        var value = (int)e.NewValue;
        HoverOpacityText = $"{value}%";
        ServiceContainer.Settings.VolumeMixer.HoverOpacity = value;
        SaveSettings();
        ServiceContainer.VolumeOverlayService?.RefreshSettings();
    }

    private void SliderWidthNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading) return;
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.SliderWidth = (int)sender.Value;
            SaveSettings();
            ServiceContainer.VolumeOverlayService?.RefreshSettings();
        }
    }

    private void ShowMicrophoneVolumeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ServiceContainer.Settings.VolumeMixer.ShowMicrophoneVolume = ShowMicrophoneVolumeToggle.IsOn;
        SaveSettings();
        ServiceContainer.VolumeOverlayService?.RefreshSettings();
        System.Diagnostics.Debug.WriteLine($"[Settings] Microphone volume {(ShowMicrophoneVolumeToggle.IsOn ? "shown" : "hidden")}");
    }

    private void ShowCameraBrightnessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ServiceContainer.Settings.VolumeMixer.ShowCameraBrightness = ShowCameraBrightnessToggle.IsOn;
        SaveSettings();
        ServiceContainer.VolumeOverlayService?.RefreshSettings();
        System.Diagnostics.Debug.WriteLine($"[Settings] Camera brightness {(ShowCameraBrightnessToggle.IsOn ? "shown" : "hidden")}");
    }

    private void ShowScreenBrightnessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ServiceContainer.Settings.VolumeMixer.ShowScreenBrightness = ShowScreenBrightnessToggle.IsOn;
        SaveSettings();
        ServiceContainer.VolumeOverlayService?.RefreshSettings();
        System.Diagnostics.Debug.WriteLine($"[Settings] Screen brightness {(ShowScreenBrightnessToggle.IsOn ? "shown" : "hidden")}");
    }

    // ── Behavior ──────────────────────────────────────────────────────────────

    private void HoverDelayNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading) return;
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.HoverDelayMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void MixerHideDelayNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading) return;
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.MixerHideDelayMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void AnimationSpeedNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading) return;
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.AnimationMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void PollingIntervalNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading) return;
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.PollingMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void DragModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ServiceContainer.Settings.VolumeMixer.DragMode = DragModeToggle.IsOn;
        SaveSettings();
    }

    private void OnlyActiveAppsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ServiceContainer.Settings.VolumeMixer.OnlyActiveApps = OnlyActiveAppsToggle.IsOn;
        SaveSettings();
    }

    private async void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        var enabled = StartWithWindowsToggle.IsOn;
        try
        {
            var success = await StartupManager.SetStartWithWindows(enabled);
            if (success)
            {
                ServiceContainer.Settings.VolumeMixer.StartWithWindows = enabled;
                SaveSettings();
            }
            else
            {
                StartWithWindowsToggle.IsOn = !enabled;
                ShowHint("Failed to update startup settings. Check app permissions.");
            }
        }
        catch (Exception ex)
        {
            StartWithWindowsToggle.IsOn = !enabled;
            ShowHint($"Error: {ex.Message}");
        }
    }

    // ── Blacklist ─────────────────────────────────────────────────────────────

    private void BlacklistListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RemoveBlacklistButton.IsEnabled = BlacklistListView.SelectedItem != null;

    private void BlacklistInputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddBlacklistItem();
            e.Handled = true;
        }
    }

    private void AddBlacklistButton_Click(object sender, RoutedEventArgs e) => AddBlacklistItem();

    private void AddBlacklistItem()
    {
        var input = BlacklistInputTextBox.Text;
        if (string.IsNullOrWhiteSpace(input)) { ShowHint("Please enter a process name."); return; }

        var normalized = VolumeMixerSettings.NormalizeProcessName(input);
        if (string.IsNullOrEmpty(normalized)) { ShowHint("Invalid process name."); return; }

        if (ServiceContainer.Settings.VolumeMixer.Blacklist.Contains(normalized))
        {
            ShowHint("This process is already in the blacklist.");
            return;
        }

        ServiceContainer.Settings.VolumeMixer.Blacklist.Add(normalized);
        SaveSettings();

        if (BlacklistListView.ItemsSource is ObservableCollection<string> list)
            list.Add(normalized);

        BlacklistInputTextBox.Text = string.Empty;
        HideHint();
    }

    private void RemoveBlacklistButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlacklistListView.SelectedItem is string selected)
        {
            ServiceContainer.Settings.VolumeMixer.Blacklist.Remove(selected);
            SaveSettings();
            if (BlacklistListView.ItemsSource is ObservableCollection<string> list)
                list.Remove(selected);
            HideHint();
        }
    }

    private void ShowHint(string message)
    {
        BlacklistHintTextBlock.Text = message;
        BlacklistHintTextBlock.Visibility = Visibility.Visible;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { BlacklistHintTextBlock.Visibility = Visibility.Collapsed; timer.Stop(); };
        timer.Start();
    }

    private void HideHint() => BlacklistHintTextBlock.Visibility = Visibility.Collapsed;

    // ── Auto mute schedule ────────────────────────────────────────────────────

    private void BuildMuteWindowList()
    {
        var windows = ServiceContainer.Settings.VolumeMixer.MuteWindows;

        if (MuteWindowsList.ItemsSource == null) MuteWindowsList.ItemsSource = _muteWindowItems;

        _muteWindowItems.Clear();
        foreach (var window in windows) _muteWindowItems.Add(new ViewModels.MuteWindowItem(window));

        NoMuteWindowsText.Visibility = _muteWindowItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMuteScheduleStatus()
    {
        if (MuteScheduleStatusText == null) return;

        var settings = ServiceContainer.Settings.VolumeMixer;
        var summary = MuteSchedule.Summary(settings.MuteWindows);
        var service = ServiceContainer.MuteScheduleService;

        if (!settings.MuteScheduleEnabled)
        {
            MuteScheduleStatusText.Text = settings.MuteWindows.Count == 0
                ? "Schedule is off and no ranges are configured."
                : $"Schedule is off. Ranges: {summary}";
            return;
        }

        var targets = DescribeTargets(settings);
        if (targets == null)
        {
            MuteScheduleStatusText.Text = "Schedule is on but neither speakers nor microphone are selected, so nothing will happen.";
            return;
        }

        if (service?.ActiveWindowText is { } active)
        {
            var muted = new System.Collections.Generic.List<string>();
            if (service.IsSpeakersMutedBySchedule) muted.Add("speakers");
            if (service.IsMicrophoneMutedBySchedule) muted.Add("microphone");

            MuteScheduleStatusText.Text = muted.Count > 0
                ? $"Muted right now for {active} ({string.Join(" and ", muted)})."
                : $"Range {active} is active, but the devices could not be muted (no default device?).";
            return;
        }

        MuteScheduleStatusText.Text = settings.MuteWindows.Count == 0
            ? $"Schedule is on for {targets} but no ranges are configured, so nothing will happen."
            : $"Waiting ({targets}). Ranges: {summary}";
    }

    /// <summary>"speakers", "microphone" or "speakers and microphone"; null when neither is selected.</summary>
    private static string? DescribeTargets(VolumeMixerSettings settings)
    {
        var speakers = settings.MuteSpeakersOnSchedule;
        var microphone = settings.MuteMicrophoneOnSchedule;

        if (speakers && microphone) return "speakers and microphone";
        if (speakers) return "speakers";
        if (microphone) return "microphone";
        return null;
    }

    private void MuteTarget_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var settings = ServiceContainer.Settings.VolumeMixer;
        settings.MuteSpeakersOnSchedule = MuteSpeakersCheckBox.IsChecked == true;
        settings.MuteMicrophoneOnSchedule = MuteMicrophoneCheckBox.IsChecked == true;

        SaveSettings();
        ServiceContainer.RefreshMuteSchedule();
        UpdateMuteScheduleStatus();
    }

    private void MuteScheduleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        ServiceContainer.Settings.VolumeMixer.MuteScheduleEnabled = MuteScheduleToggle.IsOn;
        SaveSettings();
        ServiceContainer.RefreshMuteSchedule();
        UpdateMuteScheduleStatus();
        System.Diagnostics.Debug.WriteLine($"[Settings] Auto mute schedule {(MuteScheduleToggle.IsOn ? "enabled" : "disabled")}");
    }

    private void AddMuteWindow_Click(object sender, RoutedEventArgs e) => _ = AddMuteWindowAsync();

    private async Task AddMuteWindowAsync()
    {
        var settings = ServiceContainer.Settings.VolumeMixer;

        if (settings.MuteWindows.Count >= MuteSchedule.MaxWindows)
        {
            await ShowMuteNoticeAsync("That is enough ranges",
                $"Up to {MuteSchedule.MaxWindows} ranges can be active. Remove one before adding another.");
            return;
        }

        var range = await ShowMuteWindowDialogAsync(null);
        if (range == null) return;

        settings.MuteWindows.Add(new MuteWindow
        {
            Start = MuteSchedule.Format(range.Value.Start),
            End = MuteSchedule.Format(range.Value.End)
        });

        SaveSettings();
        ServiceContainer.RefreshMuteSchedule();
        BuildMuteWindowList();
        UpdateMuteScheduleStatus();
    }

    private async void EditMuteWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ViewModels.MuteWindowItem item) return;

        var range = await ShowMuteWindowDialogAsync(item.Window);
        if (range == null) return;

        item.Window.Start = MuteSchedule.Format(range.Value.Start);
        item.Window.End = MuteSchedule.Format(range.Value.End);

        SaveSettings();
        ServiceContainer.RefreshMuteSchedule();
        BuildMuteWindowList();
        UpdateMuteScheduleStatus();
    }

    private void DeleteMuteWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ViewModels.MuteWindowItem item) return;

        ServiceContainer.Settings.VolumeMixer.MuteWindows.Remove(item.Window);
        SaveSettings();
        ServiceContainer.RefreshMuteSchedule();
        BuildMuteWindowList();
        UpdateMuteScheduleStatus();
    }

    private async Task<(TimeSpan Start, TimeSpan End)?> ShowMuteWindowDialogAsync(MuteWindow? existing)
    {
        var startPicker = new TimePicker
        {
            Header = "Mute from",
            ClockIdentifier = "24HourClock",
            Time = MuteSchedule.ParseOrDefault(existing?.Start, TimeSpan.FromHours(23))
        };

        var endPicker = new TimePicker
        {
            Header = "Unmute at",
            ClockIdentifier = "24HourClock",
            Time = MuteSchedule.ParseOrDefault(existing?.End, TimeSpan.FromHours(7))
        };

        var hint = new TextBlock
        {
            Text = "If the end is earlier than the start the range crosses midnight, so 23:00 – 05:00 mutes overnight.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(startPicker);
        panel.Children.Add(endPicker);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            Title = existing == null ? "Add mute range" : "Edit mute range",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.ActualTheme ?? ServiceContainer.CurrentTheme
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        if (startPicker.Time == endPicker.Time)
        {
            await ShowMuteNoticeAsync("That range is empty", "Pick a different start and end time.");
            return null;
        }

        return (startPicker.Time, endPicker.Time);
    }

    private async Task ShowMuteNoticeAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.ActualTheme ?? ServiceContainer.CurrentTheme
        };

        await dialog.ShowAsync();
    }

    // ── Restore Defaults ──────────────────────────────────────────────────────

    private void RestoreAppearanceDefaults_Click(object sender, RoutedEventArgs e)
    {
        var s = ServiceContainer.Settings.VolumeMixer;
        s.IconSize      = 32;
        s.IdleOpacity   = 50;
        s.HoverOpacity  = 100;
        s.SliderWidth   = 200;
        s.ShowCameraBrightness = true;
        s.ShowScreenBrightness = true;
        SaveSettings();
        ServiceContainer.VolumeOverlayService?.RefreshSettings();

        // Use the central helper so the tray menu checkmarks are updated too
        ServiceContainer.SetVolumeMixerPosition("top-center");

        // Reload UI
        _isLoading = true;
        try
        {
            IconPositionComboBox.SelectedIndex = 2; // top-center
            IconSizeNumberBox.Value            = 32;
            IdleOpacitySlider.Value            = 50;
            IdleOpacityText                    = "50%";
            HoverOpacitySlider.Value           = 100;
            HoverOpacityText                   = "100%";
            SliderWidthNumberBox.Value         = 200;
            ShowCameraBrightnessToggle.IsOn    = true;
            ShowScreenBrightnessToggle.IsOn    = true;
        }
        finally
        {
            _isLoading = false;
        }

        System.Diagnostics.Debug.WriteLine("[Settings] Appearance defaults restored");
    }

    private void RestoreBehaviorDefaults_Click(object sender, RoutedEventArgs e)
    {
        var s = ServiceContainer.Settings.VolumeMixer;
        s.HoverDelayMs     = 500;
        s.MixerHideDelayMs = 1000;
        s.AnimationMs      = 250;
        s.PollingMs        = 150;
        s.DragMode         = false;
        s.OnlyActiveApps   = false;
        SaveSettings();
        ServiceContainer.VolumeOverlayService?.RefreshSettings();

        // Reload UI
        HoverDelayNumberBox.Value     = 500;
        MixerHideDelayNumberBox.Value = 1000;
        AnimationSpeedNumberBox.Value = 250;
        PollingIntervalNumberBox.Value = 150;
        DragModeToggle.IsOn           = false;
        OnlyActiveAppsToggle.IsOn     = false;

        System.Diagnostics.Debug.WriteLine("[Settings] Behavior defaults restored");
    }

    private void ClearBlacklist_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.Settings.VolumeMixer.Blacklist.Clear();
        SaveSettings();

        if (BlacklistListView.ItemsSource is System.Collections.ObjectModel.ObservableCollection<string> list)
            list.Clear();

        System.Diagnostics.Debug.WriteLine("[Settings] Blacklist cleared");
    }
}
