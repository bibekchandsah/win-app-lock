using System;
using System.Text.Json;
using AppLock.Models;

namespace AppLock.Island.Bridge;

/// <summary>Colours the UI needs, resolved from the theme and the opacity setting.</summary>
public readonly record struct IslandPalette(string Fill, string Text, string DimText, string FaintText, bool IsDark);

/// <summary>
/// Builds the host → UI messages of island-webview2.md section 5. Only this class decides the
/// wire format, so the UI and the host cannot drift apart.
/// </summary>
public static class BridgeMessages
{
    public const string SettingsSnapshot = "settings.snapshot";
    public const string SettingsChanged = "settings.changed";
    public const string StateTime = "state.time";
    public const string StateBattery = "state.battery";
    public const string StateHover = "state.hover";
    public const string AnimPause = "anim.pause";
    public const string AnimResume = "anim.resume";

    public static string Envelope(string type, object? payload)
    {
        return JsonSerializer.Serialize(new { v = 1, type, payload });
    }

    /// <summary>Resolves the island palette. The Island has its own colours; AppLock pages keep theme resources.</summary>
    public static IslandPalette ResolvePalette(IslandSettings settings, bool systemUsesLightTheme)
    {
        bool dark = settings.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => !systemUsesLightTheme,
        };

        double alpha = Math.Clamp(settings.OpacityPercent, 40, 100) / 100d;

        return dark
            ? new IslandPalette(
                Fill: $"rgba(17, 17, 20, {alpha:0.###})",
                Text: "#FFFFFFFF",
                DimText: "rgba(255, 255, 255, 0.72)",
                FaintText: "rgba(255, 255, 255, 0.5)",
                IsDark: true)
            : new IslandPalette(
                Fill: $"rgba(250, 250, 252, {Math.Max(alpha, 0.92):0.###})",
                Text: "#FF16161A",
                DimText: "rgba(22, 22, 26, 0.68)",
                FaintText: "rgba(22, 22, 26, 0.48)",
                IsDark: false);
    }

    public static string SettingsSnapshotMessage(IslandSettings settings, IslandPalette palette, string appVersion)
    {
        return Envelope(SettingsSnapshot, new
        {
            enabled = settings.Enabled,
            position = settings.Position,
            shape = settings.Shape,
            theme = settings.Theme,
            dark = palette.IsDark,
            sizePercent = Math.Clamp(settings.SizePercent, 70, 150),
            opacityPercent = Math.Clamp(settings.OpacityPercent, 40, 100),
            hardwareAcceleration = settings.HardwareAcceleration,
            showClock = settings.ShowClock,
            showBattery = settings.ShowBattery,
            hotkey = settings.Hotkey,
            fill = palette.Fill,
            text = palette.Text,
            dimText = palette.DimText,
            faintText = palette.FaintText,
            version = appVersion,
        });
    }

    public static string TimeMessage(DateTime now)
    {
        return Envelope(StateTime, new
        {
            hhmm = now.ToString("HH:mm"),
            seconds = now.Second,
        });
    }

    public static string BatteryMessage(int percent, bool charging, bool present)
    {
        return Envelope(StateBattery, new { percent, charging, present });
    }

    public static string HoverMessage(bool inside)
    {
        return Envelope(StateHover, new { inside });
    }
}
