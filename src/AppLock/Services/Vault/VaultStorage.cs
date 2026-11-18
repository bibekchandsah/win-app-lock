using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

public static class VaultStorage
{
    public const string ConfigFileName = "vault.config";
    public const string ConfigBakFileName = "vault.config.bak";
    public const string IndexFileName = "index.enc";
    public const string IndexBakFileName = "index.enc.bak";
    public const string LockFileName = "vault.lock";
    public const string DataDirName = "data";
    public const string TrashDirName = "trash";

    public static void InitializeVaultFolder(string vaultPath)
    {
        Directory.CreateDirectory(vaultPath);
        Directory.CreateDirectory(Path.Combine(vaultPath, DataDirName));
        Directory.CreateDirectory(Path.Combine(vaultPath, TrashDirName));
    }

    public static void SaveConfig(string vaultPath, VaultConfig config, byte[] configMacKey)
    {
        config.ConfigMac = VaultCrypto.ComputeConfigMac(config, configMacKey);

        var configPath = Path.Combine(vaultPath, ConfigFileName);
        var bakPath = Path.Combine(vaultPath, ConfigBakFileName);
        var tmpPath = Path.Combine(vaultPath, ConfigFileName + ".tmp");

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmpPath, json);

        AtomicReplace(tmpPath, configPath, bakPath);
    }

    public static VaultConfig LoadConfig(string vaultPath, byte[]? configMacKey = null, bool fallbackToBak = true, bool allowFastTest = false)
    {
        var configPath = Path.Combine(vaultPath, ConfigFileName);
        var bakPath = Path.Combine(vaultPath, ConfigBakFileName);

        Exception? primaryError = null;

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<VaultConfig>(json);
                if (config != null)
                {
                    VaultCrypto.EnforceKdfFloors(config.Kdf, allowFastTest);
                    if (configMacKey != null && !VaultCrypto.VerifyConfigMac(config, configMacKey))
                    {
                        throw new CryptographicException("vault.config MAC verification failed (header was tampered with).");
                    }
                    return config;
                }
            }
            catch (Exception ex)
            {
                primaryError = ex;
            }
        }
        else
        {
            primaryError = new FileNotFoundException("vault.config not found", configPath);
        }

        if (fallbackToBak && File.Exists(bakPath))
        {
            try
            {
                var bakJson = File.ReadAllText(bakPath);
                var bakConfig = JsonSerializer.Deserialize<VaultConfig>(bakJson);
                if (bakConfig != null)
                {
                    VaultCrypto.EnforceKdfFloors(bakConfig.Kdf, allowFastTest);
                    if (configMacKey != null && !VaultCrypto.VerifyConfigMac(bakConfig, configMacKey))
                    {
                        throw new CryptographicException("vault.config.bak MAC verification failed.");
                    }
                    return bakConfig;
                }
            }
            catch
            {
                // Both failed
            }
        }

        throw primaryError ?? new CryptographicException("Failed to load valid vault configuration.");
    }

    public static void SaveIndex(string vaultPath, VaultIndexData indexData, byte[] indexKey)
    {
        var encryptedBytes = VaultIndex.EncryptIndex(indexData, indexKey);

        var indexPath = Path.Combine(vaultPath, IndexFileName);
        var bakPath = Path.Combine(vaultPath, IndexBakFileName);
        var tmpPath = Path.Combine(vaultPath, IndexFileName + ".tmp");

        File.WriteAllBytes(tmpPath, encryptedBytes);
        AtomicReplace(tmpPath, indexPath, bakPath);
    }

    public static VaultIndexData LoadIndex(string vaultPath, byte[] indexKey, Guid vaultId, long? expectedMinRevision = null, bool fallbackToBak = true)
    {
        var indexPath = Path.Combine(vaultPath, IndexFileName);
        var bakPath = Path.Combine(vaultPath, IndexBakFileName);

        Exception? primaryError = null;

        if (File.Exists(indexPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(indexPath);
                return VaultIndex.DecryptIndex(bytes, indexKey, vaultId, expectedMinRevision);
            }
            catch (Exception ex)
            {
                primaryError = ex;
            }
        }
        else
        {
            primaryError = new FileNotFoundException("index.enc not found", indexPath);
        }

        if (fallbackToBak && File.Exists(bakPath))
        {
            try
            {
                var bakBytes = File.ReadAllBytes(bakPath);
                return VaultIndex.DecryptIndex(bakBytes, indexKey, vaultId, expectedMinRevision);
            }
            catch
            {
                // Fallback also failed
            }
        }

        throw primaryError ?? new CryptographicException("Failed to decrypt vault file index.");
    }

    public static string GetObjectPath(string vaultPath, string objectId, bool isTrash = false)
    {
        if (string.IsNullOrEmpty(objectId)) throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));
        string cleanId = Path.GetFileName(objectId);
        string prefix = cleanId.Length >= 2 ? cleanId.Substring(0, 2) : "00";

        var folder = Path.Combine(vaultPath, isTrash ? TrashDirName : DataDirName, prefix);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, cleanId);
    }

    public static List<string> ScanOrphanObjects(string vaultPath, HashSet<string> knownObjectIds)
    {
        var orphans = new List<string>();
        foreach (var subDir in new[] { DataDirName, TrashDirName })
        {
            var dirPath = Path.Combine(vaultPath, subDir);
            if (!Directory.Exists(dirPath)) continue;

            foreach (var file in Directory.EnumerateFiles(dirPath, "*.obj", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (!knownObjectIds.Contains(fileName))
                {
                    orphans.Add(file);
                }
            }
        }
        return orphans;
    }

    private static void AtomicReplace(string sourceTmpPath, string destinationPath, string backupPath)
    {
        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(sourceTmpPath, destinationPath, backupPath, ignoreMetadataErrors: true);
            }
            catch
            {
                // On filesystems where File.Replace fails (e.g. some network shares or FAT32)
                try
                {
                    File.Copy(destinationPath, backupPath, overwrite: true);
                }
                catch { }

                File.Copy(sourceTmpPath, destinationPath, overwrite: true);
                try { File.Delete(sourceTmpPath); } catch { }
            }
        }
        else
        {
            File.Move(sourceTmpPath, destinationPath);
        }
    }
}
