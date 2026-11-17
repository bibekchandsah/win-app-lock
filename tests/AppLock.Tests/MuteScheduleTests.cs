using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.Services.VolumeMixer;

namespace AppLock.Tests;

public class MuteScheduleTests
{
    private static List<MuteWindow> Windows(params (string Start, string End)[] ranges)
    {
        var list = new List<MuteWindow>();
        foreach (var (start, end) in ranges) list.Add(new MuteWindow { Start = start, End = end });
        return list;
    }

    private static bool IsMuted(int hour, int minute, params (string Start, string End)[] ranges)
        => MuteSchedule.IsActive(new DateTime(2026, 1, 1, hour, minute, 0), Windows(ranges), out _);

    [Theory]
    [InlineData(6, 0, true)]   // 05:00 – 07:00
    [InlineData(7, 0, false)]
    [InlineData(4, 59, false)]
    public void DaytimeRange(int hour, int minute, bool expected)
        => Assert.Equal(expected, IsMuted(hour, minute, ("05:00", "07:00")));

    [Theory]
    [InlineData(23, 30, true)] // 23:00 – 05:00 crosses midnight
    [InlineData(2, 0, true)]
    [InlineData(5, 0, false)]
    [InlineData(12, 0, false)]
    public void OvernightRange(int hour, int minute, bool expected)
        => Assert.Equal(expected, IsMuted(hour, minute, ("23:00", "05:00")));

    [Fact]
    public void MultipleRanges_AreAllHonoured()
    {
        var ranges = new[] { ("05:00", "07:00"), ("23:00", "05:00") };

        Assert.True(IsMuted(6, 0, ranges));
        Assert.True(IsMuted(1, 0, ranges));
        Assert.False(IsMuted(12, 0, ranges));
    }

    [Fact]
    public void EmptyOrEqualRanges_AreIgnored()
    {
        Assert.False(MuteSchedule.IsActive(DateTime.Now, Windows(), out _));
        Assert.False(MuteSchedule.IsActive(new DateTime(2026, 1, 1, 12, 0, 0), Windows(("12:00", "12:00")), out _));
    }

    [Fact]
    public void Normalize_DropsInvalidAndRewritesTimes()
    {
        var list = new List<MuteWindow>
        {
            new() { Start = "5:00", End = "7:00" },
            new() { Start = "nope", End = "07:00" },
            new() { Start = "10:00", End = "10:00" }
        };

        Assert.True(MuteSchedule.Normalize(list));
        Assert.Single(list);
        Assert.Equal("05:00", list[0].Start);
        Assert.Equal("07:00", list[0].End);
    }

    [Fact]
    public void GetStatus_ReportsActiveRangeUntilItsEnd()
    {
        var status = MuteSchedule.GetStatus(new DateTime(2026, 1, 1, 1, 0, 0), true, Windows(("23:00", "05:00")));

        Assert.True(status.IsActive);
        Assert.Contains("until 05:00", status.Text);
    }

    [Fact]
    public void GetStatus_ReportsNextStartTodayOrTomorrow()
    {
        var today = MuteSchedule.GetStatus(new DateTime(2026, 1, 1, 12, 0, 0), true, Windows(("23:00", "05:00")));
        Assert.False(today.IsActive);
        Assert.Contains("starts at 23:00", today.Text);
        Assert.DoesNotContain("tomorrow", today.Text);

        var tomorrow = MuteSchedule.GetStatus(new DateTime(2026, 1, 1, 23, 30, 0), true, Windows(("05:00", "07:00")));
        Assert.False(tomorrow.IsActive);
        Assert.Contains("starts at 05:00 (tomorrow)", tomorrow.Text);
    }

    [Fact]
    public void GetStatus_SaysWhenScheduleIsOffOrEmpty()
    {
        Assert.False(MuteSchedule.GetStatus(DateTime.Now, false, Windows(("23:00", "05:00"))).IsActive);
        Assert.Contains("off", MuteSchedule.GetStatus(DateTime.Now, false, Windows()).Text);
        Assert.Contains("no ranges", MuteSchedule.GetStatus(DateTime.Now, true, Windows()).Text);
    }

    [Fact]
    public void Describe_MarksOvernightRanges()
    {
        Assert.Equal("23:00 – 05:00 (next day)", MuteSchedule.Describe(new MuteWindow { Start = "23:00", End = "05:00" }));
        Assert.Equal("05:00 – 07:00", MuteSchedule.Describe(new MuteWindow { Start = "05:00", End = "07:00" }));
    }
}
