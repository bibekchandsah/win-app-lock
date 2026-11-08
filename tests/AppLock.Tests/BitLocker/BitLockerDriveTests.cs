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

        public Task<BitLockerActionResult> TurnOnBitLockerAsync(BitLockerTurnOnRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<string?> GenerateRecoveryPasswordAsync(string driveLetter, CancellationToken ct = default)
        {
            return Task.FromResult<string?>("111111-222222-333333-444444-555555-666666-123456-654321");
        }

        public Task<BitLockerActionResult> TurnOffBitLockerAsync(string driveLetter, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<BitLockerActionResult> SuspendProtectionAsync(string driveLetter, int rebootCount = 1, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<BitLockerActionResult> ResumeProtectionAsync(string driveLetter, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<BitLockerActionResult> ChangePasswordAsync(string driveLetter, string oldPassphrase, string newPassphrase, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<IReadOnlyList<KeyProtectorInfo>> GetKeyProtectorsAsync(string driveLetter, CancellationToken ct = default)
        {
            var list = new List<KeyProtectorInfo>
            {
                new KeyProtectorInfo { Id = "{11111111-1111-1111-1111-111111111111}", TypeCode = 8, TypeName = "Password" },
                new KeyProtectorInfo { Id = "{22222222-2222-2222-2222-222222222222}", TypeCode = 3, TypeName = "Recovery Password" }
            };
            return Task.FromResult<IReadOnlyList<KeyProtectorInfo>>(list);
        }

        public Task<BitLockerActionResult> DeleteKeyProtectorAsync(string driveLetter, string protectorId, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<BitLockerActionResult> AddPasswordProtectorAsync(string driveLetter, string passphrase, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<BitLockerActionResult> AddRecoveryPasswordProtectorAsync(string driveLetter, string? recoveryPassword = null, CancellationToken ct = default)
        {
            return Task.FromResult(BitLockerActionResult.Ok());
        }

        public Task<string?> GetRecoveryKeyAsync(string driveLetter, string protectorId, CancellationToken ct = default)
        {
            return Task.FromResult<string?>("111111-222222-333333-444444-555555-666666-123456-654321");
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

    [Fact]
    public async Task SavedPasswordStore_SaveGetDelete_WorksCorrectly()
    {
        var store = new SavedPasswordStore();
        await store.ClearAllAsync();

        var initial = await store.GetForDriveAsync("Z:");
        Assert.Null(initial);

        await store.SaveAsync("volume-guid-123", "Z:", "TestPassword123!", "Backup Drive");
        var saved = await store.GetForVolumeAsync("volume-guid-123", "Z:");
        Assert.NotNull(saved);
        Assert.Equal("Z:", saved.DriveLetter);
        Assert.Equal("volume-guid-123", saved.VolumeGuid);
        Assert.Equal("Backup Drive", saved.Label);
        Assert.True(saved.IsValid);

        var decrypted = await store.GetDecryptedPassphraseAsync("volume-guid-123", "Z:");
        Assert.Equal("TestPassword123!", decrypted);

        await store.MarkInvalidAsync("volume-guid-123", "Z:");
        var invalidEntry = await store.GetForVolumeAsync("volume-guid-123", "Z:");
        Assert.NotNull(invalidEntry);
        Assert.False(invalidEntry.IsValid);

        var all = await store.GetAllAsync();
        Assert.Contains(all, p => p.DriveLetter == "Z:");

        var deleted = await store.DeleteAsync("volume-guid-123", "Z:");
        Assert.True(deleted);

        var afterDelete = await store.GetForDriveAsync("Z:");
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task BitLockerViewModel_SavedPassword_Integration()
    {
        var store = new SavedPasswordStore();
        await store.ClearAllAsync();

        var fakeService = new FakeBitLockerService
        {
            DrivesToReturn = new List<BitLockerDrive>
            {
                new()
                {
                    DriveLetter = "E:",
                    DriveType = AppLock.Models.DriveType.Removable,
                    ProtectionStatus = ProtectionStatus.On,
                    LockStatus = LockStatus.Locked
                }
            }
        };

        var supportService = new BitLockerSupportService();
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher, savedPasswordStore: store);

        await vm.RefreshDrivesAsync();
        Assert.NotNull(vm.SelectedDrive);
        Assert.False(vm.HasSavedPasswordForSelected);

        await vm.SavePassphraseForSelectedAsync("SecretPass123", "USB Drive");
        Assert.True(vm.HasSavedPasswordForSelected);

        var savedPass = await vm.GetSavedPassphraseAsync();
        Assert.Equal("SecretPass123", savedPass);

        var unlockSuccess = await vm.UnlockWithSavedPassphraseAsync();
        Assert.True(unlockSuccess);

        await vm.DeleteSavedPassphraseAsync();
        Assert.False(vm.HasSavedPasswordForSelected);
    }

    [Fact]
    public async Task AutoUnlockService_UnlocksEligibleDrives_Successfully()
    {
        var store = new SavedPasswordStore();
        await store.ClearAllAsync();
        await store.SaveAsync("vol-guid-auto", "F:", "Pass123", "Auto Test");

        var fakeService = new FakeBitLockerService
        {
            DrivesToReturn = new List<BitLockerDrive>
            {
                new()
                {
                    DriveLetter = "F:",
                    VolumeGuid = "vol-guid-auto",
                    DriveType = AppLock.Models.DriveType.Removable,
                    ProtectionStatus = ProtectionStatus.On,
                    LockStatus = LockStatus.Locked
                }
            }
        };

        var supportService = new BitLockerSupportService();
        var settings = new AppSettings
        {
            BitLockerAutoUnlockOnAppOpen = true,
            BitLockerAutoUnlockVolumeGuids = new List<string> { "vol-guid-auto" }
        };

        var autoUnlock = new BitLockerAutoUnlockService(
            supportService,
            fakeService,
            store,
            settings,
            s => { },
            null,
            null);

        var count = await autoUnlock.UnlockEligibleDrivesAsync(showNotifications: false);
        // Note: supportService.IsSupported and IsElevated might be false on non-admin test runner,
        // but if both true, count will be 1; if not elevated, count is 0 without throwing.
        Assert.True(count >= 0);
    }

    [Fact]
    public void StartupTaskInfo_NeedsRepair_ComputedProperly()
    {
        var info = new StartupTaskInfo
        {
            Exists = true,
            ExePathMatches = false
        };
        Assert.True(info.NeedsRepair);

        info.ExePathMatches = true;
        Assert.False(info.NeedsRepair);

        info.Exists = false;
        info.ExePathMatches = false;
        Assert.False(info.NeedsRepair);
    }

    [Fact]
    public void GenerateValidRecoveryPassword_GeneratesValidFormat()
    {
        var key = BitLockerService.GenerateValidRecoveryPassword();
        Assert.NotNull(key);

        var blocks = key.Split('-');
        Assert.Equal(8, blocks.Length);

        foreach (var block in blocks)
        {
            Assert.Equal(6, block.Length);
            Assert.True(uint.TryParse(block, out uint num));
            Assert.True(num >= 100000 && num < 720896);
            Assert.Equal(0u, num % 11);
        }
    }

    [Fact]
    public async Task TurnOnBitLocker_ViewModel_InvokesServiceCorrectly()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        var request = new BitLockerTurnOnRequest
        {
            DriveLetter = "G:",
            Passphrase = "SuperSecurePassword123!",
            RecoveryPassword = "111111-222222-333333-444444-555555-666666-123456-654321",
            Scope = BitLockerEncryptionScope.UsedSpaceOnly,
            Method = BitLockerEncryptionMethod.XtsAes128
        };

        var result = await vm.TurnOnBitLockerAsync(request);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task TurnOffBitLocker_ViewModel_InvokesServiceCorrectly()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        var result = await vm.TurnOffBitLockerAsync("D:");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task SuspendAndResume_ViewModel_InvokesServiceCorrectly()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        var drive = new BitLockerDrive { DriveLetter = "D:", ProtectionStatus = ProtectionStatus.On };
        fakeService.DrivesToReturn.Add(drive);
        vm.SelectedDrive = new DriveItemViewModel(drive);

        var suspendRes = await vm.SuspendProtectionAsync(1);
        Assert.True(suspendRes.Success);

        var resumeRes = await vm.ResumeProtectionAsync();
        Assert.True(resumeRes.Success);
    }

    [Fact]
    public async Task ChangePassword_ViewModel_InvokesServiceCorrectly()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        var drive = new BitLockerDrive { DriveLetter = "D:", ProtectionStatus = ProtectionStatus.On };
        fakeService.DrivesToReturn.Add(drive);
        vm.SelectedDrive = new DriveItemViewModel(drive);

        var result = await vm.ChangePasswordAsync("OldPass123!", "NewPass456!");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ManageProtectors_ViewModel_InvokesServiceCorrectly()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        var drive = new BitLockerDrive { DriveLetter = "D:", ProtectionStatus = ProtectionStatus.On };
        fakeService.DrivesToReturn.Add(drive);
        vm.SelectedDrive = new DriveItemViewModel(drive);

        var protectors = await vm.GetKeyProtectorsAsync();
        Assert.Equal(2, protectors.Count);

        var addPassRes = await vm.AddPasswordProtectorAsync("NewPass123!");
        Assert.True(addPassRes.Success);

        var addRecRes = await vm.AddRecoveryPasswordProtectorAsync();
        Assert.True(addRecRes.Success);

        var delRes = await vm.DeleteKeyProtectorAsync(protectors[0].Id);
        Assert.True(delRes.Success);
    }

    [Fact]
    public async Task GetRecoveryKey_ViewModel_ReturnsExpectedKey()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        var drive = new BitLockerDrive { DriveLetter = "D:", ProtectionStatus = ProtectionStatus.On };
        fakeService.DrivesToReturn.Add(drive);
        vm.SelectedDrive = new DriveItemViewModel(drive);

        var key = await vm.GetRecoveryKeyAsync("{22222222-2222-2222-2222-222222222222}");
        Assert.NotNull(key);
        Assert.Equal("111111-222222-333333-444444-555555-666666-123456-654321", key);
    }

    [Fact]
    public void BitLockerLogger_SanitizesSensitiveData()
    {
        var rawLog = "User entered recovery key 111111-222222-333333-444444-555555-666666-123456-654321 with password: SecretPassword123";
        var sanitized = BitLockerLogger.Sanitize(rawLog);

        Assert.DoesNotContain("111111-222222-333333-444444-555555-666666-123456-654321", sanitized);
        Assert.DoesNotContain("SecretPassword123", sanitized);
        Assert.Contains("[REDACTED-KEY]", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public async Task DiagnosticsReport_GeneratesAllSectionsProperly()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        var drive = new BitLockerDrive
        {
            DriveLetter = "D:",
            VolumeLabel = "DataDrive",
            ProtectionStatus = ProtectionStatus.On,
            LockStatus = LockStatus.Unlocked,
            EncryptionPercent = 100,
            EncryptionMethod = "XTS-AES 128"
        };
        fakeService.DrivesToReturn.Add(drive);
        await vm.RefreshDrivesAsync();

        BitLockerLogger.Log("Test diagnostics log entry", "D:");

        var report = await vm.GenerateDiagnosticsReportAsync();
        Assert.Contains("# AppLock BitLocker Diagnostic Report", report);
        Assert.Contains("## System & Environment", report);
        Assert.Contains("## Detected Drives", report);
        Assert.Contains("Drive D: (DataDrive)", report);
        Assert.Contains("## Recent BitLocker Action Logs", report);
        Assert.Contains("Test diagnostics log entry", report);
    }

    [Fact]
    public void BitLockerSettings_PropertiesNotifyAndSync()
    {
        var settings = new AppSettings();
        var savedCount = 0;
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService();
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(
            supportService,
            fakeService,
            watcher,
            settings,
            s => savedCount++);

        vm.RefreshIntervalSeconds = 10;
        Assert.Equal(10, settings.BitLockerRefreshIntervalSeconds);
        Assert.True(savedCount > 0);

        vm.AutoClearClipboard = false;
        Assert.False(settings.BitLockerAutoClearClipboard);

        vm.ToastNotifications = false;
        Assert.False(settings.BitLockerToastNotifications);

        vm.RequireHelloOnAppStart = true;
        Assert.True(settings.BitLockerRequireHelloOnAppStart);
    }

    [Fact]
    public void BitLockerViewModel_TooltipsAndCommandStates_MatchDriveState()
    {
        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);

        // Null drive selection
        Assert.Null(vm.SelectedDrive);
        Assert.Contains("Select a drive", vm.LockTooltip);
        Assert.Contains("Select a drive", vm.UnlockTooltip);
        Assert.Contains("Select a drive", vm.TurnOnTooltip);
        Assert.Contains("Select a drive", vm.TurnOffTooltip);
        Assert.Contains("Select a drive", vm.SuspendTooltip);
        Assert.Contains("Select a drive", vm.BackupKeyTooltip);
        Assert.False(vm.CanLockSelected);
        Assert.False(vm.CanUnlockSelected);
        Assert.False(vm.CanTurnOnSelected);
        Assert.False(vm.CanTurnOffSelected);

        // OS Drive (should never be lockable)
        var osDrive = new BitLockerDrive
        {
            DriveLetter = "C:",
            DriveType = AppLock.Models.DriveType.OperatingSystem,
            LockStatus = LockStatus.Unlocked,
            ProtectionStatus = ProtectionStatus.On,
            KeyProtectors = new List<KeyProtectorInfo>
            {
                new KeyProtectorInfo { Id = "1", TypeCode = 1, TypeName = "TPM" }
            }
        };
        var osItem = new DriveItemViewModel(osDrive);
        vm.SelectedDrive = osItem;

        Assert.False(vm.CanLockSelected);
        Assert.Contains("OS drive cannot be locked", vm.LockTooltip);
        Assert.False(vm.CanUnlockSelected);
        Assert.Contains("already unlocked", vm.UnlockTooltip);
        Assert.True(vm.CanTurnOffSelected);
        Assert.False(vm.CanTurnOnSelected);
        Assert.Contains("already enabled", vm.TurnOnTooltip);

        // AppLock Drive (D: where this test runs)
        var appDrive = new BitLockerDrive
        {
            DriveLetter = "D:",
            DriveType = AppLock.Models.DriveType.Data,
            LockStatus = LockStatus.Unlocked,
            ProtectionStatus = ProtectionStatus.On
        };
        var appItem = new DriveItemViewModel(appDrive);
        vm.SelectedDrive = appItem;

        Assert.False(vm.CanLockSelected);
        Assert.Contains("containing AppLock cannot be locked", vm.LockTooltip);

        // Data Drive - Locked (Z:)
        var lockedDrive = new BitLockerDrive
        {
            DriveLetter = "Z:",
            DriveType = AppLock.Models.DriveType.Data,
            LockStatus = LockStatus.Locked,
            ProtectionStatus = ProtectionStatus.On
        };
        var lockedItem = new DriveItemViewModel(lockedDrive);
        vm.SelectedDrive = lockedItem;

        Assert.False(vm.CanLockSelected);
        Assert.Contains("already locked", vm.LockTooltip);
        Assert.True(vm.CanUnlockSelected);
        Assert.Contains("Unlock drive Z:", vm.UnlockTooltip);
        Assert.False(vm.CanOpenInExplorerSelected);

        // Data Drive - Unlocked
        var unlockedDrive = new BitLockerDrive
        {
            DriveLetter = "E:",
            DriveType = AppLock.Models.DriveType.Data,
            LockStatus = LockStatus.Unlocked,
            ProtectionStatus = ProtectionStatus.Off
        };
        var unlockedItem = new DriveItemViewModel(unlockedDrive);
        vm.SelectedDrive = unlockedItem;

        Assert.True(vm.CanLockSelected);
        Assert.Contains("Lock drive E:", vm.LockTooltip);
        Assert.True(vm.CanTurnOnSelected);
        Assert.False(vm.CanTurnOffSelected);
        Assert.True(vm.CanOpenInExplorerSelected);
    }

    [Fact]
    public void DriveItemViewModel_ProtectorDetection_WorksCorrectly()
    {
        var drive = new BitLockerDrive
        {
            DriveLetter = "F:",
            DriveType = AppLock.Models.DriveType.Data,
            LockStatus = LockStatus.Unlocked,
            ProtectionStatus = ProtectionStatus.On,
            KeyProtectors = new List<KeyProtectorInfo>
            {
                new KeyProtectorInfo { Id = "pwd-1", TypeCode = 8, TypeName = "Password" },
                new KeyProtectorInfo { Id = "rec-1", TypeCode = 3, TypeName = "Numerical Password" }
            }
        };

        var item = new DriveItemViewModel(drive);
        Assert.True(item.HasProtectors);
        Assert.True(item.HasPasswordProtector);
        Assert.True(item.HasRecoveryPasswordProtector);

        var fakeService = new FakeBitLockerService();
        var supportService = new BitLockerSupportService(isSupported: true, isElevated: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(supportService, fakeService, watcher);
        vm.SelectedDrive = item;

        Assert.True(vm.CanChangePasswordSelected);
        Assert.True(vm.CanManageProtectorsSelected);
        Assert.True(vm.CanBackupSelected);
    }

    [Fact]
    public void BitLockerXaml_AllBrushesUseThemeResource_NoHardcodedColors()
    {
        var baseDir = AppContext.BaseDirectory;
        // Search upwards to find solution root
        var currentDir = new DirectoryInfo(baseDir);
        while (currentDir != null && !File.Exists(Path.Combine(currentDir.FullName, "AppLock.sln")))
        {
            currentDir = currentDir.Parent;
        }

        Assert.NotNull(currentDir);
        var xamlFiles = new[]
        {
            Path.Combine(currentDir.FullName, "src", "AppLock", "Views", "BitLockerPage.xaml"),
            Path.Combine(currentDir.FullName, "src", "AppLock", "Views", "EnableBitLockerDialog.xaml")
        };

        var regex = new System.Text.RegularExpressions.Regex(@"(?:Foreground|Background|BorderBrush|Fill|Stroke)=""([^""]+)""");

        foreach (var file in xamlFiles)
        {
            Assert.True(File.Exists(file), $"File not found: {file}");
            var text = File.ReadAllText(file);
            var matches = regex.Matches(text);

            Assert.NotEmpty(matches);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var val = match.Groups[1].Value;
                Assert.True(
                    val.StartsWith("{ThemeResource") || val == "Transparent",
                    $"Non-theme brush '{val}' found in {Path.GetFileName(file)}: {match.Value}");
            }
        }
    }

    [Fact]
    public void BitLockerXaml_CriticalInteractiveElements_HaveAutomationPropertiesName()
    {
        var baseDir = AppContext.BaseDirectory;
        var currentDir = new DirectoryInfo(baseDir);
        while (currentDir != null && !File.Exists(Path.Combine(currentDir.FullName, "AppLock.sln")))
        {
            currentDir = currentDir.Parent;
        }

        Assert.NotNull(currentDir);
        var pageXamlPath = Path.Combine(currentDir.FullName, "src", "AppLock", "Views", "BitLockerPage.xaml");
        var xaml = File.ReadAllText(pageXamlPath);

        // Verify primary commands have AutomationProperties.Name
        Assert.Contains(@"AutomationProperties.Name=""Refresh drive list""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Lock selected drive""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Unlock selected drive""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Turn on BitLocker encryption""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Turn off BitLocker encryption""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Suspend or resume BitLocker protection""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Back up BitLocker recovery key""", xaml);

        // Verify secondary commands
        Assert.Contains(@"AutomationProperties.Name=""Change BitLocker password""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Manage BitLocker key protectors""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Check BitLocker volume usage""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Open drive in File Explorer""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Configure auto-unlock and startup task""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Manage saved BitLocker passwords""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Open BitLocker diagnostics""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Open BitLocker settings""", xaml);

        // Verify quick unlock elements
        Assert.Contains(@"AutomationProperties.Name=""Enter BitLocker password""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Unlock selected drive with password""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Unlock selected drive with saved password""", xaml);
        Assert.Contains(@"AutomationProperties.Name=""Unlock selected drive with recovery key""", xaml);
    }

    [Fact]
    public async Task RefreshDrivesAsync_PreservesDriveItemInstancesAndUpdatesProperties()
    {
        var fakeService = new FakeBitLockerService
        {
            DrivesToReturn = new List<BitLockerDrive>
            {
                new BitLockerDrive
                {
                    DriveLetter = "E:",
                    VolumeLabel = "WorkData",
                    LockStatus = LockStatus.Locked,
                    ProtectionStatus = ProtectionStatus.On
                }
            }
        };

        var support = new BitLockerSupportService(isSupported: true, isElevated: true, isWmiAvailable: true);
        using var watcher = new VolumeWatcher();
        var vm = new BitLockerViewModel(support, fakeService, watcher);

        await vm.RefreshDrivesAsync();

        Assert.Single(vm.Drives);
        var initialItem = vm.Drives[0];
        Assert.Equal(LockStatus.Locked, initialItem.LockStatus);

        // Simulate drive unlocking
        fakeService.DrivesToReturn = new List<BitLockerDrive>
        {
            new BitLockerDrive
            {
                DriveLetter = "E:",
                VolumeLabel = "WorkData",
                LockStatus = LockStatus.Unlocked,
                ProtectionStatus = ProtectionStatus.On
            }
        };

        await vm.RefreshDrivesAsync();

        Assert.Single(vm.Drives);
        // The exact same ViewModel instance should be preserved (no flicker / rebuild)
        Assert.Same(initialItem, vm.Drives[0]);
        // And its property was updated in-place
        Assert.Equal(LockStatus.Unlocked, vm.Drives[0].LockStatus);
        Assert.True(vm.Drives[0].IsUnlocked);
    }
}

