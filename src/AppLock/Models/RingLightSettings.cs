using System.Collections.Generic;

namespace AppLock.Models;

public class RingLightSettings
{
    public bool Enabled { get; set; }
    public bool StartEnabledAtLaunch { get; set; }
    public bool RememberState { get; set; } = true;
    public string ColorMode { get; set; } = "Temperature"; // Temperature | Custom
    public int Kelvin { get; set; } = 4500;
    public string CustomColorHex { get; set; } = "#FFFFFF";
    public int BrightnessPercent { get; set; } = 80;
    public int ThicknessPercent { get; set; } = 8;
    public int SoftnessPercent { get; set; } = 50;
    public int CornerRadiusPx { get; set; } = 0;
    public bool EdgeTop { get; set; } = true;
    public bool EdgeBottom { get; set; } = true;
    public bool EdgeLeft { get; set; } = true;
    public bool EdgeRight { get; set; } = true;
    public string Style { get; set; } = "Frame"; // Frame | FullGlow
    public bool KeepTaskbarClear { get; set; } = true;
    public int FadeMs { get; set; } = 300;
    public string DisplayTarget { get; set; } = "Primary"; // Primary | BuiltIn | All | <displayId>
    public bool ShowInScreenCapture { get; set; }
    public bool BoostScreenBrightness { get; set; }
    public int BoostBrightnessPercent { get; set; } = 100;
    public bool ShowNotificationOnToggle { get; set; }
    public AutoRingSettings Auto { get; set; } = new();
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+L";
    public string? IncreaseHotkey { get; set; }
    public string? DecreaseHotkey { get; set; }
    public string? WarmerHotkey { get; set; }
    public string? CoolerHotkey { get; set; }
    public List<RingPreset> CustomPresets { get; set; } = new();
    public string? ActivePresetId { get; set; }
}

public class AutoRingSettings
{
    public bool OnWhenCameraInUse { get; set; }
    public bool TurnOffWhenCameraStops { get; set; } = true;
    public bool OnlySelectedApps { get; set; }
    public List<string> AppFilter { get; set; } = new(); // exe names or package family names
    public int AutoOffMinutes { get; set; }              // 0 = never
    public bool AutoWarmth { get; set; }
    public int DayKelvin { get; set; } = 5500;
    public int NightKelvin { get; set; } = 3500;
    public string NightStart { get; set; } = "20:00";
    public string NightEnd { get; set; } = "07:00";
}

public class RingPreset
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public int Kelvin { get; set; } = 4500;
    public string CustomColorHex { get; set; } = "#FFFFFF";
    public string ColorMode { get; set; } = "Temperature";
    public int BrightnessPercent { get; set; } = 80;
    public int ThicknessPercent { get; set; } = 8;
    public int SoftnessPercent { get; set; } = 50;
    public bool EdgeTop { get; set; } = true;
    public bool EdgeBottom { get; set; } = true;
    public bool EdgeLeft { get; set; } = true;
    public bool EdgeRight { get; set; } = true;
}

