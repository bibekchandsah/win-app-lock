using AppLock.Models;
using AppLock.Services.SpeedMeter;

namespace AppLock.Tests.SpeedMeter;

public class SpeedMeterPresetsTests
{
    [Fact]
    public void MonitorPreset_EnablesTheFullStatPanel()
    {
        var settings = new SpeedMeterSettings();

        SpeedMeterPresets.Apply(settings, "monitor");

        Assert.Equal("Vertical", settings.Layout);
        Assert.True(settings.ShowCpu);
        Assert.True(settings.ShowRam);
        Assert.True(settings.ShowDisk);
        Assert.True(settings.ShowGpu);
        Assert.True(settings.ShowTodayTotal);
    }

    [Fact]
    public void MinimalPreset_IsTransparentAndCompact()
    {
        var settings = new SpeedMeterSettings();

        SpeedMeterPresets.Apply(settings, "minimal");

        Assert.Equal("Transparent", settings.ThemePreset);
        Assert.Equal(0, settings.BackgroundOpacityPercent);
        Assert.True(settings.Compact);
        Assert.False(settings.ShowCpu);
    }

    [Fact]
    public void StreamerPreset_StaysOutOfCapture()
    {
        var settings = new SpeedMeterSettings { HideFromCapture = false };

        SpeedMeterPresets.Apply(settings, "streamer");

        Assert.True(settings.HideFromCapture);
        Assert.Equal("Transparent", settings.ThemePreset);
    }

    [Fact]
    public void AppearanceRoundTrip_KeepsColorsAndLayout()
    {
        var source = new SpeedMeterSettings
        {
            TextColor = "#112233",
            DownloadColor = "#AABBCC",
            Layout = "Vertical",
            ScalePercent = 130
        };

        var json = SpeedMeterPresets.CaptureAppearance(source);
        var target = new SpeedMeterSettings();

        SpeedMeterPresets.ApplyAppearanceJson(target, json);

        Assert.Equal("#112233", target.TextColor);
        Assert.Equal("#AABBCC", target.DownloadColor);
        Assert.Equal("Vertical", target.Layout);
        Assert.Equal(130, target.ScalePercent);
    }

    [Fact]
    public void AppearanceRoundTrip_NeverChangesEnabledShortcutOrAlerts()
    {
        var source = new SpeedMeterSettings { Enabled = true, Compact = true };
        var json = SpeedMeterPresets.CaptureAppearance(source);

        var target = new SpeedMeterSettings
        {
            Enabled = false,
            ToggleHotkey = new HotkeyBinding { Modifiers = 0x0002, VirtualKey = 0x41 }
        };
        target.Alerts.Anchor = "BottomLeft";

        SpeedMeterPresets.ApplyAppearanceJson(target, json);

        Assert.True(target.Compact);
        Assert.False(target.Enabled);
        Assert.Equal(0x0002u, target.ToggleHotkey!.Modifiers);
        Assert.Equal(0x41u, target.ToggleHotkey.VirtualKey);
        Assert.Equal("BottomLeft", target.Alerts.Anchor);
    }

    [Fact]
    public void InvalidJson_IsIgnored()
    {
        var settings = new SpeedMeterSettings { TextColor = "#445566" };

        SpeedMeterPresets.ApplyAppearanceJson(settings, "not json at all");

        Assert.Equal("#445566", settings.TextColor);
    }
}
