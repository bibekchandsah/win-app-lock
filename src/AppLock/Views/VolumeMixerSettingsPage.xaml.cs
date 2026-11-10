using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AppLock.Services;
using AppLock.Services.VolumeMixer;
using AppLock.Models;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;

namespace AppLock.Views;

public sealed partial class VolumeMixerSettingsPage : Page, INotifyPropertyChanged
{
    private string _idleOpacityText = "50%";
    private string _hoverOpacityText = "100%";

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
        this.InitializeComponent();
        LoadSettings();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void LoadSettings()
    {
        var s = ServiceContainer.Settings.VolumeMixer;

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

    private void SaveSettings() => ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

    // ── Appearance ────────────────────────────────────────────────────────────

    private void IconPositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconPositionComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ServiceContainer.Settings.VolumeMixer.IconPosition = tag;
            ServiceContainer.Settings.VolumeMixer.CustomX = null;
            ServiceContainer.Settings.VolumeMixer.CustomY = null;
            SaveSettings();
        }
    }

    private void IconSizeNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.IconSize = (int)sender.Value;
            SaveSettings();
        }
    }

    private void IdleOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var value = (int)e.NewValue;
        IdleOpacityText = $"{value}%";
        ServiceContainer.Settings.VolumeMixer.IdleOpacity = value;
        SaveSettings();
    }

    private void HoverOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var value = (int)e.NewValue;
        HoverOpacityText = $"{value}%";
        ServiceContainer.Settings.VolumeMixer.HoverOpacity = value;
        SaveSettings();
    }

    private void SliderWidthNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.SliderWidth = (int)sender.Value;
            SaveSettings();
        }
    }

    // ── Behavior ──────────────────────────────────────────────────────────────

    private void HoverDelayNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.HoverDelayMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void MixerHideDelayNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.MixerHideDelayMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void AnimationSpeedNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.AnimationMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void PollingIntervalNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(sender.Value))
        {
            ServiceContainer.Settings.VolumeMixer.PollingMs = (int)sender.Value;
            SaveSettings();
        }
    }

    private void DragModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ServiceContainer.Settings.VolumeMixer.DragMode = DragModeToggle.IsOn;
        SaveSettings();
    }

    private void OnlyActiveAppsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ServiceContainer.Settings.VolumeMixer.OnlyActiveApps = OnlyActiveAppsToggle.IsOn;
        SaveSettings();
    }

    private async void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
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
        timer.Tick += (s, _) => { BlacklistHintTextBlock.Visibility = Visibility.Collapsed; ((DispatcherTimer)s).Stop(); };
        timer.Start();
    }

    private void HideHint() => BlacklistHintTextBlock.Visibility = Visibility.Collapsed;
}
