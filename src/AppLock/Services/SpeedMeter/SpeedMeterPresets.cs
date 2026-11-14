using System.Collections.Generic;
using System.Text.Json;
using AppLock.Models;

namespace AppLock.Services.SpeedMeter;

/// <summary>
/// Built-in appearance presets and the appearance-only copy used by custom presets and settings
/// import. Nothing here touches Enabled, the hotkey, alerts or data usage settings.
/// </summary>
public static class SpeedMeterPresets
{
    public static readonly IReadOnlyList<(string Id, string Name)> BuiltIn = new[]
    {
        ("minimal", "Minimal"),
        ("gamer", "Gamer"),
        ("streamer", "Streamer"),
        ("monitor", "Monitor")
    };

    public static void Apply(SpeedMeterSettings settings, string id)
    {
        switch (id)
        {
            case "minimal":
                settings.ThemePreset = "Transparent";
                settings.BackgroundOpacityPercent = 0;
                settings.Compact = true;
                settings.ScalePercent = 90;
                settings.CornerRadiusPx = 8;
                settings.Layout = "Horizontal";
                settings.ShowShadow = false;
                settings.ShowBorder = false;
                settings.ShowSparkline = false;
                settings.ShowCpu = false;
                settings.ShowRam = false;
                settings.ShowDisk = false;
                settings.ShowGpu = false;
                settings.ShowBattery = false;
                settings.ShowTodayTotal = false;
                break;

            case "gamer":
                settings.ThemePreset = "Glass";
                settings.BackgroundOpacityPercent = 70;
                settings.Compact = true;
                settings.ScalePercent = 100;
                settings.CornerRadiusPx = 10;
                settings.Layout = "Horizontal";
                settings.ShowShadow = true;
                settings.ShowSparkline = true;
                settings.ShowCpu = true;
                settings.ShowRam = true;
                settings.ShowDisk = false;
                settings.ShowGpu = false;
                settings.ShowBattery = false;
                settings.ShowTodayTotal = false;
                break;

            case "streamer":
                settings.ThemePreset = "Transparent";
                settings.BackgroundOpacityPercent = 0;
                settings.Compact = false;
                settings.ScalePercent = 100;
                settings.CornerRadiusPx = 12;
                settings.Layout = "Horizontal";
                settings.ShowShadow = false;
                settings.ShowBorder = false;
                settings.ShowSparkline = false;
                settings.ShowCpu = false;
                settings.ShowRam = false;
                settings.ShowDisk = false;
                settings.ShowGpu = false;
                settings.ShowBattery = false;
                settings.ShowTodayTotal = false;
                settings.HideFromCapture = true;
                break;

            case "monitor":
                settings.ThemePreset = "Solid";
                settings.BackgroundOpacityPercent = 85;
                settings.Compact = false;
                settings.ScalePercent = 100;
                settings.CornerRadiusPx = 12;
                settings.Layout = "Vertical";
                settings.ShowShadow = true;
                settings.ShowSparkline = true;
                settings.ShowCpu = true;
                settings.ShowRam = true;
                settings.ShowDisk = true;
                settings.ShowGpu = true;
                settings.ShowBattery = false;
                settings.ShowTodayTotal = true;
                break;
        }
    }

    /// <summary>Serializes just the parts a preset or an import is allowed to change.</summary>
    public static string CaptureAppearance(SpeedMeterSettings settings)
        => JsonSerializer.Serialize(Snapshot(settings));

    /// <summary>Copies the exported appearance onto the live settings object.</summary>
    public static void ApplyAppearanceJson(SpeedMeterSettings target, string json)
    {
        SpeedMeterSettings? source;

        try
        {
            source = JsonSerializer.Deserialize<SpeedMeterSettings>(json);
        }
        catch (JsonException)
        {
            // A hand-edited or unrelated JSON file must leave the current settings untouched.
            return;
        }

        if (source is null) return;

        CopyAppearance(source, target);
    }

    public static void CopyAppearance(SpeedMeterSettings from, SpeedMeterSettings to)
    {
        to.ThemePreset = from.ThemePreset;
        to.BackgroundColor = from.BackgroundColor;
        to.BackgroundOpacityPercent = from.BackgroundOpacityPercent;
        to.TextColor = from.TextColor;
        to.DownloadColor = from.DownloadColor;
        to.UploadColor = from.UploadColor;
        to.ScalePercent = from.ScalePercent;
        to.FontFamily = from.FontFamily;
        to.FontWeight = from.FontWeight;
        to.CornerRadiusPx = from.CornerRadiusPx;
        to.ShowBorder = from.ShowBorder;
        to.ShowShadow = from.ShowShadow;
        to.Layout = from.Layout;
        to.Compact = from.Compact;
        to.Units = from.Units;
        to.Decimals = from.Decimals;
        to.IntervalSeconds = from.IntervalSeconds;
        to.ShowSparkline = from.ShowSparkline;
        to.UseIcons = from.UseIcons;

        to.ShowCpu = from.ShowCpu;
        to.ShowRam = from.ShowRam;
        to.RamFormat = from.RamFormat;
        to.ShowDisk = from.ShowDisk;
        to.ShowGpu = from.ShowGpu;
        to.ShowBattery = from.ShowBattery;
        to.ShowTodayTotal = from.ShowTodayTotal;
        to.WarnCpuPercent = from.WarnCpuPercent;
        to.WarnRamPercent = from.WarnRamPercent;
        if (from.RowOrder.Count > 0) to.RowOrder = new List<string>(from.RowOrder);

        to.SnapToEdges = from.SnapToEdges;
        to.HideInFullscreen = from.HideInFullscreen;
        to.HideWhenIdle = from.HideWhenIdle;
        to.IdleThresholdKBps = from.IdleThresholdKBps;
        to.IdleSeconds = from.IdleSeconds;
        to.HideWhenDisconnected = from.HideWhenDisconnected;
        to.HideFromCapture = from.HideFromCapture;
        to.FadeOnHover = from.FadeOnHover;
        to.DoubleClickAction = from.DoubleClickAction;
    }

    private static SpeedMeterSettings Snapshot(SpeedMeterSettings settings)
    {
        var copy = new SpeedMeterSettings();
        CopyAppearance(settings, copy);
        return copy;
    }
}
