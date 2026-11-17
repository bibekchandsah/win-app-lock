using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>Processor basics: name, vendor, cores/threads, clocks and caches.</summary>
public sealed class CpuProvider : IInfoProvider
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public string Id => "cpu";
    public string Category => "cpu";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(10);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Processor", Category = Category };
        var group = section.Group("Processor");
        var source = "Source: WMI Win32_Processor";

        var rows = Wmi.Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, ProcessorId, AddressWidth FROM Win32_Processor");
        var cpu = rows.Count > 0 ? rows[0] : new Dictionary<string, object?>();

        string? name = cpu.Str("Name") ?? PcRegistry.MachineString(CpuKey, "ProcessorNameString");
        string? vendor = cpu.Str("Manufacturer") ?? PcRegistry.MachineString(CpuKey, "VendorIdentifier");

        name = name?.Replace("(R)", string.Empty).Replace("(TM)", string.Empty).Replace("CPU ", string.Empty).Trim();
        name = name == null ? null : string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        group.Items.Add(InfoItem.Of("cpu.name", "Name", name, DetailLevel.Basic, "The model name of this processor.", source));
        group.Items.Add(InfoItem.Of("cpu.vendor", "Vendor", vendor, DetailLevel.Detailed, "Who made the processor.", source));

        int? cores = cpu.Int("NumberOfCores");
        int? threads = cpu.Int("NumberOfLogicalProcessors");
        group.Items.Add(cores is > 0
            ? InfoItem.Of("cpu.cores", "Physical cores", cores.ToString(), DetailLevel.Basic,
                "The number of physical cores. Each can run work independently of the others.", source)
            : InfoItem.MissingValue("cpu.cores", "Physical cores", MissingReason.NotReported, DetailLevel.Basic, source));

        group.Items.Add(threads is > 0
            ? InfoItem.Of("cpu.threads", "Logical processors", threads.ToString(), DetailLevel.Basic,
                "How many threads Windows can run at the same time (with Hyper-Threading or SMT this is higher than the core count).", source)
            : InfoItem.Of("cpu.threads", "Logical processors", Environment.ProcessorCount.ToString(), DetailLevel.Basic,
                "How many threads Windows can run at the same time.", "Source: Windows processor count"));

        int? maxClock = cpu.Int("MaxClockSpeed");
        int? baseClock = maxClock;
        int? currentClock = cpu.Int("CurrentClockSpeed");
        group.Items.Add(maxClock is > 0
            ? InfoItem.Of("cpu.maxClock", "Clock speed", $"{maxClock / 1000d:0.00} GHz", DetailLevel.Basic,
                "The clock speed Windows reports for this processor.", source)
            : InfoItem.MissingValue("cpu.maxClock", "Clock speed", MissingReason.NotReported, DetailLevel.Basic, source));

        if (baseClock is > 0)
        {
            group.Items.Add(InfoItem.Of("cpu.baseClock", "Base clock (reported)", $"{baseClock / 1000d:0.00} GHz", DetailLevel.Detailed,
                "Windows reports the rated clock speed here; boost clocks are typically higher.", source));
        }

        if (currentClock is > 0)
        {
            group.Items.Add(InfoItem.Of("cpu.currentClock", "Current clock (reported)", $"{currentClock / 1000d:0.00} GHz", DetailLevel.Detailed,
                "The clock Windows reports right now. It changes constantly, so treat it as a snapshot.", source));
        }

        int? l2 = cpu.Int("L2CacheSize");
        int? l3 = cpu.Int("L3CacheSize");
        if (l2 is > 0)
        {
            group.Items.Add(InfoItem.Of("cpu.l2", "L2 cache", PcFormat.Size((ulong)l2.Value * 1024), DetailLevel.Detailed,
                "Fast memory shared by groups of cores.", source));
        }

        if (l3 is > 0)
        {
            group.Items.Add(InfoItem.Of("cpu.l3", "L3 cache", PcFormat.Size((ulong)l3.Value * 1024), DetailLevel.Detailed,
                "The last-level cache shared by all cores. Bigger caches help games and heavy apps.", source));
        }

        group.Items.Add(InfoItem.Of("cpu.socket", "Socket", cpu.Str("SocketDesignation"), DetailLevel.Detailed,
            "The physical socket the processor sits in.", source));

        int? width = cpu.Int("AddressWidth");
        group.Items.Add(InfoItem.Of("cpu.addressWidth", "Address width", width is > 0 ? $"{width}-bit" : null, DetailLevel.Everything,
            "How wide the processor's memory addresses are.", source));

        group.Items.Add(InfoItem.Of("cpu.id", "Processor ID", cpu.Str("ProcessorId"), DetailLevel.Everything,
            "An identifier reported by the processor.", source, sensitivity: InfoSensitivity.Identifier));

        progress?.Report(1);
        return section;
    }
}
