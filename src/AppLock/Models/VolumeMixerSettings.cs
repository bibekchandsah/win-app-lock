using System;
using System.Collections.Generic;

namespace AppLock.Models;

public sealed class VolumeMixerSettings
{
    public bool Enabled { get; set; } = false;
    public string IconPosition { get; set; } = "top-center";
    public int IconSize { get; set; } = 32;
    public int IdleOpacity { get; set; } = 50;
    public int HoverOpacity { get; set; } = 100;
    public int SliderWidth { get; set; } = 200;
    public int HoverDelayMs { get; set; } = 500;
    public int MixerHideDelayMs { get; set; } = 1000;
    public int AnimationMs { get; set; } = 250;
    public int PollingMs { get; set; } = 150;
    public bool DragMode { get; set; } = false;
    public bool OnlyActiveApps { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string ThemeMode { get; set; } = "System";
    public List<string> Blacklist { get; set; } = new();
    public double? CustomX { get; set; }   // used when dragged
    public double? CustomY { get; set; }

    /// <summary>
    /// Validates and clamps all values to acceptable ranges.
    /// </summary>
    public void Clamp()
    {
        // Icon position must be one of the valid positions
        if (!IsValidIconPosition(IconPosition))
            IconPosition = "top-center";

        // Numeric ranges
        IconSize = Math.Clamp(IconSize, 16, 64);
        IdleOpacity = Math.Clamp(IdleOpacity, 10, 100);
        HoverOpacity = Math.Clamp(HoverOpacity, 10, 100);
        SliderWidth = Math.Clamp(SliderWidth, 100, 400);
        HoverDelayMs = Math.Clamp(HoverDelayMs, 0, 3000);
        MixerHideDelayMs = Math.Clamp(MixerHideDelayMs, 200, 5000);
        AnimationMs = Math.Clamp(AnimationMs, 0, 1000);
        PollingMs = Math.Clamp(PollingMs, 50, 1000);

        // Theme mode
        if (ThemeMode != "Light" && ThemeMode != "Dark" && ThemeMode != "System")
            ThemeMode = "System";

        // Blacklist: ensure all items are normalized
        if (Blacklist != null && Blacklist.Count > 0)
        {
            for (int i = 0; i < Blacklist.Count; i++)
            {
                Blacklist[i] = NormalizeProcessName(Blacklist[i]);
            }
        }
        else
        {
            Blacklist = new List<string>();
        }
    }

    private static bool IsValidIconPosition(string position)
    {
        return position switch
        {
            "top-left" => true,
            "top-right" => true,
            "top-center" => true,
            "bottom-left" => true,
            "bottom-right" => true,
            "bottom-center" => true,
            _ => false
        };
    }

    /// <summary>
    /// Normalizes a process name: trim, lower-case, add .exe if missing.
    /// </summary>
    public static string NormalizeProcessName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        name = name.Trim().ToLowerInvariant();

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";

        return name;
    }
}
