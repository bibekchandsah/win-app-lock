using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;

namespace AppLock.Services;

/// <summary>
/// Step 5: Scheduled lock evaluation.
/// Checks if current local time falls inside any scheduled TimeBlock for a LockRule.
/// Handles midnight-spanning blocks, multiple days, and merges overlapping intervals.
/// Uses IClock abstraction for unit testing.
/// </summary>
public class ScheduleEvaluator
{
    private readonly IClock _clock;

    public ScheduleEvaluator(IClock clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Checks if the rule has an active scheduled block right now.
    /// </summary>
    public bool IsInsideSchedule(LockRule rule, out DateTime until)
    {
        return IsInsideSchedule(rule, _clock.Now, out until);
    }

    /// <summary>
    /// Pure evaluation function given a specific local DateTime.
    /// </summary>
    public static bool IsInsideSchedule(LockRule rule, DateTime now, out DateTime until)
    {
        until = default;

        if (rule?.SelfDiscipline?.TimeBlocks == null || rule.SelfDiscipline.TimeBlocks.Count == 0)
            return false;

        var intervals = new List<(DateTime Start, DateTime End)>();

        // Check yesterday, today, and tomorrow to cover midnight-spanning blocks
        DateTime today = now.Date;
        DateTime[] daysToCheck = { today.AddDays(-1), today, today.AddDays(1) };

        foreach (var day in daysToCheck)
        {
            // Convert DayOfWeek (Sunday=0..Saturday=6) to Mon=0..Sun=6
            int dayIdx = day.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)day.DayOfWeek - 1;

            foreach (var block in rule.SelfDiscipline.TimeBlocks)
            {
                if (block.Days == null || dayIdx >= block.Days.Length || !block.Days[dayIdx])
                    continue;

                if (block.Start == block.End)
                    continue; // Invalid/zero-length block

                DateTime startDt = day.Add(block.Start.ToTimeSpan());
                DateTime endDt = (block.Start < block.End)
                    ? day.Add(block.End.ToTimeSpan())
                    : day.AddDays(1).Add(block.End.ToTimeSpan()); // Midnight-spanning

                intervals.Add((startDt, endDt));
            }
        }

        if (intervals.Count == 0)
            return false;

        // Sort by start time and merge overlapping or contiguous intervals
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var iv in intervals)
        {
            if (merged.Count == 0)
            {
                merged.Add(iv);
            }
            else
            {
                var last = merged[^1];
                if (iv.Start <= last.End)
                {
                    // Overlapping or contiguous: extend end
                    DateTime newEnd = iv.End > last.End ? iv.End : last.End;
                    merged[^1] = (last.Start, newEnd);
                }
                else
                {
                    merged.Add(iv);
                }
            }
        }

        // Find if now falls within any merged interval
        foreach (var iv in merged)
        {
            if (iv.Start <= now && now < iv.End)
            {
                until = iv.End;
                return true;
            }
        }

        return false;
    }
}

