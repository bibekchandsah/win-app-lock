using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using AppLock.Services.PcChecker.Parsers;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Memory: totals and live usage, plus every memory module with its slot, capacity, type, rated vs
/// configured speed, part number and the channel / memory-profile hints.
/// </summary>
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

    private sealed class ModuleInfo
    {
        public string Locator = string.Empty;
        public string Bank = string.Empty;
        public ulong CapacityBytes;
        public string TypeName = "Unknown";
        public string FormFactor = "Unknown";
        public int RatedSpeed;
        public int ConfiguredSpeed;
        public string Manufacturer = string.Empty;
        public string PartNumber = string.Empty;
        public string Serial = string.Empty;
        public int Rank;
        public int DataWidth;
        public int TotalWidth;
        public int VoltageMillivolts;
    }

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Memory", Category = Category };
        var group = section.Group("Memory");

        var system = Wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
        var os = Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        var smbios = SmbiosParser.GetTable();
        var modules = ReadModules(smbios);
        var fallbackModules = modules.Count == 0 ? ReadWmiModules() : modules;

        ulong installed = system.Count > 0 ? system[0].ULong("TotalPhysicalMemory") ?? 0 : 0;
        if (installed == 0 && fallbackModules.Count > 0)
        {
            installed = (ulong)fallbackModules.Sum(m => (long)m.CapacityBytes);
        }

        ulong usable = 0;
        ulong free = 0;
        if (os.Count > 0)
        {
            usable = (os[0].ULong("TotalVisibleMemorySize") ?? 0) * 1024;
            free = (os[0].ULong("FreePhysicalMemory") ?? 0) * 1024;
        }

        group.Items.Add(installed > 0
            ? InfoItem.Of("memory.installed", "Installed", PcFormat.Size(installed), DetailLevel.Basic,
                "The total physical memory fitted in this PC.", "Source: WMI Win32_ComputerSystem")
            : InfoItem.MissingValue("memory.installed", "Installed", MissingReason.NotReported, DetailLevel.Basic));

        group.Items.Add(usable > 0
            ? InfoItem.Of("memory.usable", "Usable by Windows", PcFormat.Size(usable), DetailLevel.Basic,
                "The part of the installed memory Windows can use. Some is reserved by the hardware.",
                "Source: WMI Win32_OperatingSystem")
            : InfoItem.MissingValue("memory.usable", "Usable by Windows", MissingReason.NotReported, DetailLevel.Basic));

        if (usable > 0)
        {
            var inUse = usable - free;
            var usedPercent = (double)inUse / usable * 100;

            var usedItem = InfoItem.Of("memory.inUse", "In use",
                $"{PcFormat.Size(inUse)} ({PcFormat.Percent(usedPercent)})", DetailLevel.Basic,
                "How much memory is in use right now, with apps and Windows itself.",
                "Source: WMI Win32_OperatingSystem");
            usedItem.Status = usedPercent >= 90 ? InfoStatus.Warning : InfoStatus.None;
            group.Items.Add(usedItem);

            group.Items.Add(InfoItem.Of("memory.available", "Available", PcFormat.Size(free), DetailLevel.Basic,
                "Memory that is free for apps to use right now.", "Source: WMI Win32_OperatingSystem"));

            if (installed > usable && installed - usable > 0)
            {
                group.Items.Add(InfoItem.Of("memory.hardwareReserved", "Hardware reserved", PcFormat.Size(installed - usable),
                    DetailLevel.Detailed, "Installed memory the hardware keeps for itself, typically the integrated graphics."));
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.4);

        // ── Slots and configuration ─────────────────────────────────────────────
        var slotGroup = section.Group("Slots and configuration");
        int? slotCount = null;
        ulong? maxCapacity = null;
        var array = smbios?.First(16);
        if (array != null)
        {
            var slots = array.WordAt(0x0D);
            if (slots > 0) slotCount = slots;

            var maximumKb = array.DwordAt(0x07);
            if (maximumKb > 0 && maximumKb != 0x80000000) maxCapacity = (ulong)maximumKb * 1024;
        }

        var usedSlots = fallbackModules.Count;
        if (slotCount is > 0)
        {
            slotGroup.Items.Add(InfoItem.Of("memory.slotUsage", "Slots used",
                $"{usedSlots} of {slotCount}", DetailLevel.Basic,
                "How many memory slots are occupied, and how many the board has.",
                "Source: SMBIOS physical memory array"));
            if (slotCount - usedSlots > 0)
            {
                slotGroup.Items.Add(InfoItem.Of("memory.freeSlots", "Free slots",
                    (slotCount - usedSlots).ToString(), DetailLevel.Detailed,
                    "Empty slots you could add memory to later.",
                    "Source: SMBIOS physical memory array", InfoStatus.Info));
            }
        }

        if (maxCapacity is > 0)
        {
            slotGroup.Items.Add(InfoItem.Of("memory.maxCapacity", "Maximum supported", PcFormat.Size(maxCapacity.Value),
                DetailLevel.Detailed, "The most memory this motherboard supports according to its firmware.",
                "Source: SMBIOS physical memory array"));
        }

        if (fallbackModules.Count > 0)
        {
            var channels = EstimatedChannels(fallbackModules);
            var channelItem = InfoItem.Of("memory.channels", "Channel layout", channels,
                DetailLevel.Detailed,
                "Running memory in dual channel roughly doubles the bandwidth the processor can use.",
                "Estimated from module capacities and slot names");
            if (channels.StartsWith("Single", StringComparison.OrdinalIgnoreCase) && fallbackModules.Count >= 2)
                channelItem.Status = InfoStatus.Warning;
            slotGroup.Items.Add(channelItem);

            var slowestRated = fallbackModules.Where(m => m.RatedSpeed > 0).Select(m => m.RatedSpeed).DefaultIfEmpty(0).Max();
            var slowestConfigured = fallbackModules.Where(m => m.ConfiguredSpeed > 0).Select(m => m.ConfiguredSpeed).DefaultIfEmpty(0).Min();

            if (slowestRated > 0 && slowestConfigured > 0 && slowestConfigured < slowestRated)
            {
                slotGroup.Items.Add(InfoItem.Of("memory.profile", "Rated vs configured speed",
                    $"{slowestRated} MHz rated, running at {slowestConfigured} MHz", DetailLevel.Detailed,
                    "Memory is running below its rated speed. On desktop PCs this usually means the XMP / EXPO profile is off in the BIOS; laptops often cap the speed by design.",
                    "Source: SMBIOS memory device", InfoStatus.Info));
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.7);

        // ── Modules ─────────────────────────────────────────────────────────────
        var moduleGroup = section.Group("Modules");
        if (fallbackModules.Count == 0)
        {
            moduleGroup.Items.Add(InfoItem.MissingValue("memory.modules", "Modules", MissingReason.NotReported, DetailLevel.Basic));
        }

        for (int i = 0; i < fallbackModules.Count; i++)
        {
            var module = fallbackModules[i];
            var name = string.IsNullOrWhiteSpace(module.Locator) ? $"Module {i + 1}" : module.Locator;

            var facts = new List<string>
            {
                PcFormat.Size(module.CapacityBytes),
                module.TypeName
            };
            if (module.ConfiguredSpeed > 0) facts.Add($"{module.ConfiguredSpeed} MHz");
            else if (module.RatedSpeed > 0) facts.Add($"{module.RatedSpeed} MHz");
            if (!string.IsNullOrWhiteSpace(module.Manufacturer)) facts.Add(module.Manufacturer.Trim());

            moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.summary", name, string.Join(" · ", facts), DetailLevel.Basic,
                "One installed memory module.", "Source: SMBIOS memory device"));

            moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.locator", $"{name} slot", module.Locator, DetailLevel.Detailed,
                "Where the module sits on the board.", "Source: SMBIOS memory device"));
            moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.bank", $"{name} bank", module.Bank, DetailLevel.Everything,
                null, "Source: SMBIOS memory device"));
            moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.type", $"{name} type",
                $"{module.TypeName} {module.FormFactor}".Trim(), DetailLevel.Detailed,
                "The memory generation and whether it is a DIMM (desktop) or SODIMM (laptop).",
                "Source: SMBIOS memory device"));

            if (module.RatedSpeed > 0)
            {
                moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.rated", $"{name} rated speed",
                    $"{module.RatedSpeed} MHz", DetailLevel.Detailed, "The speed the module was designed for.",
                    "Source: SMBIOS memory device"));
            }
            if (module.ConfiguredSpeed > 0)
            {
                moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.configured", $"{name} configured speed",
                    $"{module.ConfiguredSpeed} MHz", DetailLevel.Detailed, "The speed it is actually running at.",
                    "Source: SMBIOS memory device"));
            }

            if (!string.IsNullOrWhiteSpace(module.PartNumber))
            {
                moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.part", $"{name} part number",
                    module.PartNumber.Trim(), DetailLevel.Detailed,
                    "The manufacturer's part number, useful when matching sticks.",
                    "Source: SMBIOS memory device"));
            }

            if (module.Rank > 0)
            {
                moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.rank", $"{name} rank",
                    $"{module.Rank}R", DetailLevel.Everything, null, "Source: SMBIOS memory device"));
            }

            if (module.VoltageMillivolts > 0)
            {
                moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.voltage", $"{name} voltage",
                    $"{module.VoltageMillivolts / 1000d:0.000} V", DetailLevel.Everything, null,
                    "Source: SMBIOS memory device"));
            }

            if (module.TotalWidth > 0)
            {
                var ecc = module.TotalWidth > module.DataWidth ? " (ECC)" : string.Empty;
                moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.width", $"{name} width",
                    $"{module.DataWidth} of {module.TotalWidth} bits{ecc}", DetailLevel.Everything,
                    "The data width of the module; a wider total width means error-correcting memory.",
                    "Source: SMBIOS memory device"));
            }

            moduleGroup.Items.Add(InfoItem.Of($"memory.module.{i}.serial", $"{name} serial", module.Serial,
                DetailLevel.Everything, null, "Source: SMBIOS memory device", sensitivity: InfoSensitivity.Identifier));
        }

        progress?.Report(1);
        return section;
    }

    private static string EstimatedChannels(List<ModuleInfo> modules)
    {
        if (modules.Count == 0) return "Unknown";
        if (modules.Count == 1) return "Single channel";

        var locators = modules.Select(m => m.Locator).ToList();
        bool locatorsSuggestChannels = locators.Any(l => l.Contains("ChannelA", StringComparison.OrdinalIgnoreCase))
                                       || locators.Any(l => l.Contains("ChannelB", StringComparison.OrdinalIgnoreCase));

        var capacities = modules.Select(m => m.CapacityBytes).Where(c => c > 0).Distinct().Count();
        if (modules.Count >= 2 && capacities == 1 && (locatorsSuggestChannels || modules.Count >= 2))
        {
            return modules.Count >= 4 ? "Dual channel (4 modules, estimated)" : "Dual channel (estimated)";
        }

        return "Single channel (estimated)";
    }

    private static List<ModuleInfo> ReadModules(SmbiosTable? table)
    {
        var result = new List<ModuleInfo>();
        if (table == null) return result;

        foreach (var device in table.OfType(17))
        {
            var sizeField = device.WordAt(0x0C);
            ulong bytes;
            if (sizeField == 0xFFFF)
            {
                bytes = (ulong)device.DwordAt(0x1C) * 1024UL * 1024UL; // Extended size is in MB
            }
            else if (sizeField == 0x7FFF || sizeField == 0)
            {
                continue; // Slot is empty or the size is unknown
            }
            else
            {
                bytes = (ulong)sizeField;
                if (bytes >= 0x8000)
                {
                    // Bit 15 means the value is in kilobytes.
                    bytes = (ulong)(sizeField & 0x7FFF) * 1024UL;
                }
                else
                {
                    bytes *= 1024UL * 1024UL; // MB
                }
            }

            if (bytes == 0) continue;

            result.Add(new ModuleInfo
            {
                Locator = device.StringAt(0x10),
                Bank = device.StringAt(0x11),
                CapacityBytes = bytes,
                TypeName = SmbiosParser.MemoryTypeName(device.ByteAt(0x12)),
                FormFactor = SmbiosParser.FormFactorName(device.ByteAt(0x0E)),
                RatedSpeed = device.WordAt(0x15),
                ConfiguredSpeed = device.WordAt(0x20),
                Manufacturer = device.StringAt(0x17),
                PartNumber = device.StringAt(0x1A),
                Serial = device.StringAt(0x18),
                Rank = device.ByteAt(0x1B) & 0x0F,
                DataWidth = device.WordAt(0x0A),
                TotalWidth = device.WordAt(0x08),
                VoltageMillivolts = device.WordAt(0x26)
            });
        }

        return result;
    }

    private static List<ModuleInfo> ReadWmiModules()
    {
        var result = new List<ModuleInfo>();
        var rows = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, BankLabel, SMBIOSMemoryType, FormFactor, SerialNumber FROM Win32_PhysicalMemory");

        foreach (var row in rows)
        {
            var capacity = row.ULong("Capacity") ?? 0;
            if (capacity == 0) continue;

            result.Add(new ModuleInfo
            {
                Locator = row.Str("DeviceLocator") ?? string.Empty,
                Bank = row.Str("BankLabel") ?? string.Empty,
                CapacityBytes = capacity,
                TypeName = SmbiosParser.MemoryTypeName((byte)(row.Int("SMBIOSMemoryType") ?? 0)),
                FormFactor = SmbiosParser.FormFactorName((byte)(row.Int("FormFactor") ?? 0)),
                RatedSpeed = row.Int("Speed") ?? 0,
                ConfiguredSpeed = row.Int("ConfiguredClockSpeed") ?? 0,
                Manufacturer = row.Str("Manufacturer") ?? string.Empty,
                PartNumber = row.Str("PartNumber") ?? string.Empty,
                Serial = row.Str("SerialNumber") ?? string.Empty
            });
        }

        return result;
    }
}
