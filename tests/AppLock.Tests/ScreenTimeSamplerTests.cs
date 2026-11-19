using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.Services.ScreenTime;
using Xunit;

namespace AppLock.Tests;

public class ScreenTimeSamplerTests
{
    // ── Slice splitting ───────────────────────────────────────────────────────

    [Fact]
    public void Split_CrossesHourBoundary()
    {
        var parts = UsageSlicer.Split(new DateTime(2026, 11, 19, 10, 59, 30), 90);

        Assert.Equal(2, parts.Count);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 59, 30), parts[0].BucketStart);
        Assert.Equal(30, parts[0].Seconds);
        Assert.Equal(new DateTime(2026, 11, 19, 11, 0, 0), parts[1].BucketStart);
        Assert.Equal(60, parts[1].Seconds);
    }

    [Fact]
    public void Split_CrossesMidnight()
    {
        var parts = UsageSlicer.Split(new DateTime(2026, 11, 19, 23, 59, 30), 60);

        Assert.Equal(2, parts.Count);
        Assert.Equal(30, parts[0].Seconds);
        Assert.Equal(new DateTime(2026, 11, 19, 23, 59, 30), parts[0].BucketStart);
        Assert.Equal(30, parts[1].Seconds);
        Assert.Equal(new DateTime(2026, 11, 20, 0, 0, 0), parts[1].BucketStart);
    }

    [Fact]
    public void Split_MergesRepeatedDstHourIntoTheSameBucket()
    {
        // Wall-clock bucketing: the repeated 01:xx hour of a fall-back day lands in one row.
        var parts = UsageSlicer.Split(new DateTime(2026, 11, 1, 1, 30, 0), 3600);

        Assert.Equal(2, parts.Count);
        Assert.Equal(1800, parts[0].Seconds);
        Assert.Equal(1, parts[0].BucketStart.Hour);
        Assert.Equal(1800, parts[1].Seconds);
        Assert.Equal(2, parts[1].BucketStart.Hour);
    }

    [Fact]
    public void Split_IgnoresNonPositiveSeconds()
        => Assert.Empty(UsageSlicer.Split(DateTime.Now, 0));

    // ── Slice tracker ─────────────────────────────────────────────────────────

    [Fact]
    public void Tracker_CountsActiveSecondsForTheSameApp()
    {
        var tracker = new UsageSliceTracker();
        var start = new DateTime(2026, 11, 19, 14, 0, 0);

        tracker.Observe("exe:a", true, start, 0);
        tracker.Observe("exe:a", true, start.AddSeconds(1), 1);
        var second = tracker.Observe("exe:a", true, start.AddSeconds(2), 2);
        var third = tracker.Observe("exe:a", true, start.AddSeconds(3), 3);

        Assert.Equal(2, second.Concat(third).Sum(u => u.Seconds));
        Assert.All(second.Concat(third), u => Assert.Equal("exe:a", u.AppKey));
    }

    [Fact]
    public void Tracker_PausesWhileIdleAndResumes()
    {
        var tracker = new UsageSliceTracker();
        var start = new DateTime(2026, 11, 19, 14, 0, 0);

        tracker.Observe("exe:a", true, start, 0);
        var active = tracker.Observe("exe:a", true, start.AddSeconds(1), 1);
        Assert.Equal(1, active.Sum(u => u.Seconds));

        // 60 s idle: nothing may be credited while inactive.
        for (int i = 2; i <= 62; i++)
            Assert.Empty(tracker.Observe("exe:a", false, start.AddSeconds(i), i));

        // First active observation after the pause only re-opens the slice.
        tracker.Observe("exe:a", true, start.AddSeconds(63), 63);
        var resumed = tracker.Observe("exe:a", true, start.AddSeconds(64), 64);

        Assert.Equal(1, resumed.Sum(u => u.Seconds));
    }

    [Fact]
    public void Tracker_DiscardsSubSecondSwitches()
    {
        var tracker = new UsageSliceTracker();
        var start = new DateTime(2026, 11, 19, 14, 0, 0);

        tracker.Observe("exe:a", true, start, 0);
        Assert.Empty(tracker.Observe("exe:b", true, start.AddMilliseconds(300), 0.3));
        Assert.Empty(tracker.Observe("exe:a", true, start.AddMilliseconds(600), 0.6));
        var afterBlip = tracker.Observe("exe:a", true, start.AddMilliseconds(1100), 1.1);
        var later = tracker.Observe("exe:a", true, start.AddMilliseconds(2100), 2.1);

        // The 0.3 s visit to B never gets a second, and nothing is credited twice.
        Assert.DoesNotContain(afterBlip.Concat(later), u => u.AppKey == "exe:b");
        Assert.Equal(1, afterBlip.Concat(later).Sum(u => u.Seconds));
    }

    [Fact]
    public void Tracker_ClosesTheOldSliceWhenTheAppChanges()
    {
        var tracker = new UsageSliceTracker();
        var start = new DateTime(2026, 11, 19, 14, 0, 0);

        tracker.Observe("exe:a", true, start, 0);
        tracker.Observe("exe:a", true, start.AddSeconds(1), 1);

        var switched = tracker.Observe("exe:b", true, start.AddSeconds(2), 2);

        Assert.Single(switched);
        Assert.Equal("exe:a", switched[0].AppKey);
        Assert.Equal(1, switched[0].Seconds);
    }

    [Fact]
    public void Tracker_ReportsOneOpenPerVisit()
    {
        var tracker = new UsageSliceTracker();
        var start = new DateTime(2026, 11, 19, 14, 0, 0);

        tracker.Observe("exe:a", true, start, 0);
        var first = tracker.Observe("exe:a", true, start.AddSeconds(1), 1);
        var second = tracker.Observe("exe:a", true, start.AddSeconds(2), 2);

        Assert.Equal(1, first.Single().Opens);
        Assert.Equal(0, second.Single().Opens);
    }

    [Fact]
    public void Tracker_SplitsLongSlicesAtHourBoundary()
    {
        var tracker = new UsageSliceTracker();
        var start = new DateTime(2026, 11, 19, 10, 59, 58);

        tracker.Observe("exe:a", true, start, 0);
        var parts = tracker.Observe("exe:a", true, start.AddSeconds(4), 4);

        // A 4 s slice is split: 2 s before 11:00, 2 s after.
        Assert.Equal(2, parts.Count);
        Assert.Equal(10, parts[0].BucketStart.Hour);
        Assert.Equal(11, parts[1].BucketStart.Hour);
        Assert.Equal(4, parts.Sum(p => p.Seconds));
    }

    // ── Activity gate ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(600, false)]
    public void Gate_UsesTheSharedIdleThreshold(double idleSeconds, bool expected)
    {
        Assert.Equal(expected, ActivityGate.IsActive(idleSeconds, false, true, true, 60));
    }

    [Fact]
    public void Gate_MediaKeepsTheAppActiveOnlyWhenEnabled()
    {
        Assert.True(ActivityGate.IsActive(300, true, true, true, 60));
        Assert.False(ActivityGate.IsActive(300, true, false, true, 60));
        Assert.False(ActivityGate.IsActive(300, false, true, true, 60));
    }

    [Fact]
    public void Gate_LockedOrSuspendedSessionIsNeverActive()
    {
        Assert.False(ActivityGate.IsActive(0, true, true, false, 60));
    }

    // ── Clock guard ───────────────────────────────────────────────────────────

    [Fact]
    public void Clock_GoingBackwardsDoesNotStartANewDay()
    {
        var quotaDay = new DateOnly(2026, 11, 19);
        var yesterday = new DateOnly(2026, 11, 18);

        Assert.True(UsageClockGuard.IsRolledBack(yesterday, quotaDay));
        Assert.False(UsageClockGuard.ShouldStartNewDay(yesterday, quotaDay));
        Assert.True(UsageClockGuard.ShouldStartNewDay(new DateOnly(2026, 11, 20), quotaDay));
    }

    // ── Range maths ───────────────────────────────────────────────────────────

    [Fact]
    public void Range_WeekRunsMondayToSunday()
    {
        var range = new UsageRange(UsageRangeKind.Week, new DateOnly(2026, 11, 19));

        Assert.Equal(new DateOnly(2026, 11, 16), range.From);
        Assert.Equal(new DateOnly(2026, 11, 22), range.To);
        Assert.Equal(new DateOnly(2026, 11, 9), range.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 11, 15), range.PreviousTo);
    }

    [Fact]
    public void Range_MonthStartsOnTheFirst()
    {
        var range = new UsageRange(UsageRangeKind.Month, new DateOnly(2026, 2, 14));

        Assert.Equal(new DateOnly(2026, 2, 1), range.From);
        Assert.Equal(new DateOnly(2026, 2, 28), range.To);
        Assert.Equal(new DateOnly(2026, 1, 1), range.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 1, 31), range.PreviousTo);
    }

    [Fact]
    public void Range_NextIsDisabledAtTheCurrentPeriod()
    {
        var today = new DateOnly(2026, 11, 19);
        Assert.False(new UsageRange(UsageRangeKind.Today, today).CanGoNext(today));
        Assert.True(new UsageRange(UsageRangeKind.Today, today.AddDays(-1)).CanGoNext(today));
    }

    // ── Limit policy ──────────────────────────────────────────────────────────

    [Fact]
    public void Limit_FirstLimitNeedsNoAuth()
        => Assert.False(LimitPolicy.RequiresAuth(null, 60));

    [Theory]
    [InlineData(30, 60, true)]
    [InlineData(60, 30, false)]
    [InlineData(60, 60, false)]
    [InlineData(60, null, true)]
    public void Limit_RaisingOrRemovingNeedsAuth(int current, int? next, bool expected)
        => Assert.Equal(expected, LimitPolicy.RequiresAuth(current, next));

    [Fact]
    public void RemovingTheLastLimitDeletesALimitOnlyRule()
    {
        var rule = new LockRule { Mode = LockRuleMode.LimitOnly };
        rule.SelfDiscipline.QuotaEnabled = true;

        Assert.True(LimitPolicy.ShouldDeleteRuleOnRemoveLimit(rule));

        rule.SelfDiscipline.IdleRelockEnabled = true;
        Assert.False(LimitPolicy.ShouldDeleteRuleOnRemoveLimit(rule));
    }

    [Fact]
    public void RemovingTheLastLimitKeepsAnAlwaysLockedRule()
    {
        var rule = new LockRule { Mode = LockRuleMode.AlwaysLocked };
        Assert.False(LimitPolicy.ShouldDeleteRuleOnRemoveLimit(rule));
    }
}
