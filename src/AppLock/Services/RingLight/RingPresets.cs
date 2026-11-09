using System.Collections.Generic;
using AppLock.Models;

namespace AppLock.Services.RingLight;

/// <summary>
/// Built-in preset definitions and helpers for applying a preset to <see cref="RingLightSettings"/>.
/// </summary>
public static class RingPresets
{
    // ── Built-in preset IDs (stable, never change) ────────────────────────
    public const string WarmId    = "builtin:warm";
    public const string NeutralId = "builtin:neutral";
    public const string CoolId    = "builtin:cool";
    public const string SoftId    = "builtin:soft";
    public const string StudioId  = "builtin:studio";
    public const string MaxId     = "builtin:maxlight";

    public static IReadOnlyList<RingPreset> BuiltIn { get; } = new List<RingPreset>
    {
        new RingPreset
        {
            Id            = WarmId,
            Name          = "Warm",
            IsBuiltIn     = true,
            ColorMode     = "Temperature",
            Kelvin        = 3200,
            BrightnessPercent = 85,
            ThicknessPercent  = 10,
            SoftnessPercent   = 50,
            EdgeTop = true, EdgeBottom = true, EdgeLeft = true, EdgeRight = true,
        },
        new RingPreset
        {
            Id            = NeutralId,
            Name          = "Neutral",
            IsBuiltIn     = true,
            ColorMode     = "Temperature",
            Kelvin        = 4500,
            BrightnessPercent = 80,
            ThicknessPercent  = 8,
            SoftnessPercent   = 50,
            EdgeTop = true, EdgeBottom = true, EdgeLeft = true, EdgeRight = true,
        },
        new RingPreset
        {
            Id            = CoolId,
            Name          = "Cool",
            IsBuiltIn     = true,
            ColorMode     = "Temperature",
            Kelvin        = 6000,
            BrightnessPercent = 80,
            ThicknessPercent  = 8,
            SoftnessPercent   = 50,
            EdgeTop = true, EdgeBottom = true, EdgeLeft = true, EdgeRight = true,
        },
        new RingPreset
        {
            Id            = SoftId,
            Name          = "Soft",
            IsBuiltIn     = true,
            ColorMode     = "Temperature",
            Kelvin        = 4500,
            BrightnessPercent = 60,
            ThicknessPercent  = 12,
            SoftnessPercent   = 80,
            EdgeTop = true, EdgeBottom = true, EdgeLeft = true, EdgeRight = true,
        },
        new RingPreset
        {
            Id            = StudioId,
            Name          = "Studio",
            IsBuiltIn     = true,
            ColorMode     = "Temperature",
            Kelvin        = 5000,
            BrightnessPercent = 90,
            ThicknessPercent  = 10,
            SoftnessPercent   = 50,
            EdgeTop = true, EdgeBottom = false, EdgeLeft = true, EdgeRight = true,
        },
        new RingPreset
        {
            Id            = MaxId,
            Name          = "Max light",
            IsBuiltIn     = true,
            ColorMode     = "Temperature",
            Kelvin        = 6500,
            BrightnessPercent = 100,
            ThicknessPercent  = 18,
            SoftnessPercent   = 30,
            EdgeTop = true, EdgeBottom = true, EdgeLeft = true, EdgeRight = true,
        },
    };

    /// <summary>Apply a preset's visual settings to the given <see cref="RingLightSettings"/> instance.</summary>
    public static void ApplyTo(RingPreset preset, RingLightSettings settings)
    {
        settings.ColorMode        = preset.ColorMode;
        settings.Kelvin           = preset.Kelvin;
        settings.CustomColorHex   = preset.CustomColorHex;
        settings.BrightnessPercent = preset.BrightnessPercent;
        settings.ThicknessPercent = preset.ThicknessPercent;
        settings.SoftnessPercent  = preset.SoftnessPercent;
        settings.EdgeTop          = preset.EdgeTop;
        settings.EdgeBottom       = preset.EdgeBottom;
        settings.EdgeLeft         = preset.EdgeLeft;
        settings.EdgeRight        = preset.EdgeRight;
        settings.ActivePresetId   = preset.Id;
    }

    /// <summary>Snapshot the current visual settings into a new <see cref="RingPreset"/>.</summary>
    public static RingPreset SnapshotFrom(RingLightSettings s, string name) => new RingPreset
    {
        Name              = name,
        IsBuiltIn         = false,
        ColorMode         = s.ColorMode,
        Kelvin            = s.Kelvin,
        CustomColorHex    = s.CustomColorHex,
        BrightnessPercent = s.BrightnessPercent,
        ThicknessPercent  = s.ThicknessPercent,
        SoftnessPercent   = s.SoftnessPercent,
        EdgeTop           = s.EdgeTop,
        EdgeBottom        = s.EdgeBottom,
        EdgeLeft          = s.EdgeLeft,
        EdgeRight         = s.EdgeRight,
    };
}

