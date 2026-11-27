using System;
using AppLock.Services.SpeedMeter;
using Xunit;

namespace AppLock.Tests.SpeedMeter;

public class AppBandwidthProviderTests
{
    [Fact]
    public void FormatAppName_PrefersAttributionName()
    {
        Assert.Equal("Microsoft Edge", AppBandwidthProvider.FormatAppName("Microsoft Edge", @"C:\Program Files\msedge.exe"));
    }

    [Fact]
    public void FormatAppName_ExtractsExeNameFromDevicePath()
    {
        Assert.Equal("msedge", AppBandwidthProvider.FormatAppName("", @"\device\harddiskvolume3\program files (x86)\microsoft\edge\application\msedge.exe"));
    }

    [Fact]
    public void FormatAppName_StripsStorePackageHashAndPublisherPrefix()
    {
        Assert.Equal("WhatsAppDesktop", AppBandwidthProvider.FormatAppName(null, "5319275A.WhatsAppDesktop_cv1g1gvanyjgm"));
    }

    [Fact]
    public void FormatAppName_EmptyIdReturnsSystemAndBackground()
    {
        Assert.Equal("System & Background", AppBandwidthProvider.FormatAppName("", ""));
    }

    [Fact]
    public void AggregateEntries_CombinesDuplicateAppsAndSortsDescending()
    {
        var entries = new (string?, string?, ulong, ulong)[]
        {
            ("", @"\device\harddiskvolume3\app\v1\msedgewebview2.exe", 100, 50),
            ("", @"\device\harddiskvolume3\app\v2\msedgewebview2.exe", 300, 150),
            ("", @"\device\harddiskvolume3\app\chrome.exe", 1000, 200),
            ("", @"\device\harddiskvolume3\app\idle.exe", 0, 0),
        };

        var result = AppBandwidthProvider.AggregateEntries(entries);

        Assert.Equal(2, result.Count);
        Assert.Equal("chrome", result[0].AppName);
        Assert.Equal(1200UL, result[0].TotalBytes);
        Assert.Equal("msedgewebview2", result[1].AppName);
        Assert.Equal(400UL, result[1].BytesReceived);
        Assert.Equal(200UL, result[1].BytesSent);
    }

    [Fact]
    public void ResolveRange_HandlesYesterdayAndSpecificDate()
    {
        var now = new DateTimeOffset(2026, 11, 27, 15, 30, 0, TimeSpan.FromHours(5));

        var (yStart, yEnd, _) = AppBandwidthProvider.ResolveRange(BandwidthPeriod.Yesterday, null, now);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.FromHours(5)), yStart);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.FromHours(5)), yEnd);

        var (sStart, sEnd, _) = AppBandwidthProvider.ResolveRange(BandwidthPeriod.SpecificDate, new DateTime(2026, 11, 20), now);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(5)), sStart);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.FromHours(5)), sEnd);

        // DateRange swaps reversed dates automatically and caps end at now when end == today.
        var (rStart, rEnd, _) = AppBandwidthProvider.ResolveRange(
            BandwidthPeriod.DateRange,
            new DateTime(2026, 11, 27),
            now,
            new DateTime(2026, 11, 22));
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.FromHours(5)), rStart);
        Assert.Equal(now, rEnd);
    }

    [Fact]
    public void PickTopApp_SelectsHighestDownloadOrUploadConsumer()
    {
        var items = new[]
        {
            new AppBandwidthUsage { AppName = "edge", BytesReceived = 5_000_000, BytesSent = 100_000 },
            new AppBandwidthUsage { AppName = "obs", BytesReceived = 200_000, BytesSent = 8_000_000 }
        };

        Assert.Equal("edge", AppBandwidthProvider.PickTopApp(items, isDownload: true));
        Assert.Equal("obs", AppBandwidthProvider.PickTopApp(items, isDownload: false));
        Assert.Null(AppBandwidthProvider.PickTopApp(Array.Empty<AppBandwidthUsage>(), isDownload: true));
    }
}
