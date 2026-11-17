using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using Microsoft.Win32;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Graphics adapters: name, exact VRAM (from the driver's registry key), driver version/date,
/// hardware-accelerated GPU scheduling and the Vulkan loader. Feature levels and engine counters
/// come with the live performance view.
/// </summary>
public sealed class GpuProvider : IInfoProvider
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    public string Id => "gpu";
    public string Category => "graphics";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(10);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private sealed class RegistryAdapter
    {
        public string Description = string.Empty;
        public string DriverVersion = string.Empty;
        public string DriverDate = string.Empty;
        public string Provider = string.Empty;
        public ulong VramBytes;
    }

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Graphics", Category = Category };
        var group = section.Group("Graphics adapters");
        const string wmiSource = "Source: WMI Win32_VideoController";

        var adapters = Wmi.Query("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor, CurrentHorizontalResolution, " +
                                 "CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController");
        var registryAdapters = ReadRegistryAdapters();

        if (adapters.Count == 0)
        {
            group.Items.Add(InfoItem.MissingValue("gpu.name", "Graphics adapter", MissingReason.NotReported, DetailLevel.Basic, wmiSource));
        }

        for (int i = 0; i < adapters.Count; i++)
        {
            var adapter = adapters[i];
            var suffix = i.ToString();
            string? name = adapter.Str("Name");

            var match = registryAdapters.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r.Description) && name != null &&
                (r.Description.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                 name.Contains(r.Description, StringComparison.OrdinalIgnoreCase)));

            group.Items.Add(InfoItem.Of($"gpu.{suffix}.name", i == 0 ? "Adapter" : $"Adapter {i + 1}", name, DetailLevel.Basic,
                "The graphics processor Windows lists for this PC.", wmiSource));

            var driverVersion = adapter.Str("DriverVersion") ?? match?.DriverVersion;
            group.Items.Add(InfoItem.Of($"gpu.{suffix}.driver", "Driver version", driverVersion, DetailLevel.Basic,
                "The installed graphics driver version.", match != null
                    ? "Source: registry display class key"
                    : wmiSource));

            var driverDate = adapter.Date("DriverDate");
            var driverDateText = driverDate.HasValue ? PcFormat.DateText(driverDate.Value) : match?.DriverDate;
            group.Items.Add(InfoItem.Of($"gpu.{suffix}.driverDate", "Driver date", driverDateText, DetailLevel.Basic,
                "When the installed graphics driver was released.", wmiSource));

            // The 32-bit AdapterRAM field caps at 4 GB; the driver key stores the real size.
            ulong vram = match?.VramBytes ?? 0;
            if (vram == 0) vram = adapter.ULong("AdapterRAM") ?? 0;

            if (vram > 0)
            {
                var vramItem = InfoItem.Of($"gpu.{suffix}.vram", "Dedicated VRAM", PcFormat.Size(vram), DetailLevel.Basic,
                    "The graphics memory on the card itself. Much faster than the shared system memory.",
                    match?.VramBytes > 0 ? "Source: registry HardwareInformation.qwMemorySize" : wmiSource);
                if (match is not { VramBytes: > 0 } && vram >= 4_000_000_000UL)
                {
                    vramItem.Value = $"{PcFormat.Size(vram)} (Windows caps this at 4 GB)";
                    vramItem.Status = InfoStatus.Info;
                }
                group.Items.Add(vramItem);
            }

            if (!string.IsNullOrWhiteSpace(match?.Provider))
            {
                group.Items.Add(InfoItem.Of($"gpu.{suffix}.provider", "Driver provider", match!.Provider, DetailLevel.Detailed,
                    "Who publishes the graphics driver.", "Source: registry display class key"));
            }

            group.Items.Add(InfoItem.Of($"gpu.{suffix}.processor", "Video processor", adapter.Str("VideoProcessor"), DetailLevel.Everything,
                null, wmiSource));
            group.Items.Add(InfoItem.Of($"gpu.{suffix}.pnpId", "Device ID", adapter.Str("PNPDeviceID"), DetailLevel.Everything, null,
                wmiSource, sensitivity: InfoSensitivity.Identifier));
        }

        int? width = adapters.Count > 0 ? adapters[0].Int("CurrentHorizontalResolution") : null;
        int? height = adapters.Count > 0 ? adapters[0].Int("CurrentVerticalResolution") : null;
        int? refresh = adapters.Count > 0 ? adapters[0].Int("CurrentRefreshRate") : null;

        if (width is > 0 && height is > 0)
        {
            var mode = $"{width} × {height}" + (refresh is > 0 ? $" @ {refresh} Hz" : string.Empty);
            group.Items.Add(InfoItem.Of("gpu.mode", "Current display mode", mode, DetailLevel.Detailed,
                "The resolution and refresh rate of the primary display right now.", wmiSource));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.6);

        var features = section.Group("Graphics features");
        var hwSchMode = PcRegistry.ReadLong(RegistryHive.LocalMachine, GraphicsDriversKey, "HwSchMode");
        var hagsItem = InfoItem.Of("gpu.hags", "Hardware-accelerated GPU scheduling",
            hwSchMode is null ? null : hwSchMode.Value == 2 ? "On" : "Off", DetailLevel.Detailed,
            "Lets the GPU manage its own memory scheduling; needed for some newer features.",
            "Source: registry GraphicsDrivers HwSchMode");
        if (hwSchMode is null) hagsItem.Missing = MissingReason.NotReported;
        features.Items.Add(hagsItem);

        var directX = PcRegistry.MachineString(@"SOFTWARE\Microsoft\DirectX", "Version");
        features.Items.Add(InfoItem.Of("gpu.directx", "DirectX runtime", directX, DetailLevel.Detailed,
            "The DirectX version installed on this PC.", "Source: registry SOFTWARE\\Microsoft\\DirectX"));

        var vulkan = File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));
        features.Items.Add(InfoItem.Of("gpu.vulkan", "Vulkan runtime",
            vulkan ? "Installed (vulkan-1.dll)" : "Not installed", DetailLevel.Detailed,
            "Vulkan is a modern graphics API used by some games and emulators. Its exact version needs a Vulkan instance, which this page does not create.",
            "Source: System32\\vulkan-1.dll"));

        features.Items.Add(InfoItem.MissingValue("gpu.featureLevel", "Direct3D feature level",
            MissingReason.NotReported, DetailLevel.Detailed,
            "Creating a Direct3D device to read the feature level is not implemented yet; the graphics tier ships with DXGI in a later pass."));

        progress?.Report(1);
        return section;
    }

    private static List<RegistryAdapter> ReadRegistryAdapters()
    {
        var result = new List<RegistryAdapter>();

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey == null) return result;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;

                using var adapterKey = classKey.OpenSubKey(subKeyName);
                if (adapterKey == null) continue;

                var description = adapterKey.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(description)) continue;

                ulong vram = 0;
                if (adapterKey.GetValue("HardwareInformation.qwMemorySize") is byte[] qwordBytes && qwordBytes.Length >= 8)
                {
                    vram = BitConverter.ToUInt64(qwordBytes, 0);
                }
                else if (adapterKey.GetValue("HardwareInformation.qwMemorySize") is long qword)
                {
                    vram = qword > 0 ? (ulong)qword : 0;
                }
                else if (adapterKey.GetValue("HardwareInformation.MemorySize") is int dword && dword > 0)
                {
                    vram = (uint)dword;
                }

                result.Add(new RegistryAdapter
                {
                    Description = description,
                    DriverVersion = adapterKey.GetValue("DriverVersion") as string ?? string.Empty,
                    DriverDate = adapterKey.GetValue("DriverDate") as string ?? string.Empty,
                    Provider = adapterKey.GetValue("ProviderName") as string ?? string.Empty,
                    VramBytes = vram
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Display class registry read failed: {ex.Message}");
        }

        return result;
    }
}
