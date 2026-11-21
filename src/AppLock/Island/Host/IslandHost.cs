using System;
using System.Runtime.InteropServices;
using AppLock.Services;
using Microsoft.UI.Dispatching;

namespace AppLock.Island.Host;

/// <summary>
/// Owns the island window for the lifetime of the app: creates it once (never per show), feeds it
/// the clock and battery, applies settings live and registers the show/hide hotkey.
/// Call <see cref="Start"/> on the UI thread.
/// </summary>
public sealed class IslandHost
{
    public static IslandHost Instance { get; } = new();

    private IslandWindow? _window;
    private DispatcherQueueTimer? _clockTimer;
    private DispatcherQueueTimer? _batteryTimer;
    private int _hotkeyId = -1;
    private int _lastBatteryMinute = -1;
    private bool _started;

    public bool IsRunning => _window != null;

    /// <summary>Starts the island. Safe to call more than once.</summary>
    public void Start(bool ignoreEnabledSetting = false)
    {
        if (_started) return;

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue == null)
        {
            IslandSpikeLog.Write("host: start called off the UI thread");
            return;
        }

        _started = true;

        var settings = ServiceContainer.Settings.Island;
        if (!ignoreEnabledSetting) RegisterHotkey(settings.Hotkey);

        if (!settings.Enabled && !ignoreEnabledSetting) return;

        try
        {
            _window = new IslandWindow(settings);
            _window.Ready += OnWindowReady;
            _window.Closed += OnWindowClosed;

            _clockTimer = queue.CreateTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.IsRepeating = true;
            _clockTimer.Tick += (_, _) => OnClockTick();
            _clockTimer.Start();

            _batteryTimer = queue.CreateTimer();
            _batteryTimer.Interval = TimeSpan.FromSeconds(30);
            _batteryTimer.IsRepeating = true;
            _batteryTimer.Tick += (_, _) => SendBatteryNow();
            _batteryTimer.Start();

            SendTime();
            SendBatteryNow();

            IslandSpikeLog.Write("host: island started");
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"host: start failed ({ex.GetType().Name}: {ex.Message})");
            _window = null;
            _started = false;
        }
    }

    public void Stop()
    {
        _clockTimer?.Stop();
        _batteryTimer?.Stop();
        _clockTimer = null;
        _batteryTimer = null;

        if (_window != null)
        {
            _window.Ready -= OnWindowReady;
            _window.Closed -= OnWindowClosed;
            _window.Close();
            _window = null;
        }

        UnregisterHotkey();
        _started = false;
    }

    /// <summary>Re-applies settings after the settings page saved them.</summary>
    public void ApplySettings()
    {
        var settings = ServiceContainer.Settings.Island;

        if (!settings.Enabled)
        {
            _window?.SetIslandVisible(false);
            return;
        }

        if (_window == null)
        {
            Start();
            return;
        }

        _window.SetIslandVisible(true);
        _window.ApplySettings(settings);
    }

    public void ToggleVisibility()
    {
        if (_window == null)
        {
            Start();
            return;
        }

        _window.SetIslandVisible(!_window.IsVisible);
    }

    private void OnWindowReady()
    {
        SendTime();
        SendBatteryNow();
    }

    private void OnWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        _window = null;
        _clockTimer?.Stop();
        _batteryTimer?.Stop();
    }

    private void OnClockTick()
    {
        SendTime();

        // Battery changes slowly; one read a minute is plenty.
        int minute = DateTime.Now.Minute;
        if (minute != _lastBatteryMinute)
        {
            _lastBatteryMinute = minute;
            SendBatteryNow();
        }
    }

    private void SendTime() => _window?.SendTime(DateTime.Now);

    private void SendBatteryNow()
    {
        if (_window == null) return;

        try
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                bool present = status.BatteryFlag != BatteryFlag.NoSystemBattery
                               && status.BatteryFlag != BatteryFlag.Unknown
                               && status.BatteryLifePercent != 255;
                bool charging = (status.BatteryFlag & BatteryFlag.Charging) != 0;
                _window.SendBattery(present ? status.BatteryLifePercent : 0, charging, present);
                return;
            }
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"battery: read failed ({ex.GetType().Name})");
        }

        _window.SendBattery(0, false, false);
    }

    // ── Hotkey ───────────────────────────────────────────────────────────────

    private void RegisterHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return;

        try
        {
            var (modifiers, key) = HotkeyService.ParseHotkey(hotkey);
            if (key == 0) return;

            _hotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(modifiers, key, ToggleVisibility);
            IslandSpikeLog.Write($"host: hotkey {hotkey} id={_hotkeyId}");
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"host: hotkey registration failed ({ex.GetType().Name})");
        }
    }

    private void UnregisterHotkey()
    {
        if (_hotkeyId > 0)
        {
            ServiceContainer.HotkeyService.UnregisterHotkey(_hotkeyId);
            _hotkeyId = -1;
        }
    }

    // ── Battery ──────────────────────────────────────────────────────────────

    [Flags]
    private enum BatteryFlag : byte
    {
        High = 1,
        Low = 2,
        Critical = 4,
        Charging = 8,
        NoSystemBattery = 128,
        Unknown = 255,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public BatteryFlag BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
