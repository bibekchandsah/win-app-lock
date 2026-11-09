using System;
using System.Threading;

namespace AppLock.Services.RingLight;

/// <summary>
/// Manages timers for Ring Light, including auto turn-off after N minutes
/// and time-of-day color temperature shifts.
/// </summary>
public sealed class RingLightTimers : IDisposable
{
    private readonly object _lock = new();
    private Timer? _autoOffTimer;
    private Action? _autoOffCallback;
    private int _currentAutoOffMinutes;
    private bool _disposed;

    /// <summary>
    /// Starts or resets the auto-off timer.
    /// If <paramref name="minutes"/> is &lt;= 0, any active timer is cancelled.
    /// </summary>
    public void StartAutoOff(int minutes, Action onElapsed)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _autoOffTimer?.Dispose();
            _autoOffTimer = null;
            _autoOffCallback = onElapsed;
            _currentAutoOffMinutes = minutes;

            if (minutes > 0)
            {
                long dueMs = (long)minutes * 60 * 1000;
                _autoOffTimer = new Timer(OnAutoOffElapsed, null, dueMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Stops the auto-off timer.
    /// </summary>
    public void StopAutoOff()
    {
        lock (_lock)
        {
            _autoOffTimer?.Dispose();
            _autoOffTimer = null;
            _autoOffCallback = null;
            _currentAutoOffMinutes = 0;
        }
    }

    /// <summary>
    /// Updates the auto-off duration if it changed while the ring is on.
    /// </summary>
    public void UpdateAutoOff(int newMinutes, Action onElapsed)
    {
        lock (_lock)
        {
            if (_currentAutoOffMinutes != newMinutes)
            {
                StartAutoOff(newMinutes, onElapsed);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _autoOffTimer?.Dispose();
            _autoOffTimer = null;
            _autoOffCallback = null;
        }
    }

    private void OnAutoOffElapsed(object? state)
    {
        Action? callback;
        lock (_lock)
        {
            callback = _autoOffCallback;
            _autoOffTimer?.Dispose();
            _autoOffTimer = null;
            _currentAutoOffMinutes = 0;
        }

        try
        {
            callback?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RingLightTimers] Auto-off error: {ex.Message}");
        }
    }
}
