using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>Memory summary: installed vs usable, how much is in use, and how many modules are fitted.</summary>
public sealed class MemoryProvider : IInfoProvider
{
    public string Id => "memory";
    public string Category => "memory";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);

    /// <summary>Usage changes every second, so nothing here is cached.</summary>
    public TimeSpan CacheLifetime => TimeSpan.Zero;

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Memory", Category = Category };
        var group = section.Group("Memory");

        var system = Wmi.Query("SELECT TotalPhysicalMemory, NumberOfProcessors FROM Win32_ComputerSystem");
        var os = Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        var modules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, FormFactor, SMBIOSMemoryType FROM Win32_PhysicalMemory");

        ulong installed = system.Count > 0 ? system[0].ULong("TotalPhysicalMemory") ?? 0 : 0;

        ulong usable = 0;
        ulong free = 0;
        if (os.Count > 0)
        {
            var visibleKb = os[0].ULong("TotalVisibleMemorySize") ?? 0;
            var freeKb = os[0].ULong("FreePhysicalMemory") ?? 0;
            usable = visibleKb * 1024;
            free = freeKb * 1024;
        }

        group.Items.Add(installed > 0
            ? InfoItem.Of("memory.installed", "Installed", PcFormat.Size(installed), DetailLevel.Basic,
                "The total physical memory fitted in this PC.", "Source: WMI Win32_ComputerSystem")
            : InfoItem.MissingValue("memory.installed", "Installed", MissingReason.NotReported, DetailLevel.Basic));

        group.Items.Add(usable > 0
            ? InfoItem.Of("memory.usable", "Usable by Windows", PcFormat.Size(usable), DetailLevel.Basic,
                "The part of the installed memory Windows can use. Some is reserved by the hardware.", "Source: WMI Win32_OperatingSystem")
            : InfoItem.MissingValue("memory.usable", "Usable by Windows", MissingReason.NotReported, DetailLevel.Basic));

        if (usable > 0)
        {
            var inUse = usable - free;
            var usedPercent = usable == 0 ? 0 : (double)inUse / usable * 100;

            var usedItem = InfoItem.Of("memory.inUse", "In use",
                $"{PcFormat.Size(inUse)} ({PcFormat.Percent(usedPercent)})", DetailLevel.Basic,
                "How much memory is in use right now, with apps and Windows itself.", "Source: WMI Win32_OperatingSystem");
            usedItem.Status = usedPercent >= 90 ? InfoStatus.Warning : InfoStatus.None;
            group.Items.Add(usedItem);

            group.Items.Add(InfoItem.Of("memory.available", "Available", PcFormat.Size(free), DetailLevel.Basic,
                "Memory that is free for apps to use right now.", "Source: WMI Win32_OperatingSystem"));

            if (installed > usable && installed - usable > 0)
            {
                group.Items.Add(InfoItem.Of("memory.hardwareReserved", "Hardware reserved", PcFormat.Size(installed - usable), DetailLevel.Detailed,
                    "Installed memory the hardware keeps for itself, typically the integrated graphics."));
            }
        }

        group.Items.Add(InfoItem.Of("memory.modules", "Modules fitted",
            modules.Count > 0 ? modules.Count.ToString(CultureInfo.CurrentCulture) : null, DetailLevel.Detailed,
            "How many physical memory sticks are installed. Full per-module details come with the memory milestone.",
            "Source: WMI Win32_PhysicalMemory"));

        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        return section;
    }
}
