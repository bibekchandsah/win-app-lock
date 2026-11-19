using System;
using AppLock.Models;

namespace AppLock.Services.ScreenTime;

/// <summary>
/// Anti-bypass rules (spec 8.2/8.3) kept pure so they can be tested without dialogs.
/// </summary>
public static class LimitPolicy
{
    /// <summary>
    /// Removing a limit or raising it requires admin verification. Setting a first limit or
    /// lowering an existing one never does.
    /// </summary>
    public static bool RequiresAuth(int? currentMinutes, int? newMinutes)
    {
        if (!currentMinutes.HasValue) return false;
        if (!newMinutes.HasValue) return true;
        return newMinutes.Value > currentMinutes.Value;
    }

    /// <summary>Removing the last limit deletes a Limit-only rule entirely.</summary>
    public static bool ShouldDeleteRuleOnRemoveLimit(LockRule rule)
    {
        if (rule == null) return false;
        return rule.IsLimitOnly
            && !rule.SelfDiscipline.IdleRelockEnabled
            && (rule.SelfDiscipline.TimeBlocks == null || rule.SelfDiscipline.TimeBlocks.Count == 0);
    }

    /// <summary>Weakening is blocked while the app is inside a scheduled lock period.</summary>
    public static bool IsBlockedDuringSchedule(LockRule rule, DateTime now)
        => ScheduleEvaluator.IsInsideSchedule(rule, now, out _);
}
