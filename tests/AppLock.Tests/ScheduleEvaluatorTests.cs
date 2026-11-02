using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.Services;
using Xunit;

namespace AppLock.Tests;

public class ScheduleEvaluatorTests
{
    private class FakeClock : IClock
    {
        public DateTime Now { get; set; }
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    [Fact]
    public void RegularBlock_InsideSchedule_ReturnsTrueAndCorrectUntil()
    {
        // Arrange: Mon-Fri 09:00 - 17:00
        var rule = new LockRule
        {
            ProcessName = "notepad",
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(9, 0),
                        End = new TimeOnly(17, 0),
                        Days = new bool[] { true, true, true, true, true, false, false } // Mon-Fri
                    }
                }
            }
        };

        // Monday at 10:30 AM
        var monday = new DateTime(2026, 11, 2, 10, 30, 0); // 2026-11-02 is Monday
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        // Act
        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, monday, out DateTime until);

        // Assert
        Assert.True(inside);
        Assert.Equal(new DateTime(2026, 11, 2, 17, 0, 0), until);
    }

    [Fact]
    public void RegularBlock_OutsideSchedule_ReturnsFalse()
    {
        var rule = new LockRule
        {
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(9, 0),
                        End = new TimeOnly(17, 0),
                        Days = new bool[] { true, true, true, true, true, false, false }
                    }
                }
            }
        };

        // Monday at 18:00 (after schedule)
        var monday = new DateTime(2026, 11, 2, 18, 0, 0);
        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, monday, out _);

        Assert.False(inside);
    }

    [Fact]
    public void RegularBlock_UnselectedDay_ReturnsFalse()
    {
        var rule = new LockRule
        {
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(9, 0),
                        End = new TimeOnly(17, 0),
                        Days = new bool[] { true, true, true, true, true, false, false } // Mon-Fri only
                    }
                }
            }
        };

        // Saturday at 11:00 AM (2026-11-07 is Saturday)
        var saturday = new DateTime(2026, 11, 7, 11, 0, 0);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);

        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, saturday, out _);

        Assert.False(inside);
    }

    [Fact]
    public void MidnightSpanningBlock_OnStartDay_ReturnsTrue()
    {
        // Block starting Monday 22:00 and ending Tuesday 02:00
        var rule = new LockRule
        {
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(22, 0),
                        End = new TimeOnly(2, 0),
                        Days = new bool[] { true, false, false, false, false, false, false } // Monday only
                    }
                }
            }
        };

        // Monday at 23:15
        var mondayNight = new DateTime(2026, 11, 2, 23, 15, 0);
        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, mondayNight, out DateTime until);

        Assert.True(inside);
        Assert.Equal(new DateTime(2026, 11, 3, 2, 0, 0), until); // Ends Tuesday 02:00
    }

    [Fact]
    public void MidnightSpanningBlock_OnNextDayBeforeEnd_ReturnsTrue()
    {
        // Block starting Monday 22:00 and ending Tuesday 02:00
        var rule = new LockRule
        {
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(22, 0),
                        End = new TimeOnly(2, 0),
                        Days = new bool[] { true, false, false, false, false, false, false } // Monday only
                    }
                }
            }
        };

        // Tuesday at 01:30 AM (part of Monday's block)
        var tuesdayEarly = new DateTime(2026, 11, 3, 1, 30, 0);
        Assert.Equal(DayOfWeek.Tuesday, tuesdayEarly.DayOfWeek);

        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, tuesdayEarly, out DateTime until);

        Assert.True(inside);
        Assert.Equal(new DateTime(2026, 11, 3, 2, 0, 0), until);
    }

    [Fact]
    public void MidnightSpanningBlock_OnNextDayAfterEnd_ReturnsFalse()
    {
        var rule = new LockRule
        {
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(22, 0),
                        End = new TimeOnly(2, 0),
                        Days = new bool[] { true, false, false, false, false, false, false } // Monday only
                    }
                }
            }
        };

        // Tuesday at 02:15 AM (after block ended)
        var tuesdayAfter = new DateTime(2026, 11, 3, 2, 15, 0);
        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, tuesdayAfter, out _);

        Assert.False(inside);
    }

    [Fact]
    public void OverlappingBlocks_MergedCorrectly()
    {
        // Block 1: 10:00 - 12:00
        // Block 2: 11:30 - 14:00 (overlaps Block 1)
        var rule = new LockRule
        {
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(10, 0),
                        End = new TimeOnly(12, 0),
                        Days = new bool[] { true, true, true, true, true, true, true }
                    },
                    new()
                    {
                        Start = new TimeOnly(11, 30),
                        End = new TimeOnly(14, 0),
                        Days = new bool[] { true, true, true, true, true, true, true }
                    }
                }
            }
        };

        // Time during overlap: 11:45
        var now = new DateTime(2026, 11, 2, 11, 45, 0);
        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, now, out DateTime until);

        Assert.True(inside);
        Assert.Equal(new DateTime(2026, 11, 2, 14, 0, 0), until); // Extended to end of merged interval
    }

    [Fact]
    public void DSTDay_EvaluatesConsistentWithWallClockTime()
    {
        // In US/EU DST transition days, wall clock time is local TimeOnly
        var rule = new LockRule
        {
            SelfDiscipline = new SelfDisciplineSettings
            {
                TimeBlocks = new List<TimeBlock>
                {
                    new()
                    {
                        Start = new TimeOnly(1, 0),
                        End = new TimeOnly(4, 0),
                        Days = new bool[] { true, true, true, true, true, true, true }
                    }
                }
            }
        };

        // 2026-03-08 is US Spring DST transition day
        var dstDayTime = new DateTime(2026, 3, 8, 3, 30, 0);
        bool inside = ScheduleEvaluator.IsInsideSchedule(rule, dstDayTime, out DateTime until);

        Assert.True(inside);
        Assert.Equal(new DateTime(2026, 3, 8, 4, 0, 0), until);
    }
}

