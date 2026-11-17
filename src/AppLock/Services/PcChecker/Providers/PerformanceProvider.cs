using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Live performance snapshot: CPU total, memory pressure, disk throughput/active time, network rates and
/// GPU engine utilisation. The page re-collects this provider once per second while its category is open.
/// </summary>
public sealed class PerformanceProvider : IInfoProvider
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private long _prevReceived, _prevSent;
    private DateTime _prevNetworkAt = DateTime.UtcNow;

    public string Id => "performance";
    public string Category => "performance";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);

    /// <summary>Live readings are never cached.</summary>
    public TimeSpan CacheLifetime => TimeSpan.Zero;

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Performance", Category = Category };
        var group = section.Group("Right now");

        // ── CPU ─────────────────────────────────────────────────────────────────
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong idleTicks = ToTicks(idle);
            ulong kernelTicks = ToTicks(kernel);
            ulong userTicks = ToTicks(user);

            if (_prevKernel != 0 || _prevUser != 0)
            {
                double idleDelta = idleTicks - _prevIdle;
                double totalDelta = (kernelTicks - _prevKernel) + (userTicks - _prevUser);
                double used = totalDelta > 0 ? (totalDelta - idleDelta) / totalDelta * 100 : 0;
                used = Math.Clamp(used, 0, 100);

                var cpuItem = InfoItem.Of("performance.cpu", "CPU total", $"{used:0}%", DetailLevel.Basic,
                    "Processor time in use across all cores during the last second.",
                    "Source: GetSystemTimes");
                if (used >= 90) cpuItem.Status = InfoStatus.Warning;
                group.Items.Add(cpuItem);
            }

            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;
        }

        // ── Memory ──────────────────────────────────────────────────────────────
        var os = Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        if (os.Count > 0)
        {
            var totalKb = os[0].ULong("TotalVisibleMemorySize") ?? 0;
            var freeKb = os[0].ULong("FreePhysicalMemory") ?? 0;
            if (totalKb > 0)
            {
                var usedPercent = (double)(totalKb - freeKb) / totalKb * 100;
                var memItem = InfoItem.Of("performance.memory", "Memory in use",
                    $"{PcFormat.Percent(usedPercent)} · {PcFormat.Size((totalKb - freeKb) * 1024)} of {PcFormat.Size(totalKb * 1024)}",
                    DetailLevel.Basic, "Physical memory in use right now.", "Source: WMI Win32_OperatingSystem");
                if (usedPercent >= 90) memItem.Status = InfoStatus.Warning;
                group.Items.Add(memItem);
            }
        }

        // ── Disks ───────────────────────────────────────────────────────────────
        var disks = Wmi.Query("SELECT Name, DiskReadBytesPerSec, DiskWriteBytesPerSec, PercentDiskTime, PercentIdleTime " +
                              "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
        foreach (var disk in disks)
        {
            var name = disk.Str("Name") ?? "Disk";
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;

            var read = disk.ULong("DiskReadBytesPerSec") ?? 0;
            var write = disk.ULong("DiskWriteBytesPerSec") ?? 0;
            if (read == 0 && write == 0) continue;

            group.Items.Add(InfoItem.Of($"performance.disk.{name}", $"Disk {name}",
                $"read {PcFormat.Size(read)}/s · write {PcFormat.Size(write)}/s · busy {disk.ULong("PercentDiskTime") ?? 0}%",
                DetailLevel.Basic, "Disk throughput and how busy the drive is right now.",
                "Source: Win32_PerfFormattedData_PerfDisk_PhysicalDisk"));
        }

        // ── Network ─────────────────────────────────────────────────────────────
        try
        {
            long received = 0, sent = 0;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                try
                {
                    var stats = nic.GetIPv4Statistics();
                    received += stats.BytesReceived;
                    sent += stats.BytesSent;
                }
                catch { }
            }

            var now = DateTime.UtcNow;
            var seconds = (now - _prevNetworkAt).TotalSeconds;
            if (seconds > 0.2 && (_prevReceived != 0 || _prevSent != 0))
            {
                var downRate = Math.Max(0, (received - _prevReceived) / seconds);
                var upRate = Math.Max(0, (sent - _prevSent) / seconds);

                group.Items.Add(InfoItem.Of("performance.network", "Network",
                    $"↓ {FormatRate((ulong)downRate)} · ↑ {FormatRate((ulong)upRate)}", DetailLevel.Basic,
                    "Current download and upload rates across active adapters.", "Source: IPv4 statistics"));
            }

            _prevReceived = received;
            _prevSent = sent;
            _prevNetworkAt = now;
        }
        catch { }

        // ── GPU ─────────────────────────────────────────────────────────────────
        var engines = Wmi.Query("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
        if (engines.Count > 0)
        {
            var byType = engines
                .Where(e => e.Str("Name") is { Length: > 0 })
                .GroupBy(e => EngineType(e.Str("Name")!))
                .Select(g => new { Type = g.Key, Utilisation = g.Sum(x => (double)(x.ULong("UtilizationPercentage") ?? 0)) })
                .Where(g => g.Utilisation > 0)
                .OrderByDescending(g => g.Utilisation)
                .ToList();

            var total = byType.Sum(g => (double)g.Utilisation);
            group.Items.Add(InfoItem.Of("performance.gpu", "GPU",
                total <= 0 ? "idle" : $"{Math.Min(100, total):0}% · " + string.Join(", ", byType.Take(3).Select(g => $"{g.Type} {g.Utilisation}%")),
                DetailLevel.Basic, "Graphics engine utilisation summed over the adapters.",
                "Source: Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"));
        }

        // ── System ──────────────────────────────────────────────────────────────
        var processes = System.Diagnostics.Process.GetProcesses();
        group.Items.Add(InfoItem.Of("performance.processes", "Processes", processes.Length.ToString(), DetailLevel.Detailed,
            "How many processes are running.", "Source: Process API"));
        foreach (var process in processes) process.Dispose();

        group.Items.Add(InfoItem.Of("performance.uptime", "Uptime",
            PcFormat.Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64)), DetailLevel.Detailed,
            "How long Windows has been running.", "Source: system uptime counter"));

        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        return section;
    }

    private static string EngineType(string name)
    {
        // Instance names look like pid_1234_luid_0x00000000_0x0000C5E6_phys_0_eng_0_engtype_3D
        var marker = name.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? name[(marker + 8)..] : "GPU";
    }

    private static string FormatRate(ulong bytesPerSecond)
        => bytesPerSecond > 1_000_000 ? $"{bytesPerSecond / 1_000_000d:0.0} MB/s" : $"{bytesPerSecond / 1000d:0} KB/s";

    private static ulong ToTicks(FileTime time) => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);
}
