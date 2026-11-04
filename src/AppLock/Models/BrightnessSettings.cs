using System;
using System.Collections.Generic;

namespace AppLock.Models;

public class BrightnessSettings
{
    public List<BrightnessPreset> Presets { get; set; } = DefaultPresets();
    public HotkeyBinding? IncreaseHotkey { get; set; }
    public HotkeyBinding? DecreaseHotkey { get; set; }
    public int StepPercent { get; set; } = 10;
    public bool ShowOsd { get; set; } = true;
    public bool PressAgainRestoresPrevious { get; set; }
    public bool RestoreOnExit { get; set; }
    public bool RememberAndApplyAtStartup { get; set; }
    public int LastBrightness { get; set; } = 100;
    public int MinSoftwareBrightness { get; set; } = 10;
    public string? SelectedDisplayId { get; set; } // null = all displays
    public bool HotkeysEnabled { get; set; } = true; // Master switch for brightness hotkeys
    public BlurSettings Blur { get; set; } = new();

    public static List<BrightnessPreset> DefaultPresets()
    {
        var presets = new List<BrightnessPreset>();
        
        // 0% = Ctrl+Shift+0
        presets.Add(new BrightnessPreset 
        { 
            Percent = 0, 
            Hotkey = new HotkeyBinding { Modifiers = 0x0007, VirtualKey = 0x30 } // Ctrl+Shift+0
        });
        
        // 10-90% = Ctrl+Shift+1-9
        for (int i = 1; i <= 9; i++)
        {
            presets.Add(new BrightnessPreset 
            { 
                Percent = i * 10, 
                Hotkey = new HotkeyBinding { Modifiers = 0x0007, VirtualKey = (uint)(0x30 + i) }
            });
        }
        
        // 100% = Ctrl+Shift+F
        presets.Add(new BrightnessPreset 
        { 
            Percent = 100, 
            Hotkey = new HotkeyBinding { Modifiers = 0x0007, VirtualKey = 0x46 } // F key
        });

        return presets;
    }
}

public class BrightnessPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Percent { get; set; }
    public HotkeyBinding? Hotkey { get; set; }
}

public class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
}

public class BlurSettings
{
    public int LevelPercent { get; set; } = 50;
    public HotkeyBinding? ToggleHotkey { get; set; } // default Ctrl+Shift+B
    public int DimPercent { get; set; }
    public string Tint { get; set; } = "None"; // None, Dark, Light
    public bool ExcludeActiveWindow { get; set; }
    public bool KeepOnAfterRestart { get; set; }
    public string? SelectedDisplayId { get; set; }
    public bool HotkeyEnabled { get; set; } = true; // Master switch for blur hotkey
}
