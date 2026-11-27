using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace AppLock.Services.SpeedMeter;

/// <summary>Bandwidth consumed by one application over a queried time range.</summary>
public sealed class AppBandwidthUsage
{
    public string AppName { get; init; } = string.Empty;
    public string AttributionId { get; init; } = string.Empty;
    public ulong BytesReceived { get; init; }
    public ulong BytesSent { get; init; }
    public ulong TotalBytes => BytesReceived + BytesSent;
}

/// <summary>Selectable time windows for per-app bandwidth inspection.</summary>
public enum BandwidthPeriod
{
    Today,
    Yesterday,
    ThisWeek,
    ThisMonth,
    Last30Days,
    SpecificDate,
    DateRange
}

/// <summary>
/// Queries Windows' local Network Data Usage (SRUM/NDU) store via WinRT
/// <see cref="ConnectionProfile.GetAttributedNetworkUsageAsync"/> without requiring admin rights.
/// </summary>
public static class AppBandwidthProvider
{
    /// <summary>Resolves a period preset (or specific date / date range) into local start/end bounds and a human-readable label.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End, string Label) ResolveRange(
        BandwidthPeriod period,
        DateTime? specificDate = null,
        DateTimeOffset? nowOverride = null,
        DateTime? rangeEndDate = null)
    {
        var now = nowOverride ?? DateTimeOffset.Now;
        var today = now.Date;
        var offset = now.Offset;

        return period switch
        {
            BandwidthPeriod.Today => (
                new DateTimeOffset(today, offset),
                now,
                today.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture)),

            BandwidthPeriod.Yesterday => (
                new DateTimeOffset(today.AddDays(-1), offset),
                new DateTimeOffset(today, offset),
                today.AddDays(-1).ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture)),

            BandwidthPeriod.ThisWeek => ResolveThisWeek(today, now, offset),

            BandwidthPeriod.ThisMonth => (
                new DateTimeOffset(new DateTime(today.Year, today.Month, 1), offset),
                now,
                today.ToString("MMMM yyyy", CultureInfo.CurrentCulture)),

            BandwidthPeriod.Last30Days => (
                new DateTimeOffset(today.AddDays(-29), offset),
                now,
                $"{today.AddDays(-29):MMM d} – {today:MMM d, yyyy}"),

            BandwidthPeriod.SpecificDate => ResolveSpecificDate(specificDate ?? today, now, offset),

            BandwidthPeriod.DateRange => ResolveDateRange(specificDate ?? today, rangeEndDate ?? specificDate ?? today, now, offset),

            _ => (new DateTimeOffset(today, offset), now, today.ToString("d", CultureInfo.CurrentCulture))
        };
    }

    private static (DateTimeOffset Start, DateTimeOffset End, string Label) ResolveDateRange(
        DateTime fromDate, DateTime toDate, DateTimeOffset now, TimeSpan offset)
    {
        var startDay = fromDate.Date;
        var endDay = toDate.Date;
        if (endDay < startDay)
        {
            (startDay, endDay) = (endDay, startDay);
        }

        var start = new DateTimeOffset(startDay, offset);
        var end = endDay >= now.Date ? now : new DateTimeOffset(endDay.AddDays(1), offset);
        var label = startDay == endDay
            ? startDay.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture)
            : $"{startDay:MMM d, yyyy} – {endDay:MMM d, yyyy}";
        return (start, end, label);
    }

    private static (DateTimeOffset Start, DateTimeOffset End, string Label) ResolveThisWeek(
        DateTime today, DateTimeOffset now, TimeSpan offset)
    {
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int diff = (7 + (today.DayOfWeek - firstDay)) % 7;
        var weekStart = today.AddDays(-diff);
        return (
            new DateTimeOffset(weekStart, offset),
            now,
            $"{weekStart:MMM d} – {today:MMM d, yyyy}");
    }

    private static (DateTimeOffset Start, DateTimeOffset End, string Label) ResolveSpecificDate(
        DateTime date, DateTimeOffset now, TimeSpan offset)
    {
        var day = date.Date;
        var start = new DateTimeOffset(day, offset);
        var end = day == now.Date ? now : new DateTimeOffset(day.AddDays(1), offset);
        return (start, end, day.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture));
    }

    /// <summary>Queries per-app bandwidth across all available connection profiles for [start, end).</summary>
    public static async Task<IReadOnlyList<AppBandwidthUsage>> QueryAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
    {
        if (end <= start) return Array.Empty<AppBandwidthUsage>();

        return await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            var profiles = new List<ConnectionProfile>();
            var seenAdapterIds = new HashSet<Guid>();

            try
            {
                var internet = NetworkInformation.GetInternetConnectionProfile();
                if (internet != null)
                {
                    profiles.Add(internet);
                    if (internet.NetworkAdapter != null)
                    {
                        seenAdapterIds.Add(internet.NetworkAdapter.NetworkAdapterId);
                    }
                }

                foreach (var p in NetworkInformation.GetConnectionProfiles())
                {
                    if (p.NetworkAdapter != null && !seenAdapterIds.Add(p.NetworkAdapter.NetworkAdapterId))
                        continue;
                    profiles.Add(p);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Profile enumeration error: {ex.Message}");
            }

            var states = new NetworkUsageStates
            {
                Roaming = TriStates.DoNotCare,
                Shared = TriStates.DoNotCare
            };

            var rawEntries = new List<(string? Name, string? Id, ulong Rx, ulong Tx)>();

            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var usages = await profile.GetAttributedNetworkUsageAsync(start, end, states);
                    if (usages == null) continue;

                    foreach (var u in usages)
                    {
                        if (u.BytesReceived == 0 && u.BytesSent == 0) continue;
                        rawEntries.Add((u.AttributionName, u.AttributionId, u.BytesReceived, u.BytesSent));
                    }
                }
                catch
                {
                    // Disconnected, virtual, or unsupported profile: skip silently.
                }
            }

            return AggregateEntries(rawEntries);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Normalizes app names and aggregates duplicate entries (e.g. across adapters or versioned folders).</summary>
    public static IReadOnlyList<AppBandwidthUsage> AggregateEntries(
        IEnumerable<(string? Name, string? Id, ulong Rx, ulong Tx)> entries)
    {
        var map = new Dictionary<string, (string Id, ulong Rx, ulong Tx)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, id, rx, tx) in entries)
        {
            if (rx == 0 && tx == 0) continue;

            var friendly = FormatAppName(name, id);
            if (map.TryGetValue(friendly, out var existing))
            {
                map[friendly] = (existing.Id, existing.Rx + rx, existing.Tx + tx);
            }
            else
            {
                map[friendly] = (id ?? string.Empty, rx, tx);
            }
        }

        return map
            .Select(kvp => new AppBandwidthUsage
            {
                AppName = kvp.Key,
                AttributionId = kvp.Value.Id,
                BytesReceived = kvp.Value.Rx,
                BytesSent = kvp.Value.Tx
            })
            .OrderByDescending(x => x.TotalBytes)
            .ToList();
    }

    /// <summary>Converts a WinRT AttributionName / AttributionId into a readable app label.</summary>
    public static string FormatAppName(string? attributionName, string? attributionId)
    {
        if (!string.IsNullOrWhiteSpace(attributionName))
        {
            return attributionName.Trim();
        }

        if (string.IsNullOrWhiteSpace(attributionId))
        {
            return "System & Background";
        }

        var id = attributionId.Trim();

        // Win32 device path e.g. "\device\harddiskvolume3\program files\...\msedge.exe"
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || id.Contains('\\'))
        {
            var fileName = Path.GetFileNameWithoutExtension(id);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        // UWP / MSIX PackageFamilyName e.g. "5319275A.WhatsAppDesktop_cv1g1gvanyjgm"
        var underscore = id.LastIndexOf('_');
        if (underscore > 0 && id.Length - underscore - 1 is >= 10 and <= 15)
        {
            var pkg = id[..underscore];
            var dot = pkg.IndexOf('.');
            if (dot > 0 && dot < pkg.Length - 1)
            {
                return pkg[(dot + 1)..];
            }
            return pkg;
        }

        return id;
    }
}
