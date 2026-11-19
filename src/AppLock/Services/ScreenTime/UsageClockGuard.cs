using System;

namespace AppLock.Services.ScreenTime;

/// <summary>
/// Section 5.6: moving the clock backwards must not start a fresh quota day. The counter keeps
/// rolling into the day it already belongs to until real time passes midnight.
/// </summary>
public static class UsageClockGuard
{
    public static bool IsRolledBack(DateOnly today, DateOnly lastSeenDay) => today < lastSeenDay;

    public static bool ShouldStartNewDay(DateOnly today, DateOnly quotaDay) => today > quotaDay;
}
