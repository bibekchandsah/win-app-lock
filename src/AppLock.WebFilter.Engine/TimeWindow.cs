namespace AppLock.WebFilter.Engine;

/// <summary>
/// A daily window expressed in local time. <see cref="Days"/> is Monday..Sunday (index 0..6) and
/// names the day the window <em>starts</em>, so 22:00-02:00 on Friday also covers 00:00-02:00 on
/// Saturday. Using local time and time-only comparisons keeps daylight-saving changes harmless.
/// </summary>
public sealed record TimeWindow(TimeOnly Start, TimeOnly End, bool[] Days)
{
    public TimeWindow() : this(new TimeOnly(0, 0), new TimeOnly(0, 0), new bool[7])
    {
    }

    public bool IsActiveAt(DateTime localNow)
    {
        if (Start == End) return false;
        if (Days is not { Length: 7 }) return false;

        var time = TimeOnly.FromDateTime(localNow);
        int today = ToMondayIndex(localNow.DayOfWeek);

        if (Start < End)
        {
            return Days[today] && time >= Start && time < End;
        }

        // The window crosses midnight: it covers the start day's evening and the next morning.
        if (Days[today] && time >= Start) return true;

        int yesterday = (today + 6) % 7;
        return Days[yesterday] && time < End;
    }

    public static int ToMondayIndex(DayOfWeek day)
        => day == DayOfWeek.Sunday ? 6 : (int)day - 1;
}

public static class ScheduleWindows
{
    /// <summary>No windows means the rule is active all the time.</summary>
    public static bool IsActive(IReadOnlyList<TimeWindow>? windows, DateTime localNow)
    {
        if (windows == null || windows.Count == 0) return true;

        for (int i = 0; i < windows.Count; i++)
        {
            if (windows[i].IsActiveAt(localNow)) return true;
        }

        return false;
    }
}
