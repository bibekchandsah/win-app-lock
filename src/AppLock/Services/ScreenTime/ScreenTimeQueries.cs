using System;
using System.Globalization;

namespace AppLock.Services.ScreenTime;

public enum UsageRangeKind
{
    Today,
    Week,
    Month
}

/// <summary>
/// A selected range plus the immediately preceding range of the same length, used for the
/// "vs yesterday/last week" delta line.
/// </summary>
public readonly record struct UsageRange(UsageRangeKind Kind, DateOnly Anchor)
{
    public DateOnly From => Kind switch
    {
        UsageRangeKind.Today => Anchor,
        UsageRangeKind.Week => StartOfWeek(Anchor),
        _ => new DateOnly(Anchor.Year, Anchor.Month, 1)
    };

    public DateOnly To => Kind switch
    {
        UsageRangeKind.Today => Anchor,
        UsageRangeKind.Week => From.AddDays(6),
        _ => From.AddMonths(1).AddDays(-1)
    };

    public DateOnly PreviousFrom => Kind switch
    {
        UsageRangeKind.Today => Anchor.AddDays(-1),
        UsageRangeKind.Week => From.AddDays(-7),
        _ => From.AddMonths(-1)
    };

    public DateOnly PreviousTo => From.AddDays(-1);

    public int DayCount => To.DayNumber - From.DayNumber + 1;

    public bool CanGoNext(DateOnly today) => To < today;

    public UsageRange Shift(int direction)
    {
        if (direction == 0) return this;

        return Kind switch
        {
            UsageRangeKind.Today => this with { Anchor = Anchor.AddDays(direction) },
            UsageRangeKind.Week => this with { Anchor = Anchor.AddDays(7 * direction) },
            _ => this with { Anchor = Anchor.AddMonths(direction) }
        };
    }

    public string Title => Kind switch
    {
        UsageRangeKind.Today => Anchor.ToString("ddd, d MMM yyyy", CultureInfo.CurrentCulture),
        UsageRangeKind.Week => $"{From:d MMM} – {To:d MMM yyyy}",
        _ => From.ToString("MMMM yyyy", CultureInfo.CurrentCulture)
    };

    public string DeltaSuffix => Kind switch
    {
        UsageRangeKind.Today => "vs yesterday",
        UsageRangeKind.Week => "vs last week",
        _ => "vs last month"
    };

    private static DateOnly StartOfWeek(DateOnly day)
    {
        // Monday first, matching the rest of the app's schedule UI.
        int offset = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-offset);
    }
}
