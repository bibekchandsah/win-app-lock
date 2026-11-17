using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AppLock.Models;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Pure logic for the auto-mute schedule: parsing "HH:mm" text, deciding whether a moment falls inside one of
/// the configured windows (including windows that cross midnight) and formatting them for the UI.
/// </summary>
public static class MuteSchedule
{
    public const int MaxWindows = 12;

    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var parsed))
            return false;

        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1)) return false;

        time = parsed;
        return true;
    }

    public static TimeSpan ParseOrDefault(string? text, TimeSpan fallback)
        => TryParse(text, out var time) ? time : fallback;

    public static string Format(TimeSpan time)
        => $"{(int)time.TotalHours:00}:{time.Minutes:00}";

    /// <summary>True when the given local time falls inside any enabled window.</summary>
    public static bool IsActive(DateTime now, IEnumerable<MuteWindow>? windows, out MuteWindow? activeWindow)
    {
        activeWindow = null;
        if (windows == null) return false;

        var time = now.TimeOfDay;

        foreach (var window in windows)
        {
            if (!TryParse(window.Start, out var start) || !TryParse(window.End, out var end)) continue;

            // Identical start and end would mean "all day" by accident; such a range is ignored.
            if (start == end) continue;

            var inside = start < end
                ? time >= start && time < end
                : time >= start || time < end; // crosses midnight

            if (!inside) continue;

            activeWindow = window;
            return true;
        }

        return false;
    }

    /// <summary>What the schedule is doing right now, in one line for tooltips and status labels.</summary>
    public sealed record ScheduleStatus(bool IsActive, string Text);

    /// <summary>
    /// Tells whether a range is active and, when it is not, when the next one starts, so the overlay can say
    /// "Auto muted until 05:00" or "Auto mute starts at 23:00 (tomorrow)" without opening settings.
    /// </summary>
    public static ScheduleStatus GetStatus(DateTime now, bool enabled, IReadOnlyList<MuteWindow>? windows)
    {
        if (!enabled) return new ScheduleStatus(false, "Auto mute schedule is off");
        if (windows == null || windows.Count == 0) return new ScheduleStatus(false, "Auto mute schedule is on, but no ranges are set");

        if (IsActive(now, windows, out var active) && active != null)
        {
            return new ScheduleStatus(true, $"Auto muted until {active.End} ({Describe(active)})");
        }

        DateTime? next = null;
        MuteWindow? nextWindow = null;

        foreach (var window in windows)
        {
            if (!TryParse(window.Start, out var start)) continue;

            var today = now.Date + start;
            var when = today > now ? today : today.AddDays(1);

            if (next == null || when < next)
            {
                next = when;
                nextWindow = window;
            }
        }

        if (nextWindow == null || next == null) return new ScheduleStatus(false, "Auto mute schedule is on, but no valid ranges are set");

        var relative = next.Value.Date == now.Date ? string.Empty : " (tomorrow)";
        return new ScheduleStatus(false, $"Auto mute starts at {nextWindow.Start}{relative} ({Describe(nextWindow)})");
    }

    public static bool CrossesMidnight(MuteWindow window)
        => TryParse(window.Start, out var start) && TryParse(window.End, out var end) && end < start;

    public static string Describe(MuteWindow window)
    {
        var text = $"{window.Start} – {window.End}";
        return CrossesMidnight(window) ? text + " (next day)" : text;
    }

    /// <summary>
    /// Removes unusable entries and rewrites the times in a single consistent shape. Returns the list that was
    /// changed, so callers know whether to save.
    /// </summary>
    public static bool Normalize(List<MuteWindow>? windows)
    {
        if (windows == null) return false;

        var changed = false;

        if (windows.Count > MaxWindows)
        {
            windows.RemoveRange(MaxWindows, windows.Count - MaxWindows);
            changed = true;
        }

        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var window = windows[i];
            if (window == null)
            {
                windows.RemoveAt(i);
                changed = true;
                continue;
            }

            var startValid = TryParse(window.Start, out var start);
            var endValid = TryParse(window.End, out var end);

            if (!startValid || !endValid || start == end)
            {
                windows.RemoveAt(i);
                changed = true;
                continue;
            }

            var normalizedStart = Format(start);
            var normalizedEnd = Format(end);

            if (window.Start != normalizedStart)
            {
                window.Start = normalizedStart;
                changed = true;
            }

            if (window.End != normalizedEnd)
            {
                window.End = normalizedEnd;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>"Muted by schedule" plus every configured range, for the settings page.</summary>
    public static string Summary(IReadOnlyList<MuteWindow>? windows)
        => windows == null || windows.Count == 0
            ? "No ranges configured."
            : string.Join(", ", windows.Select(Describe));
}
