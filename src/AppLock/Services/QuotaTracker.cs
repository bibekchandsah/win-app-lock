using System;
using System.IO;
using AppLock.Models;
using AppLock.Services.ScreenTime;

namespace AppLock.Services;

/// <summary>
/// Self-Discipline daily quota. It no longer measures foreground time on its own: the shared
/// <see cref="ForegroundUsageSampler"/> raises one tick per counted second and this class adds the
/// same delta to the HMAC-protected counter, so the ring and the Screen Time chip always agree.
/// The counter in settings stays the source of truth for enforcement; SQLite history is display-only.
/// </summary>
public class QuotaTracker
{
    private readonly LockManager _lockManager;
    private readonly OverlayManager _overlayManager;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;

    private int _flushCountdown = 30; // flush every 30 counted seconds
    private DateOnly _lastClockWarningDay = DateOnly.MinValue;

    public QuotaTracker(
        LockManager lockManager,
        OverlayManager overlayManager,
        SettingsStore settingsStore,
        AppSettings settings,
        IClock clock)
    {
        _lockManager = lockManager;
        _overlayManager = overlayManager;
        _settingsStore = settingsStore;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>
    /// Runs once per second for rollover and enforcement only. Counting happens in
    /// <see cref="OnUsageTicked"/>.
    /// </summary>
    public void OnTick()
    {
        if (!_lockManager.IsProtectionEnabled) return;

        bool dirty = false;
        var today = _clock.Today;

        foreach (var rule in _lockManager.GetAllRules())
        {
            var sd = rule.SelfDiscipline;
            if (!sd.QuotaEnabled) continue;

            if (UsageClockGuard.IsRolledBack(today, sd.QuotaDate))
            {
                WarnClockRolledBack(today, rule);
                continue; // never reset or start a fresh day on a backwards clock
            }

            if (UsageClockGuard.ShouldStartNewDay(today, sd.QuotaDate))
            {
                sd.QuotaDate = today;
                sd.UsedSecondsToday = 0;
                sd.ExtraSecondsGrantedToday = 0;
                dirty = true;
            }

            if (sd.UsedSecondsToday >= LimitSeconds(sd))
                _overlayManager.ForceLock(rule, LockReason.Quota);
        }

        if (dirty) CountFlushTick();
    }

    /// <summary>
    /// Called for each second the shared sampler counted for the foreground app. Adds exactly the
    /// same delta that was written to the history bucket.
    /// </summary>
    public void OnUsageTicked(string appKey, int seconds, nint foregroundHwnd)
    {
        if (!_lockManager.IsProtectionEnabled) return;
        if (seconds <= 0 || string.IsNullOrEmpty(appKey)) return;

        bool dirty = false;
        var today = _clock.Today;

        foreach (var rule in _lockManager.GetAllRules())
        {
            var sd = rule.SelfDiscipline;
            if (!sd.QuotaEnabled) continue;

            bool bound = RuleMatchesApp(rule, appKey);
            if (!bound && !string.IsNullOrEmpty(rule.AppKey)) continue;

            // Existing rules that cannot be bound by path/title keep their original behavior:
            // count only while they are the foreground window with an active unlock session.
            if (!bound && !_overlayManager.IsRuleForegroundAndUnlocked(rule, foregroundHwnd))
                continue;

            if (bound) dirty = true;

            if (UsageClockGuard.IsRolledBack(today, sd.QuotaDate))
                continue;

            if (UsageClockGuard.ShouldStartNewDay(today, sd.QuotaDate))
            {
                sd.QuotaDate = today;
                sd.UsedSecondsToday = 0;
                sd.ExtraSecondsGrantedToday = 0;
                dirty = true;
            }

            int limit = LimitSeconds(sd);
            if (sd.UsedSecondsToday >= limit)
                continue;

            sd.UsedSecondsToday += seconds;
            dirty = true;

            QuotaUpdated?.Invoke(rule);

            if (sd.UsedSecondsToday >= limit)
                _overlayManager.ForceLock(rule, LockReason.Quota);
        }

        if (dirty) CountFlushTick();
    }

    private static int LimitSeconds(SelfDisciplineSettings sd)
        => Math.Max(1, sd.DailyLimitMinutes) * 60 + Math.Max(0, sd.ExtraSecondsGrantedToday);

    /// <summary>
    /// Binds an old rule without an AppKey the first time its process is seen, then matches by
    /// the shared identity afterwards. Returns false when this app is not the rule's app.
    /// </summary>
    private static bool RuleMatchesApp(LockRule rule, string appKey)
    {
        if (!string.IsNullOrEmpty(rule.AppKey))
            return string.Equals(rule.AppKey, appKey, StringComparison.OrdinalIgnoreCase);

        if (appKey.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            string path = appKey.Substring("exe:".Length);

            if (!string.IsNullOrEmpty(rule.ProcessPath) &&
                rule.ProcessPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                rule.AppKey = appKey;
                return true;
            }

            string fileName = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(rule.ProcessName) &&
                rule.ProcessName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                rule.AppKey = appKey;
                return true;
            }
        }
        else if (appKey.StartsWith("proc:", StringComparison.OrdinalIgnoreCase))
        {
            string name = appKey.Substring("proc:".Length);
            if (!string.IsNullOrEmpty(rule.ProcessName) &&
                rule.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                rule.AppKey = appKey;
                return true;
            }
        }

        return false;
    }

    private void WarnClockRolledBack(DateOnly today, LockRule rule)
    {
        if (_lastClockWarningDay == today) return;
        _lastClockWarningDay = today;

        string appName = string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.ProcessName : rule.DisplayName;
        LogService.LogAction("security", $"System clock moved backwards for {appName}; quota day kept at {rule.SelfDiscipline.QuotaDate:yyyy-MM-dd}.");
        ServiceContainer.NotificationService.ShowWarning(
            "System clock changed",
            "The date moved backwards, so today's limit was not reset. AppLock keeps counting the existing day.");
    }

    private void CountFlushTick()
    {
        _flushCountdown--;
        if (_flushCountdown > 0) return;

        Flush();
        _flushCountdown = 30;
    }

    /// <summary>Persist quota counters immediately. Call on app exit and dialog save.</summary>
    public void Flush()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QuotaTracker] Flush error: {ex.Message}");
        }
    }

    /// <summary>Fired whenever a rule's UsedSecondsToday changes so open dialogs update live.</summary>
    public static event Action<LockRule>? QuotaUpdated;

    /// <summary>Triggers the QuotaUpdated event manually (e.g. when extra time is granted).</summary>
    public static void FireQuotaUpdated(LockRule rule)
    {
        QuotaUpdated?.Invoke(rule);
    }
}
