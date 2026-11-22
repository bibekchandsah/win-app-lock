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

        SizeSlider.Value    = s.SizePercent;
        OpacitySlider.Value = s.OpacityPercent;
        YOffsetSlider.Value = s.YOffsetDip;
        UpdateSizeLabel();
        UpdateOpacityLabel();
        UpdateYOffsetLabel();

        ShowClockToggle.IsOn   = s.ShowClock;
        TimeFormat24HCheck.IsChecked = s.TimeFormat24H;
        ShowSecondsCheck.IsChecked = s.ShowSeconds;
        ShowBatteryToggle.IsOn = s.ShowBattery;

        HotkeyBox.Text = string.IsNullOrWhiteSpace(s.Hotkey) ? "Ctrl+Shift+W" : s.Hotkey;

        _loading = false;
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void OnSettingChanged(object sender, RoutedEventArgs e) => Save();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    private void OnCheckChanged(object sender, RoutedEventArgs e) => Save();

    private void OnHotkeyChanged(object sender, TextChangedEventArgs e) => Save();

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
        s.SizePercent   = (int)SizeSlider.Value;
        s.OpacityPercent = (int)OpacitySlider.Value;
        s.YOffsetDip    = (int)YOffsetSlider.Value;
        s.ShowClock     = ShowClockToggle.IsOn;
        s.TimeFormat24H = TimeFormat24HCheck.IsChecked ?? true;
        s.ShowSeconds   = ShowSecondsCheck.IsChecked ?? false;
        s.ShowBattery   = ShowBatteryToggle.IsOn;
        s.Hotkey        = HotkeyBox.Text.Trim();

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SetWindowIslandEnabled(s.Enabled);

        if (s.Enabled != prevEnabled)
        {
            if (s.Enabled)
            {
                WindowIslandHost.Instance.Start(ignoreEnabledSetting: false, useDedicatedThread: true);
            }
            else
            {
                WindowIslandHost.Instance.Stop();
            }
        }
        else if (s.Enabled)
        {
            WindowIslandHost.Instance.ApplySettings(s);
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
}
