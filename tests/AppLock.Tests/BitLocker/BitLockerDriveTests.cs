using AppLock.Models;
using AppLock.Services.BitLocker;
using AppLock.ViewModels;
using Xunit;

namespace AppLock.Tests.BitLocker;

public class BitLockerDriveTests
{
    private class FakeBitLockerService : IBitLockerService
    {
        public List<BitLockerDrive> DrivesToReturn { get; set; } = new();

        public Task<IReadOnlyList<BitLockerDrive>> GetDrivesAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<BitLockerDrive>>(DrivesToReturn);
        }

        public Task<BitLockerActionResult> UnlockWithPassphraseAsync(string driveLetter, string passphrase, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<BitLockerActionResult> UnlockWithNumericalPasswordAsync(string driveLetter, string numericalPassword, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<BitLockerActionResult> LockAsync(string driveLetter, bool forceDismount = true, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }
    }

    [Theory]
    [InlineData(1, "TPM")]
    [InlineData(2, "External Key")]
    [InlineData(3, "Recovery Password")]
    [InlineData(4, "TPM + PIN")]
    [InlineData(5, "TPM + Key")]
    [InlineData(6, "TPM + PIN + Key")]
    [InlineData(7, "Public Key")]
    [InlineData(8, "Password")]
    [InlineData(9, "TPM Network Key")]
    [InlineData(999, "Unknown Protector")]
    public void MapProtectorType_MapsCorrectly(int typeCode, string expected)
    {
        var result = BitLockerService.MapProtectorType(typeCode);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, "None")]
    [InlineData(1, "AES-128 (Diffuser)")]
    [InlineData(2, "AES-256 (Diffuser)")]
    [InlineData(3, "AES-128")]
    [InlineData(4, "AES-256")]
    [InlineData(5, "Hardware Encryption")]
    [InlineData(6, "XTS-AES 128")]
    [InlineData(7, "XTS-AES 256")]
    [InlineData(99, "Unknown")]
    public void MapEncryptionMethod_MapsCorrectly(uint method, string expected)
    {
        var result = BitLockerService.MapEncryptionMethod(method);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 0, "Fully Decrypted")]
    [InlineData(1, 100, "Fully Encrypted")]
    [InlineData(2, 50, "Encrypting (50%)")]
    [InlineData(3, 30, "Decrypting (30%)")]
    [InlineData(4, 20, "Encryption Paused (20%)")]
    [InlineData(5, 10, "Decryption Paused (10%)")]
    public void MapConversionStatus_MapsCorrectly(uint status, uint percent, string expected)
    {
        var result = BitLockerService.MapConversionStatus(status, percent);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DriveItemViewModel_FormatsCapacityAndLockStateCorrectly()
    {
        var drive = new BitLockerDrive
        {
            DriveLetter = "D:",
            VolumeLabel = "DataDrive",
            VolumeGuid = "\\\\?\\Volume{1234}\\",
            DriveType = AppLock.Models.DriveType.Data,
            ProtectionStatus = ProtectionStatus.On,
            LockStatus = LockStatus.Locked,
            EncryptionPercent = 100,
            ConversionStatus = "Fully Encrypted",
            EncryptionMethod = "XTS-AES 256",
            TotalBytes = 500_000_000_000,
            FreeBytes = 250_000_000_000,
            KeyProtectors = new List<KeyProtectorInfo>
            {
                new() { Id = "{ABC}", TypeCode = 3, TypeName = "Recovery Password" }
            }
        };

        var vm = new DriveItemViewModel(drive);

        Assert.Equal("D: (DataDrive)", vm.DisplayName);
        Assert.Equal("Fixed Data", vm.DriveTypeDisplay);
        Assert.Equal("Protection On", vm.ProtectionStatusDisplay);
        Assert.Equal("Locked", vm.LockStatusDisplay);
        Assert.True(vm.IsLocked);
        Assert.False(vm.IsUnlocked);
        Assert.False(vm.CanLock); // locked drive cannot be locked again
        Assert.True(vm.CanUnlock);
        Assert.Equal(50.0, vm.CapacityUsedPercent);
        Assert.True(vm.HasProtectors);
        Assert.Equal("Recovery Password", vm.ProtectorsSummary);
    }

    [Fact]
    public async Task BitLockerViewModel_RefreshesAndSelectsDrive()
    {
        var fakeService = new FakeBitLockerService
        {
            DrivesToReturn = new List<BitLockerDrive>
            {
                new()
                {
                    DriveLetter = "C:",
                    VolumeLabel = "OS",
                    DriveType = AppLock.Models.DriveType.OperatingSystem,
                    ProtectionStatus = ProtectionStatus.On,
                    LockStatus = LockStatus.Unlocked,
                    EncryptionPercent = 100,
                    ConversionStatus = "Fully Encrypted",
                    EncryptionMethod = "XTS-AES 128"
                },
                new()
                {
                    DriveLetter = "E:",
                    VolumeLabel = "USB",
                    DriveType = AppLock.Models.DriveType.Removable,
                    ProtectionStatus = ProtectionStatus.Off,
                    LockStatus = LockStatus.Unlocked,
                    EncryptionPercent = 0
                }
            }
        };

        var supportService = new BitLockerSupportService();
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        await vm.RefreshDrivesAsync();

        Assert.Equal(2, vm.Drives.Count);
        Assert.True(vm.HasDrives);
        Assert.False(vm.IsEmpty);
        Assert.NotNull(vm.SelectedDrive);
        Assert.Equal("C:", vm.SelectedDrive.DriveLetter);
        Assert.True(vm.SelectedDrive.IsOsDrive);
        Assert.False(vm.SelectedDrive.CanLock); // OS drive can't be locked
    }
}

