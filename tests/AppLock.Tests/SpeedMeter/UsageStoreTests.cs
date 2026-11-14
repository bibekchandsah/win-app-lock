using AppLock.Services.SpeedMeter;

namespace AppLock.Tests.SpeedMeter;

public class UsageStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "AppLockUsageTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best effort: a leftover temp folder must not fail a test run.
        }
    }

    [Fact]
    public void AddBytes_AccumulatesPerDayAndAdapter()
    {
        var store = new UsageStore(_directory);
        var now = DateTime.Now;

        store.AddBytes("a", "Wi-Fi", 1000, 500, now);
        store.AddBytes("a", "Wi-Fi", 2000, 1000, now);
        store.AddBytes("b", "Ethernet", 3000, 0, now);

        Assert.Equal((3000L, 1500L), store.Today("a"));
        Assert.Equal((3000L, 0L), store.Today("b"));
        Assert.Equal((6000L, 1500L), store.Today());
    }

    [Fact]
    public void AddBytes_IgnoresZeroDeltas()
    {
        var store = new UsageStore(_directory);
        store.AddBytes("a", "Wi-Fi", 0, 0, DateTime.Now);

        Assert.Equal((0L, 0L), store.Today());
    }

    [Fact]
    public void Total_FiltersByDateRange()
    {
        var store = new UsageStore(_directory);
        var today = DateTime.Today;

        store.AddBytes("a", "Wi-Fi", 100, 0, today.AddDays(-2));
        store.AddBytes("a", "Wi-Fi", 200, 0, today);

        Assert.Equal((100L, 0L), store.Total(today.AddDays(-2), today.AddDays(-1)));
        Assert.Equal((300L, 0L), store.Total(today.AddDays(-2), today.AddDays(1)));
    }

    [Fact]
    public void Daily_ReturnsOnePointPerDay()
    {
        var store = new UsageStore(_directory);
        store.AddBytes("a", "Wi-Fi", 512, 128, DateTime.Today);

        var points = store.Daily(7);

        Assert.Equal(7, points.Count);
        Assert.Equal(512, points[^1].BytesDown);
        Assert.Equal(128, points[^1].BytesUp);
    }

    [Fact]
    public void Csv_ContainsHeaderAndRows()
    {
        var store = new UsageStore(_directory);
        store.AddBytes("a", "Wi-Fi, 5GHz", 1024, 512, DateTime.Today);

        var lines = store.BuildCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("date,adapterId,adapterName,downBytes,upBytes", lines[0]);
        Assert.Contains("\"Wi-Fi, 5GHz\"", lines[1]);
        Assert.Contains("1024", lines[1]);
    }

    [Fact]
    public void SaveAndReload_KeepsRowsAndCapState()
    {
        var store = new UsageStore(_directory);
        store.AddBytes("a", "Wi-Fi", 777, 111, DateTime.Today);
        store.MarkDailyCapAlert("2026-01-01|80");
        store.FlushIfDue(DateTime.Now, force: true);

        var reloaded = new UsageStore(_directory);

        Assert.Equal((777L, 111L), reloaded.Today());
        Assert.Equal("2026-01-01|80", reloaded.LastDailyCapAlert);
    }

    [Fact]
    public void Reset_ClearsRowsAndAlerts()
    {
        var store = new UsageStore(_directory);
        store.AddBytes("a", "Wi-Fi", 100, 100, DateTime.Today);
        store.MarkMonthlyCapAlert("2026-01-01|100");

        store.Reset();

        Assert.Equal((0L, 0L), store.Today());
        Assert.Null(store.LastMonthlyCapAlert);
    }

    [Fact]
    public void AddBytes_PrunesRowsOlderThanRetention()
    {
        var store = new UsageStore(_directory);
        var old = DateTime.Today.AddMonths(-13);

        store.AddBytes("a", "Wi-Fi", 500, 0, old);
        store.AddBytes("a", "Wi-Fi", 100, 0, DateTime.Today);

        Assert.Equal((0L, 0L), store.Total(old, old.AddDays(1)));
        Assert.Equal((100L, 0L), store.Today());
    }

    [Theory]
    [InlineData(20, 2026, 10, 20)]
    [InlineData(5, 2026, 11, 5)]
    [InlineData(14, 2026, 11, 14)]
    public void MonthStart_UsesTheResetDay(int resetDay, int year, int month, int day)
    {
        var start = UsageStore.MonthStart(new DateTime(2026, 11, 14), resetDay);

        Assert.Equal(new DateTime(year, month, day), start);
    }
}
