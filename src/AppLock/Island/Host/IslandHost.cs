using System;
using System.Runtime.InteropServices;
using AppLock.Island.Events;
using AppLock.Models;
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
    private LiveEventQueue? _cards;
    private LockKeyCardSource? _lockKeys;
    private ScreenshotCardSource? _screenshots;
    private IslandVolumeController? _volume;
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

            _cards = new LiveEventQueue(
                queue,
                json => _window?.PostRaw(json),
                () => _window == null || _window.IsHovered || !_window.IsVisible);

            _window.MotionStarted += duration => _cards?.HoldForMotion(duration);
            _window.WheelTurned += OnWheelTurned;
            _window.VolumeRequested += OnVolumeRequested;

            _lockKeys = new LockKeyCardSource(queue);
            _lockKeys.CapsLockChanged += OnCapsLockChanged;
            _lockKeys.Start();

            _window.Clipboard.Captured += OnClipboardCaptured;
            _window.Clipboard.Start();

            _screenshots = new ScreenshotCardSource(queue);
            _screenshots.Saved += OnScreenshotSaved;
            _screenshots.Start(settings.ScreenshotsFolder);

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

        _lockKeys?.Dispose();
        _lockKeys = null;
        _screenshots?.Dispose();
        _screenshots = null;
        _cards?.Dispose();
        _cards = null;
        _volume?.Dispose();
        _volume = null;

        if (_window != null)
        {
            _window.Ready -= OnWindowReady;
            _window.Closed -= OnWindowClosed;
            _window.WheelTurned -= OnWheelTurned;
            _window.VolumeRequested -= OnVolumeRequested;
            _window.Clipboard.Captured -= OnClipboardCaptured;
            _window.Clipboard.Dispose();
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
        _screenshots?.Start(settings.ScreenshotsFolder);
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

    // ── Live cards (island.md 5.2) ────────────────────────────────────────────

    private void OnCapsLockChanged(bool on)
    {
        var settings = ServiceContainer.Settings.Island;
        if (!settings.ShowCapsLockCard) return;

        IslandSpikeLog.Write($"card: caps {(on ? "on" : "off")}");
        _cards?.Push(new IslandCard(
            Kind: "locks",
            Icon: on ? "\uE72E" : "\uE785",
            Title: on ? "Caps Lock on" : "Caps Lock off",
            Detail: string.Empty,
            Accent: on ? "#FFD15C" : "#FFFFFF",
            Priority: IslandCardPriority.LockKey,
            StayMs: settings.CardStayMs));
    }

    private void OnClipboardCaptured(ClipboardCardInfo info)
    {
        var settings = ServiceContainer.Settings.Island;
        if (!settings.ShowClipboardCard) return;

        bool image = info.Kind == "clipboard.image";
        IslandSpikeLog.Write($"card: {info.Kind}");
        _cards?.Push(new IslandCard(
            Kind: info.Kind,
            Icon: image ? "\uE91B" : "\uE8C8",
            Title: info.Title,
            Detail: info.Detail,
            Accent: "#FFFFFF",
            Priority: IslandCardPriority.Clipboard,
            StayMs: settings.CardStayMs));
    }

    private void OnScreenshotSaved(string fileName)
    {
        var settings = ServiceContainer.Settings.Island;
        if (!settings.ShowScreenshotCard) return;

        IslandSpikeLog.Write($"card: screenshot {fileName}");
        _cards?.Push(new IslandCard(
            Kind: "screenshot",
            Icon: "\uE722",
            Title: "Screenshot saved",
            Detail: fileName,
            Accent: "#FFFFFF",
            Priority: IslandCardPriority.Screenshot,
            StayMs: settings.CardStayMs));
    }

    private void OnWheelTurned(int notches)
    {
        var settings = ServiceContainer.Settings.Island;
        if (!settings.ShowVolumeCard) return;

        _volume ??= new IslandVolumeController();
        int value = _volume.Step(notches * 2);
        if (value < 0) return;

        PushVolumeCard(value, settings);
    }

    private void OnVolumeRequested(int percent)
    {
        var settings = ServiceContainer.Settings.Island;
        _volume ??= new IslandVolumeController();
        int value = _volume.Set(percent);
        if (value < 0 || !settings.ShowVolumeCard) return;

        PushVolumeCard(value, settings);
    }

    private void PushVolumeCard(int value, IslandSettings settings)
    {
        IslandSpikeLog.Write($"card: volume {value}%");
        _cards?.Push(new IslandCard(
            Kind: "volume",
            Icon: value == 0 ? "\uE74F" : "\uE767",
            Title: "Volume",
            Detail: $"{value}%",
            Accent: "#FFFFFF",
            Priority: IslandCardPriority.Volume,
            StayMs: settings.CardStayMs,
            Value: value));
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
