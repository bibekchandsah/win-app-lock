using System;
using System.Threading;
using AppLock.Models;

namespace AppLock.Services.RingLight;

/// <summary>
/// Manages timers for Ring Light, including auto turn-off after N minutes
/// and time-of-day color temperature shifts (auto warmth).
/// </summary>
public sealed class RingLightTimers : IDisposable
{
    private readonly object _lock = new();
    private Timer? _autoOffTimer;
    private Action? _autoOffCallback;
    private int _currentAutoOffMinutes;

    private Timer? _autoWarmthTimer;
    private Action<int>? _autoWarmthCallback;
    private AutoRingSettings? _autoWarmthSettings;
    private int _lastReportedKelvin = -1;

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

    // ── Auto Warmth ───────────────────────────────────────────────────────────

    /// <summary>
    /// Starts monitoring time-of-day for auto warmth shifts, evaluating every minute.
    /// </summary>
    public void StartAutoWarmth(AutoRingSettings settings, Action<int> onKelvinChanged)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _autoWarmthSettings = settings;
            _autoWarmthCallback = onKelvinChanged;
            _lastReportedKelvin = -1;

            if (settings.AutoWarmth)
            {
                // Immediately evaluate once
                EvaluateAutoWarmth();

                _autoWarmthTimer?.Dispose();
                // Check every minute (60,000 ms)
                _autoWarmthTimer = new Timer(_ => EvaluateAutoWarmth(), null, 60000, 60000);
            }
            else
            {
                StopAutoWarmth();
            }
        }
    }

    /// <summary>
    /// Stops the auto warmth timer.
    /// </summary>
    public void StopAutoWarmth()
    {
        lock (_lock)
        {
            _autoWarmthTimer?.Dispose();
            _autoWarmthTimer = null;
            _autoWarmthCallback = null;
            _autoWarmthSettings = null;
            _lastReportedKelvin = -1;
        }
    }

    /// <summary>
    /// Updates settings for active auto warmth.
    /// </summary>
    public void UpdateAutoWarmth(AutoRingSettings settings, Action<int> onKelvinChanged)
    {
        lock (_lock)
        {
            if (settings.AutoWarmth)
                StartAutoWarmth(settings, onKelvinChanged);
            else
                StopAutoWarmth();
        }
    }

    /// <summary>
    /// Calculates the target color temperature in Kelvin for the current time.
    /// Interpolates smoothly over a 15-minute window around day/night transitions.
    /// </summary>
    public static int CalculateAutoWarmthKelvin(AutoRingSettings auto, DateTime now)
    {
        if (!auto.AutoWarmth) return auto.DayKelvin;

        if (!TimeSpan.TryParse(auto.NightStart, out var nightStart))
            nightStart = new TimeSpan(20, 0, 0);

        if (!TimeSpan.TryParse(auto.NightEnd, out var nightEnd))
            nightEnd = new TimeSpan(7, 0, 0);

        var time = now.TimeOfDay;

        bool isNight;
        if (nightStart < nightEnd)
            isNight = time >= nightStart && time < nightEnd;
        else
            isNight = time >= nightStart || time < nightEnd;

        // 15-minute transition window around boundaries
        const double transitionWindow = 15.0;

        // Near NightStart (Day -> Night transition)
        double diffNightStart = GetTimeDifferenceMinutes(time, nightStart);
        if (Math.Abs(diffNightStart) <= transitionWindow)
        {
            double factor = (diffNightStart + transitionWindow) / (2.0 * transitionWindow);
            factor = Math.Clamp(factor, 0.0, 1.0);
            return (int)Math.Round(auto.DayKelvin + factor * (auto.NightKelvin - auto.DayKelvin));
        }

        // Near NightEnd (Night -> Day transition)
        double diffNightEnd = GetTimeDifferenceMinutes(time, nightEnd);
        if (Math.Abs(diffNightEnd) <= transitionWindow)
        {
            double factor = (diffNightEnd + transitionWindow) / (2.0 * transitionWindow);
            factor = Math.Clamp(factor, 0.0, 1.0);
            return (int)Math.Round(auto.NightKelvin + factor * (auto.DayKelvin - auto.NightKelvin));
        }

        return isNight ? auto.NightKelvin : auto.DayKelvin;
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

            _autoWarmthTimer?.Dispose();
            _autoWarmthTimer = null;
            _autoWarmthCallback = null;
            _autoWarmthSettings = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void EvaluateAutoWarmth()
    {
        Action<int>? callback;
        int kelvin;

        lock (_lock)
        {
            if (_autoWarmthSettings == null || !_autoWarmthSettings.AutoWarmth) return;
            callback = _autoWarmthCallback;
            kelvin = CalculateAutoWarmthKelvin(_autoWarmthSettings, DateTime.Now);

            if (kelvin == _lastReportedKelvin) return;
            _lastReportedKelvin = kelvin;
        }

        try
        {
            callback?.Invoke(kelvin);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RingLightTimers] Auto warmth callback error: {ex.Message}");
        }
    }

    private static double GetTimeDifferenceMinutes(TimeSpan current, TimeSpan target)
    {
        double diff = (current - target).TotalMinutes;
        // Handle midnight wrap
        if (diff > 720) diff -= 1440;
        if (diff < -720) diff += 1440;
        return diff;
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
