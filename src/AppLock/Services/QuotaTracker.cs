using System;
using System.Runtime.InteropServices;
using AppLock.Models;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace AppLock.Services;

/// <summary>
/// Step 4: Quota tracking.
/// Counts foreground seconds for each rule with QuotaEnabled, handles midnight rollover,
/// flushes to disk every 30s, and fires ForceLock when the limit is exceeded.
/// Uses IClock for testability.
/// </summary>
public class QuotaTracker
{
    private readonly LockManager _lockManager;
    private readonly OverlayManager _overlayManager;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;

    private int _flushCountdown = 30; // flush every 30 ticks (seconds)
    private const double IdlePauseSeconds = 60.0; // pause counting if idle >= 60s

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
    /// Called every second from SelfDisciplineService.
    /// Returns true if settings were modified (so caller can trigger flush).
    /// </summary>
    public void OnTick(double idleSeconds, nint foregroundHwnd)
    {
        if (!_lockManager.IsProtectionEnabled) return;

        bool dirty = false;
        var today = _clock.Today;

        foreach (var rule in _lockManager.GetAllRules())
        {
            var sd = rule.SelfDiscipline;
            if (!sd.QuotaEnabled) continue;

            // --- Midnight rollover ---
            if (sd.QuotaDate != today)
            {
                sd.QuotaDate = today;
                sd.UsedSecondsToday = 0;
                sd.ExtraSecondsGrantedToday = 0;
                dirty = true;
            }

            // --- Already over limit? enforce immediately ---
            int limitSec = sd.DailyLimitMinutes * 60 + sd.ExtraSecondsGrantedToday;
            if (sd.UsedSecondsToday >= limitSec)
            {
                _overlayManager.ForceLock(rule, LockReason.Quota);
                continue;
            }

            // --- Count only when: app unlocked + foreground + not idle ---
            if (idleSeconds >= IdlePauseSeconds) continue;

            // Check if foreground HWND matches this rule
            if (!IsForegroundHwndMatchingRule(foregroundHwnd, rule)) continue;

            // Check if rule is currently in an unlocked session
            if (!IsWindowHandleUnlocked(foregroundHwnd)) continue;

            sd.UsedSecondsToday++;
            dirty = true;

            // Notify live dialog listeners
            QuotaUpdated?.Invoke(rule);

            // Re-check limit after incrementing
            if (sd.UsedSecondsToday >= limitSec)
            {
                _overlayManager.ForceLock(rule, LockReason.Quota);
            }
        }

        if (dirty)
        {
            _flushCountdown--;
            if (_flushCountdown <= 0)
            {
                Flush();
                _flushCountdown = 30;
            }
        }
    }

    /// <summary>
    /// Persist quota counters immediately. Call on app exit and dialog save.
    /// </summary>
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

    /// <summary>
    /// Fired on the timer thread whenever a rule's UsedSecondsToday changes.
    /// SelfDisciplineDialog subscribes to update the ring live.
    /// </summary>
    public static event Action<LockRule>? QuotaUpdated;

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private bool IsForegroundHwndMatchingRule(nint hwnd, LockRule rule)
    {
        if (hwnd == 0) return false;
        try
        {
            uint pid = 0;
            unsafe { PInvoke.GetWindowThreadProcessId(new HWND(hwnd), &pid); }
            if (pid == 0) return false;

            var hProc = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc.IsInvalid) return false;

            string processName;
            using (hProc)
            {
                var buf = new char[260];
                uint sz = (uint)buf.Length;
                unsafe
                {
                    fixed (char* p = buf)
                        PInvoke.QueryFullProcessImageName(hProc, 0, new Windows.Win32.Foundation.PWSTR(p), ref sz);
                }
                string path = new string(buf).TrimEnd('\0');
                processName = System.IO.Path.GetFileNameWithoutExtension(path);
            }

            if (string.IsNullOrEmpty(processName)) return false;

            // Simple process-name match (covers the main lock case)
            if (!string.IsNullOrEmpty(rule.ProcessName) &&
                processName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Prefix match for UWP (e.g. "Calculator" vs "CalculatorApp")
            if (!string.IsNullOrEmpty(rule.ProcessName))
            {
                string rn = rule.ProcessName;
                string wn = processName;
                if ((wn.StartsWith(rn, StringComparison.OrdinalIgnoreCase) ||
                     rn.StartsWith(wn, StringComparison.OrdinalIgnoreCase)) &&
                    Math.Min(rn.Length, wn.Length) >= 5)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the given HWND is currently in an unlocked session
    /// by asking OverlayManager's public method.
    /// Fast path: we pass the hwnd directly, no enumeration needed.
    /// </summary>
    private bool IsWindowHandleUnlocked(nint hwnd)
    {
        // Delegate to a lighter HWND-based check exposed on OverlayManager
        return _overlayManager.IsHwndUnlocked(hwnd);
    }
}

