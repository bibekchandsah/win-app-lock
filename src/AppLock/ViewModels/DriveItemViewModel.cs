using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models;

namespace AppLock.ViewModels;

public partial class DriveItemViewModel : ObservableObject
{
    [ObservableProperty]
    private BitLockerDrive _model;

    public DriveItemViewModel(BitLockerDrive model)
    {
        _model = model;
    }

    public string DriveLetter => Model.DriveLetter;
    public string VolumeLabel => Model.VolumeLabel;
    public string VolumeGuid => Model.VolumeGuid;
    public Models.DriveType DriveType => Model.DriveType;
    public ProtectionStatus ProtectionStatus => Model.ProtectionStatus;
    public LockStatus LockStatus => Model.LockStatus;
    public int EncryptionPercent => Model.EncryptionPercent;
    public string ConversionStatus => string.IsNullOrEmpty(Model.ConversionStatus)
        ? (Model.EncryptionPercent == 100 ? "Fully Encrypted" : $"{Model.EncryptionPercent}%")
        : Model.ConversionStatus;
    public string EncryptionMethod => string.IsNullOrEmpty(Model.EncryptionMethod) ? "None" : Model.EncryptionMethod;
    public IReadOnlyList<KeyProtectorInfo> KeyProtectors => Model.KeyProtectors;
    public long TotalBytes => Model.TotalBytes;
    public long FreeBytes => Model.FreeBytes;

    public string DisplayName => !string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{DriveLetter} ({VolumeLabel})"
        : (!string.IsNullOrWhiteSpace(DriveLetter) ? $"{DriveLetter} (Local Disk)" : (string.IsNullOrWhiteSpace(VolumeGuid) ? "Drive" : VolumeGuid));

    public string DriveTypeDisplay => DriveType switch
    {
        Models.DriveType.OperatingSystem => "Operating System",
        Models.DriveType.Removable => "Removable Drive",
        _ => "Fixed Data"
    };

    public string DriveIconGlyph => DriveType switch
    {
        Models.DriveType.OperatingSystem => "\uEDA2",
        Models.DriveType.Removable => "\uE88E",
        _ => "\uEDA2"
    };

    public string ProtectionStatusDisplay => ProtectionStatus switch
    {
        ProtectionStatus.On => "Protection On",
        ProtectionStatus.Off => "Protection Off",
        ProtectionStatus.Suspended => "Suspended",
        _ => "Unknown"
    };

    public string ProtectionGlyph => ProtectionStatus switch
    {
        ProtectionStatus.On => "\uE8FB",
        ProtectionStatus.Off => "\uE711",
        ProtectionStatus.Suspended => "\uE7BA",
        _ => "\uE9CE"
    };

    public string LockStatusDisplay => LockStatus switch
    {
        LockStatus.Locked => "Locked",
        LockStatus.Unlocked => "Unlocked",
        _ => "Unknown"
    };

    public string LockIconGlyph => LockStatus switch
    {
        LockStatus.Locked => "\uE72E",
        LockStatus.Unlocked => "\uE785",
        _ => "\uE897"
    };

    public static string? CurrentAppDrive { get; set; } = Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory)?.TrimEnd('\\');

    public bool IsLocked => LockStatus == LockStatus.Locked;
    public bool IsUnlocked => LockStatus == LockStatus.Unlocked;
    public bool IsOsDrive => DriveType == Models.DriveType.OperatingSystem ||
        string.Equals(DriveLetter, Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public bool IsAppDrive => !string.IsNullOrEmpty(CurrentAppDrive) &&
        string.Equals(DriveLetter, CurrentAppDrive, StringComparison.OrdinalIgnoreCase);

    public bool CanLock => !IsOsDrive && !IsAppDrive && IsUnlocked;
    public bool CanUnlock => IsLocked;

    public string LockDisabledReason
    {
        get
        {
            if (IsOsDrive) return "The OS drive cannot be locked.";
            if (IsAppDrive) return "The drive containing AppLock cannot be locked.";
            if (!IsUnlocked) return "The drive is already locked.";
            return string.Empty;
        }
    }

    public bool HasCapacity => TotalBytes > 0;
    public double CapacityUsedPercent => TotalBytes > 0
        ? Math.Clamp(((double)(TotalBytes - FreeBytes) / TotalBytes) * 100.0, 0, 100)
        : 0;

    public string CapacityDisplay => TotalBytes > 0
        ? $"{FormatBytes(FreeBytes)} free of {FormatBytes(TotalBytes)}"
        : (IsLocked ? "Volume locked" : "Capacity unavailable");

    public bool HasProtectors => KeyProtectors.Count > 0;
    public bool HasPasswordProtector => KeyProtectors.Any(p => p.TypeCode == 8);
    public bool HasRecoveryPasswordProtector => KeyProtectors.Any(p => p.TypeCode == 3);

    public string ProtectorsSummary => KeyProtectors.Count > 0
        ? string.Join(", ", KeyProtectors.Select(p => p.TypeName))
        : "None";

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.#} {suffixes[i]}";
    }
}

