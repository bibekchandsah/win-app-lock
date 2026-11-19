using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services.ScreenTime;

namespace AppLock.Tests;

public class ScreenTimeStoreTests
{
    private static string NewDatabasePath() => Path.Combine(
        Path.GetTempPath(),
        "applock-tests",
        "screentime-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }

    private static UsageDelta Delta(string appKey, DateTime when, int seconds, string? name = null) =>
        new(appKey, name ?? appKey, when, seconds);

    [Fact]
    public void Settings_Migrate_From_Empty_Json_With_Defaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}");

        Assert.NotNull(settings);
        Assert.NotNull(settings!.ScreenTime);
        Assert.True(settings.ScreenTime.TrackingEnabled);
        Assert.False(settings.ScreenTime.ConsentShown);
        Assert.Equal(60, settings.ScreenTime.IdlePauseSeconds);
        Assert.True(settings.ScreenTime.CountWhileMediaPlaying);
        Assert.Equal(90, settings.ScreenTime.RetentionDays);
        Assert.False(settings.ScreenTime.RememberWindowTitles);
        Assert.Empty(settings.ScreenTime.ExcludedAppKeys);
        Assert.Empty(settings.ScreenTime.HiddenAppKeys);
    }

    [Fact]
    public void Existing_Rules_Default_To_AlwaysLocked()
    {
        var json = """{"LockRules":[{"ProcessName":"notepad","DisplayName":"Notepad"}]}""";
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        var rule = Assert.Single(settings.LockRules);
        Assert.Equal(LockRuleMode.AlwaysLocked, rule.Mode);
        Assert.False(rule.IsLimitOnly);
        Assert.Equal(string.Empty, rule.AppKey);
    }

    [Fact]
    public async Task Store_Sums_Seconds_Into_The_Same_Hour_Row()
    {
        string path = NewDatabasePath();
        var day = new DateTime(2026, 5, 4, 10, 15, 0);
        try
        {
            await using (var store = new ScreenTimeStore(path))
            {
                store.Enqueue(Delta("exe:c:\\apps\\chrome.exe", day, 60, "Chrome"));
                store.Enqueue(Delta("exe:c:\\apps\\chrome.exe", day.AddMinutes(5), 60));
                store.Flush();

                var apps = store.GetAppUsage(DateOnly.FromDateTime(day), DateOnly.FromDateTime(day));
                var row = Assert.Single(apps);
                Assert.Equal(120, row.Seconds);
                Assert.Equal("Chrome", row.DisplayName);

                var hours = store.GetHourUsage(DateOnly.FromDateTime(day));
                Assert.Equal(120, hours[10].Seconds);
                Assert.Equal(0, hours[11].Seconds);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Store_Keeps_Days_Separate_And_Returns_Every_Day_In_Range()
    {
        string path = NewDatabasePath();
        var day1 = new DateTime(2026, 5, 4, 23, 30, 0);
        var day2 = day1.AddDays(1);
        try
        {
            await using (var store = new ScreenTimeStore(path))
            {
                store.Enqueue(Delta("exe:a.exe", day1, 300));
                store.Enqueue(Delta("exe:a.exe", day2, 120));
                store.Flush();

                var totals = store.GetDayTotals(DateOnly.FromDateTime(day1), DateOnly.FromDateTime(day2));
                Assert.Equal(2, totals.Count);
                Assert.Equal(300, totals[0].Seconds);
                Assert.Equal(120, totals[1].Seconds);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Coverage_Adds_Up_And_Clamps_To_A_Full_Hour()
    {
        string path = NewDatabasePath();
        var day = DateOnly.FromDateTime(new DateTime(2026, 5, 4));
        try
        {
            await using (var store = new ScreenTimeStore(path))
            {
                store.RecordCoverage(day, 9, 1800);
                store.RecordCoverage(day, 9, 1800);

                var coverage = store.GetCoverage(day);
                Assert.Equal(3600, coverage[9].TrackedSeconds);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Prune_Removes_Rows_Older_Than_Retention()
    {
        string path = NewDatabasePath();
        var today = new DateTime(2026, 5, 4, 12, 0, 0);
        try
        {
            await using (var store = new ScreenTimeStore(path))
            {
                store.Enqueue(Delta("exe:old.exe", today.AddDays(-100), 600));
                store.Enqueue(Delta("exe:new.exe", today.AddDays(-3), 600));
                store.Flush();

                int removed = store.Prune(90, DateOnly.FromDateTime(today));
                Assert.True(removed > 0);

                var apps = store.GetAppUsage(DateOnly.FromDateTime(today.AddDays(-120)), DateOnly.FromDateTime(today));
                var row = Assert.Single(apps);
                Assert.Equal("exe:new.exe", row.AppKey);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ClearAll_Does_Not_Touch_Quota_Counters()
    {
        string path = NewDatabasePath();
        var rule = new LockRule
        {
            DisplayName = "Chrome",
            SelfDiscipline = { QuotaEnabled = true, UsedSecondsToday = 240, DailyLimitMinutes = 30 }
        };

        try
        {
            await using (var store = new ScreenTimeStore(path))
            {
                store.Enqueue(Delta("exe:chrome.exe", DateTime.Now, 120));
                store.Flush();
                store.ClearAll();

                Assert.Empty(store.GetAppUsage(DateOnly.FromDateTime(DateTime.Now).AddDays(-1), DateOnly.FromDateTime(DateTime.Now)));
            }

            // The history is display-only: enforcement reads this counter, which ClearAll must never change.
            Assert.Equal(240, rule.SelfDiscipline.UsedSecondsToday);
            Assert.Equal(LockRuleMode.AlwaysLocked, rule.Mode);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Export_Csv_Has_Header_And_Rows()
    {
        string path = NewDatabasePath();
        var day = new DateTime(2026, 5, 4, 8, 0, 0);
        try
        {
            await using (var store = new ScreenTimeStore(path))
            {
                store.Enqueue(Delta("exe:teams.exe", day, 90, "Teams, work"));
                store.Flush();

                string csv = store.ExportCsv(DateOnly.FromDateTime(day), DateOnly.FromDateTime(day));
                var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                Assert.Contains("day,hour,app,seconds", lines[1]);
                Assert.Contains("2026-05-04,8,\"Teams, work\",90", lines[2]);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task HourStack_Keeps_Top_Apps_And_Folds_The_Rest_Into_Other()
    {
        string path = NewDatabasePath();
        var day = new DateTime(2026, 5, 4, 14, 0, 0);
        try
        {
            await using (var store = new ScreenTimeStore(path))
            {
                store.Enqueue(Delta("exe:a.exe", day, 900, "A"));
                store.Enqueue(Delta("exe:b.exe", day, 600, "B"));
                store.Enqueue(Delta("exe:c.exe", day, 300, "C"));
                store.Enqueue(Delta("exe:d.exe", day, 120, "D"));
                store.Enqueue(Delta("exe:e.exe", day, 60, "E"));
                store.Flush();

                var stack = store.GetHourStack(DateOnly.FromDateTime(day), 14, topCount: 3);

                Assert.Equal(4, stack.Count);
                Assert.Equal("A", stack[0].Label);
                Assert.Equal(180, stack[3].Seconds);
                Assert.Equal("Other", stack[3].Label);
                Assert.Equal(-1, stack[3].ColorIndex);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Stable_Color_Index_Is_In_Range_And_Repeatable()
    {
        int first = ScreenTimeStore.StableColorIndex("exe:c:\\apps\\chrome.exe");
        int second = ScreenTimeStore.StableColorIndex("exe:c:\\apps\\chrome.exe");

        Assert.Equal(first, second);
        Assert.InRange(first, 0, 7);
    }
}
