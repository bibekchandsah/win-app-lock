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
    }

    /// <summary>Shows the widget again if it was closed while the service kept running.</summary>
    public void ShowWidget()
    {
        if (!IsRunning) return;
        EnsureWidget();
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
