using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace AppLock.Services.SpeedMeter;

/// <summary>One process on the top-app ticker: the value shown and the text to display.</summary>
public sealed class TopAppStat
{
    public string Name { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>The busiest process per metric, as shown by the widget's scrolling line.</summary>
public sealed class TopAppsStats
{
    public TopAppStat? Cpu { get; init; }
    public TopAppStat? Ram { get; init; }
    public TopAppStat? Disk { get; init; }
    public TopAppStat? Gpu { get; init; }

    public static TopAppsStats Empty { get; } = new();
}

/// <summary>
/// Finds the process using the most CPU, memory, disk and GPU. CPU and disk are deltas between
/// two samples; RAM is the working set; GPU comes from Windows' per-engine performance counters
/// (instance names carry the owning pid). Per-app network usage needs ETW, so it is not reported.
/// </summary>
public sealed class TopAppsProvider
{
    private sealed class Snapshot
    {
        public TimeSpan CpuTime;
        public ulong DiskBytes;
        public long WorkingSet;
        public string Name = string.Empty;
    }

    private readonly Dictionary<int, Snapshot> _previous = new();
    private long _previousTimestamp;
    private int _tick;
    private IReadOnlyList<(int Pid, double Percent)> _gpuByPid = Array.Empty<(int, double)>();

    private const int GpuRefreshTicks = 3;

    public TopAppsStats Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _previousTimestamp == 0
            ? 0
            : (now - _previousTimestamp) / (double)Stopwatch.Frequency;

        _previousTimestamp = now;

        var current = new Dictionary<int, Snapshot>(64);
        var currentPids = new HashSet<int>();

        foreach (var process in SafeProcesses())
        {
            try
            {
                if (process.HasExited) continue;

                var id = process.Id;
                if (id <= 4) continue;

                var snapshot = new Snapshot
                {
                    CpuTime = process.TotalProcessorTime,
                    WorkingSet = process.WorkingSet64,
                    Name = process.ProcessName
                };

                snapshot.DiskBytes = ReadDiskBytes(process);

                current[id] = snapshot;
                currentPids.Add(id);
            }
            catch
            {
                // Process ended or access denied while sampling; it simply does not take part.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (++_tick % GpuRefreshTicks == 0)
        {
            _gpuByPid = ReadGpuByPid();
        }

        var result = Build(current, elapsed, currentPids);

        _previous.Clear();
        foreach (var pair in current) _previous[pair.Key] = pair.Value;

        return result;
    }

    private TopAppsStats Build(Dictionary<int, Snapshot> current, double elapsedSeconds, HashSet<int> pids)
    {
        TopAppStat? cpu = null;

        if (elapsedSeconds > 0.05)
        {
            var cores = Math.Max(1, Environment.ProcessorCount);
            double bestValue = 0;

            foreach (var (id, snapshot) in current)
            {
                if (!_previous.TryGetValue(id, out var previous)) continue;

                var delta = (snapshot.CpuTime - previous.CpuTime).TotalSeconds;
                var percent = Math.Clamp(delta / (elapsedSeconds * cores) * 100, 0, 100);
                if (percent <= bestValue) continue;

                bestValue = percent;
                cpu = new TopAppStat { Name = snapshot.Name, Value = percent, Text = $"{snapshot.Name} {percent:F0}%" };
            }
        }

        TopAppStat? disk = null;

        foreach (var (id, snapshot) in current)
        {
            if (!_previous.TryGetValue(id, out var previous)) continue;

            var bytes = snapshot.DiskBytes > previous.DiskBytes ? snapshot.DiskBytes - previous.DiskBytes : 0;
            if (bytes <= 0) continue;

            var perSecond = bytes / Math.Max(0.2, elapsedSeconds);
            if (disk is not null && perSecond <= disk.Value) continue;

            var text = perSecond >= 1024 * 1024
                ? $"{perSecond / (1024 * 1024):F1} MB/s"
                : $"{perSecond / 1024:F0} KB/s";

            disk = new TopAppStat { Name = snapshot.Name, Value = perSecond, Text = $"{snapshot.Name} {text}" };
        }

        var ramProcess = current.OrderByDescending(pair => pair.Value.WorkingSet).FirstOrDefault();
        TopAppStat? ram = ramProcess.Value is null
            ? null
            : new TopAppStat
            {
                Name = ramProcess.Value.Name,
                Value = ramProcess.Value.WorkingSet,
                Text = $"{ramProcess.Value.Name} {ramProcess.Value.WorkingSet / (1024d * 1024d):F0} MB"
            };

        TopAppStat? gpu = null;
        foreach (var (pid, percent) in _gpuByPid)
        {
            if (!pids.Contains(pid) || percent <= 0) continue;
            if (gpu is not null && percent <= gpu.Value) continue;

            var name = current.TryGetValue(pid, out var snapshot) ? snapshot.Name : $"pid {pid}";
            gpu = new TopAppStat { Name = name, Value = percent, Text = $"{name} {percent:F0}%" };
        }

        return new TopAppsStats { Cpu = cpu, Ram = ram, Disk = disk, Gpu = gpu };
    }

    /// <summary>Disk bytes read + written by a process so far (kernel counters).</summary>
    private static ulong ReadDiskBytes(Process process)
    {
        try
        {
            if (!GetProcessIoCounters(process.Handle, out var counters)) return 0;
            return counters.ReadTransferCount + counters.WriteTransferCount;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>GPU utilisation per pid from the per-engine GPU performance counters.</summary>
    private static IReadOnlyList<(int Pid, double Percent)> ReadGpuByPid()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

            searcher.Options.Timeout = TimeSpan.FromSeconds(2);

            var totals = new Dictionary<int, double>();

            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                var name = item["Name"]?.ToString() ?? string.Empty;
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pidIndex = name.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                if (pidIndex < 0) continue;

                var digits = new string(name[(pidIndex + 4)..].TakeWhile(char.IsDigit).ToArray());
                if (!int.TryParse(digits, out var pid)) continue;

                if (item["UtilizationPercentage"] is null) continue;
                var value = Convert.ToDouble(item["UtilizationPercentage"]);

                totals[pid] = totals.TryGetValue(pid, out var existing) ? existing + value : value;
            }

            return totals.Select(pair => (pair.Key, pair.Value)).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Top GPU app unavailable: {ex.Message}");
            return Array.Empty<(int, double)>();
        }
    }

    private static IEnumerable<Process> SafeProcesses()
    {
        try
        {
            return Process.GetProcesses();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Process enumeration failed: {ex.Message}");
            return Array.Empty<Process>();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IO_COUNTERS counters);
}
