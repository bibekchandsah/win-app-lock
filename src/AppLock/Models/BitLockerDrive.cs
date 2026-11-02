namespace AppLock.Models;

public enum DriveType { OperatingSystem, Data, Removable }
public enum ProtectionStatus { Off = 0, On = 1, Suspended = 2, Unknown = 3 }
public enum LockStatus { Unlocked = 0, Locked = 1, Unknown = 2 }

public class BitLockerDrive
{
    public string DriveLetter { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string VolumeGuid { get; set; } = "";
    public DriveType DriveType { get; set; }
    public ProtectionStatus ProtectionStatus { get; set; }
    public LockStatus LockStatus { get; set; }
    public int EncryptionPercent { get; set; }
    public string ConversionStatus { get; set; } = "";
    public string EncryptionMethod { get; set; } = "";
    public List<KeyProtectorInfo> KeyProtectors { get; set; } = new();
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{DriveLetter} ({VolumeLabel})"
        : (!string.IsNullOrWhiteSpace(DriveLetter) ? $"{DriveLetter} (Local Disk)" : (string.IsNullOrWhiteSpace(VolumeGuid) ? "Drive" : VolumeGuid));
}
