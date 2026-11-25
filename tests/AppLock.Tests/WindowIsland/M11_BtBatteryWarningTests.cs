using System.Collections.Generic;
using AppLock.Models;
using AppLock.WindowIsland.Modules.Controls;
using Xunit;

namespace AppLock.Tests.WindowIsland;

public sealed class M11_BtBatteryWarningTests
{
    [Fact]
    public void ParseThresholds_ParsesDeduplicatesAndSortsDescending()
    {
        var parsed = ControlsSettings.ParseThresholds("10, 80, 50, 20, 80, 150, 0, abc");
        Assert.Equal(new List<int> { 80, 50, 20, 10 }, parsed);
    }

    [Fact]
    public void CheckThreshold_FiresOncePerThresholdAndResetsWhenRecharged()
    {
        var tracker = new BtBatteryWarningTracker();
        var thresholds = new List<int> { 80, 50, 20, 10 };

        // 85% -> no warning
        Assert.Null(tracker.CheckThreshold("buds", 85, thresholds));

        // Drops to 79% (< 80) -> warns 80 once
        Assert.Equal(80, tracker.CheckThreshold("buds", 79, thresholds));
        Assert.Null(tracker.CheckThreshold("buds", 75, thresholds));

        // Drops to 49% (< 50) -> warns 50 once
        Assert.Equal(50, tracker.CheckThreshold("buds", 49, thresholds));

        // Drops to 19% (< 20) -> warns 20 once
        Assert.Equal(20, tracker.CheckThreshold("buds", 19, thresholds));

        // Drops to 9% (< 10) -> warns 10 once
        Assert.Equal(10, tracker.CheckThreshold("buds", 9, thresholds));
        Assert.Null(tracker.CheckThreshold("buds", 8, thresholds));

        // Recharged to 86% (>= 80 + 5) -> resets, so dropping to 79% warns again
        Assert.Null(tracker.CheckThreshold("buds", 86, thresholds));
        Assert.Equal(80, tracker.CheckThreshold("buds", 79, thresholds));
    }

    [Fact]
    public void CheckThreshold_InitialLowConnection_FiresLowestCrossedWithoutSpamming()
    {
        var tracker = new BtBatteryWarningTracker();
        var thresholds = new List<int> { 80, 50, 20, 10 };

        // Connects already at 15% -> fires 20 (lowest crossed), marks 80 & 50 & 20 seen
        Assert.Equal(20, tracker.CheckThreshold("mouse", 15, thresholds));
        Assert.Null(tracker.CheckThreshold("mouse", 14, thresholds));

        // Dropping below 10 still warns 10
        Assert.Equal(10, tracker.CheckThreshold("mouse", 9, thresholds));
    }
}
