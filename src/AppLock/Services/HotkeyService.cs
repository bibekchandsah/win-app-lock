using System;
using System.Collections.Generic;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AppLock.Services;

/// <summary>
/// Service for registering and handling system-wide hotkeys
/// </summary>
public class HotkeyService : IDisposable
{
    private readonly Dictionary<int, Action> _hotkeyActions = new();
    private HWND _messageWindowHwnd;
    private int _nextHotkeyId = 1;
    private const int WM_HOTKEY = 0x0312;

    public event EventHandler<int>? HotkeyPressed;

    public HotkeyService()
    {
        // Create a message-only window for receiving hotkey messages
        CreateMessageWindow();
    }

    private void CreateMessageWindow()
    {
        // For WinUI 3, we'll use the main window's HWND
        // This will be set from the MainWindow
    }

    public void SetWindowHandle(HWND hwnd)
    {
        _messageWindowHwnd = hwnd;
    }

    /// <summary>
    /// Register a hotkey with the system
    /// </summary>
    /// <param name="modifiers">Modifier keys (Ctrl, Alt, Shift, Win)</param>
    /// <param name="key">Virtual key code</param>
    /// <param name="action">Action to execute when hotkey is pressed</param>
    /// <returns>Hotkey ID for later unregistration, or -1 if failed</returns>
    public int RegisterHotkey(HOT_KEY_MODIFIERS modifiers, uint key, Action action)
    {
        if (_messageWindowHwnd == HWND.Null)
        {
            System.Diagnostics.Debug.WriteLine("Cannot register hotkey: window handle not set");
            return -1;
        }

        int hotkeyId = _nextHotkeyId++;

        bool success = PInvoke.RegisterHotKey(_messageWindowHwnd, hotkeyId, modifiers, key);

        if (success)
        {
            _hotkeyActions[hotkeyId] = action;
            System.Diagnostics.Debug.WriteLine($"Registered hotkey {hotkeyId}: {modifiers}+{key}");
            return hotkeyId;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"Failed to register hotkey: {modifiers}+{key}");
            return -1;
        }
    }

    /// <summary>
    /// Unregister a previously registered hotkey
    /// </summary>
    public void UnregisterHotkey(int hotkeyId)
    {
        if (_messageWindowHwnd != HWND.Null && hotkeyId > 0)
        {
            PInvoke.UnregisterHotKey(_messageWindowHwnd, hotkeyId);
            _hotkeyActions.Remove(hotkeyId);
            System.Diagnostics.Debug.WriteLine($"Unregistered hotkey {hotkeyId}");
        }
    }

    /// <summary>
    /// Process a WM_HOTKEY message
    /// Should be called from the window's message handler
    /// </summary>
    public void ProcessHotkeyMessage(int hotkeyId)
    {
        if (_hotkeyActions.TryGetValue(hotkeyId, out var action))
        {
            System.Diagnostics.Debug.WriteLine($"Hotkey {hotkeyId} pressed, executing action");
            action?.Invoke();
            HotkeyPressed?.Invoke(this, hotkeyId);
        }
    }

    /// <summary>
    /// Parse a hotkey string like "Ctrl+Alt+L" into modifiers and key
    /// </summary>
    public static (HOT_KEY_MODIFIERS modifiers, uint key) ParseHotkey(string hotkeyString)
    {
        var parts = hotkeyString.Split('+');
        HOT_KEY_MODIFIERS modifiers = 0;
        uint key = 0;

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            switch (trimmed.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;
                    break;
                default:
                    // It's a key
                    key = GetVirtualKeyCode(trimmed);
                    break;
            }
        }

        return (modifiers, key);
    }

    /// <summary>
    /// Convert key name to virtual key code
    /// </summary>
    private static uint GetVirtualKeyCode(string keyName)
    {
        // Common keys
        return keyName.ToUpperInvariant() switch
        {
            "A" => 0x41, "B" => 0x42, "C" => 0x43, "D" => 0x44,
            "E" => 0x45, "F" => 0x46, "G" => 0x47, "H" => 0x48,
            "I" => 0x49, "J" => 0x4A, "K" => 0x4B, "L" => 0x4C,
            "M" => 0x4D, "N" => 0x4E, "O" => 0x4F, "P" => 0x50,
            "Q" => 0x51, "R" => 0x52, "S" => 0x53, "T" => 0x54,
            "U" => 0x55, "V" => 0x56, "W" => 0x57, "X" => 0x58,
            "Y" => 0x59, "Z" => 0x5A,
            "0" => 0x30, "1" => 0x31, "2" => 0x32, "3" => 0x33,
            "4" => 0x34, "5" => 0x35, "6" => 0x36, "7" => 0x37,
            "8" => 0x38, "9" => 0x39,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
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
            _ => 0
        };
    }

    public void Dispose()
    {
        // Unregister all hotkeys
        foreach (var hotkeyId in _hotkeyActions.Keys.ToList())
        {
            UnregisterHotkey(hotkeyId);
        }
        _hotkeyActions.Clear();
    }
}
