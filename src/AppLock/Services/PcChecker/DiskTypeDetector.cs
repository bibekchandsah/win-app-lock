using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AppLock.Services.PcChecker;

public sealed class DriveMediaTypeInfo
{
    public string DriveLetter { get; init; } = string.Empty;
    public string MediaType { get; init; } = "Disk"; // "SSD", "HDD", "NVMe SSD", "SATA SSD"
    public string? BusType { get; init; }
    public string? Model { get; init; }
    public int? SpindleSpeed { get; init; }

    public bool IsSsd => MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase);
    public bool IsHdd => MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase);

    public string DisplaySummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(BusType)) parts.Add(BusType);
            parts.Add(MediaType);
            if (SpindleSpeed is > 0) parts.Add($"{SpindleSpeed} RPM");
            return string.Join(" ", parts);
        }
    }
}

/// <summary>
/// Detects physical disk media type (SSD vs HDD) and bus interface for a logical drive.
/// Uses native Win32 IOCTLs (StorageDeviceSeekPenaltyProperty &amp; StorageDeviceProperty)
/// with WMI MSFT_PhysicalDisk fallback.
/// </summary>
public static class DiskTypeDetector
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.I1)]
        public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DESCRIPTOR_HEADER
    {
        public uint Version;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.I1)]
        public bool RemovableMedia;
        [MarshalAs(UnmanagedType.I1)]
        public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public int BusType;
        public uint RawPropertiesLength;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        out DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// Gets media type info for a drive letter (e.g. 'C' or "C:\\").
    /// </summary>
    public static DriveMediaTypeInfo GetDriveInfo(string driveRoot)
    {
        string letter = driveRoot.TrimEnd('\\', ':').ToUpperInvariant();
        if (string.IsNullOrEmpty(letter)) letter = "C";

        bool? isSsd = null;
        string? busType = null;
        string? model = null;
        int? spindleSpeed = null;

        // 1. Try Win32 IOCTL
        try
        {
            string volumePath = $@"\\.\{letter}:";
            using var handle = CreateFile(volumePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                // Query Seek Penalty (PropertyId 7: StorageDeviceSeekPenaltyProperty)
                var seekQuery = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = 7,
                    QueryType = 0,
                    AdditionalParameters = new byte[1]
                };

                if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref seekQuery, Marshal.SizeOf(seekQuery),
                        out DEVICE_SEEK_PENALTY_DESCRIPTOR seekDesc, Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(),
                        out int bytesReturned, IntPtr.Zero))
                {
                    isSsd = !seekDesc.IncursSeekPenalty;
                }

                // Query Storage Device Property (PropertyId 0: StorageDeviceProperty)
                var deviceQuery = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = 0,
                    QueryType = 0,
                    AdditionalParameters = new byte[1]
                };

                byte[] buffer = new byte[1024];
                if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref deviceQuery, Marshal.SizeOf(deviceQuery),
                        buffer, buffer.Length, out bytesReturned, IntPtr.Zero) && bytesReturned > 0)
                {
                    GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        var desc = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(pinned.AddrOfPinnedObject());
                        busType = ParseBusType(desc.BusType);

                        if (desc.ProductIdOffset > 0 && desc.ProductIdOffset < buffer.Length)
                        {
                            model = ReadAsciiString(buffer, (int)desc.ProductIdOffset);
                        }
                    }
                    finally
                    {
                        pinned.Free();
                    }
                }
            }
        }
        catch { }

        // 2. Try WMI MSFT_PhysicalDisk & Win32_DiskDrive enrichment
        try
        {
            var managedDisks = Wmi.Query("SELECT DeviceId, MediaType, BusType, SpindleSpeed, FriendlyName, Model FROM MSFT_PhysicalDisk",
                @"root\Microsoft\Windows\Storage");

            if (managedDisks.Count > 0)
            {
                // Find matching disk by model or first disk if single
                var match = managedDisks.FirstOrDefault(m =>
                    (!string.IsNullOrEmpty(model) && (m.Str("FriendlyName")?.Contains(model, StringComparison.OrdinalIgnoreCase) == true ||
                                                     m.Str("Model")?.Contains(model, StringComparison.OrdinalIgnoreCase) == true)))
                    ?? managedDisks.FirstOrDefault();

                if (match != null)
                {
                    if (string.IsNullOrEmpty(model))
                        model = match.Str("FriendlyName") ?? match.Str("Model");

                    int mediaInt = match.Int("MediaType") ?? 0;
                    if (mediaInt == 4) isSsd = true;
                    else if (mediaInt == 3) isSsd = false;

                    if (string.IsNullOrEmpty(busType) && match.Int("BusType") is int bt && bt > 0)
                        busType = StorageProvider_BusType(bt);

                    spindleSpeed = match.Int("SpindleSpeed");
                }
            }
        }
        catch { }

        // Fallback name check if model contains "SSD" or "NVMe"
        if (!string.IsNullOrEmpty(model))
        {
            if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                isSsd = true;
        }

        string finalMediaType = isSsd switch
        {
            true  => "SSD",
            false => "HDD",
            null  => "Disk"
        };

        return new DriveMediaTypeInfo
        {
            DriveLetter = letter,
            MediaType = finalMediaType,
            BusType = busType,
            Model = model?.Trim(),
            SpindleSpeed = spindleSpeed
        };
    }

    private static string? ParseBusType(int busType) => busType switch
    {
        1  => "SCSI",
        2  => "ATAPI",
        3  => "ATA",
        4  => "1394",
        5  => "SSA",
        6  => "Fibre",
        7  => "USB",
        8  => "RAID",
        9  => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "FileBackedVirtual",
        17 => "NVMe",
        _  => null
    };

    private static string? StorageProvider_BusType(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        17 => "NVMe",
        18 => "Microsoft Spaces",
        _ => null
    };

    private static string? ReadAsciiString(byte[] buffer, int offset)
    {
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        if (end > offset)
        {
            return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        }
        return null;
    }
}
