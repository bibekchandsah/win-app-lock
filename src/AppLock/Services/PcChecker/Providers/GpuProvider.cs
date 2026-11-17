using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>Graphics adapters: name, driver version and date, and the VRAM Windows reports.</summary>
public sealed class GpuProvider : IInfoProvider
{
    public string Id => "gpu";
    public string Category => "graphics";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(10);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Graphics", Category = Category };
        var group = section.Group("Graphics adapters");
        var source = "Source: WMI Win32_VideoController";

        var adapters = Wmi.Query("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController");

        if (adapters.Count == 0)
        {
            group.Items.Add(InfoItem.MissingValue("gpu.name", "Graphics adapter", MissingReason.NotReported, DetailLevel.Basic, source));
        }

        for (int i = 0; i < adapters.Count; i++)
        {
            var adapter = adapters[i];
            var suffix = i.ToString();
            string? name = adapter.Str("Name");

            group.Items.Add(InfoItem.Of($"gpu.{suffix}.name", i == 0 ? "Adapter" : $"Adapter {i + 1}", name, DetailLevel.Basic,
                "The graphics processor Windows lists for this PC.", source));

            group.Items.Add(InfoItem.Of($"gpu.{suffix}.driver", "Driver version", adapter.Str("DriverVersion"), DetailLevel.Basic,
                "The installed graphics driver version.", source));

            var driverDate = adapter.Date("DriverDate");
            group.Items.Add(driverDate.HasValue
                ? InfoItem.Of($"gpu.{suffix}.driverDate", "Driver date", PcFormat.DateText(driverDate.Value), DetailLevel.Basic,
                    "When the installed graphics driver was released.", source)
                : InfoItem.MissingValue($"gpu.{suffix}.driverDate", "Driver date", MissingReason.NotReported, DetailLevel.Basic, source));

            // AdapterRAM is a 32-bit field, so cards with more than 4 GB report it capped.
            ulong? vram = adapter.ULong("AdapterRAM");
            if (vram is > 0)
            {
                var vramItem = InfoItem.Of($"gpu.{suffix}.vram", "VRAM (reported)", PcFormat.Size(vram.Value), DetailLevel.Detailed,
                    "The video memory Windows reports. Cards with more than 4 GB may report a capped value here; the exact figure comes with the graphics milestone.", source);
                if (vram.Value >= 4_000_000_000UL) vramItem.Status = InfoStatus.Info;
                group.Items.Add(vramItem);
            }

            group.Items.Add(InfoItem.Of($"gpu.{suffix}.processor", "Video processor", adapter.Str("VideoProcessor"), DetailLevel.Everything, null, source));
            group.Items.Add(InfoItem.Of($"gpu.{suffix}.pnpId", "Device ID", adapter.Str("PNPDeviceID"), DetailLevel.Everything, null, source,
                sensitivity: InfoSensitivity.Identifier));
        }

        int? width = adapters.Count > 0 ? adapters[0].Int("CurrentHorizontalResolution") : null;
        int? height = adapters.Count > 0 ? adapters[0].Int("CurrentVerticalResolution") : null;
        int? refresh = adapters.Count > 0 ? adapters[0].Int("CurrentRefreshRate") : null;

        if (width is > 0 && height is > 0)
        {
            var mode = $"{width} × {height}" + (refresh is > 0 ? $" @ {refresh} Hz" : string.Empty);
            group.Items.Add(InfoItem.Of("gpu.mode", "Current display mode", mode, DetailLevel.Detailed,
                "The resolution and refresh rate of the primary display right now.", source));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        return section;
    }
}
