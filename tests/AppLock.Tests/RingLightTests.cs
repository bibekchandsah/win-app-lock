using System;
using System.Collections.Generic;
using System.Text.Json;
using AppLock.Models;
using AppLock.Services.RingLight;
using Xunit;

namespace AppLock.Tests;

public class RingLightTests
{
    // ── 1. ColorTemperature (Kelvin → RGB) ───────────────────────────────────

    [Fact]
    public void ColorTemperature_Endpoints_AreSane()
    {
        var warm = ColorTemperature.ToColor(2700, 100);
        var cool = ColorTemperature.ToColor(6500, 100);

        // 2700K is warm: strong red, lower blue
        Assert.True(warm.R > warm.B, $"Expected warm Red ({warm.R}) > Blue ({warm.B})");
        Assert.True(warm.R >= 250, $"Expected warm Red near maximum, got {warm.R}");

        // 6500K is daylight/cool: blue is much higher than at 2700K
        Assert.True(cool.B > warm.B, $"Expected cool Blue ({cool.B}) > warm Blue ({warm.B})");
    }

    [Fact]
    public void ColorTemperature_BlueChannel_IsMonotonicWithKelvin()
    {
        byte previousBlue = 0;
        for (int k = 2700; k <= 6500; k += 200)
        {
            var color = ColorTemperature.ToColor(k, 100);
            Assert.True(color.B >= previousBlue, $"Blue decreased at {k}K: {color.B} < {previousBlue}");
            previousBlue = color.B;
        }
    }

    [Fact]
    public void ColorTemperature_Clamping_WorksOutsideRange()
    {
        var clampedLow = ColorTemperature.ToColor(1000, 100);
        var exactLow   = ColorTemperature.ToColor(2700, 100);
        Assert.Equal(exactLow.R, clampedLow.R);
        Assert.Equal(exactLow.G, clampedLow.G);
        Assert.Equal(exactLow.B, clampedLow.B);

        var clampedHigh = ColorTemperature.ToColor(10000, 100);
        var exactHigh   = ColorTemperature.ToColor(6500, 100);
        Assert.Equal(exactHigh.R, clampedHigh.R);
        Assert.Equal(exactHigh.G, clampedHigh.G);
        Assert.Equal(exactHigh.B, clampedHigh.B);
    }

    [Fact]
    public void ColorTemperature_BrightnessScaling_ScalesChannels()
    {
        var full = ColorTemperature.ToColor(4500, 100);
        var half = ColorTemperature.ToColor(4500, 50);
        var zero = ColorTemperature.ToColor(4500, 0);

        Assert.Equal(0, zero.R);
        Assert.Equal(0, zero.G);
        Assert.Equal(0, zero.B);

        // Within rounding tolerance of ±2
        Assert.InRange((int)half.R, (full.R / 2) - 2, (full.R / 2) + 2);
        Assert.InRange((int)half.G, (full.G / 2) - 2, (full.G / 2) + 2);
        Assert.InRange((int)half.B, (full.B / 2) - 2, (full.B / 2) + 2);
    }

    // ── 2. Presets ───────────────────────────────────────────────────────────

    [Fact]
    public void RingPresets_BuiltInPresets_ContainRequiredDefaults()
    {
        var builtIns = RingPresets.BuiltIn;
        Assert.Contains(builtIns, p => p.Name == "Warm" && p.Kelvin == 3200 && p.BrightnessPercent == 85);
        Assert.Contains(builtIns, p => p.Name == "Neutral" && p.Kelvin == 4500);
        Assert.Contains(builtIns, p => p.Name == "Cool" && p.Kelvin == 6000);
        Assert.Contains(builtIns, p => p.Name == "Soft" && p.SoftnessPercent == 80);
        Assert.Contains(builtIns, p => p.Name == "Studio" && p.EdgeBottom == false);
        Assert.Contains(builtIns, p => p.Name == "Max light" && p.BrightnessPercent == 100 && p.Kelvin == 6500);
    }

    [Fact]
    public void RingPresets_ApplyTo_MutatesSettingsCorrectly()
    {
        var settings = new RingLightSettings
        {
            Kelvin = 4500,
            BrightnessPercent = 50,
            ThicknessPercent = 5,
        };

        var warmPreset = RingPresets.BuiltIn[0]; // Warm: 3200K, 85%, 10%
        RingPresets.ApplyTo(warmPreset, settings);

        Assert.Equal(warmPreset.Kelvin, settings.Kelvin);
        Assert.Equal(warmPreset.BrightnessPercent, settings.BrightnessPercent);
        Assert.Equal(warmPreset.ThicknessPercent, settings.ThicknessPercent);
        Assert.Equal(warmPreset.Id, settings.ActivePresetId);
    }

    [Fact]
    public void RingPresets_SnapshotFrom_CreatesMatchingPreset()
    {
        var settings = new RingLightSettings
        {
            ColorMode = "Custom",
            CustomColorHex = "#FFAA55",
            BrightnessPercent = 92,
            ThicknessPercent = 14,
            SoftnessPercent = 65,
            EdgeTop = true,
            EdgeBottom = false,
        };

        var snapshot = RingPresets.SnapshotFrom(settings, "MyPreset");

        Assert.Equal("MyPreset", snapshot.Name);
        Assert.False(snapshot.IsBuiltIn);
        Assert.Equal("Custom", snapshot.ColorMode);
        Assert.Equal("#FFAA55", snapshot.CustomColorHex);
        Assert.Equal(92, snapshot.BrightnessPercent);
        Assert.Equal(14, snapshot.ThicknessPercent);
        Assert.Equal(65, snapshot.SoftnessPercent);
        Assert.True(snapshot.EdgeTop);
        Assert.False(snapshot.EdgeBottom);
    }

    // ── 3. Auto Warmth Math (RingLightTimers) ─────────────────────────────────

    [Fact]
    public void AutoWarmth_ReturnsDayKelvin_AtNoon()
    {
        var auto = new AutoRingSettings
        {
            AutoWarmth = true,
            DayKelvin = 5500,
            NightKelvin = 3500,
            NightStart = "20:00",
            NightEnd = "07:00",
        };

        var noon = new DateTime(2026, 11, 9, 12, 0, 0);
        int kelvin = RingLightTimers.CalculateAutoWarmthKelvin(auto, noon);

        Assert.Equal(5500, kelvin);
    }

    [Fact]
    public void AutoWarmth_ReturnsNightKelvin_AtMidnight()
    {
        var auto = new AutoRingSettings
        {
            AutoWarmth = true,
            DayKelvin = 5500,
            NightKelvin = 3500,
            NightStart = "20:00",
            NightEnd = "07:00",
        };

        var midnight = new DateTime(2026, 11, 9, 0, 0, 0);
        int kelvin = RingLightTimers.CalculateAutoWarmthKelvin(auto, midnight);

        Assert.Equal(3500, kelvin);
    }

    [Fact]
    public void AutoWarmth_Interpolates_NearNightStart()
    {
        var auto = new AutoRingSettings
        {
            AutoWarmth = true,
            DayKelvin = 5500,
            NightKelvin = 3500,
            NightStart = "20:00",
            NightEnd = "07:00",
        };

        // Exactly at 20:00, midpoint of 15m transition
        var transitionPoint = new DateTime(2026, 11, 9, 20, 0, 0);
        int kelvin = RingLightTimers.CalculateAutoWarmthKelvin(auto, transitionPoint);

        // Should be halfway between 5500 and 3500: 4500K ± 50K
        Assert.InRange(kelvin, 4450, 4550);
    }

    [Fact]
    public void AutoWarmth_Respects_DisabledSetting()
    {
        var auto = new AutoRingSettings
        {
            AutoWarmth = false,
            DayKelvin = 5500,
            NightKelvin = 3500,
            NightStart = "20:00",
            NightEnd = "07:00",
        };

        var midnight = new DateTime(2026, 11, 9, 0, 0, 0);
        int kelvin = RingLightTimers.CalculateAutoWarmthKelvin(auto, midnight);

        Assert.Equal(5500, kelvin);
    }

    // ── 4. Camera App Filter Matching ────────────────────────────────────────

    [Fact]
    public void CameraUsageMonitor_MatchesFilter_AnyAppMode()
    {
        var auto = new AutoRingSettings
        {
            OnlySelectedApps = false,
            AppFilter = new List<string> { "Teams.exe" },
        };

        var active = new List<string> { "msedge.exe" };
        Assert.True(CameraUsageMonitor.MatchesFilter(active, auto));

        Assert.False(CameraUsageMonitor.MatchesFilter(new List<string>(), auto));
    }

    [Fact]
    public void CameraUsageMonitor_MatchesFilter_SelectedAppsMode()
    {
        var auto = new AutoRingSettings
        {
            OnlySelectedApps = true,
            AppFilter = new List<string> { "Teams.exe", "Zoom.exe" },
        };

        // Matching app
        Assert.True(CameraUsageMonitor.MatchesFilter(new[] { "teams.exe" }, auto));
        Assert.True(CameraUsageMonitor.MatchesFilter(new[] { "C:\\Programs\\Zoom.exe" }, auto));

        // Non-matching app
        Assert.False(CameraUsageMonitor.MatchesFilter(new[] { "chrome.exe" }, auto));
        Assert.False(CameraUsageMonitor.MatchesFilter(new List<string>(), auto));
    }

    // ── 5. Settings Serialization & Migration ────────────────────────────────

    [Fact]
    public void RingLightSettings_Defaults_AreValid()
    {
        var s = new RingLightSettings();
        Assert.Equal(4500, s.Kelvin);
        Assert.Equal(80, s.BrightnessPercent);
        Assert.Equal(8, s.ThicknessPercent);
        Assert.Equal(50, s.SoftnessPercent);
        Assert.Equal("Frame", s.Style);
        Assert.Equal("Primary", s.DisplayTarget);
        Assert.True(s.KeepTaskbarClear);
        Assert.True(s.RememberState);
        Assert.Equal(300, s.FadeMs);
        Assert.Equal(100, s.BoostBrightnessPercent);
    }

    [Fact]
    public void RingLightSettings_JsonRoundTrip_PreservesValues()
    {
        var original = new RingLightSettings
        {
            Enabled = true,
            Kelvin = 3400,
            BrightnessPercent = 75,
            ThicknessPercent = 12,
            Style = "FullGlow",
            KeepTaskbarClear = false,
            DisplayTarget = "BuiltIn",
            BoostScreenBrightness = true,
            BoostBrightnessPercent = 90,
            Auto = new AutoRingSettings
            {
                OnWhenCameraInUse = true,
                OnlySelectedApps = true,
                AppFilter = new List<string> { "Teams.exe", "Discord.exe" },
                AutoOffMinutes = 15,
                AutoWarmth = true,
                DayKelvin = 5000,
                NightKelvin = 3000,
            }
        };

        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<RingLightSettings>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Enabled);
        Assert.Equal(3400, deserialized.Kelvin);
        Assert.Equal(75, deserialized.BrightnessPercent);
        Assert.Equal("FullGlow", deserialized.Style);
        Assert.False(deserialized.KeepTaskbarClear);
        Assert.Equal("BuiltIn", deserialized.DisplayTarget);
        Assert.True(deserialized.BoostScreenBrightness);
        Assert.Equal(90, deserialized.BoostBrightnessPercent);
        Assert.True(deserialized.Auto.OnWhenCameraInUse);
        Assert.Equal(2, deserialized.Auto.AppFilter.Count);
        Assert.Contains("Discord.exe", deserialized.Auto.AppFilter);
        Assert.Equal(15, deserialized.Auto.AutoOffMinutes);
        Assert.True(deserialized.Auto.AutoWarmth);
        Assert.Equal(5000, deserialized.Auto.DayKelvin);
        Assert.Equal(3000, deserialized.Auto.NightKelvin);
    }
}
