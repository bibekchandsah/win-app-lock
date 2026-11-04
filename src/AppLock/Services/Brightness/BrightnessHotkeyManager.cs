using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.Brightness;

/// <summary>
/// Manages brightness hotkey registration and actions
/// </summary>
public class BrightnessHotkeyManager : IDisposable
{
    private readonly HotkeyService _hotkeyService;
    private readonly BrightnessService _brightnessService;
    private readonly OsdService _osdService;
    private readonly BlurOverlayService _blurOverlayService;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<int, Guid> _hotkeyIdToPresetId = new();
    private int _previousBrightness = 100;
    private Guid? _lastPressedPresetId;
    private int _blurToggleHotkeyId = -1;

    public BrightnessHotkeyManager(
        HotkeyService hotkeyService,
        BrightnessService brightnessService,
        OsdService osdService,
        BlurOverlayService blurOverlayService,
        DispatcherQueue dispatcher)
    {
        _hotkeyService = hotkeyService;
        _brightnessService = brightnessService;
        _osdService = osdService;
        _blurOverlayService = blurOverlayService;
        _dispatcher = dispatcher;
    }

    public void RegisterAllHotkeys()
    {
        UnregisterAllHotkeys();

        var settings = ServiceContainer.Settings.Brightness;

        // Register preset hotkeys
        foreach (var preset in settings.Presets)
        {
            if (preset.Hotkey != null)
            {
                RegisterPresetHotkey(preset);
            }
        }

        // Register blur toggle hotkey
        RegisterBlurToggleHotkey();

        // Register reset display hotkey
        RegisterResetDisplayHotkey();
    }

    private void RegisterPresetHotkey(BrightnessPreset preset)
    {
        if (preset.Hotkey == null) return;

        try
        {
            var modifiers = (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)preset.Hotkey.Modifiers;
            var key = preset.Hotkey.VirtualKey;

            int hotkeyId = _hotkeyService.RegisterHotkey(modifiers, key, () =>
            {
                OnPresetHotkeyPressed(preset);
            });

            if (hotkeyId > 0)
            {
                _hotkeyIdToPresetId[hotkeyId] = preset.Id;
                System.Diagnostics.Debug.WriteLine($"Registered brightness preset {preset.Percent}% hotkey");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to register preset hotkey: {ex.Message}");
        }
    }

    private void RegisterBlurToggleHotkey()
    {
        try
        {
            var settings = ServiceContainer.Settings.Brightness.Blur;
            
            // Default: Ctrl+Shift+B
            uint modifiers = 0x0007; // MOD_CONTROL | MOD_SHIFT | MOD_ALT (0x0002 | 0x0004 | 0x0001)
            uint key = 0x42; // B

            if (settings.ToggleHotkey != null)
            {
                modifiers = settings.ToggleHotkey.Modifiers;
                key = settings.ToggleHotkey.VirtualKey;
            }
            else
            {
                // Set default
                modifiers = 0x0006; // Ctrl+Shift (without Alt)
                settings.ToggleHotkey = new HotkeyBinding { Modifiers = modifiers, VirtualKey = key };
            }

            var hotkeyModifiers = (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)modifiers;

            _blurToggleHotkeyId = _hotkeyService.RegisterHotkey(hotkeyModifiers, key, () =>
            {
                OnBlurTogglePressed();
            });

            if (_blurToggleHotkeyId > 0)
            {
                System.Diagnostics.Debug.WriteLine("Registered blur toggle hotkey (Ctrl+Shift+B)");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to register blur toggle hotkey: {ex.Message}");
        }
    }

    private void RegisterResetDisplayHotkey()
    {
        try
        {
            // Ctrl+Alt+Shift+R
            const uint MOD_CONTROL = 0x0002;
            const uint MOD_ALT = 0x0001;
            const uint MOD_SHIFT = 0x0004;
            uint modifiers = MOD_CONTROL | MOD_ALT | MOD_SHIFT;
            uint key = 0x52; // R

            var hotkeyModifiers = (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)modifiers;

            int hotkeyId = _hotkeyService.RegisterHotkey(hotkeyModifiers, key, () =>
            {
                OnResetDisplayPressed();
            });

            if (hotkeyId > 0)
            {
                System.Diagnostics.Debug.WriteLine("Registered reset display hotkey (Ctrl+Alt+Shift+R)");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to register reset display hotkey: {ex.Message}");
        }
    }

    private async void OnPresetHotkeyPressed(BrightnessPreset preset)
    {
        try
        {
            var settings = ServiceContainer.Settings.Brightness;
            
            // Check if hotkeys are enabled
            if (!settings.HotkeysEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Brightness hotkeys are disabled");
                return;
            }
            
            int targetBrightness = preset.Percent;

            // Check "press again to restore previous"
            if (settings.PressAgainRestoresPrevious && _lastPressedPresetId == preset.Id)
            {
                targetBrightness = _previousBrightness;
                _lastPressedPresetId = null;
            }
            else
            {
                // Save current as previous
                _previousBrightness = await _brightnessService.GetBrightnessAsync(settings.SelectedDisplayId);
                _lastPressedPresetId = preset.Id;
            }

            // Set brightness
            await _brightnessService.SetBrightnessAsync(targetBrightness, settings.SelectedDisplayId, debounce: false);

            // Show OSD
            if (settings.ShowOsd)
            {
                _osdService.Show(targetBrightness);
            }

            System.Diagnostics.Debug.WriteLine($"Brightness set to {targetBrightness}% via hotkey");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Preset hotkey action error: {ex.Message}");
        }
    }

    private void OnBlurTogglePressed()
    {
        try
        {
            var settings = ServiceContainer.Settings.Brightness.Blur;
            
            // Check if blur hotkey is enabled
            if (!settings.HotkeyEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Blur hotkey is disabled");
                return;
            }
            
            _blurOverlayService.Toggle();
            System.Diagnostics.Debug.WriteLine($"Blur overlay toggled: {_blurOverlayService.IsActive}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Blur toggle error: {ex.Message}");
        }
    }

    private async void OnResetDisplayPressed()
    {
        try
        {
            var settings = ServiceContainer.Settings.Brightness;

            // Hide blur if active
            if (_blurOverlayService.IsActive)
            {
                _blurOverlayService.Hide();
            }

            // Set to 100%
            await _brightnessService.SetBrightnessAsync(100, settings.SelectedDisplayId, debounce: false);

            // Show OSD
            if (settings.ShowOsd)
            {
                _osdService.Show(100);
            }

            System.Diagnostics.Debug.WriteLine("Reset display to 100%");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reset display error: {ex.Message}");
        }
    }

    public void UnregisterAllHotkeys()
    {
        foreach (var hotkeyId in _hotkeyIdToPresetId.Keys)
        {
            _hotkeyService.UnregisterHotkey(hotkeyId);
        }
        _hotkeyIdToPresetId.Clear();

        if (_blurToggleHotkeyId > 0)
        {
            _hotkeyService.UnregisterHotkey(_blurToggleHotkeyId);
            _blurToggleHotkeyId = -1;
        }
    }

    public void Dispose()
    {
        UnregisterAllHotkeys();
    }
}
