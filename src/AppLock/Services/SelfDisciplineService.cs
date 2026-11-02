using System;
using AppLock.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.Win32;

namespace AppLock.Services;

/// <summary>
/// Single 1-second timer coordinating self-discipline enforcement per LockRule:
///   Step 3: Idle relock
///   Step 4: Quota tracking + flush
///   Step 5: Schedule evaluation + SystemEvents hooks
/// </summary>
public class SelfDisciplineService : IDisposable
{
    private readonly LockManager _lockManager;
    private readonly OverlayManager _overlayManager;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly DispatcherQueue _dispatcherQueue;

    private readonly QuotaTracker _quotaTracker;
    private readonly ScheduleEvaluator _scheduleEvaluator;

    private System.Threading.Timer? _tickTimer;
    private bool _disposed;

    public SelfDisciplineService(
        LockManager lockManager,
        OverlayManager overlayManager,
        SettingsStore settingsStore,
        AppSettings settings,
        DispatcherQueue dispatcherQueue)
    {
        _lockManager = lockManager;
        _overlayManager = overlayManager;
        _settingsStore = settingsStore;
        _settings = settings;
        _dispatcherQueue = dispatcherQueue;

        _quotaTracker = new QuotaTracker(lockManager, overlayManager, settingsStore, settings, SystemClock.Instance);
        _scheduleEvaluator = new ScheduleEvaluator(SystemClock.Instance);

        // When a rule is unlocked via the overlay, reset its idle-relock flag
        _overlayManager.RuleUnlocked += (_, rule) => rule.IdleRelockPending = false;

        try
        {
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SelfDisciplineService] SystemEvents subscription failed: {ex.Message}");
        }
    }

    public void Start()
    {
        _tickTimer?.Dispose();
        _tickTimer = new System.Threading.Timer(OnTick, null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        OnTick(null);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            OnTick(null);
        }
    }

    private void OnTick(object? state)
    {
        if (_disposed) return;
        if (!_lockManager.IsProtectionEnabled) return;

        try
        {
            double idleSeconds = IdleService.GetIdleTimeMinutes() * 60.0;
            nint foregroundHwnd = (nint)PInvoke.GetForegroundWindow().Value;

            foreach (var rule in _lockManager.GetAllRules())
            {
                var sd = rule.SelfDiscipline;

                // --- Step 5: Schedule evaluation ---
                if (_scheduleEvaluator.IsInsideSchedule(rule, out DateTime until))
                {
                    if (rule.EmergencyOverrideUntil.HasValue && DateTime.Now < rule.EmergencyOverrideUntil.Value)
                    {
                        // Active emergency override for this scheduled block: bypass force-relock
                        continue;
                    }

                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        _overlayManager.ForceLock(rule, LockReason.Schedule);
                    });
                    continue; // Skip idle relock check while schedule is active
                }
                else
                {
                    // Reset emergency override once schedule ends
                    rule.EmergencyOverrideUntil = null;
                }

                // --- Step 3: Idle Relock ---
                EvaluateIdleRelock(rule, sd, idleSeconds / 60.0);
            }

            // --- Step 4: Quota tracking ---
            _quotaTracker.OnTick(idleSeconds, foregroundHwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SelfDisciplineService] Tick error: {ex.Message}");
        }
    }

    private void EvaluateIdleRelock(LockRule rule, SelfDisciplineSettings sd, double idleMinutes)
    {
        if (!sd.IdleRelockEnabled) return;
        if (idleMinutes < sd.IdleRelockMinutes) return;
        if (rule.IdleRelockPending) return;

        rule.IdleRelockPending = true;

        _dispatcherQueue.TryEnqueue(() =>
        {
            _overlayManager.ForceLock(rule, LockReason.Idle);
        });
    }

    /// <summary>Flush quota counters to disk immediately (call on exit).</summary>
    public void Flush() => _quotaTracker.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();

        try
        {
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }

        _quotaTracker.Flush(); // persist counters on exit
    }
}
