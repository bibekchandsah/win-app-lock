using System;
using System.Collections.Generic;
using System.Text;
using Windows.System;

namespace AppLock.Services;

public static class HotkeyParser
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public static bool TryParse(string hotkeyText, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkeyText))
            return false;

        string[] parts = hotkeyText.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_CONTROL;
            }
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_ALT;
            }
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_SHIFT;
            }
            else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_WIN;
            }
            else
            {
                // This is the key
                vk = ParseVirtualKey(part);
            }
        }

        if (vk == 0) return false;

        // Validation rule: Require at least one modifier, except for function keys (F1 to F24)
        bool isFunctionKey = vk >= (uint)VirtualKey.F1 && vk <= (uint)VirtualKey.F24;
        if (!isFunctionKey && modifiers == 0)
        {
            return false;
        }

        return true;
    }

    public static string FormatHotkey(uint modifiers, uint vk)
    {
        var sb = new StringBuilder();
        if ((modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & MOD_ALT) != 0) sb.Append("Alt+");
        if ((modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((modifiers & MOD_WIN) != 0) sb.Append("Win+");

        string keyName = FormatVirtualKey(vk);
        sb.Append(keyName);
        return sb.ToString();
    }

    private static uint ParseVirtualKey(string keyName)
    {
        if (keyName.Length == 1)
        {
            char c = char.ToUpperInvariant(keyName[0]);
            if (c >= 'A' && c <= 'Z') return (uint)c;
            if (c >= '0' && c <= '9') return (uint)c;
        }

        if (Enum.TryParse<VirtualKey>(keyName, ignoreCase: true, out var vk))
        {
            return (uint)vk;
        }

        return keyName.ToUpperInvariant() switch
        {
            "ESC" or "ESCAPE" => (uint)VirtualKey.Escape,
            "SPACE" => (uint)VirtualKey.Space,
            "TAB" => (uint)VirtualKey.Tab,
            "ENTER" or "RETURN" => (uint)VirtualKey.Enter,
            "BACKSPACE" or "BACK" => (uint)VirtualKey.Back,
            "DELETE" or "DEL" => (uint)VirtualKey.Delete,
            "INSERT" or "INS" => (uint)VirtualKey.Insert,
            "HOME" => (uint)VirtualKey.Home,
            "END" => (uint)VirtualKey.End,
            "PAGEUP" or "PGUP" => (uint)VirtualKey.PageUp,
            "PAGEDOWN" or "PGDN" => (uint)VirtualKey.PageDown,
            _ => 0
        };
    }

    private static string FormatVirtualKey(uint vk)
    {
        if (vk >= (uint)VirtualKey.A && vk <= (uint)VirtualKey.Z)
        {
            return ((char)vk).ToString();
        }
        if (vk >= (uint)VirtualKey.Number0 && vk <= (uint)VirtualKey.Number9)
        {
            return ((char)vk).ToString();
        }
        if (vk >= (uint)VirtualKey.F1 && vk <= (uint)VirtualKey.F24)
        {
            return "F" + (vk - (uint)VirtualKey.F1 + 1);
        }

        var virtualKey = (VirtualKey)vk;
        return virtualKey switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Enter => "Enter",
            VirtualKey.Tab => "Tab",
            VirtualKey.Escape => "Esc",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            _ => virtualKey.ToString()
        };
    }
}

