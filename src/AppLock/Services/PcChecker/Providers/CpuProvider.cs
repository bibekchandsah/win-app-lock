using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Processor details: model, topology (cores, hybrid classes, NUMA), caches, clocks, socket,
/// microcode and the instruction sets this CPU reports through CPUID.
/// </summary>
public sealed class CpuProvider : IInfoProvider
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    private const int RelationProcessorCore = 0;
    private const int RelationNumaNode = 1;
    private const int RelationCache = 2;
    private const int RelationProcessorPackage = 3;
    private const int RelationAll = 0xFFFF;

    public string Id => "cpu";
    public string Category => "cpu";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(30);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Processor", Category = Category };
        var group = section.Group("Processor");
        const string wmiSource = "Source: WMI Win32_Processor";

        var rows = Wmi.Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, " +
                             "L2CacheSize, L3CacheSize, SocketDesignation, ProcessorId, AddressWidth, Architecture, " +
                             "VirtualizationFirmwareEnabled, VMMonitorModeExtensions, SecondLevelAddressTranslationExtensions, " +
                             "NumberOfEnabledCore, NumberOfCores, UniqueId FROM Win32_Processor");
        var cpu = rows.Count > 0 ? rows[0] : new Dictionary<string, object?>();

        string? name = Clean(cpu.Str("Name") ?? PcRegistry.MachineString(CpuKey, "ProcessorNameString"));
        string? vendor = cpu.Str("Manufacturer") ?? PcRegistry.MachineString(CpuKey, "VendorIdentifier");
        var topology = ReadTopology();

        group.Items.Add(InfoItem.Of("cpu.name", "Name", name, DetailLevel.Basic, "The model name of this processor.", wmiSource));
        group.Items.Add(InfoItem.Of("cpu.vendor", "Vendor", vendor, DetailLevel.Detailed, "Who made the processor.", wmiSource));

        int? cores = topology.PhysicalCores > 0 ? topology.PhysicalCores : cpu.Int("NumberOfCores");
        int threads = topology.LogicalProcessors > 0 ? topology.LogicalProcessors : cpu.Int("NumberOfLogicalProcessors") ?? Environment.ProcessorCount;

        group.Items.Add(cores is > 0
            ? InfoItem.Of("cpu.cores", "Physical cores", cores.ToString(), DetailLevel.Basic,
                "The number of physical cores. Each can run work independently of the others.",
                topology.PhysicalCores > 0 ? "Source: GetLogicalProcessorInformationEx" : wmiSource)
            : InfoItem.MissingValue("cpu.cores", "Physical cores", MissingReason.NotReported, DetailLevel.Basic, wmiSource));

        group.Items.Add(InfoItem.Of("cpu.threads", "Logical processors", threads.ToString(), DetailLevel.Basic,
            "How many threads Windows can run at once. With Hyper-Threading or SMT this is higher than the core count.",
            topology.LogicalProcessors > 0 ? "Source: GetLogicalProcessorInformationEx" : wmiSource));

        if (topology.Hybrid)
        {
            group.Items.Add(InfoItem.Of("cpu.hybrid", "Core layout",
                $"{topology.PerformanceCores} performance · {topology.EfficiencyCores} efficiency", DetailLevel.Basic,
                "Hybrid processors mix fast performance cores with efficient cores that use less power.",
                "Source: GetLogicalProcessorInformationEx (efficiency class)"));
        }

        int? maxClock = cpu.Int("MaxClockSpeed");
        if (maxClock is > 0)
        {
            group.Items.Add(InfoItem.Of("cpu.maxClock", "Clock speed", $"{maxClock / 1000d:0.00} GHz", DetailLevel.Basic,
                "The rated clock speed Windows reports. Boost clocks are usually higher.", wmiSource));
        }

        var identifier = PcRegistry.MachineString(CpuKey, "Identifier");
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            group.Items.Add(InfoItem.Of("cpu.identifier", "Family / model / stepping", identifier.Trim(), DetailLevel.Detailed,
                "The processor's family, model and stepping, useful when checking compatibility or updates.",
                "Source: registry HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"));
        }

        group.Items.Add(InfoItem.Of("cpu.socket", "Socket", cpu.Str("SocketDesignation"), DetailLevel.Detailed,
            "The physical socket the processor sits in.", wmiSource));

        int? width = cpu.Int("AddressWidth");
        group.Items.Add(InfoItem.Of("cpu.addressWidth", "Address width", width is > 0 ? $"{width}-bit" : null, DetailLevel.Everything,
            "How wide the processor's memory addresses are.", wmiSource));

        var microcode = ReadMicrocodeRevision();
        group.Items.Add(microcode != null
            ? InfoItem.Of("cpu.microcode", "Microcode revision", microcode, DetailLevel.Everything,
                "The firmware revision of the processor. Windows updates it along with the BIOS.",
                "Source: registry HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0")
            : InfoItem.MissingValue("cpu.microcode", "Microcode revision", MissingReason.NotReported, DetailLevel.Everything));

        group.Items.Add(InfoItem.Of("cpu.id", "Processor ID", cpu.Str("ProcessorId"), DetailLevel.Everything,
            "An identifier reported by the processor.", wmiSource, sensitivity: InfoSensitivity.Identifier));

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.4);

        // ── Caches ──────────────────────────────────────────────────────────────
        var cacheGroup = section.Group("Caches");
        if (topology.Caches.Count > 0)
        {
            const string cacheSource = "Source: GetLogicalProcessorInformationEx";
            foreach (var cache in topology.Caches.OrderBy(c => c.Level).ThenBy(c => c.TypeName))
            {
                cacheGroup.Items.Add(InfoItem.Of($"cpu.cache.{cache.Level}.{cache.TypeName}", $"{cache.TypeName} cache",
                    PcFormat.Size(cache.TotalBytes), DetailLevel.Basic,
                    cache.Level == 3 ? "The last-level cache, shared by all cores. Bigger caches help games and heavy apps."
                        : "Fast memory inside the processor, much quicker than RAM.",
                    cacheSource));
            }
        }
        else
        {
            int? l2 = cpu.Int("L2CacheSize");
            int? l3 = cpu.Int("L3CacheSize");
            if (l2 is > 0)
                cacheGroup.Items.Add(InfoItem.Of("cpu.cache.l2", "L2 cache", PcFormat.Size((ulong)l2.Value * 1024), DetailLevel.Basic, null, wmiSource));
            if (l3 is > 0)
                cacheGroup.Items.Add(InfoItem.Of("cpu.cache.l3", "L3 cache", PcFormat.Size((ulong)l3.Value * 1024), DetailLevel.Basic, null, wmiSource));
            if (l2 is null && l3 is null)
                cacheGroup.Items.Add(InfoItem.MissingValue("cpu.cache", "Cache", MissingReason.NotReported, DetailLevel.Basic));
        }

        if (topology.NumaNodes > 0)
        {
            cacheGroup.Items.Add(InfoItem.Of("cpu.numa", "NUMA nodes", topology.NumaNodes.ToString(), DetailLevel.Everything,
                "Groups of cores with their own fast memory. Servers use them to keep memory access local.",
                "Source: GetLogicalProcessorInformationEx"));
        }

        if (topology.Packages > 0)
        {
            cacheGroup.Items.Add(InfoItem.Of("cpu.packages", "Processor packages", topology.Packages.ToString(), DetailLevel.Detailed,
                "How many physical processor packages are installed.", "Source: GetLogicalProcessorInformationEx"));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.7);

        // ── Features ────────────────────────────────────────────────────────────
        var featureGroup = section.Group("Features and virtualisation");
        foreach (var (label, supported) in ReadInstructionSets())
        {
            featureGroup.Items.Add(InfoItem.Of($"cpu.feature.{label.Replace(' ', '_')}", label,
                supported ? "Supported" : "Not supported", DetailLevel.Detailed,
                null, "Source: CPUID"));
        }

        featureGroup.Items.Add(StatusItem("cpu.vtFirmware", "Virtualisation enabled in firmware",
            BoolText(cpu.Str("VirtualizationFirmwareEnabled")),
            "Whether virtual machines (Hyper-V, WSL 2, Docker) can use the processor's virtualisation support.", wmiSource));

        featureGroup.Items.Add(StatusItem("cpu.slat", "SLAT (second-level address translation)",
            BoolText(cpu.Str("SecondLevelAddressTranslationExtensions")),
            "Hardware support that Hyper-V and Windows Sandbox need.", wmiSource));

        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        return section;
    }

    private static InfoItem StatusItem(string key, string label, string? value, string explain, string source)
    {
        var item = InfoItem.Of(key, label, value, DetailLevel.Detailed, explain, source);
        if (value == "Yes") item.Status = InfoStatus.Ok;
        return item;
    }

    private static string? BoolText(string? raw) => raw switch
    {
        "True" or "true" => "Yes",
        "False" or "false" => "No",
        _ => raw
    };

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = name.Replace("(R)", string.Empty).Replace("(TM)", string.Empty).Replace("CPU ", string.Empty).Trim();
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? ReadMicrocodeRevision()
    {
        try
        {
            var bytes = PcRegistry.ReadBytes(Microsoft.Win32.RegistryHive.LocalMachine, CpuKey, "Update Revision");
            if (bytes == null || bytes.Length == 0) return null;

            // The value is a little-endian QWORD; the revision is the low DWORD.
            uint revision = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
            return revision == 0 ? null : $"0x{revision:X8}";
        }
        catch
        {
            return null;
        }
    }

    // ── Topology ───────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    private sealed record CacheInfo(int Level, string TypeName, ulong TotalBytes);

    private sealed class Topology
    {
        public int PhysicalCores;
        public int LogicalProcessors;
        public int Packages;
        public int NumaNodes;
        public int PerformanceCores;
        public int EfficiencyCores;
        public List<CacheInfo> Caches = new();
        public bool Hybrid => PerformanceCores > 0 && EfficiencyCores > 0;
    }

    private static Topology ReadTopology()
    {
        var topology = new Topology();

        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref length);
            if (length == 0) return topology;

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length)) return topology;

                var cacheSizes = new Dictionary<(int Level, int Type), ulong>();
                int offset = 0;

                while (offset + 8 <= length)
                {
                    int relationship = Marshal.ReadInt32(buffer, offset);
                    int size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size < 8) break;

                    int payload = offset + 8;

                    switch (relationship)
                    {
                        case RelationProcessorCore:
                        {
                            topology.PhysicalCores++;
                            byte efficiencyClass = Marshal.ReadByte(buffer, payload + 1);
                            if (efficiencyClass == 0) topology.PerformanceCores++;
                            else topology.EfficiencyCores++;

                            int groupCount = Marshal.ReadInt16(buffer, payload + 22);
                            int maskOffset = payload + 24;
                            for (int g = 0; g < groupCount && maskOffset + 8 <= offset + size; g++)
                            {
                                ulong mask = (ulong)Marshal.ReadInt64(buffer, maskOffset);
                                topology.LogicalProcessors += System.Numerics.BitOperations.PopCount(mask);
                                maskOffset += 16;
                            }
                            break;
                        }

                        case RelationCache:
                        {
                            int level = Marshal.ReadByte(buffer, payload);
                            uint cacheSize = (uint)Marshal.ReadInt32(buffer, payload + 4);
                            int type = Marshal.ReadInt32(buffer, payload + 8);
                            var key = (level, type);
                            cacheSizes[key] = cacheSizes.TryGetValue(key, out var existing) ? existing + cacheSize : cacheSize;
                            break;
                        }

                        case RelationNumaNode:
                            topology.NumaNodes++;
                            break;

                        case RelationProcessorPackage:
                            topology.Packages++;
                            break;
                    }

                    offset += size;
                }

                foreach (var pair in cacheSizes)
                {
                    topology.Caches.Add(new CacheInfo(pair.Key.Level, CacheTypeName(pair.Key.Type), pair.Value));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Processor topology failed: {ex.Message}");
        }

        return topology;
    }

    private static string CacheTypeName(int type) => type switch
    {
        1 => "Unified",
        2 => "Instruction",
        3 => "Data",
        4 => "Trace",
        _ => "Cache"
    };

    // ── CPUID ──────────────────────────────────────────────────────────────────

    private static IEnumerable<(string Label, bool Supported)> ReadInstructionSets()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported)
        {
            // ARM64 or another architecture: report the features we can check through the runtime.
            yield return ("SIMD (NEON)", RuntimeInformation.ProcessArchitecture == Architecture.Arm64);
            yield break;
        }

        var leaf1 = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
        var leaf7 = System.Runtime.Intrinsics.X86.X86Base.CpuId(7, 0);

        uint ecx1 = (uint)leaf1.Ecx;
        uint edx1 = (uint)leaf1.Edx;
        uint ebx7 = (uint)leaf7.Ebx;

        yield return ("SSE", Bit(edx1, 25));
        yield return ("SSE2", Bit(edx1, 26));
        yield return ("SSE3", Bit(ecx1, 0));
        yield return ("SSSE3", Bit(ecx1, 9));
        yield return ("SSE4.1", Bit(ecx1, 19));
        yield return ("SSE4.2", Bit(ecx1, 20));
        yield return ("AVX", Bit(ecx1, 28));
        yield return ("AVX2", Bit(ebx7, 5));
        yield return ("AVX-512F", Bit(ebx7, 16));
        yield return ("FMA", Bit(ecx1, 12));
        yield return ("AES-NI", Bit(ecx1, 25));
        yield return ("SHA", Bit(ebx7, 29));
        yield return ("BMI1", Bit(ebx7, 3));
        yield return ("BMI2", Bit(ebx7, 8));
        yield return ("POPCNT", Bit(ecx1, 23));
        yield return ("RDRAND", Bit(ecx1, 30));
        yield return ("RDSEED", Bit(ebx7, 18));
    }

    private static bool Bit(uint value, int index) => (value & (1u << index)) != 0;
}
