using System.IO;
using System.Management;
using AppLock.Models;

namespace AppLock.Services.BitLocker;

public class BitLockerService : IBitLockerService
{
    private readonly BitLockerSupportService _supportService;

    public BitLockerService(BitLockerSupportService supportService)
    {
        _supportService = supportService;
    }

    public async Task<IReadOnlyList<BitLockerDrive>> GetDrivesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => QueryDrives(ct), ct);
    }

    private IReadOnlyList<BitLockerDrive> QueryDrives(CancellationToken ct)
    {
        var drivesList = new List<BitLockerDrive>();
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\').ToUpperInvariant() ?? "C:";

        // Get snapshot of mounted drives from DriveInfo
        var driveInfoMap = new Dictionary<string, DriveInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType == System.IO.DriveType.Fixed || di.DriveType == System.IO.DriveType.Removable)
                {
                    var letter = di.Name.TrimEnd('\\').ToUpperInvariant();
                    driveInfoMap[letter] = di;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get DriveInfo: {ex.Message}");
        }

        var processedLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try WMI query if supported
        bool wmiQueried = false;
        try
        {
            ct.ThrowIfCancellationRequested();

            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_EncryptableVolume"));
            using var collection = searcher.Get();

            foreach (ManagementObject mo in collection)
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    var drive = ParseEncryptableVolume(mo, systemDrive, driveInfoMap);
                    drivesList.Add(drive);

                    if (!string.IsNullOrEmpty(drive.DriveLetter))
                    {
                        processedLetters.Add(drive.DriveLetter.ToUpperInvariant());
                    }
                }
            }

            wmiQueried = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WMI Win32_EncryptableVolume query failed (non-elevated or error): {ex.Message}");
        }

        // Add any remaining drives from DriveInfo that were not in WMI
        foreach (var kvp in driveInfoMap)
        {
            var letter = kvp.Key;
            var di = kvp.Value;

            if (processedLetters.Contains(letter)) continue;

            var isOs = string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase);
            var driveType = isOs
                ? AppLock.Models.DriveType.OperatingSystem
                : (di.DriveType == System.IO.DriveType.Removable ? AppLock.Models.DriveType.Removable : AppLock.Models.DriveType.Data);

            string label = "";
            long total = 0;
            long free = 0;
            bool isReady = false;

            try
            {
                isReady = di.IsReady;
                if (isReady)
                {
                    label = di.VolumeLabel;
                    total = di.TotalSize;
                    free = di.AvailableFreeSpace;
                }
            }
            catch { }

            var drive = new BitLockerDrive
            {
                DriveLetter = letter,
                VolumeLabel = label,
                VolumeGuid = "",
                DriveType = driveType,
                ProtectionStatus = wmiQueried ? ProtectionStatus.Off : ProtectionStatus.Unknown,
                LockStatus = isReady ? LockStatus.Unlocked : LockStatus.Unknown,
                EncryptionPercent = 0,
                ConversionStatus = wmiQueried ? "Fully Decrypted" : "Elevation required",
                EncryptionMethod = wmiQueried ? "None" : "Elevation required",
                TotalBytes = total,
                FreeBytes = free,
                KeyProtectors = new List<KeyProtectorInfo>()
            };

            drivesList.Add(drive);
        }

        // Sort: OS drive first, then Data drives by letter, then Removable drives
        return drivesList
            .OrderBy(d => d.DriveType == AppLock.Models.DriveType.OperatingSystem ? 0 : (d.DriveType == AppLock.Models.DriveType.Data ? 1 : 2))
            .ThenBy(d => d.DriveLetter)
            .ToList();
    }

    private static BitLockerDrive ParseEncryptableVolume(
        ManagementObject mo,
        string systemDrive,
        Dictionary<string, DriveInfo> driveInfoMap)
    {
        var deviceId = mo["DeviceID"] as string ?? "";
        var driveLetter = mo["DriveLetter"] as string ?? "";
        driveLetter = driveLetter.TrimEnd('\\').ToUpperInvariant();

        // Protection status
        uint protectionStatus = 3; // Unknown
        try
        {
            using var outParams = mo.InvokeMethod("GetProtectionStatus", null, null);
            if (outParams != null && outParams["ProtectionStatus"] != null)
            {
                protectionStatus = Convert.ToUInt32(outParams["ProtectionStatus"]);
            }
        }
        catch { }

        // Lock status
        uint lockStatus = 2; // Unknown
        try
        {
            using var outParams = mo.InvokeMethod("GetLockStatus", null, null);
            if (outParams != null && outParams["LockStatus"] != null)
            {
                lockStatus = Convert.ToUInt32(outParams["LockStatus"]);
            }
        }
        catch { }

        // Conversion status & encryption percentage
        uint conversionStatus = 0;
        uint encryptionPercentage = 0;
        try
        {
            using var outParams = mo.InvokeMethod("GetConversionStatus", null, null);
            if (outParams != null)
            {
                if (outParams["ConversionStatus"] != null)
                    conversionStatus = Convert.ToUInt32(outParams["ConversionStatus"]);
                if (outParams["EncryptionPercentage"] != null)
                    encryptionPercentage = Convert.ToUInt32(outParams["EncryptionPercentage"]);
            }
        }
        catch { }

        // Encryption method
        uint encryptionMethod = 0;
        try
        {
            using var outParams = mo.InvokeMethod("GetEncryptionMethod", null, null);
            if (outParams != null && outParams["EncryptionMethod"] != null)
            {
                encryptionMethod = Convert.ToUInt32(outParams["EncryptionMethod"]);
            }
        }
        catch { }

        // Key protectors
        var protectors = new List<KeyProtectorInfo>();
        try
        {
            using var inParams = mo.GetMethodParameters("GetKeyProtectors");
            inParams["KeyProtectorType"] = 0; // 0 = return all
            using var outParams = mo.InvokeMethod("GetKeyProtectors", inParams, null);
            if (outParams != null && outParams["VolumeKeyProtectorID"] is string[] protectorIds)
            {
                foreach (var id in protectorIds)
                {
                    int typeCode = 0;
                    try
                    {
                        using var inType = mo.GetMethodParameters("GetKeyProtectorType");
                        inType["VolumeKeyProtectorID"] = id;
                        using var outType = mo.InvokeMethod("GetKeyProtectorType", inType, null);
                        if (outType != null && outType["KeyProtectorType"] != null)
                        {
                            typeCode = Convert.ToInt32(outType["KeyProtectorType"]);
                        }
                    }
                    catch { }

                    protectors.Add(new KeyProtectorInfo
                    {
                        Id = id,
                        TypeCode = typeCode,
                        TypeName = MapProtectorType(typeCode)
                    });
                }
            }
        }
        catch { }

        // Match with DriveInfo for label and capacity
        string label = "";
        long total = 0;
        long free = 0;
        bool isRemovable = false;

        if (!string.IsNullOrEmpty(driveLetter) && driveInfoMap.TryGetValue(driveLetter, out var di))
        {
            isRemovable = di.DriveType == System.IO.DriveType.Removable;
            try
            {
                if (di.IsReady)
                {
                    label = di.VolumeLabel;
                    total = di.TotalSize;
                    free = di.AvailableFreeSpace;
                }
            }
            catch { }
        }

        var isOsDrive = string.Equals(driveLetter, systemDrive, StringComparison.OrdinalIgnoreCase);
        var driveType = isOsDrive
            ? AppLock.Models.DriveType.OperatingSystem
            : (isRemovable ? AppLock.Models.DriveType.Removable : AppLock.Models.DriveType.Data);

        var protection = protectionStatus switch
        {
            0 => ProtectionStatus.Off,
            1 => ProtectionStatus.On,
            2 => ProtectionStatus.Suspended,
            _ => ProtectionStatus.Unknown
        };

        var lockState = lockStatus switch
        {
            0 => LockStatus.Unlocked,
            1 => LockStatus.Locked,
            _ => LockStatus.Unknown
        };

        return new BitLockerDrive
        {
            DriveLetter = driveLetter,
            VolumeLabel = label,
            VolumeGuid = deviceId,
            DriveType = driveType,
            ProtectionStatus = protection,
            LockStatus = lockState,
            EncryptionPercent = (int)Math.Clamp(encryptionPercentage, 0, 100),
            ConversionStatus = MapConversionStatus(conversionStatus, encryptionPercentage),
            EncryptionMethod = MapEncryptionMethod(encryptionMethod),
            KeyProtectors = protectors,
            TotalBytes = total,
            FreeBytes = free
        };
    }

    public static string MapProtectorType(int typeCode)
    {
        return typeCode switch
        {
            1 => "TPM",
            2 => "External Key",
            3 => "Recovery Password",
            4 => "TPM + PIN",
            5 => "TPM + Key",
            6 => "TPM + PIN + Key",
            7 => "Public Key",
            8 => "Password",
            9 => "TPM Network Key",
            _ => "Unknown Protector"
        };
    }

    public static string MapEncryptionMethod(uint method)
    {
        return method switch
        {
            0 => "None",
            1 => "AES-128 (Diffuser)",
            2 => "AES-256 (Diffuser)",
            3 => "AES-128",
            4 => "AES-256",
            5 => "Hardware Encryption",
            6 => "XTS-AES 128",
            7 => "XTS-AES 256",
            _ => "Unknown"
        };
    }

    public static string MapConversionStatus(uint status, uint percentage)
    {
        return status switch
        {
            0 => "Fully Decrypted",
            1 => "Fully Encrypted",
            2 => $"Encrypting ({percentage}%)",
            3 => $"Decrypting ({percentage}%)",
            4 => $"Encryption Paused ({percentage}%)",
            5 => $"Decryption Paused ({percentage}%)",
            _ => percentage == 100 ? "Fully Encrypted" : (percentage == 0 ? "Fully Decrypted" : $"{percentage}%")
        };
    }
}
