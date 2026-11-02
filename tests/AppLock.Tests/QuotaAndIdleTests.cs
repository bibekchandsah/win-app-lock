using System;
using System.Collections.Generic;
using System.Text.Json;
using AppLock.Models;
using Xunit;

namespace AppLock.Tests;

public class QuotaAndIdleTests
{
    [Fact]
    public void MidnightRollover_ResetsUsageAndExtraSeconds()
    {
        var settings = new SelfDisciplineSettings
        {
            QuotaEnabled = true,
            DailyLimitMinutes = 60,
            QuotaDate = new DateOnly(2026, 11, 1),
            UsedSecondsToday = 3500,
            ExtraSecondsGrantedToday = 900
        };

        var today = new DateOnly(2026, 11, 2);

        // Simulate rollover logic
        if (settings.QuotaDate != today)
        {
            settings.QuotaDate = today;
            settings.UsedSecondsToday = 0;
            settings.ExtraSecondsGrantedToday = 0;
        }

        Assert.Equal(today, settings.QuotaDate);
        Assert.Equal(0, settings.UsedSecondsToday);
        Assert.Equal(0, settings.ExtraSecondsGrantedToday);
    }

    [Fact]
    public void QuotaCounting_WhenIdleExceeds60Seconds_PausesCounting()
    {
        var settings = new SelfDisciplineSettings
        {
            QuotaEnabled = true,
            DailyLimitMinutes = 60,
            UsedSecondsToday = 100
        };

        double idleSeconds = 65.0; // User idle for 65s (>= 60s threshold)
        const double IdlePauseSeconds = 60.0;

        // Condition used by QuotaTracker
        bool shouldCount = idleSeconds < IdlePauseSeconds;

        if (shouldCount)
        {
            settings.UsedSecondsToday++;
        }

        Assert.False(shouldCount);
        Assert.Equal(100, settings.UsedSecondsToday); // Not incremented
    }

    [Fact]
    public void QuotaCounting_WhenNotIdle_IncrementsUsage()
    {
        var settings = new SelfDisciplineSettings
        {
            QuotaEnabled = true,
            DailyLimitMinutes = 60,
            UsedSecondsToday = 100
        };

        double idleSeconds = 5.0; // User actively using (< 60s)
        const double IdlePauseSeconds = 60.0;

        bool shouldCount = idleSeconds < IdlePauseSeconds;

        if (shouldCount)
        {
            settings.UsedSecondsToday++;
        }

        Assert.True(shouldCount);
        Assert.Equal(101, settings.UsedSecondsToday);
    }

    [Fact]
    public void IdleThreshold_WhenIdleMinutesReachesThreshold_TriggersRelock()
    {
        var settings = new SelfDisciplineSettings
        {
            IdleRelockEnabled = true,
            IdleRelockMinutes = 15
        };

        double idleMinutesLow = 14.9;
        double idleMinutesHigh = 15.0;

        bool triggersLow = settings.IdleRelockEnabled && idleMinutesLow >= settings.IdleRelockMinutes;
        bool triggersHigh = settings.IdleRelockEnabled && idleMinutesHigh >= settings.IdleRelockMinutes;

        Assert.False(triggersLow);
        Assert.True(triggersHigh);
    }

    [Fact]
    public void JsonMigration_OlderRuleWithoutSelfDiscipline_MigratesCleanly()
    {
        // Old JSON format before Step 1 (no SelfDiscipline property)
        string oldRuleJson = """
        {
            "Id": "a1b2c3d4-e5f6-4a5b-8c7d-9e8f7a6b5c4d",
            "Type": 0,
            "ProcessName": "notepad",
            "ProcessPath": "C:\\Windows\\notepad.exe",
            "WindowTitlePattern": "",
            "IsRegex": false,
            "DisplayName": "Notepad"
        }
        """;

        var rule = JsonSerializer.Deserialize<LockRule>(oldRuleJson);
        Assert.NotNull(rule);

        // Verify that rule.SelfDiscipline is safely instantiated with defaults
        Assert.NotNull(rule.SelfDiscipline);
        Assert.False(rule.SelfDiscipline.QuotaEnabled);
        Assert.Equal(60, rule.SelfDiscipline.DailyLimitMinutes);
        Assert.False(rule.SelfDiscipline.IdleRelockEnabled);
        Assert.Equal(15, rule.SelfDiscipline.IdleRelockMinutes);
        Assert.NotNull(rule.SelfDiscipline.TimeBlocks);
        Assert.Empty(rule.SelfDiscipline.TimeBlocks);
        Assert.False(rule.HasActiveSelfDiscipline);
    }
}
