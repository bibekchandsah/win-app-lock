using System;
using System.Collections.Generic;

namespace AppLock.Models;

/// <summary>One aggregated piece of foreground time, queued for the single SQLite writer.</summary>
public sealed record UsageDelta(
    string AppKey,
    string DisplayName,
    DateTime LocalTimestamp,
    int Seconds,
    int Opens = 0,
    string? ExePath = null,
    string? Aumid = null,
    byte[]? IconPng = null);

/// <summary>Per-app total for a queried range. Seconds is the sum of the hourly rows.</summary>
public sealed record AppUsageRow(string AppKey, string DisplayName, long Seconds, int Opens)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Seconds);
}

/// <summary>Total foreground seconds in one local hour of one day.</summary>
public sealed record HourUsage(int Hour, int Seconds);

/// <summary>Total foreground seconds for one local day.</summary>
public sealed record DayUsage(DateOnly Day, int Seconds);

/// <summary>How much tracking actually ran in an hour, so gaps can be told apart from idle time.</summary>
public sealed record CoverageHour(int Hour, int TrackedSeconds);

/// <summary>A single bar in the usage chart.</summary>
public sealed record BarSeriesPoint(string Label, double Value, string? Tooltip = null);

/// <summary>A colored part of a stacked chart bar (one app, or "Other").</summary>
public sealed record BarSegment(string AppKey, string Label, int Seconds, int ColorIndex, string Tooltip);

/// <summary>One bar of the usage chart: hourly (Today) or daily (Week/Month).</summary>
public sealed record UsageBar(
    string Label,
    string Tooltip,
    double Value,
    IReadOnlyList<BarSegment> Segments,
    bool Incomplete = false,
    bool Future = false,
    DateOnly? Day = null);

/// <summary>An app row as persisted in the history database.</summary>
public sealed record StoredApp(string AppKey, string DisplayName, string? ExePath, string? Aumid);
