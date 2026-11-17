using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Storage: physical disks (model, size, bus, media type, health, firmware), partitions and mounted
/// volumes with their file system, free space and cluster size.
/// </summary>
public sealed class StorageProvider : IInfoProvider
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    public string Id => "storage";
    public string Category => "storage";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(12);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(10);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Storage", Category = Category };
        var disks = Wmi.Query("SELECT Number, Model, Size, InterfaceType, MediaType, Partitions, SerialNumber, FirmwareRevision, Index, " +
                              "PNPDeviceID FROM Win32_DiskDrive");
        var managed = Wmi.Query("SELECT DeviceId, MediaType, BusType, SpindleSpeed, HealthStatus, FriendlyName FROM MSFT_PhysicalDisk",
            StorageScope);

        var summary = section.Group("Summary");
        const string diskSource = "Source: WMI Win32_DiskDrive";

        ulong totalCapacity = 0;
        foreach (var disk in disks)
        {
            totalCapacity += disk.ULong("Size") ?? 0;
        }

        summary.Items.Add(InfoItem.Of("storage.diskCount", "Physical disks", disks.Count > 0 ? disks.Count.ToString() : null,
            DetailLevel.Basic, "How many physical drives are connected, including USB drives.", diskSource));

        summary.Items.Add(totalCapacity > 0
            ? InfoItem.Of("storage.total", "Total capacity", PcFormat.Size(totalCapacity), DetailLevel.Basic,
                "The combined raw capacity of all connected drives.", diskSource)
            : InfoItem.MissingValue("storage.total", "Total capacity", MissingReason.NotReported, DetailLevel.Basic, diskSource));

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        if (TryGetVolumeSpace(systemDrive, out var systemFree, out var systemTotal) && systemTotal > 0)
        {
            var freePercent = (double)systemFree / systemTotal * 100;
            var freeItem = InfoItem.Of("storage.systemFree", "System drive free space",
                $"{PcFormat.Size(systemFree)} of {PcFormat.Size(systemTotal)} ({PcFormat.Percent(freePercent)})", DetailLevel.Basic,
                "How much space is left on the drive Windows is installed on.");
            freeItem.Status = freePercent < 5 ? InfoStatus.Problem : freePercent < 10 ? InfoStatus.Warning : InfoStatus.Ok;
            summary.Items.Add(freeItem);
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.4);

        // ── Physical disks ──────────────────────────────────────────────────────
        var diskGroup = section.Group("Physical disks");
        if (disks.Count == 0)
        {
            diskGroup.Items.Add(InfoItem.MissingValue("storage.disk", "Disk", MissingReason.NotReported, DetailLevel.Basic, diskSource));
        }

        for (int i = 0; i < disks.Count; i++)
        {
            var disk = disks[i];
            var index = disk.Str("Index") ?? i.ToString();
            var model = disk.Str("Model")?.Trim();
            var size = disk.ULong("Size");
            var managedDisk = managed.FirstOrDefault(m => m.Str("DeviceId") == index);

            var bus = managedDisk != null ? BusType(managedDisk.Int("BusType") ?? 0) : disk.Str("InterfaceType");
            var media = managedDisk != null ? MediaType(managedDisk.Int("MediaType") ?? 0) : null;
            var health = managedDisk?.Str("HealthStatus");

            var facts = new List<string>();
            if (!string.IsNullOrWhiteSpace(model)) facts.Add(model);
            if (size is > 0) facts.Add(PcFormat.Size(size.Value));
            if (!string.IsNullOrWhiteSpace(media)) facts.Add(media);
            if (!string.IsNullOrWhiteSpace(bus)) facts.Add(bus);

            var diskItem = InfoItem.Of($"storage.disk.{index}", $"Disk {index}", string.Join(" · ", facts), DetailLevel.Basic,
                "One physical drive, with its media type and connection.",
                managedDisk != null ? "Source: WMI MSFT_PhysicalDisk" : diskSource);

            if (string.Equals(health, "Warning", StringComparison.OrdinalIgnoreCase)) diskItem.Status = InfoStatus.Warning;
            else if (string.Equals(health, "Unhealthy", StringComparison.OrdinalIgnoreCase)) diskItem.Status = InfoStatus.Problem;
            diskGroup.Items.Add(diskItem);

            var spindle = managedDisk?.Int("SpindleSpeed");
            if (spindle is > 0)
            {
                diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}.rpm", $"Disk {index} spindle speed", $"{spindle} rpm",
                    DetailLevel.Detailed, "Rotational speed of a hard disk. SSDs report nothing here.",
                    "Source: WMI MSFT_PhysicalDisk"));
            }

            diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}.firmware", $"Disk {index} firmware",
                disk.Str("FirmwareRevision"), DetailLevel.Detailed, "The drive's own firmware version.", diskSource));
            diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}.partitions", $"Disk {index} partitions",
                disk.Str("Partitions"), DetailLevel.Detailed, "How many partitions this drive holds.", diskSource));
            diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}.serial", $"Disk {index} serial", disk.Str("SerialNumber"),
                DetailLevel.Everything, null, diskSource, sensitivity: InfoSensitivity.Identifier));
            diskGroup.Items.Add(InfoItem.Of($"storage.disk.{index}.path", $"Disk {index} device path",
                disk.Str("PNPDeviceID"), DetailLevel.Everything, null, diskSource, sensitivity: InfoSensitivity.Identifier));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.7);

        // ── Partition style and roles ───────────────────────────────────────────
        var diskInfo = Wmi.Query("SELECT Number, PartitionStyle, IsBoot, IsSystem, IsOffline, IsReadOnly FROM MSFT_Disk", StorageScope);
        if (diskInfo.Count > 0)
        {
            var rolesGroup = section.Group("Partition style and roles");
            foreach (var disk in diskInfo)
            {
                var number = disk.Str("Number") ?? "?";
                var style = disk.Int("PartitionStyle") switch
                {
                    1 => "MBR",
                    2 => "GPT",
                    _ => "Unknown"
                };

                var roles = new List<string>();
                if (IsTrue(disk.Str("IsSystem"))) roles.Add("system");
                if (IsTrue(disk.Str("IsBoot"))) roles.Add("boot");
                if (IsTrue(disk.Str("IsOffline"))) roles.Add("offline");
                if (IsTrue(disk.Str("IsReadOnly"))) roles.Add("read-only");

                rolesGroup.Items.Add(InfoItem.Of($"storage.style.{number}", $"Disk {number}",
                    roles.Count > 0 ? $"{style} · {string.Join(", ", roles)}" : style, DetailLevel.Detailed,
                    "GPT is the modern partition style and is required for UEFI booting.",
                    "Source: WMI MSFT_Disk"));
            }
        }

        // ── Volumes ─────────────────────────────────────────────────────────────
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
                    "A mounted volume: file system, used space and free space.");
                item.RawValue = total.ToString(CultureInfo.InvariantCulture);
                if (freePercent < 5) item.Status = InfoStatus.Problem;
                else if (freePercent < 10) item.Status = InfoStatus.Warning;
                volumeGroup.Items.Add(item);

                if (TryGetClusterSize(drive.Name, out var clusterBytes))
                {
                    volumeGroup.Items.Add(InfoItem.Of($"volume.{drive.Name}.cluster", $"{drive.Name.TrimEnd('\\')} cluster size",
                        PcFormat.Size(clusterBytes), DetailLevel.Everything,
                        "The smallest block the file system allocates.", "Source: GetDiskFreeSpace"));
                }
            }
        }
        catch (Exception ex)
        {
            volumeGroup.Items.Add(InfoItem.MissingValue("volume.error", "Volumes", MissingReason.Error));
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Volume enumeration failed: {ex.Message}");
        }

        progress?.Report(1);
        return section;
    }

    private static bool IsTrue(string? value) => value is "True" or "true" or "1";

    private static bool TryGetVolumeSpace(string drive, out ulong free, out ulong total)
    {
        free = 0;
        total = 0;
        try
        {
            var info = new DriveInfo(drive);
            if (!info.IsReady) return false;
            total = (ulong)info.TotalSize;
            free = (ulong)info.AvailableFreeSpace;
            return total > 0;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetDiskFreeSpace(string? rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    private static bool TryGetClusterSize(string drive, out ulong bytes)
    {
        bytes = 0;
        try
        {
            var root = drive.TrimEnd('\\') + "\\";
            if (!GetDiskFreeSpace(root, out var sectors, out var bytesPerSector, out _, out _)) return false;
            if (sectors == 0 || bytesPerSector == 0) return false;
            bytes = (ulong)sectors * bytesPerSector;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? BusType(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => null
    };

    private static string? MediaType(int mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => null
    };

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
