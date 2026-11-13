using System;
using System.Globalization;
using AppLock.Models;

namespace AppLock.Services.RingLight;

/// <summary>
/// Decides whether camera automation may switch the ring light on right now, based on the optional
/// active-hours window in <see cref="AutoRingSettings"/>. A window such as 20:00 to 07:00 crosses
/// midnight; an empty window, identical start/end, or disabled window means "any time".
/// </summary>
public static class AutoRingSchedule
{
    /// <summary>
    /// Parses a 24-hour time such as "20:00", "8:00" or "8pm". Returns false for incomplete input so
    /// callers can keep the previous saved value while the user is still typing.
    /// </summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();

        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var parsed) && IsWithinDay(parsed))
        {
            time = parsed;
            return true;
        }

        // Accepts the culture's own clock forms, including "8pm" and "5 pm", but not a bare number
        // (DateTime would read "8" as a day of the month).
        if (HasExplicitClockPart(trimmed) &&
            DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
        {
            time = dt.TimeOfDay;
            return true;
        }

        return false;
    }

    private static bool HasExplicitClockPart(string text)
    {
        if (text.IndexOf(':') >= 0) return true;

        foreach (var c in text)
        {
            if (char.IsLetter(c)) return true;
        }
        return false;
    }

    private static bool IsWithinDay(TimeSpan t) => t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);

    /// <summary>True when automation is allowed now (always true when no window is configured).</summary>
    public static bool IsActive(AutoRingSettings auto, DateTime now)
    {
        if (auto == null || !auto.ScheduleEnabled) return true;
        if (!TryParseTime(auto.ScheduleStart, out var start) || !TryParseTime(auto.ScheduleEnd, out var end))
            return true;

        if (start == end) return true; // covers the whole day

        var time = now.TimeOfDay;
        return start < end
            ? time >= start && time < end   // same-day window
            : time >= start || time < end;  // wraps past midnight
    }

    /// <summary>Human readable window for the settings page.</summary>
    public static string Describe(string? startText, string? endText)
    {
        if (!TryParseTime(startText, out var start) || !TryParseTime(endText, out var end))
            return "Use 24-hour times such as 20:00 and 07:00.";

        var from = start.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        var to = end.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        if (start == end) return $"On all day ({from}).";
        return start < end
            ? $"On daily from {from} to {to}."
            : $"On daily from {from} to {to} (the next morning).";
    }
}
