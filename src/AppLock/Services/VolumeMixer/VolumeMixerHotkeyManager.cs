using System;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Manages Volume Mixer hotkey registration and toggle action
/// </summary>
public class VolumeMixerHotkeyManager : IDisposable
{
    private readonly HotkeyService _hotkeyService;
    private readonly DispatcherQueue _dispatcherQueue;
    private int _toggleHotkeyId = -1;

    public VolumeMixerHotkeyManager(HotkeyService hotkeyService, DispatcherQueue dispatcherQueue)
    {
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    /// <summary>
    /// Register or re-register the Volume Mixer toggle hotkey based on current settings
    /// </summary>
    public void RegisterHotkey()
    {
        // Unregister existing hotkey
        if (_toggleHotkeyId > 0)
        {
            _hotkeyService.UnregisterHotkey(_toggleHotkeyId);
            _toggleHotkeyId = -1;
            System.Diagnostics.Debug.WriteLine("[VolumeMixerHotkey] Unregistered previous hotkey");
        }

        var settings = ServiceContainer.Settings.VolumeMixer;
        
        // Check if hotkeys are enabled
        if (!settings.HotkeyEnabled)
        {
            System.Diagnostics.Debug.WriteLine("[VolumeMixerHotkey] Hotkey is disabled in settings");
            return;
        }

        try
        {
            // Parse hotkey string (e.g., "Alt+V")
            var (modifiers, key) = HotkeyService.ParseHotkey(settings.Hotkey);
            
            if (key == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixerHotkey] Invalid hotkey: {settings.Hotkey}");
                return;
            }

            // Register toggle hotkey
            _toggleHotkeyId = _hotkeyService.RegisterHotkey(modifiers, key, () =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        // Toggle Volume Mixer enabled state
                        bool currentState = ServiceContainer.Settings.VolumeMixer.Enabled;
                        bool newState = !currentState;
                        
                        System.Diagnostics.Debug.WriteLine($"[VolumeMixerHotkey] Toggling: {currentState} → {newState}");
                        
                        ServiceContainer.SetVolumeMixerEnabled(newState);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VolumeMixerHotkey] Error toggling via hotkey: {ex.Message}");
                    }
                });
            });

            if (_toggleHotkeyId > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixerHotkey] ✅ Successfully registered: {settings.Hotkey} (ID: {_toggleHotkeyId})");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixerHotkey] ❌ FAILED to register: {settings.Hotkey} - Hotkey may already be in use");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixerHotkey] Registration error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_toggleHotkeyId > 0)
        {
            _hotkeyService.UnregisterHotkey(_toggleHotkeyId);
            _toggleHotkeyId = -1;
            System.Diagnostics.Debug.WriteLine("[VolumeMixerHotkey] Disposed");
        }
    }
}
