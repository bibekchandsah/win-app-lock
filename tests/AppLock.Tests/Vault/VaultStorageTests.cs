using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using AppLock.Models.Vault;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;
using Xunit;

namespace AppLock.Tests.Vault;

public class VaultStorageTests : IDisposable
{
    private readonly string _tempVaultDir;
    private readonly byte[] _configMacKey = new byte[32];
    private readonly byte[] _indexKey = new byte[32];

    public VaultStorageTests()
    {
        _tempVaultDir = Path.Combine(Path.GetTempPath(), "AppLockVaultTest_" + Guid.NewGuid().ToString("N"));
        VaultStorage.InitializeVaultFolder(_tempVaultDir);
        RandomNumberGenerator.Fill(_configMacKey);
        RandomNumberGenerator.Fill(_indexKey);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempVaultDir))
            {
                Directory.Delete(_tempVaultDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Storage_ConfigBackupFallback_WorksWhenPrimaryCorrupt()
    {
        var config = new VaultConfig
        {
            VaultId = Guid.NewGuid(),
            Name = "Primary Vault",
            Kdf = new KdfParams { Algorithm = "fast_test", MemorySizeKb = 65536, Iterations = 3 }
        };

        // Save generation 1
        VaultStorage.SaveConfig(_tempVaultDir, config, _configMacKey);

        // Save generation 2 (generation 1 moves to .bak)
        config.Name = "Updated Vault";
        VaultStorage.SaveConfig(_tempVaultDir, config, _configMacKey);

        // Verify primary loads
        var loaded = VaultStorage.LoadConfig(_tempVaultDir, _configMacKey, allowFastTest: true);
        Assert.Equal("Updated Vault", loaded.Name);

        // Corrupt primary vault.config
        var primaryFile = Path.Combine(_tempVaultDir, VaultStorage.ConfigFileName);
        File.WriteAllText(primaryFile, "CORRUPT JSON CONTENT");

        // Should fall back to vault.config.bak
        var fallbackLoaded = VaultStorage.LoadConfig(_tempVaultDir, _configMacKey, fallbackToBak: true, allowFastTest: true);
        Assert.NotNull(fallbackLoaded);
        Assert.Equal("Primary Vault", fallbackLoaded.Name);
    }

    [Fact]
    public void Storage_IndexBackupFallback_WorksWhenPrimaryCorrupt()
    {
        var vaultId = Guid.NewGuid();
        var index1 = new VaultIndex(vaultId);
        index1.AddItem(new IndexItem { Name = "file1.txt" });

        // Save generation 1
        VaultStorage.SaveIndex(_tempVaultDir, index1.Data, _indexKey);

        // Save generation 2
        var index2 = new VaultIndex(vaultId);
        index2.AddItem(new IndexItem { Name = "file2.txt" });
        index2.IncrementRevision();
        VaultStorage.SaveIndex(_tempVaultDir, index2.Data, _indexKey);

        // Corrupt primary index.enc
        var primaryFile = Path.Combine(_tempVaultDir, VaultStorage.IndexFileName);
        File.WriteAllBytes(primaryFile, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        // Fallback loads generation 1 from .bak
        var fallbackData = VaultStorage.LoadIndex(_tempVaultDir, _indexKey, vaultId, fallbackToBak: true);
        Assert.NotNull(fallbackData);
        Assert.Single(fallbackData.Items);
        Assert.Equal("file1.txt", fallbackData.Items[0].Name);
    }

    [Fact]
    public void Storage_FanOutPaths_AndOrphanDetection()
    {
        var objectId1 = "3fa85f64-5717-4562-b3fc-2c963f66afa6.obj";
        var objectId2 = "3fa85f64-9999-4562-b3fc-2c963f66afa6.obj";
        var orphanId = "a1b2c3d4-0000-0000-0000-000000000000.obj";

        var path1 = VaultStorage.GetObjectPath(_tempVaultDir, objectId1);
        var path2 = VaultStorage.GetObjectPath(_tempVaultDir, objectId2);
        var pathOrphan = VaultStorage.GetObjectPath(_tempVaultDir, orphanId);

        // Fan out uses first 2 characters
        Assert.Contains(Path.Combine("data", "3f"), path1);
        Assert.Contains(Path.Combine("data", "a1"), pathOrphan);

        File.WriteAllText(path1, "obj1");
        File.WriteAllText(path2, "obj2");
        File.WriteAllText(pathOrphan, "orphan");

        var known = new HashSet<string> { objectId1, objectId2 };
        var orphans = VaultStorage.ScanOrphanObjects(_tempVaultDir, known);

        Assert.Single(orphans);
        Assert.Equal(pathOrphan, orphans[0]);
    }
}
