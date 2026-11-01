using System;
using AppLock.Models;
using Microsoft.UI.Dispatching;

namespace AppLock.Services;

/// <summary>
/// Single 1-second timer that coordinates self-discipline enforcement per LockRule:
///   - Step 3: Idle relock
///   - Step 4: Quota tracking (added in Step 4)
///   - Step 5: Schedule evaluation (added in Step 5)
/// </summary>
public class SelfDisciplineService : IDisposable
{
    private readonly LockManager _lockManager;
    private readonly OverlayManager _overlayManager;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly DispatcherQueue _dispatcherQueue;

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

        // When a rule is unlocked via the overlay, reset its idle-relock flag
        _overlayManager.RuleUnlocked += (_, rule) => rule.IdleRelockPending = false;
    }

    public void Start()
    {
        _tickTimer?.Dispose();
        // 1-second tick, starting after 1 second
        _tickTimer = new System.Threading.Timer(OnTick, null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;
    }

    private void OnTick(object? state)
    {
        if (_disposed) return;
        if (!_lockManager.IsProtectionEnabled) return;

        try
        {
            double idleMinutes = IdleService.GetIdleTimeMinutes();

            foreach (var rule in _lockManager.GetAllRules())
            {
                var sd = rule.SelfDiscipline;

                // --- Step 3: Idle Relock ---
                EvaluateIdleRelock(rule, sd, idleMinutes);

                // Steps 4 & 5 will be inserted here later
            }
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

        // Only relock if the app is currently unlocked (i.e. has an active unlock session).
        // We check this by asking OverlayManager to ForceLock — it will only show the
        // overlay if the window is in the unlocked timer or indefinitely-unlocked set.
        // To avoid noisy repeated calls, we track a per-rule "relocked due to idle" flag.
        if (rule.IdleRelockPending) return;

        rule.IdleRelockPending = true;

        _dispatcherQueue.TryEnqueue(() =>
        {
            _overlayManager.ForceLock(rule, LockReason.Idle);
        });
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}

