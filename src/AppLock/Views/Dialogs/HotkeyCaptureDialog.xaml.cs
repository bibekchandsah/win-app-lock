using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using AppLock.Models;
using AppLock.Services.Brightness;

namespace AppLock.Views.Dialogs;

public sealed partial class HotkeyCaptureDialog : ContentDialog
{
    private uint _modifiers;
    private uint _virtualKey;
    private readonly Func<HotkeyBinding?, ValidationResult> _validator;

    public HotkeyBinding? CapturedHotkey { get; private set; }

    public HotkeyCaptureDialog(HotkeyBinding? currentHotkey, Func<HotkeyBinding?, ValidationResult> validator)
    {
        this.InitializeComponent();
        _validator = validator;

        if (currentHotkey != null)
        {
            _modifiers = currentHotkey.Modifiers;
            _virtualKey = currentHotkey.VirtualKey;
            UpdateKeycapDisplay();
        }
        else
        {
            // Show placeholder
            KeycapDisplay.ItemsSource = new List<string> { "Press a key..." };
        }

        ApplyTheme();
        Loaded += HotkeyCaptureDialog_Loaded;
    }

    private void ApplyTheme()
    {
        if (this.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            this.RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            this.RequestedTheme = AppLock.Services.ServiceContainer.CurrentTheme;
        }
    }

    private void HotkeyCaptureDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // Focus the root panel to receive keyboard input
        RootPanel.Focus(FocusState.Programmatic);
        
        // Re-apply theme when loaded
        ApplyTheme();
        
        System.Diagnostics.Debug.WriteLine("[HotkeyCaptureDialog] Dialog loaded and focused");
    }

    private void ContentDialog_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[HotkeyCaptureDialog] PreviewKeyDown event: {e.Key} (OriginalKey: {e.OriginalKey})");
        HandleKeyDown(e);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        System.Diagnostics.Debug.WriteLine($"[HotkeyCaptureDialog] OnKeyDown: {e.Key}");
        HandleKeyDown(e);
    }

    private void HandleKeyDown(KeyRoutedEventArgs e)
    {
        // Use OriginalKey which is the actual physical key pressed
        var key = e.OriginalKey != VirtualKey.None ? e.OriginalKey : e.Key;
        
        System.Diagnostics.Debug.WriteLine($"[HotkeyCaptureDialog] HandleKeyDown called with key: {key} (Original: {e.OriginalKey}, Key: {e.Key})");
        
        // Update modifiers
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_ALT = 0x0001;
        const uint MOD_SHIFT = 0x0004;
        const uint MOD_WIN = 0x0008;

        _modifiers = 0;
        
        // Use GetKeyState from user32.dll for WinUI 3
        if (IsKeyDown(VirtualKey.Control))
        {
            _modifiers |= MOD_CONTROL;
            System.Diagnostics.Debug.WriteLine("[HotkeyCaptureDialog] Ctrl detected");
        }
        if (IsKeyDown(VirtualKey.Menu)) // Alt key
        {
            _modifiers |= MOD_ALT;
            System.Diagnostics.Debug.WriteLine("[HotkeyCaptureDialog] Alt detected");
        }
        if (IsKeyDown(VirtualKey.Shift))
        {
            _modifiers |= MOD_SHIFT;
            System.Diagnostics.Debug.WriteLine("[HotkeyCaptureDialog] Shift detected");
        }
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            _modifiers |= MOD_WIN;
            System.Diagnostics.Debug.WriteLine("[HotkeyCaptureDialog] Win detected");
        }

        System.Diagnostics.Debug.WriteLine($"[HotkeyCaptureDialog] Total modifiers: {_modifiers}");

        // Ignore modifier keys themselves
        if (key == VirtualKey.Control || key == VirtualKey.Shift || key == VirtualKey.Menu ||
            key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows)
        {
            UpdateKeycapDisplay();
            e.Handled = true;
            return;
        }

        // Capture the key
        _virtualKey = (uint)key;
        System.Diagnostics.Debug.WriteLine($"[HotkeyCaptureDialog] Virtual key captured: {_virtualKey} ({key})");
        
        UpdateKeycapDisplay();
        ValidateCurrentHotkey();

        e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private bool IsKeyDown(VirtualKey key)
    {
        return (GetKeyState((int)key) & 0x8000) != 0;
    }

    private void UpdateKeycapDisplay()
    {
        var keycaps = new List<string>();

        const uint MOD_CONTROL = 0x0002;
        const uint MOD_ALT = 0x0001;
        const uint MOD_SHIFT = 0x0004;
        const uint MOD_WIN = 0x0008;

        if ((_modifiers & MOD_CONTROL) != 0) keycaps.Add("Ctrl");
        if ((_modifiers & MOD_ALT) != 0) keycaps.Add("Alt");
        if ((_modifiers & MOD_SHIFT) != 0) keycaps.Add("Shift");
        if ((_modifiers & MOD_WIN) != 0) keycaps.Add("Win");

        if (_virtualKey != 0)
        {
            keycaps.Add(GetKeyName(_virtualKey));
        }

        KeycapDisplay.ItemsSource = keycaps.Any() ? keycaps : new List<string> { "Press a key..." };
    }

    private void ValidateCurrentHotkey()
    {
        if (_virtualKey == 0)
        {
            ErrorInfoBar.IsOpen = false;
            IsPrimaryButtonEnabled = false;
            return;
        }

        var binding = new HotkeyBinding { Modifiers = _modifiers, VirtualKey = _virtualKey };
        var result = _validator(binding);

        if (result.IsValid)
        {
            ErrorInfoBar.IsOpen = false;
            IsPrimaryButtonEnabled = true;
        }
        else
        {
            ErrorInfoBar.Message = result.ErrorMessage ?? "Invalid hotkey";
            ErrorInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = false;
        }
    }

    private void SaveButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_virtualKey != 0)
        {
            CapturedHotkey = new HotkeyBinding { Modifiers = _modifiers, VirtualKey = _virtualKey };
        }
    }

    private void ClearButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        CapturedHotkey = null;
        args.Cancel = true; // Don't close the dialog
        _modifiers = 0;
        _virtualKey = 0;
        UpdateKeycapDisplay();
        ErrorInfoBar.IsOpen = false;
    }

    private string GetKeyName(uint virtualKey)
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
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            _ => $"Key{virtualKey:X}"
        };
    }
}
