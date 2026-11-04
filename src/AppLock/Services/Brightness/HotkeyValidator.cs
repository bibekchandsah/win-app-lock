using System.Collections.Generic;
using System.Linq;
using AppLock.Models;

namespace AppLock.Services.Brightness;

public class HotkeyValidator
{
    // System-reserved hotkeys that should never be allowed
    private static readonly HashSet<string> SystemReservedHotkeys = new()
    {
        "Ctrl+Alt+Del",
        "Win+L",
        "Alt+Tab",
        "Alt+F4",
        "Win+D",
        "Win+Tab",
        "Ctrl+Shift+Esc",
        "Win+R",
        "Win+E",
        "Alt+Esc",
        "Ctrl+Esc"
    };

    public static ValidationResult ValidateHotkey(
        HotkeyBinding? binding,
        List<BrightnessPreset> existingPresets,
        Guid? currentPresetId,
        string? emergencyHotkey,
        HotkeyBinding? increaseHotkey,
        HotkeyBinding? decreaseHotkey,
        HotkeyBinding? blurToggleHotkey)
    {
        if (binding == null)
        {
            return ValidationResult.Success();
        }

        // Must have at least one modifier (unless it's F-key with modifier)
        if (binding.Modifiers == 0)
        {
            return ValidationResult.Failure("Hotkey must include at least one modifier key (Ctrl, Alt, Shift, or Win)");
        }

        // Check against system reserved
        string hotkeyString = FormatHotkey(binding);
        if (SystemReservedHotkeys.Contains(hotkeyString))
        {
            return ValidationResult.Failure($"'{hotkeyString}' is reserved by Windows and cannot be used");
        }

        // Check against emergency hotkey (Ctrl+Alt+L by default)
        if (!string.IsNullOrEmpty(emergencyHotkey))
        {
            var emergencyBinding = ParseHotkey(emergencyHotkey);
            if (emergencyBinding != null && AreEqual(binding, emergencyBinding))
            {
                return ValidationResult.Failure($"This hotkey is already used for Emergency Lock");
            }
        }

        // Check against other brightness presets
        foreach (var preset in existingPresets)
        {
            if (preset.Id == currentPresetId) continue; // Skip self
            if (preset.Hotkey != null && AreEqual(binding, preset.Hotkey))
            {
                return ValidationResult.Failure($"This hotkey is already used for {preset.Percent}% brightness");
            }
        }

        // Check against increase/decrease hotkeys
        if (increaseHotkey != null && AreEqual(binding, increaseHotkey))
        {
            return ValidationResult.Failure("This hotkey is already used for Increase Brightness");
        }
        if (decreaseHotkey != null && AreEqual(binding, decreaseHotkey))
        {
            return ValidationResult.Failure("This hotkey is already used for Decrease Brightness");
        }

        // Check against blur toggle
        if (blurToggleHotkey != null && AreEqual(binding, blurToggleHotkey))
        {
            return ValidationResult.Failure("This hotkey is already used for Blur Overlay Toggle");
        }

        return ValidationResult.Success();
    }

    public static string FormatHotkey(HotkeyBinding binding)
    {
        var parts = new List<string>();

        const uint MOD_CONTROL = 0x0002;
        const uint MOD_ALT = 0x0001;
        const uint MOD_SHIFT = 0x0004;
        const uint MOD_WIN = 0x0008;

        if ((binding.Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((binding.Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((binding.Modifiers & MOD_WIN) != 0) parts.Add("Win");

        parts.Add(GetKeyName(binding.VirtualKey));

        return string.Join("+", parts);
    }

    public static HotkeyBinding? ParseHotkey(string hotkeyString)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString))
            return null;

        var parts = hotkeyString.Split('+').Select(p => p.Trim()).ToArray();
        uint modifiers = 0;
        uint key = 0;

        const uint MOD_CONTROL = 0x0002;
        const uint MOD_ALT = 0x0001;
        const uint MOD_SHIFT = 0x0004;
        const uint MOD_WIN = 0x0008;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    key = GetVirtualKeyCode(part);
                    break;
            }
        }

        if (key == 0)
            return null;

        return new HotkeyBinding { Modifiers = modifiers, VirtualKey = key };
    }

    private static bool AreEqual(HotkeyBinding a, HotkeyBinding b)
    {
        return a.Modifiers == b.Modifiers && a.VirtualKey == b.VirtualKey;
    }

    private static string GetKeyName(uint virtualKey)
    {
        return virtualKey switch
        {
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(), // A-Z
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(), // 0-9
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}", // F1-F24
            0x20 => "Space",
            0x0D => "Enter",
            0x1B => "Esc",
            0x09 => "Tab",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ => $"Key{virtualKey:X}"
        };
    }

    private static uint GetVirtualKeyCode(string keyName)
    {
        var upper = keyName.ToUpperInvariant();
        
        // Letters
        if (upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z')
            return upper[0];
        
        // Numbers
        if (upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9')
            return upper[0];
        
        // Function keys
        if (upper.StartsWith("F") && int.TryParse(upper[1..], out int fNum) && fNum >= 1 && fNum <= 24)
            return (uint)(0x70 + fNum - 1);

        return upper switch
        {
            "SPACE" => 0x20,
            "ENTER" => 0x0D,
            "ESC" => 0x1B,
            "TAB" => 0x09,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            _ => 0
        };
    }
}

public class ValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }

    public static ValidationResult Success() => new() { IsValid = true };
    public static ValidationResult Failure(string message) => new() { IsValid = false, ErrorMessage = message };
}
