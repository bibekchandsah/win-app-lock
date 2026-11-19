using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AppLock.Models.Vault;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Tests.Vault;

public class VaultManagementTests : IDisposable
{
    private readonly string _testDir;

    public VaultManagementTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AppLock_Test_Mgmt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    private static string ReadLockFileContent(string lockFilePath)
    {
        using var fs = new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    [Fact]
    public void VaultLockFile_Acquire_CreatesLockFileAndDisposes()
    {
        var lockFilePath = Path.Combine(_testDir, VaultLockFile.LockFileName);
        Assert.False(File.Exists(lockFilePath));

        using (var handle = VaultLockFile.Acquire(_testDir))
        {
            Assert.True(File.Exists(lockFilePath));
            var json = ReadLockFileContent(lockFilePath);
            var info = JsonSerializer.Deserialize<VaultLockInfo>(json);
            Assert.NotNull(info);
            Assert.Equal(Environment.ProcessId, info.ProcessId);
        }

        // On dispose, lock file is deleted
        Assert.False(File.Exists(lockFilePath));
    }

    [Fact]
    public void VaultLockFile_ActiveConflict_ThrowsConflictException()
    {
        using var handle1 = VaultLockFile.Acquire(_testDir);

        // Attempting to acquire again without forceTakeOver should throw
        var ex = Assert.Throws<VaultLockConflictException>(() => VaultLockFile.Acquire(_testDir, forceTakeOver: false));
        Assert.NotNull(ex.LockInfo);
        Assert.Equal(Environment.ProcessId, ex.LockInfo.ProcessId);
    }

    [Fact]
    public void VaultLockFile_ForceTakeover_Succeeds()
    {
        var handle1 = VaultLockFile.Acquire(_testDir);
        Assert.NotNull(handle1);

        try
        {
            // Force takeover should overwrite
            using var handle2 = VaultLockFile.Acquire(_testDir, forceTakeOver: true);
            Assert.NotNull(handle2);
        }
        finally
        {
            handle1.Dispose();
        }
    }

    [Fact]
    public void VaultLockFile_StaleLockFromDeadPid_SilentlyTakenOver()
    {
        var lockFilePath = Path.Combine(_testDir, VaultLockFile.LockFileName);

        // Write a fake lock file with a dead or non-AppLock PID (e.g. PID 999999)
        var staleInfo = new VaultLockInfo
        {
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            ProcessId = 999999,
            LockedAtUtc = DateTime.UtcNow.AddHours(-1)
        };
        File.WriteAllText(lockFilePath, JsonSerializer.Serialize(staleInfo));

        // Acquire should silently take it over
        using var handle = VaultLockFile.Acquire(_testDir, forceTakeOver: false);
        Assert.NotNull(handle);

        var json = ReadLockFileContent(lockFilePath);
        var activeInfo = JsonSerializer.Deserialize<VaultLockInfo>(json);
        Assert.NotNull(activeInfo);
        Assert.Equal(Environment.ProcessId, activeInfo.ProcessId);
    }

    [Fact]
    public async Task HelloKeyService_Enroll_Unlock_Revoke_RoundTrip()
    {
        var vaultId = Guid.NewGuid();
        var vmk = new byte[32];
        RandomNumberGenerator.Fill(vmk);

        var config = new VaultConfig
        {
            VaultId = vaultId,
            Name = "HelloTestVault"
        };
        var index = new VaultIndexData { VaultId = vaultId };

        using var session = new VaultSession(vaultId, _testDir, "HelloTestVault", vmk, index);

        // 1. Enroll
        HelloKeyService.Enroll(session, config);

        var helloSlot = config.Slots.Find(s => s.Type == "windows_hello");
        Assert.NotNull(helloSlot);
        Assert.True(HelloKeyService.IsEnrolledForVault(vaultId));

        // 2. Unlock
        var reg = new VaultRegistration { Id = vaultId, Name = "HelloTestVault", Path = _testDir };
        var unlockedVmk = await HelloKeyService.UnlockAsync(reg, config, "Test verification", CancellationToken.None);

        Assert.Equal(vmk, unlockedVmk);

        // 3. Revoke
        HelloKeyService.Revoke(vaultId, config);
        Assert.Null(config.Slots.Find(s => s.Type == "windows_hello"));
        Assert.False(HelloKeyService.IsEnrolledForVault(vaultId));
    }

    [Fact]
    public void OrphanObjects_Scan_DetectsUnreferencedFiles()
    {
        VaultStorage.InitializeVaultFolder(_testDir);

        var dataDir = Path.Combine(_testDir, "data", "a1");
        Directory.CreateDirectory(dataDir);

        var knownFile = Path.Combine(dataDir, "a1b2c3d4e5f6.obj");
        var orphanFile = Path.Combine(dataDir, "a19999999999.obj");

        File.WriteAllBytes(knownFile, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(orphanFile, new byte[] { 4, 5, 6 });

        var knownSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a1b2c3d4e5f6.obj"
        };

        var orphans = VaultStorage.ScanOrphanObjects(_testDir, knownSet);
        Assert.Single(orphans);
        Assert.Equal(Path.GetFullPath(orphanFile), Path.GetFullPath(orphans[0]));
    }

    [Fact]
    public void IndexRollback_Detected_WhenRevisionIsLowerThanExpected()
    {
        var vaultId = Guid.NewGuid();
        var indexKey = new byte[32];
        RandomNumberGenerator.Fill(indexKey);

        VaultStorage.InitializeVaultFolder(_testDir);

        // Save index revision 5
        var indexData = new VaultIndexData
        {
            VaultId = vaultId,
            Revision = 5,
            Updated = DateTime.UtcNow
        };
        VaultStorage.SaveIndex(_testDir, indexData, indexKey);

        // Trying to load with expectedMinRevision = 10 must throw CryptographicException
        var ex = Assert.Throws<CryptographicException>(() =>
            VaultStorage.LoadIndex(_testDir, indexKey, vaultId, expectedMinRevision: 10, fallbackToBak: false));

        Assert.Contains("Index rollback detected", ex.Message);
    }
}
