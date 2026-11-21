using System;
using System.Linq;
using AppLock.Island.Host;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AppLock.Views;

/// <summary>
/// Island settings (WinUI, per island-webview2.md these stay native). Every change is saved to
/// settings.json and pushed to the island over the bridge immediately.
/// </summary>
public sealed partial class IslandSettingsPage : Page
{
    private bool _loading = true;

    public IslandSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private IslandSettings Settings => ServiceContainer.Settings.Island;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = Settings;

        EnabledToggle.IsOn = settings.Enabled;
        HardwareAccelerationToggle.IsOn = settings.HardwareAcceleration;
        SelectTag(PositionBox, settings.Position);
        SelectTag(ShapeBox, settings.Shape);
        SelectTag(ThemeBox, settings.Theme);
        SizeSlider.Value = settings.SizePercent;
        OpacitySlider.Value = settings.OpacityPercent;
        HotkeyBox.Text = settings.Hotkey;
        ClockToggle.IsOn = settings.ShowClock;
        BatteryToggle.IsOn = settings.ShowBattery;
        StatusText.Text = $"Hotkey: {settings.Hotkey}";

        _loading = false;
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        var item = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));

        if (item != null) box.SelectedItem = item;
        else if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box)
    {
        return (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => Save();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e) => Save();

    private void OnHotkeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        string hotkey = HotkeyBox.Text.Trim();
        if (string.IsNullOrEmpty(hotkey))
        {
            HotkeyBox.Text = Settings.Hotkey;
            return;
        }

        var (_, key) = HotkeyService.ParseHotkey(hotkey);
        if (key == 0)
        {
            StatusText.Text = $"\"{hotkey}\" is not a valid hotkey (try Ctrl+Alt+I).";
            HotkeyBox.Text = Settings.Hotkey;
            return;
        }

        Save();
        StatusText.Text = $"Hotkey: {Settings.Hotkey}";
    }

    private void Save()
    {
        if (_loading) return;

        var settings = Settings;
        settings.Enabled = EnabledToggle.IsOn;
        settings.HardwareAcceleration = HardwareAccelerationToggle.IsOn;
        settings.Position = TagOf(PositionBox);
        settings.Shape = TagOf(ShapeBox);
        settings.Theme = TagOf(ThemeBox);
        settings.SizePercent = (int)Math.Round(SizeSlider.Value);
        settings.OpacityPercent = (int)Math.Round(OpacitySlider.Value);
        settings.ShowClock = ClockToggle.IsOn;
        settings.ShowBattery = BatteryToggle.IsOn;

        string hotkey = HotkeyBox.Text.Trim();
        if (!string.IsNullOrEmpty(hotkey) && HotkeyService.ParseHotkey(hotkey).key != 0)
        {
            settings.Hotkey = hotkey;
        }

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        IslandHost.Instance.ApplySettings();
    }
}
