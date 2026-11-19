using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

public class ReWrapResult
{
    public int SuccessCount { get; set; }
    public int UnreachableCount { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// High-level orchestration for vault management, admin password integration,
/// re-wrapping, and synchronization.
/// </summary>
public static class VaultManager
{
    private static KdfParams MakeFreshKdfParams()
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        return new KdfParams
        {
            Algorithm    = "argon2id",
            MemorySizeKb = 262144, // 256 MiB
            Iterations   = 3,
            Parallelism  = 4,
            Salt         = Convert.ToBase64String(salt)
        };
    }

    /// <summary>
    /// Re-wraps the VMK of all reachable vaults that use the AppLock admin password.
    /// Unreachable vaults are flagged PendingRewrap = true so they can be synced later.
    /// </summary>
    public static async Task<ReWrapResult> ReWrapAdminPasswordVaultsAsync(
        string oldAdminPassword,
        string newAdminPassword)
    {
        var result = new ReWrapResult();
        var settings = ServiceContainer.Settings.Vault;
        var kdf = new Argon2Kdf();

        foreach (var reg in settings.Vaults)
        {
            if (!reg.UsesAdminPassword) continue;

            if (!Directory.Exists(reg.Path))
            {
                reg.PendingRewrap = true;
                result.UnreachableCount++;
                LogService.LogAction("Vault", $"Vault '{reg.Name}' unreachable; marked PendingRewrap.");
                continue;
            }

            try
            {
                await Task.Run(() =>
                {
                    var config = VaultStorage.LoadConfig(reg.Path);

                    var oldSlot = config.Slots.FirstOrDefault(s => s.Type == "password");
                    if (oldSlot == null)
                        throw new CryptographicException("Vault config has no password slot.");

                    var vmk = KeySlots.UnlockPasswordSlot(oldSlot, config.VaultId, oldAdminPassword, kdf);
                    try
                    {
                        if (!VaultCrypto.CheckVerifier(vmk, config.VaultId, config.Verifier))
                            throw new CryptographicException("Verifier check failed during rewrap.");

                        var newSlot = KeySlots.CreatePasswordSlot(vmk, config.VaultId, newAdminPassword, MakeFreshKdfParams(), kdf);

                        config.Slots.Remove(oldSlot);
                        config.Slots.Add(newSlot);

                        var newMacKey = VaultCrypto.DeriveConfigMacKey(vmk, config.VaultId);
                        VaultStorage.SaveConfig(reg.Path, config, newMacKey);
                        reg.PendingRewrap = false;
                        result.SuccessCount++;
                        LogService.LogAction("Vault", $"Successfully re-wrapped vault '{reg.Name}' with new admin password.");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(vmk);
                    }
                });
            }
            catch (Exception ex)
            {
                reg.PendingRewrap = true;
                result.Errors.Add($"Vault '{reg.Name}': {ex.Message}");
                LogService.LogAction("Vault", $"Failed to re-wrap vault '{reg.Name}': {ex.Message}");
            }
        }

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        return result;
    }

    /// <summary>
    /// Syncs an offline or pending-rewrap vault with the current admin password.
    /// Requires that the vault has already been unlocked (VMK is available).
    /// </summary>
    public static async Task SyncVaultWithAdminPasswordAsync(
        VaultSession session,
        VaultRegistration reg,
        string currentAdminPassword)
    {
        var kdf = new Argon2Kdf();

        await Task.Run(() =>
        {
            var config = VaultStorage.LoadConfig(reg.Path);
            var oldSlot = config.Slots.FirstOrDefault(s => s.Type == "password");

            var newSlot = KeySlots.CreatePasswordSlot(session.Vmk, config.VaultId, currentAdminPassword, MakeFreshKdfParams(), kdf);

            if (oldSlot != null)
                config.Slots.Remove(oldSlot);
            config.Slots.Add(newSlot);

            VaultStorage.SaveConfig(reg.Path, config, session.ConfigMacKey);

            reg.UsesAdminPassword = true;
            reg.PendingRewrap = false;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

            LogService.LogAction("Vault", $"Synced vault '{reg.Name}' with current admin password.");
        });
    }

    /// <summary>
    /// Changes the password for an unlocked vault. Re-wraps the password slot with fresh Argon2id salt.
    /// Does not touch any encrypted file objects.
    /// </summary>
    public static async Task ChangePasswordAsync(
        VaultSession session,
        string newPassword)
    {
        var kdf = new Argon2Kdf();

        await Task.Run(() =>
        {
            var config = VaultStorage.LoadConfig(session.VaultPath);
            var oldSlot = config.Slots.FirstOrDefault(s => s.Type == "password");

            var newSlot = KeySlots.CreatePasswordSlot(session.Vmk, session.VaultId, newPassword, MakeFreshKdfParams(), kdf);

            if (oldSlot != null)
                config.Slots.Remove(oldSlot);
            config.Slots.Add(newSlot);

            VaultStorage.SaveConfig(session.VaultPath, config, session.ConfigMacKey);

            // Also mark registration as not using admin password if custom password was set
            var reg = ServiceContainer.Settings.Vault.Vaults.FirstOrDefault(v => v.Id == session.VaultId);
            if (reg != null)
            {
                reg.UsesAdminPassword = false;
                reg.PendingRewrap = false;
                ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            }

            LogService.LogAction("Vault", $"Password changed for vault '{session.VaultName}'.");
        });
    }

    /// <summary>
    /// Re-generates the recovery key for an unlocked vault, updating the recovery key slot in vault.config.
    /// Returns the new formatted 8x5 recovery key.
    /// </summary>
    public static async Task<string> RegenerateRecoveryKeyAsync(VaultSession session)
    {
        return await Task.Run(() =>
        {
            var (rawKey, formattedKey) = RecoveryKey.Generate();
            try
            {
                var config = VaultStorage.LoadConfig(session.VaultPath);
                var newSlot = KeySlots.CreateRecoverySlot(session.Vmk, session.VaultId, rawKey);

                config.Slots.RemoveAll(s => s.Type == "recovery");
                config.Slots.Add(newSlot);

                VaultStorage.SaveConfig(session.VaultPath, config, session.ConfigMacKey);
                LogService.LogAction("Vault", $"Recovery key regenerated for vault '{session.VaultName}'.");

                return formattedKey;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawKey);
            }
        });
    }

    /// <summary>
    /// Verifies the integrity of an unlocked vault.
    /// Checks config MAC, index integrity, object existence, optional deep chunk decryption, and orphan scan.
    /// </summary>
    public static async Task<VaultVerifyReport> VerifyVaultAsync(
        VaultSession session,
        bool deepVerify = false,
        IProgress<(int current, int total, string currentFile)>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var report = new VaultVerifyReport();

            // 1. Config MAC check
            try
            {
                var config = VaultStorage.LoadConfig(session.VaultPath, session.ConfigMacKey);
                report.ConfigValid = true;
            }
            catch (Exception ex)
            {
                report.ConfigValid = false;
                report.Errors.Add($"Config integrity check failed: {ex.Message}");
            }

            // 2. Index check
            try
            {
                var index = VaultStorage.LoadIndex(session.VaultPath, session.IndexKey, session.VaultId);
                report.IndexValid = true;
                report.ItemCount = index.Items.Count;
            }
            catch (Exception ex)
            {
                report.IndexValid = false;
                report.Errors.Add($"Index integrity check failed: {ex.Message}");
            }

            // 3. File objects check
            var nonDirItems = session.Index.Items.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ObjectId)).ToList();
            report.TotalFiles = nonDirItems.Count;

            var knownObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < nonDirItems.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = nonDirItems[i];
                progress?.Report((i + 1, nonDirItems.Count, item.Name));

                if (!string.IsNullOrEmpty(item.ObjectId))
                    knownObjectIds.Add(item.ObjectId);

                var objPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId!, item.IsTrash);
                if (!File.Exists(objPath))
                {
                    report.MissingFiles.Add(item.Name);
                    report.Errors.Add($"Missing object for '{item.Name}' (ID: {item.ObjectId})");
                    continue;
                }

                if (deepVerify)
                {
                    try
                    {
                        using var fs = new FileStream(objPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        long bytes = FileObjectReader.DecryptAll(fs, Stream.Null, session.FekWrapKey);
                        if (bytes != item.Size)
                        {
                            report.CorruptedFiles.Add(item.Name);
                            report.Errors.Add($"File '{item.Name}' size mismatch: index={item.Size}, decrypted={bytes}");
                        }
                        else
                        {
                            report.VerifiedFilesCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        report.CorruptedFiles.Add(item.Name);
                        report.Errors.Add($"Integrity failure for '{item.Name}': {ex.Message}");
                    }
                }
                else
                {
                    report.VerifiedFilesCount++;
                }
            }

            // 4. Orphan objects scan
            try
            {
                report.OrphanFiles = VaultStorage.ScanOrphanObjects(session.VaultPath, knownObjectIds);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"Orphan scan error: {ex.Message}");
            }

            report.IsSuccess = report.ConfigValid && report.IndexValid && report.MissingFiles.Count == 0 && report.CorruptedFiles.Count == 0;
            return report;
        }, ct);
    }

    /// <summary>
    /// Safely copies an entire vault folder to a destination path, verifying SHA-256 of each copied file.
    /// Does not require the vault to be unlocked since all files are encrypted at rest.
    /// </summary>
    public static async Task BackupVaultAsync(
        string sourceVaultPath,
        string destinationFolderPath,
        IProgress<(int copied, int total, string currentFile)>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(async () =>
        {
            if (!Directory.Exists(sourceVaultPath))
                throw new DirectoryNotFoundException($"Source vault folder '{sourceVaultPath}' does not exist.");

            var allFiles = Directory.GetFiles(sourceVaultPath, "*", SearchOption.AllDirectories);
            int total = allFiles.Length;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sourceFile = allFiles[i];
                var relPath = Path.GetRelativePath(sourceVaultPath, sourceFile);

                // Skip lock files or active temp files
                if (string.Equals(Path.GetFileName(sourceFile), VaultLockFile.LockFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                progress?.Report((i + 1, total, relPath));

                var destFile = Path.Combine(destinationFolderPath, relPath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                // Copy with retry/share
                using (var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await src.CopyToAsync(dst, ct);
                }

                // Verify SHA-256 match
                byte[] srcHash, dstHash;
                using (var sha = SHA256.Create())
                {
                    using (var s = File.OpenRead(sourceFile)) srcHash = sha.ComputeHash(s);
                    using (var d = File.OpenRead(destFile)) dstHash = sha.ComputeHash(d);
                }

                if (!CryptographicOperations.FixedTimeEquals(srcHash, dstHash))
                {
                    try { File.Delete(destFile); } catch { }
                    throw new CryptographicException($"Backup verification failed for file '{relPath}' (hash mismatch).");
                }
            }

            LogService.LogAction("Vault", $"Vault backup completed from '{sourceVaultPath}' to '{destinationFolderPath}'.");
        }, ct);
    }
}

public class VaultVerifyReport
{
    public bool IsSuccess { get; set; }
    public bool ConfigValid { get; set; }
    public bool IndexValid { get; set; }
    public int ItemCount { get; set; }
    public int TotalFiles { get; set; }
    public int VerifiedFilesCount { get; set; }
    public List<string> MissingFiles { get; } = new();
    public List<string> CorruptedFiles { get; } = new();
    public List<string> OrphanFiles { get; set; } = new();
    public List<string> Errors { get; } = new();
}
