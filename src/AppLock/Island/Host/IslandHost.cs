using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using AppLock.Island.Events;
using AppLock.Island.Modules;
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
    private IslandHomeState? _home;
    private IslandTileActions? _tiles;
    private DispatcherQueueTimer? _clockTimer;
    private DispatcherQueueTimer? _batteryTimer;
    private int _hotkeyId = -1;
    private int _lastBatteryMinute = -1;
    private bool _started;

    /// <summary>The Home tab told the host it is on screen; sampling runs only while it is.</summary>
    private bool _homeTabVisible;
    private long _motionUntilTicks;
    private string _lastVolumeFingerprint = string.Empty;
    private string _qrText = string.Empty;

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
                () => _window == null || _window.IsHovered || !_window.IsVisible,
                () => _window?.IsOpen != true);

            _window.MotionStarted += OnMotionStarted;
            _window.WheelTurned += OnWheelTurned;
            _window.VolumeRequested += OnVolumeRequested;
            _window.HomeVisibilityChanged += OnHomeVisibilityChanged;
            _window.VisibleChanged += OnVisibleChanged;
            _window.TileRequested += OnTileRequested;
            _window.MediaCommandRequested += OnMediaCommand;
            _window.VolumeDeviceRequested += OnVolumeDevice;
            _window.CardActionRequested += OnCardAction;

            // The Home tab samples on a background thread; only while it is actually visible.
            _volume ??= new IslandVolumeController();
            _home = new IslandHomeState(_volume, () => _window?.Clipboard.Recent ?? new List<RecentClip>());
            _home.StatsSampled += OnHomeStats;
            _home.MediaSampled += OnHomeMedia;
            _home.RecentSampled += OnHomeRecent;

            _tiles = new IslandTileActions(
                HideForCaptureAsync,
                ShowAfterCaptureAsync,
                () => ServiceContainer.Settings.Island.ScreenshotsFolder);

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

        _home?.Dispose();
        _home = null;
        _tiles = null;

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
            _window.MotionStarted -= OnMotionStarted;
            _window.WheelTurned -= OnWheelTurned;
            _window.VolumeRequested -= OnVolumeRequested;
            _window.HomeVisibilityChanged -= OnHomeVisibilityChanged;
            _window.VisibleChanged -= OnVisibleChanged;
            _window.TileRequested -= OnTileRequested;
            _window.MediaCommandRequested -= OnMediaCommand;
            _window.VolumeDeviceRequested -= OnVolumeDevice;
            _window.CardActionRequested -= OnCardAction;
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

    // ── Home tab (island.md 7) ───────────────────────────────────────────────

    private bool SamplingActive => _homeTabVisible && _window?.IsVisible == true;

    private bool MotionHeld => Environment.TickCount64 < _motionUntilTicks;

    private void OnMotionStarted(int durationMs)
    {
        _motionUntilTicks = Environment.TickCount64 + Math.Max(0, durationMs) + 60;
        _cards?.HoldForMotion(durationMs);
    }

    private void OnHomeVisibilityChanged(bool visible)
    {
        _homeTabVisible = visible;
        IslandSpikeLog.Write($"home: visible {visible}");
        UpdateSampling();
    }

    private void OnVisibleChanged(bool visible) => UpdateSampling();

    private void UpdateSampling()
    {
        if (SamplingActive) _home?.Start();
        else _home?.Stop();
    }

    private void OnHomeStats(HomeStats stats)
    {
        var window = _window;
        if (window == null) return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (MotionHeld || _window == null) return;
            _window.SendStats(stats);
            PushVolumeDevices(stats.Volume);
        });
    }

    private void OnHomeMedia(MediaSnapshot media)
    {
        var window = _window;
        if (window == null) return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (MotionHeld) return;
            _window?.SendMedia(media);
        });
    }

    private void OnHomeRecent(IReadOnlyList<RecentClip> items)
    {
        var window = _window;
        if (window == null) return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (MotionHeld) return;
            _window?.SendRecent(items);
        });
    }

    /// <summary>The output-device list only goes out when it changed; the level rides along.</summary>
    private void PushVolumeDevices(int percent)
    {
        if (_volume == null || _window == null) return;

        var devices = _volume.Devices();
        string fingerprint = string.Join("|", devices.Select(d => $"{d.Id}:{d.Name}:{d.IsDefault}"));
        if (string.Equals(fingerprint, _lastVolumeFingerprint, StringComparison.Ordinal)) return;

        _lastVolumeFingerprint = fingerprint;
        _window.SendVolume(percent, devices);
    }

    private void OnMediaCommand(string command)
    {
        IslandSpikeLog.Write($"media: {command}");
        if (_home != null) _ = _home.MediaCommandAsync(command);
    }

    private void OnVolumeDevice(string id)
    {
        if (_volume == null) return;

        bool changed = _volume.SetDefaultDevice(id);
        IslandSpikeLog.Write($"volume: default device {(changed ? "changed" : "not changed")}");

        _lastVolumeFingerprint = string.Empty;
        int percent = _volume.Get();
        if (percent >= 0) PushVolumeDevices(percent);
    }

    private async void OnTileRequested(string id)
    {
        try
        {
            switch (id)
            {
                case "command":
                    IslandSpikeLog.Write("tile: command bar requested (the bar arrives with milestone 8)");
                    return;

                case "network":
                    IslandSpikeLog.Write("tile: network → Speed Meter settings (island-only run has no main window)");
                    return;

                case "cpuram":
                    Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                    return;
            }

            if (_tiles == null) return;

            IslandSpikeLog.Write($"tile: {id}");
            TileOutcome? outcome = await _tiles.RunAsync(id);
            if (outcome == null) return;

            var settings = ServiceContainer.Settings.Island;
            _qrText = outcome.QrText ?? string.Empty;
            _cards?.Push(new IslandCard(
                Kind: outcome.Kind,
                Icon: outcome.Icon,
                Title: outcome.Title,
                Detail: outcome.Detail,
                Accent: outcome.Accent,
                Priority: outcome.Kind == "screenshot" ? IslandCardPriority.Screenshot : IslandCardPriority.Clipboard,
                StayMs: outcome.Actions != null ? Math.Max(settings.CardStayMs, 8000) : settings.CardStayMs,
                Actions: outcome.Actions));
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"tile: {id} failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void OnCardAction(string action)
    {
        switch (action)
        {
            case "qr.open":
                if (Uri.TryCreate(_qrText, UriKind.Absolute, out Uri? uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    IslandSpikeLog.Write($"card: qr open {uri.Host}");
                    try
                    {
                        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        IslandSpikeLog.Write($"card: qr open failed ({ex.GetType().Name})");
                    }
                }
                break;

            case "qr.copy":
                CopyText(_qrText);
                break;
        }
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            IslandSpikeLog.Write("card: qr copied");
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"card: copy failed ({ex.GetType().Name})");
        }
    }

    private Task HideForCaptureAsync()
    {
        _window?.SetIslandVisible(false);
        return Task.CompletedTask;
    }

    private Task ShowAfterCaptureAsync()
    {
        _window?.SetIslandVisible(true);
        return Task.CompletedTask;
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
        _volume ??= new IslandVolumeController();
        int value = _volume.Set(percent);
        if (value < 0) return;

        // The Home tab slider shows its own level; a card here would collapse the open shell.
        IslandSpikeLog.Write($"volume: set {value}%");
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
