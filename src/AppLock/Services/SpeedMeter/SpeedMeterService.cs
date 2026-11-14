using System;
using System.Linq;
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
    private readonly SystemStatsProvider _systemStats = new();
    private readonly ConnectivityMonitor _connectivity;
    private readonly ConnectionToastService _toasts;

    private DispatcherQueueTimer? _timer;
    private SpeedWidgetWindow? _widget;
    private NetworkFlyoutWindow? _flyout;
    private QualityMonitor? _quality;
    private int _sampling;

    private long _sessionDown;
    private long _sessionUp;

    public SpeedMeterService(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
        _connectivity = new ConnectivityMonitor(dispatcherQueue);
        _toasts = new ConnectionToastService(dispatcherQueue);
    }

    /// <summary>Connection alerts, also used by the page's preview buttons.</summary>
    public ConnectionToastService ToastService => _toasts;

    /// <summary>Outage history for the details flyout.</summary>
    public ConnectivityMonitor Connectivity => _connectivity;

    public bool IsRunning { get; private set; }

    /// <summary>Paused from the details flyout; sampling and pings stop until resumed.</summary>
    public bool IsPaused { get; private set; }

    private int _hotkeyId = -1;

    /// <summary>Raised on the UI thread once per sample.</summary>
    public event EventHandler<NetworkSpeedSample>? SampleUpdated;

    /// <summary>Raised on the UI thread once per sample with the optional CPU/RAM rows.</summary>
    public event EventHandler<SystemStats>? StatsUpdated;

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
        _connectivity.ApplySettings();
        _connectivity.ConnectionChanged -= OnConnectionChanged;
        _connectivity.ConnectionChanged += OnConnectionChanged;
        _connectivity.Start();

        SampleOnce();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _timer?.Stop();
        _timer = null;

        _connectivity.ConnectionChanged -= OnConnectionChanged;
        _connectivity.Stop();
        _toasts.CloseAll();
        CloseFlyout();

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

        // Only the enabled metrics are read, so disabled rows cost nothing.
        _systemStats.TrackCpu = settings.ShowCpu;
        _systemStats.TrackRam = settings.ShowRam;
        _systemStats.TrackDisk = settings.ShowDisk;
        _systemStats.TrackGpu = settings.ShowGpu;
        _systemStats.TrackBattery = settings.ShowBattery;

        _connectivity.ApplySettings();

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

    /// <summary>Shows or hides the details flyout. Opening it starts the quality pings.</summary>
    public void ToggleFlyout()
    {
        if (_flyout is not null)
        {
            CloseFlyout();
            return;
        }

        try
        {
            var flyout = new NetworkFlyoutWindow();
            _flyout = flyout;

            flyout.Closed += (_, _) =>
            {
                if (_flyout == flyout) _flyout = null;
                StopQuality();
            };

            flyout.PauseToggled += (_, paused) =>
            {
                IsPaused = paused;
                flyout.SetPaused(paused);
            };

            flyout.Activate();
            flyout.RefreshDetails();
            flyout.SetPaused(IsPaused);
            flyout.SetSessionTotals(_sessionDown, _sessionUp);

            var bounds = _widget?.GetBounds();
            if (bounds is { } value) flyout.PlaceNear(value.Position, value.Size);

            StartQuality();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Flyout error: {ex}");
            _flyout = null;
        }
    }

    public void CloseFlyout()
    {
        try
        {
            _flyout?.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Flyout close error: {ex.Message}");
        }

        _flyout = null;
        StopQuality();
    }

    private void StartQuality()
    {
        _quality ??= new QualityMonitor(_dispatcher);
        _quality.Updated -= OnQualityUpdated;
        _quality.Updated += OnQualityUpdated;
        _quality.Start();
    }

    private void StopQuality()
    {
        if (_quality is null) return;

        _quality.Updated -= OnQualityUpdated;
        _quality.Stop();
    }

    private void OnQualityUpdated(object? sender, QualityStats stats) => _flyout?.SetQuality(stats);

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

    /// <summary>Connection alerts: debounced by the monitor, formatted here.</summary>
    private void OnConnectionChanged(object? sender, bool online)
    {
        var alerts = ServiceContainer.Settings.SpeedMeter.Alerts;
        var adapter = _connectivity.CurrentProfileName;

        if (online)
        {
            var outage = _connectivity.Outages.FirstOrDefault();
            var duration = outage?.Duration ?? TimeSpan.Zero;

            if (duration.TotalSeconds < alerts.MinOutageToAnnounceSeconds) return;

            var parts = new System.Collections.Generic.List<string>();
            if (alerts.ShowOutageDuration) parts.Add($"Offline for {FormatDuration(duration)}");
            if (alerts.ShowAdapterName && !string.IsNullOrEmpty(adapter)) parts.Add(adapter);

            _toasts.Show(false, parts.Count > 0 ? string.Join(" · ", parts) : "The internet connection is back");
        }
        else
        {
            _toasts.Show(true, alerts.ShowAdapterName && !string.IsNullOrEmpty(adapter)
                ? adapter
                : "The internet connection dropped");
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} h {duration.Minutes} min";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes} min {duration.Seconds} s";
        return $"{(int)duration.TotalSeconds} s";
    }

    private void SampleOnce()
    {
        // One read at a time; the provider keeps per-adapter baselines between ticks.
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;

        if (IsPaused)
        {
            Volatile.Write(ref _sampling, 0);
            return;
        }

        var intervalSeconds = Math.Clamp(ServiceContainer.Settings.SpeedMeter.IntervalSeconds, 0.5, 2.0);

        _ = Task.Run(() =>
        {
            try
            {
                var sample = _provider.Sample();
                var stats = _systemStats.TrackCpu || _systemStats.TrackRam || _systemStats.TrackDisk
                            || _systemStats.TrackGpu || _systemStats.TrackBattery
                    ? _systemStats.Sample()
                    : SystemStats.Empty;

                _dispatcher.TryEnqueue(() =>
                {
                    _sessionDown += (long)(sample.BytesDownPerSec * intervalSeconds);
                    _sessionUp += (long)(sample.BytesUpPerSec * intervalSeconds);

                    _widget?.Update(sample);
                    _widget?.SetStats(stats);

                    _flyout?.Update(sample);
                    _flyout?.SetSessionTotals(_sessionDown, _sessionUp);
                    StatsUpdated?.Invoke(this, stats);

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
