using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.Models;
using AppLock.Services;

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
        UpdateSizeLabel();
        UpdateOpacityLabel();

        ShowClockToggle.IsOn   = s.ShowClock;
        ShowBatteryToggle.IsOn = s.ShowBattery;

        _loading = false;
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void OnSettingChanged(object sender, RoutedEventArgs e) => Save();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSizeLabel();
        UpdateOpacityLabel();
        Save();
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private void Save()
    {
        if (_loading) return;

        var s = ServiceContainer.Settings.WindowIsland;

        s.Enabled       = EnabledToggle.IsOn;
        s.Position      = TagOf(PositionBox) ?? s.Position;
        s.Shape         = TagOf(ShapeBox)    ?? s.Shape;
        s.Theme         = TagOf(ThemeBox)    ?? s.Theme;
        s.SizePercent   = (int)SizeSlider.Value;
        s.OpacityPercent = (int)OpacitySlider.Value;
        s.ShowClock     = ShowClockToggle.IsOn;
        s.ShowBattery   = ShowBatteryToggle.IsOn;

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SetWindowIslandEnabled(s.Enabled);
        WindowIsland.Host.WindowIslandHost.Instance.ApplySettings(s);
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
}
