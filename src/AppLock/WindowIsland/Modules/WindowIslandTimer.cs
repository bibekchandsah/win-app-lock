using System;
using System.Diagnostics;
using System.Threading;

namespace AppLock.WindowIsland.Modules;

public enum IslandTimerState
{
    Idle,
    Running,
    Paused
}

/// <summary>
/// Countdown timer for Window Island (island.md Section 9.5).
/// Presets: 5m, 10m, 15m, 25m, 45m; updates 1 Hz; shows countdown on folded pill;
/// fires Timer finished card when reaching zero.
/// </summary>
public sealed class WindowIslandTimer : IDisposable
{
    private readonly Timer _timer;
    private readonly object _lock = new();

    private TimeSpan _totalDuration = TimeSpan.FromMinutes(5);
    private TimeSpan _remaining = TimeSpan.FromMinutes(5);
    private IslandTimerState _state = IslandTimerState.Idle;

    public event Action<TimeSpan, double>? Tick;
    public event Action? Finished;

    public IslandTimerState State
    {
        get { lock (_lock) return _state; }
    }

    public TimeSpan Remaining
    {
        get { lock (_lock) return _remaining; }
    }

    public TimeSpan TotalDuration
    {
        get { lock (_lock) return _totalDuration; }
    }

    public WindowIslandTimer()
    {
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start(TimeSpan duration)
    {
        lock (_lock)
        {
            _totalDuration = duration;
            _remaining     = duration;
            _state         = IslandTimerState.Running;
        }

        _timer.Change(1000, 1000);
        NotifyTick();
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_state != IslandTimerState.Running) return;
            _state = IslandTimerState.Paused;
        }
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        NotifyTick();
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_state != IslandTimerState.Paused) return;
            _state = IslandTimerState.Running;
        }
        _timer.Change(1000, 1000);
        NotifyTick();
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = IslandTimerState.Idle;
            _remaining = _totalDuration;
        }
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        NotifyTick();
    }

    public void AddMinutes(int minutes)
    {
        lock (_lock)
        {
            _remaining = _remaining.Add(TimeSpan.FromMinutes(minutes));
            if (_totalDuration < _remaining) _totalDuration = _remaining;
            if (_state == IslandTimerState.Idle) _state = IslandTimerState.Running;
        }
        _timer.Change(1000, 1000);
        NotifyTick();
    }

    public string FormatRemaining()
    {
        TimeSpan rem;
        lock (_lock) rem = _remaining;

        if (rem.TotalHours >= 1)
            return $"{(int)rem.TotalHours}:{rem.Minutes:D2}:{rem.Seconds:D2}";
        return $"{rem.Minutes:D2}:{rem.Seconds:D2}";
    }

    private void OnTick(object? state)
    {
        bool isDone = false;
        lock (_lock)
        {
            if (_state != IslandTimerState.Running) return;

            if (_remaining > TimeSpan.FromSeconds(1))
            {
                _remaining = _remaining.Subtract(TimeSpan.FromSeconds(1));
            }
            else
            {
                _remaining = TimeSpan.Zero;
                _state     = IslandTimerState.Idle;
                isDone     = true;
            }
        }

        NotifyTick();

        if (isDone)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Finished?.Invoke();
        }
    }

    private void NotifyTick()
    {
        TimeSpan rem;
        TimeSpan total;
        lock (_lock)
        {
            rem = _remaining;
            total = _totalDuration;
        }

        double progress = total.TotalSeconds > 0
            ? Math.Clamp(1.0 - (rem.TotalSeconds / total.TotalSeconds), 0.0, 1.0)
            : 0.0;

        Tick?.Invoke(rem, progress);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
