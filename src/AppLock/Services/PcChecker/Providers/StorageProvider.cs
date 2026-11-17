using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>Storage basics: physical disks and the volumes Windows has mounted.</summary>
public sealed class StorageProvider : IInfoProvider
{
    public string Id => "storage";
    public string Category => "storage";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(10);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Storage", Category = Category };
        var summary = section.Group("Summary");
        var diskSource = "Source: WMI Win32_DiskDrive";

        var disks = Wmi.Query("SELECT Model, Size, InterfaceType, MediaType, Partitions, SerialNumber, FirmwareRevision, Index FROM Win32_DiskDrive");

        ulong totalCapacity = 0;
        ulong maxDisk = 0;
        string? largestModel = null;
        foreach (var disk in disks)
        {
            var size = disk.ULong("Size") ?? 0;
            totalCapacity += size;
            if (size > maxDisk)
            {
                maxDisk = size;
                largestModel = disk.Str("Model");
            }
        }

        summary.Items.Add(InfoItem.Of("storage.diskCount", "Physical disks",
            disks.Count > 0 ? disks.Count.ToString() : null, DetailLevel.Basic,
            "How many physical drives are connected, including USB drives.", diskSource));

        summary.Items.Add(totalCapacity > 0
            ? InfoItem.Of("storage.total", "Total capacity", PcFormat.Size(totalCapacity), DetailLevel.Basic,
                "The combined raw capacity of all connected drives.", diskSource)
            : InfoItem.MissingValue("storage.total", "Total capacity", MissingReason.NotReported, DetailLevel.Basic, diskSource));

        if (largestModel != null && maxDisk > 0)
        {
            summary.Items.Add(InfoItem.Of("storage.primary", "Largest drive", $"{largestModel.Trim()} · {PcFormat.Size(maxDisk)}", DetailLevel.Detailed,
                "The biggest drive in this PC, usually the system drive.", diskSource));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.5);

        var diskGroup = section.Group("Physical disks");
        if (disks.Count == 0)
        {
            diskGroup.Items.Add(InfoItem.MissingValue("storage.disk", "Disk", MissingReason.NotReported, DetailLevel.Basic, diskSource));
        }

        for (int i = 0; i < disks.Count; i++)
        {
            var disk = disks[i];
            var model = disk.Str("Model")?.Trim();
            var size = disk.ULong("Size");
            var interfaceType = disk.Str("InterfaceType");
            var mediaType = disk.Str("MediaType");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(model)) parts.Add(model);
            if (size is > 0) parts.Add(PcFormat.Size(size.Value));
            if (!string.IsNullOrWhiteSpace(interfaceType)) parts.Add(interfaceType);
            if (!string.IsNullOrWhiteSpace(mediaType) && mediaType != "Fixed hard disk media") parts.Add(mediaType);

            var index = disk.Str("Index") ?? i.ToString();
            diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}", $"Disk {index}", string.Join(" · ", parts), DetailLevel.Basic,
                "One physical drive. Full SMART health details come with the storage milestone.", diskSource));

            diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}.firmware", $"Disk {index} firmware", disk.Str("FirmwareRevision"),
                DetailLevel.Everything, null, diskSource));
            diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}.serial", $"Disk {index} serial", disk.Str("SerialNumber"),
                DetailLevel.Everything, null, diskSource, sensitivity: InfoSensitivity.Identifier));
        }

        progress?.Report(0.8);

        var volumeGroup = section.Group("Volumes");
        try
        {
            foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (drive.DriveType is DriveType.NoRootDirectory or DriveType.Unknown) continue;

                if (!drive.IsReady)
                {
                    volumeGroup.Items.Add(InfoItem.Of($"volume.{drive.Name}", drive.Name.TrimEnd('\\'),
                        $"{DescribeDriveType(drive.DriveType)} (not ready)", DetailLevel.Detailed,
                        "A drive Windows sees but cannot read right now."));
                    continue;
                }

                var total = (ulong)drive.TotalSize;
                var free = (ulong)drive.AvailableFreeSpace;
                var used = total > free ? total - free : 0;
                var freePercent = total == 0 ? 100 : (double)free / total * 100;

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? DescribeDriveType(drive.DriveType) : drive.VolumeLabel;
                var text = $"{label} · {drive.DriveFormat} · {PcFormat.Size(used)} used of {PcFormat.Size(total)} ({PcFormat.Size(free)} free)";

                var item = InfoItem.Of($"volume.{drive.Name}", drive.Name.TrimEnd('\\'), text, DetailLevel.Basic,
                    "A mounted volume: how much of it is used and how much is free.");
                item.RawValue = total.ToString();
                if (freePercent < 5) item.Status = InfoStatus.Problem;
                else if (freePercent < 10) item.Status = InfoStatus.Warning;
                volumeGroup.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            volumeGroup.Items.Add(InfoItem.MissingValue("volume.error", "Volumes", MissingReason.Error));
            section.Error = null;
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Volume enumeration failed: {ex.Message}");
        }

        progress?.Report(1);
        return section;
    }

    private static string DescribeDriveType(DriveType type) => type switch
    {
        DriveType.Fixed => "Local disk",
        DriveType.Removable => "Removable",
        DriveType.Network => "Network drive",
        DriveType.CDRom => "Optical drive",
        DriveType.Ram => "RAM disk",
        _ => type.ToString()
    };
}
