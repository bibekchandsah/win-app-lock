using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AppLock.Models;
using Microsoft.Win32;

namespace AppLock.Services.ScreenTime;

/// <summary>Raised once per counted second so Self-Discipline's quota uses the same delta.</summary>
public sealed class UsageTickedEventArgs : EventArgs
{
    public UsageTickedEventArgs(string appKey, int seconds, nint foregroundHwnd)
    {
        AppKey = appKey;
        Seconds = seconds;
        ForegroundHwnd = foregroundHwnd;
    }

    public string AppKey { get; }
    public int Seconds { get; }
    public nint ForegroundHwnd { get; }
}

/// <summary>
/// One shared foreground sampler (spec 5.1). It owns the only timer that measures foreground
/// time: Self-Discipline's quota and Screen Time's history both consume <see cref="UsageTicked"/>,
/// so the two can never disagree. The event hook is only used to notice switches early; the
/// one-second tick does the counting.
/// </summary>
public sealed class ForegroundUsageSampler : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint GetAncestorRoot = 2;

    private readonly Func<ScreenTimeSettings> _settings;
    private readonly ScreenTimeStore _store;
    private readonly AppIdentityResolver _resolver = new();
    private readonly UsageSliceTracker _tracker = new();
    private readonly ActivityGate _gate;
    private readonly object _tickLock = new();

    private readonly Dictionary<(string Day, int Hour), int> _coverage = new();
    private readonly HashSet<string> _iconsQueued = new(StringComparer.OrdinalIgnoreCase);

    private System.Threading.Timer? _timer;
    private WinEventDelegate? _hookCallback;
    private nint _hook;
    private bool _sessionAvailable = true;
    private bool _suspended;
    private bool _running;
    private bool _disposed;
    private DateTime _lastCoverageFlush = DateTime.UtcNow;

    public ForegroundUsageSampler(Func<ScreenTimeSettings> settings, ScreenTimeStore store)
    {
        _settings = settings;
        _store = store;

        _gate = new ActivityGate(
            () => IdleService.CurrentIdleMinutes * 60.0,
            () => MediaPlaybackMonitor.IsAnyMediaPlaying(),
            () => _sessionAvailable && !_suspended,
            () => CurrentSettings.CountWhileMediaPlaying);
    }

    public event EventHandler<UsageTickedEventArgs>? UsageTicked;

    public string? CurrentAppKey { get; private set; }
    public string? CurrentDisplayName { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsRunning => _running;

    private ScreenTimeSettings CurrentSettings => _settings() ?? new ScreenTimeSettings();

    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenTime] session events unavailable: {ex.Message}");
        }

        try
        {
            _hookCallback = OnWinEvent;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _hookCallback, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenTime] foreground hook failed: {ex.Message}");
        }

        _timer = new System.Threading.Timer(OnTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Debug.WriteLine("[ScreenTime] sampler started");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        _timer?.Dispose();
        _timer = null;

        if (_hook != 0)
        {
            try { UnhookWinEvent(_hook); } catch { }
            _hook = 0;
        }

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }

        FlushCoverage();
        Debug.WriteLine("[ScreenTime] sampler stopped");
    }

    private void OnTimer(object? state)
    {
        if (!_running || _disposed) return;

        lock (_tickLock)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenTime] tick failed: {ex.Message}");
            }
        }
    }

    private void Tick()
    {
        var settings = CurrentSettings;
        var hwnd = GetForegroundWindow();
        var identity = ResolveForeground(hwnd);

        bool active = identity != null && _gate.IsActive(settings.EffectiveIdlePauseSeconds);

        double mono = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var localNow = DateTime.Now;

        var deltas = _tracker.Observe(identity?.Key, active, localNow, mono);
        foreach (var delta in deltas)
        {
            var known = _resolver.GetKnown(delta.AppKey);
            string displayName = known?.DisplayName ?? delta.AppKey;

            if (settings.TrackingEnabled && _store != null)
            {
                byte[]? icon = null;
                if (known?.ExePath != null && _iconsQueued.Add(delta.AppKey))
                    icon = _resolver.GetIconPng(known.ExePath);

                _store.Enqueue(new UsageDelta(
                    delta.AppKey,
                    displayName,
                    delta.BucketStart,
                    delta.Seconds,
                    delta.Opens,
                    known?.ExePath,
                    known?.Aumid,
                    icon));
            }

            UsageTicked?.Invoke(this, new UsageTickedEventArgs(delta.AppKey, delta.Seconds, hwnd));
            Debug.WriteLine($"[ScreenTime] {displayName} +{delta.Seconds}s @ {delta.BucketStart:HH:mm}");
        }

        CurrentAppKey = identity?.Key;
        CurrentDisplayName = identity?.DisplayName;
        IsActive = active;

        if (settings.TrackingEnabled && _sessionAvailable && !_suspended)
            AccumulateCoverage(localNow);

        if (DateTime.UtcNow - _lastCoverageFlush >= TimeSpan.FromSeconds(60))
            FlushCoverage();
    }

    private AppIdentity? ResolveForeground(nint hwnd)
    {
        if (hwnd == 0) return null;

        // Use the root window so child controls resolve to their top-level app.
        nint root = hwnd;
        try
        {
            var ancestor = GetAncestor(hwnd, GetAncestorRoot);
            if (ancestor != 0) root = ancestor;
        }
        catch { }

        var identity = _resolver.Resolve(root);
        if (identity == null) return null;

        if (AppIdentityResolver.IsDefaultExcluded(identity.ProcessName, identity.ExePath, AppIdentityResolver.GetWindowClass(root)))
            return null;

        var settings = CurrentSettings;
        if (settings.ExcludedAppKeys.Any(k => string.Equals(k, identity.Key, StringComparison.OrdinalIgnoreCase)))
            return null;

        return identity;
    }

    private void AccumulateCoverage(DateTime localNow)
    {
        var key = (localNow.ToString("yyyy-MM-dd"), localNow.Hour);
        _coverage[key] = _coverage.TryGetValue(key, out int seconds) ? seconds + 1 : 1;
    }

    private void FlushCoverage()
    {
        if (_coverage.Count == 0) return;

        try
        {
            foreach (var pair in _coverage.ToList())
            {
                if (DateOnly.TryParse(pair.Key.Day, out var day))
                    _store?.RecordCoverage(day, pair.Key.Hour, pair.Value);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenTime] coverage flush failed: {ex.Message}");
        }
        finally
        {
            _coverage.Clear();
            _lastCoverageFlush = DateTime.UtcNow;
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Keep the callback tiny: the tick does all identity work and counting.
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _sessionAvailable = false;
                FlushCoverage();
                break;
            case SessionSwitchReason.SessionUnlock:
                RecordUnlock();
                _sessionAvailable = true;
                break;
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _sessionAvailable = true;
                break;
        }
    }

    /// <summary>Counted so the summary can show how many times the PC was unlocked today.</summary>
    private void RecordUnlock()
    {
        try
        {
            if (CurrentSettings.TrackingEnabled && _store != null)
                _store.RecordUnlock(DateOnly.FromDateTime(DateTime.Now));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenTime] unlock record failed: {ex.Message}");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _suspended = true;
            FlushCoverage();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _suspended = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate callback, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);
}

/// <summary>
/// Optional media-playing exception (spec 5.5). Any failure silently returns false so tracking
/// falls back to plain idle detection.
/// </summary>
internal static class MediaPlaybackMonitor
{
    private static readonly object _lock = new();
    private static DateTime _lastCheck = DateTime.MinValue;
    private static bool _lastResult;

    public static bool IsAnyMediaPlaying()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(5))
                return _lastResult;

            _lastCheck = DateTime.UtcNow;

            try
            {
                var operation = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (!operation.AsTask().Wait(TimeSpan.FromMilliseconds(600)))
                    return _lastResult = false;

                var manager = operation.GetResults();
                _lastResult = manager.GetSessions().Any(session =>
                    session.GetPlaybackInfo().PlaybackStatus ==
                    Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }
            catch
            {
                _lastResult = false;
            }

            return _lastResult;
        }
    }
}
