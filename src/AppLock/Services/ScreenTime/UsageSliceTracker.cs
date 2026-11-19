using System;
using System.Collections.Generic;
using AppLock.Models;

namespace AppLock.Services.ScreenTime;

/// <summary>
/// Turns a once-per-second observation of (foreground app, active) into whole-second usage
/// deltas. Kept free of Win32 so slice splitting, idle pauses and sub-second Alt-Tab noise can be
/// unit tested with a fake monotonic clock.
/// </summary>
public sealed class UsageSliceTracker
{
    private const double MinimumSliceSeconds = 0.75; // sub-second Alt-Tab blips never count
    private const int MaximumCreditedSeconds = 5; // a stalled tick must not credit minutes

    private double _lastMonoSeconds = -1;
    private DateTime _lastLocal;
    private string? _currentKey;
    private bool _currentWasActive;
    private bool _openPending;

    /// <summary>App currently attributed with the elapsed time (null while excluded/idle on desktop).</summary>
    public string? CurrentKey => _currentKey;

    public IReadOnlyList<TrackedUsage> Observe(string? appKey, bool isActive, DateTime localNow, double monoSeconds)
    {
        var results = new List<TrackedUsage>();

        if (_lastMonoSeconds < 0)
        {
            _lastMonoSeconds = monoSeconds;
            _lastLocal = localNow;
            _currentKey = appKey;
            _currentWasActive = isActive;
            _openPending = appKey != null && isActive;
            return results;
        }

        double elapsed = monoSeconds - _lastMonoSeconds;
        _lastMonoSeconds = monoSeconds;

        int seconds = elapsed < MinimumSliceSeconds
            ? 0
            : Math.Min(MaximumCreditedSeconds, (int)Math.Round(elapsed));

        bool appChanged = !string.Equals(appKey, _currentKey, StringComparison.Ordinal);

        if (appChanged)
        {
            // The elapsed second was spent in the previous app, not the new one.
            if (_currentWasActive && _currentKey != null && seconds > 0)
                Emit(results, _currentKey, seconds, _openPending);

            _currentKey = appKey;
            _currentWasActive = isActive;
            _openPending = appKey != null && isActive;
            _lastLocal = localNow;
            return results;
        }

        if (isActive && appKey != null && _currentWasActive && seconds > 0)
            Emit(results, appKey, seconds, _openPending);

        _currentWasActive = isActive;
        if (!isActive || appKey == null)
        {
            _openPending = false;
        }
        else if (seconds > 0)
        {
            _openPending = false;
        }

        _lastLocal = localNow;
        return results;
    }

    private void Emit(List<TrackedUsage> results, string appKey, int seconds, bool opens)
    {
        var parts = UsageSlicer.Split(_lastLocal, seconds);
        for (int i = 0; i < parts.Count; i++)
        {
            results.Add(new TrackedUsage(
                appKey,
                parts[i].BucketStart,
                parts[i].Seconds,
                i == 0 && opens ? 1 : 0));
        }
    }
}

/// <summary>One app's counted seconds for one local hour bucket.</summary>
public sealed record TrackedUsage(string AppKey, DateTime BucketStart, int Seconds, int Opens);
