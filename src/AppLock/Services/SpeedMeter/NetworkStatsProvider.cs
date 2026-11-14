using System.Diagnostics;
using System.Net.NetworkInformation;

namespace AppLock.Services.SpeedMeter;

/// <summary>One speed reading for the widget.</summary>
public sealed class NetworkSpeedSample
{
    public double BytesDownPerSec { get; init; }
    public double BytesUpPerSec { get; init; }
    public long PeakBytesDownPerSec { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public long LinkSpeedBitsPerSec { get; init; }

    public bool HasAdapter => !string.IsNullOrEmpty(AdapterName);
}

/// <summary>
/// Reads the Windows interface byte counters and converts them to per-second rates.
/// Uses the managed NetworkInterface APIs (same 64-bit octets as GetIfTable2) so no
/// hand-marshalled MIB_IF_ROW2 table is needed.
/// </summary>
public sealed class NetworkStatsProvider
{
    // Auto = the adapter holding the default route; AllPhysical = every real adapter summed.
    public string AdapterMode { get; set; } = "Auto";
    public string? AdapterId { get; set; }
    public bool IncludeVirtualAdapters { get; set; }

    private readonly Dictionary<string, (long In, long Out, long Timestamp)> _last = new();
    private readonly Queue<double> _downHistory = new();
    private readonly Queue<double> _upHistory = new();
    private long _peakDown;

    private const int SmoothingSamples = 3;

    private static readonly string[] VirtualKeywords =
    {
        "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "loopback",
        "tap-", "tunnel", "npcap", "tailscale", "zerotier", "bluetooth"
    };

    public NetworkSpeedSample Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var adapters = SelectAdapters(out var primary);

        long bytesIn = 0, bytesOut = 0;
        var seen = new HashSet<string>();

        foreach (var adapter in adapters)
        {
            try
            {
                var stats = adapter.GetIPStatistics();
                seen.Add(adapter.Id);
                bytesIn += stats.BytesReceived;
                bytesOut += stats.BytesSent;
            }
            catch (NetworkInformationException)
            {
                // The adapter disappeared between enumeration and the read; skip this tick.
            }
        }

        // Drop readings for adapters that are gone so a re-plug starts from a fresh baseline.
        foreach (var stale in _last.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _last.Remove(stale);
        }

        double downPerSec = 0, upPerSec = 0;
        var key = string.Join('|', adapters.Select(a => a.Id).OrderBy(id => id));

        if (_last.TryGetValue(key, out var previous))
        {
            var elapsedSeconds = (now - previous.Timestamp) / (double)Stopwatch.Frequency;
            if (elapsedSeconds > 0.05)
            {
                var downDelta = bytesIn - previous.In;
                var upDelta = bytesOut - previous.Out;

                // A negative delta means the counter reset (adapter restart); treat it as zero.
                downPerSec = Smooth(_downHistory, Math.Max(0, downDelta) / elapsedSeconds);
                upPerSec = Smooth(_upHistory, Math.Max(0, upDelta) / elapsedSeconds);
            }
        }
        else
        {
            _downHistory.Clear();
            _upHistory.Clear();
        }

        _last[key] = (bytesIn, bytesOut, now);
        _peakDown = Math.Max(_peakDown, (long)downPerSec);

        return new NetworkSpeedSample
        {
            BytesDownPerSec = downPerSec,
            BytesUpPerSec = upPerSec,
            PeakBytesDownPerSec = _peakDown,
            AdapterName = primary?.Name ?? string.Empty,
            LinkSpeedBitsPerSec = primary?.Speed ?? 0
        };
    }

    /// <summary>Average of the last few samples so the numbers stop flickering.</summary>
    private static double Smooth(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > SmoothingSamples) history.Dequeue();
        return history.Average();
    }

    /// <summary>
    /// Picks the adapters to measure. Auto prefers the adapter that owns the default gateway,
    /// which is the adapter Windows actually routes traffic through.
    /// </summary>
    private List<NetworkInterface> SelectAdapters(out NetworkInterface? primary)
    {
        primary = null;

        var candidates = new List<NetworkInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsable(adapter)) continue;
            candidates.Add(adapter);
        }

        switch (AdapterMode)
        {
            case "AllPhysical":
                primary = candidates.FirstOrDefault();
                return candidates;

            case "Specific" when !string.IsNullOrEmpty(AdapterId):
                var specific = candidates.FirstOrDefault(a => a.Id == AdapterId);
                primary = specific;
                return specific is null ? new List<NetworkInterface>() : new List<NetworkInterface> { specific };
        }

        // Auto: an adapter that carries a default gateway is the one Windows routes through.
        // .NET does not expose the route metric, so when several qualify the fastest link wins.
        var withGateway = candidates
            .Where(HasGateway)
            .OrderByDescending(a => a.Speed)
            .ToList();

        var chosen = withGateway.FirstOrDefault() ?? candidates
            .OrderByDescending(a => a.Speed)
            .FirstOrDefault();

        primary = chosen;
        return chosen is null ? new List<NetworkInterface>() : new List<NetworkInterface> { chosen };
    }

    private bool IsUsable(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up) return false;
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        if (!adapter.Supports(NetworkInterfaceComponent.IPv4)) return false;
        if (!IncludeVirtualAdapters && IsVirtual(adapter)) return false;
        return true;
    }

    private static bool IsVirtual(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return VirtualKeywords.Any(text.Contains);
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Count > 0;
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

}

/// <summary>Turns bytes-per-second into the labels the widget shows.</summary>
public static class SpeedFormatter
{
    private static readonly string[] ByteUnits = { "B/s", "KB/s", "MB/s", "GB/s" };

    /// <param name="units">Auto | Bytes | Bits</param>
    public static string Format(double bytesPerSec, string units, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 2);
        if (bytesPerSec <= 0) bytesPerSec = 0;

        if (string.Equals(units, "Bits", StringComparison.OrdinalIgnoreCase))
        {
            var bits = bytesPerSec * 8;
            if (bits >= 1_000_000) return $"{(bits / 1_000_000.0).ToString($"F{decimals}")} Mbps";
            if (bits >= 1_000) return $"{(bits / 1_000.0).ToString($"F{decimals}")} Kbps";
            return $"{bits.ToString("F0")} bps";
        }

        var index = 0;
        var value = bytesPerSec;
        while (value >= 1024 && index < ByteUnits.Length - 1)
        {
            value /= 1024;
            index++;
        }

        // Auto drops the decimals below 1 KB/s so it reads "812 B/s" instead of "812.0 B/s".
        var digits = index == 0 && string.Equals(units, "Auto", StringComparison.OrdinalIgnoreCase) ? 0 : decimals;
        return $"{value.ToString($"F{digits}")} {ByteUnits[index]}";
    }
}
