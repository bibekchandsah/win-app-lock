using System;
using System.Collections.Generic;

namespace AppLock.Services.ScreenTime;

/// <summary>
/// Splits a measured duration into the local hour buckets it actually covers. A slice that starts
/// at 10:59:30 and lasts 90 s becomes 30 s in hour 10 and 60 s in hour 11, so a midnight or hour
/// boundary never loses or duplicates time.
/// </summary>
public static class UsageSlicer
{
    public static IReadOnlyList<UsageSlicePart> Split(DateTime localStart, int seconds)
    {
        var parts = new List<UsageSlicePart>();

        if (seconds <= 0)
            return parts;

        var cursor = DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified);
        int remaining = seconds;

        while (remaining > 0)
        {
            var nextHour = new DateTime(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0)
                .AddHours(1);

            int available = (int)Math.Ceiling((nextHour - cursor).TotalSeconds);
            if (available <= 0) available = 1;

            int take = Math.Min(remaining, available);
            parts.Add(new UsageSlicePart(cursor, take));

            remaining -= take;
            cursor = cursor.AddSeconds(take);
        }

        return parts;
    }
}

/// <summary>One contiguous part of a slice inside a single local hour.</summary>
public sealed record UsageSlicePart(DateTime BucketStart, int Seconds);
