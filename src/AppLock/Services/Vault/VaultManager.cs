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
}
