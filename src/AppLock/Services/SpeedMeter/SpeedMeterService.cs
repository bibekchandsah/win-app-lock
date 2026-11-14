using System;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Views;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.SpeedMeter;

/// <summary>
/// Owns the speed meter lifecycle: the sampling timer, the stats provider and the widget window.
/// Counters are read on a background thread; only the UI update is marshalled back.
/// </summary>
public sealed class SpeedMeterService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly NetworkStatsProvider _provider = new();

    private DispatcherQueueTimer? _timer;
    private SpeedWidgetWindow? _widget;
    private int _sampling;

    public SpeedMeterService(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
    }

    public bool IsRunning { get; private set; }

    private int _hotkeyId = -1;

    /// <summary>Raised on the UI thread once per sample.</summary>
    public event EventHandler<NetworkSpeedSample>? SampleUpdated;

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;

        ApplySettings();
        EnsureWidget();

        var interval = Math.Clamp(ServiceContainer.Settings.SpeedMeter.IntervalSeconds, 0.5, 2.0);

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(interval);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => SampleOnce();
        _timer.Start();

        RegisterHotkey();
        SampleOnce();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _timer?.Stop();
        _timer = null;

        try
        {
            _widget?.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Widget close error: {ex.Message}");
        }
        _widget = null;
    }

    /// <summary>Re-reads appearance/behavior settings without restarting the timer.</summary>
    public void ApplySettings()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;

        _provider.AdapterMode = settings.AdapterMode;
        _provider.AdapterId = settings.AdapterId;
        _provider.IncludeVirtualAdapters = settings.IncludeVirtualAdapters;

        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.IntervalSeconds, 0.5, 2.0));
        }

        _widget?.ApplySettings();
    }

    /// <summary>Shows the widget again if it was closed while the service kept running.</summary>
    public void ShowWidget()
    {
        if (!IsRunning) return;
        EnsureWidget();
    }

    /// <summary>Moves the widget back to its default corner and saves that position.</summary>
    public void ResetWidgetPosition()
    {
        if (_widget is null)
        {
            var settings = ServiceContainer.Settings.SpeedMeter;
            settings.PositionPercentX = 98;
            settings.PositionPercentY = 90;
            settings.MonitorId = null;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            return;
        }

        _widget.ResetPosition();
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    /// <summary>
    /// Registers the show/hide hotkey. It stays registered while the widget is off so the same
    /// shortcut can bring it back.
    /// </summary>
    public void RegisterHotkey()
    {
        UnregisterHotkey();

        var binding = ServiceContainer.Settings.SpeedMeter.ToggleHotkey;
        if (binding is null || binding.VirtualKey == 0) return;

        _hotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(
            (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)binding.Modifiers,
            binding.VirtualKey,
            () => _dispatcher.TryEnqueue(() =>
                ServiceContainer.SetSpeedMeterEnabled(!ServiceContainer.Settings.SpeedMeter.Enabled)));
    }

    public void UnregisterHotkey()
    {
        if (_hotkeyId <= 0) return;

        ServiceContainer.HotkeyService.UnregisterHotkey(_hotkeyId);
        _hotkeyId = -1;
    }

    private void EnsureWidget()
    {
        if (_widget is not null) return;

        try
        {
            _widget = new SpeedWidgetWindow();
            _widget.Closed += (_, _) => _widget = null;
            _widget.Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Widget create error: {ex}");
            _widget = null;
        }
    }

    private void SampleOnce()
    {
        // One read at a time; the provider keeps per-adapter baselines between ticks.
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try
            {
                var sample = _provider.Sample();

                _dispatcher.TryEnqueue(() =>
                {
                    _widget?.Update(sample);

                    // Step aside for full-screen apps and videos, then come back afterwards.
                    var hide = ServiceContainer.Settings.SpeedMeter.HideInFullscreen
                               && FullscreenDetector.IsFullscreenAppActive();
                    _widget?.SetFullscreenHidden(hide);

                    SampleUpdated?.Invoke(this, sample);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Sample error: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        });
    }

    public void Dispose() => Stop();
}
