using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace AppLock.Services.SpeedMeter;

/// <summary>One reading of the optional system rows.</summary>
public sealed class SystemStats
{
    public double? CpuPercent { get; init; }
    public double? RamPercent { get; init; }
    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }
    public double? DiskPercent { get; init; }
    public double? GpuPercent { get; init; }
    public int? BatteryPercent { get; init; }

    public static SystemStats Empty { get; } = new();
}

/// <summary>
/// CPU via GetSystemTimes deltas, RAM via GlobalMemoryStatusEx, battery via GetSystemPowerStatus.
/// Disk and GPU come from Windows' formatted performance classes, queried only when enabled and
/// cached for a couple of ticks because those queries are the expensive part.
/// </summary>
public sealed class SystemStatsProvider
{
    public bool TrackCpu { get; set; }
    public bool TrackRam { get; set; }
    public bool TrackDisk { get; set; }
    public bool TrackGpu { get; set; }
    public bool TrackBattery { get; set; }

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuBaseline;

    private int _slowTick;
    private double? _cachedDisk;
    private double? _cachedGpu;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
        public ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public SystemStats Sample()
    {
        double? cpu = TrackCpu ? ReadCpu() : null;
        var (ramPercent, usedGb, totalGb) = TrackRam ? ReadRam() : (null, null, null);

        double? disk = null;
        double? gpu = null;

        if (TrackDisk || TrackGpu)
        {
            // The WMI performance classes cost a few dozen milliseconds; refresh them every 3rd tick.
            if (_slowTick++ % 3 == 0)
            {
                _cachedDisk = TrackDisk ? ReadDiskPercent() : null;
                _cachedGpu = TrackGpu ? ReadGpuPercent() : null;
            }

            disk = TrackDisk ? _cachedDisk : null;
            gpu = TrackGpu ? _cachedGpu : null;
        }

        var battery = TrackBattery ? ReadBatteryPercent() : null;

        return new SystemStats
        {
            CpuPercent = cpu,
            RamPercent = ramPercent,
            RamUsedGb = usedGb,
            RamTotalGb = totalGb,
            DiskPercent = disk,
            GpuPercent = gpu,
            BatteryPercent = battery
        };
    }

    /// <summary>Total CPU load from the idle/kernel/user times since the previous call.</summary>
    private double? ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

        var idleTime = idle.ToUInt64();
        var kernelTime = kernel.ToUInt64();
        var userTime = user.ToUInt64();

        if (!_hasCpuBaseline)
        {
            _prevIdle = idleTime;
            _prevKernel = kernelTime;
            _prevUser = userTime;
            _hasCpuBaseline = true;
            return null;
        }

        var idleDelta = idleTime - _prevIdle;
        var kernelDelta = kernelTime - _prevKernel;
        var userDelta = userTime - _prevUser;

        _prevIdle = idleTime;
        _prevKernel = kernelTime;
        _prevUser = userTime;

        // Kernel time already contains the idle time.
        var total = kernelDelta + userDelta;
        if (total == 0) return 0;

        var busy = total - idleDelta;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static (double? Percent, double? UsedGb, double? TotalGb) ReadRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (null, null, null);

        const double gb = 1024.0 * 1024 * 1024;
        var total = status.ullTotalPhys / gb;
        var used = (status.ullTotalPhys - status.ullAvailPhys) / gb;

        return (status.dwMemoryLoad, used, total);
    }

    private static double? ReadDiskPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PercentDiskTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);

            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                var value = item["PercentDiskTime"];
                if (value is not null)
                {
                    return Math.Clamp(Convert.ToDouble(value), 0, 100);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Disk stats unavailable: {ex.Message}");
        }

        return null;
    }

    private static double? ReadGpuPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);

            var total = 0.0;
            var found = false;

            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                var name = item["Name"]?.ToString() ?? string.Empty;
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = item["UtilizationPercentage"];
                if (value is null) continue;

                total += Convert.ToDouble(value);
                found = true;
            }

            if (found) return Math.Clamp(total, 0, 100);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] GPU stats unavailable: {ex.Message}");
        }

        return null;
    }

    private static int? ReadBatteryPercent()
    {
        if (!GetSystemPowerStatus(out var status)) return null;

        // 255 means "unknown"; a battery flag of 128 or 0 means there is no battery.
        if (status.BatteryLifePercent == 255) return null;
        if (status.BatteryFlag == 128 || status.BatteryFlag == 0) return null;

        return status.BatteryLifePercent;
    }
}
