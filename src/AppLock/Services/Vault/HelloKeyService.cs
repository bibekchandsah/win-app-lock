using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

/// <summary>
/// Manages the Windows Hello unlock slot for vaults.
///
/// Design:
///   The VMK is wrapped with a 32-byte "Hello KEK" (derived via HKDF from a random secret).
///   That random secret is protected with DPAPI (CurrentUser scope + per-vault entropy)
///   and stored in %LOCALAPPDATA%\AppLock\VaultHello\<vaultId>.bin.
///
///   Unlock flow:
///     1. UserConsentVerifier.RequestVerificationAsync — biometric / PIN gate.
///     2. DPAPI.Unprotect the stored secret → Hello KEK.
///     3. AES-256-GCM decrypt the wrapped VMK stored in the hello key slot.
///
///   If Hello is unavailable or fails, the caller falls back to the password slot.
/// </summary>
public static class HelloKeyService
{
    private static readonly string HelloDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "VaultHello");

    // Per-app entropy bytes for DPAPI (adds application-specific binding on top of user scope)
    private static readonly byte[] AppEntropy =
        System.Text.Encoding.UTF8.GetBytes("AppLock.Vault.HelloKeyService.Entropy.v1");

    // ---- Public API -------------------------------------------------------

    /// <summary>
    /// Returns true if Windows Hello is available on this device.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var helloService = new AppLock.Services.HelloService();
            return await helloService.IsAvailableAsync();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if this vault has a registered Hello slot on this machine.
    /// </summary>
    public static bool IsEnrolledForVault(Guid vaultId)
    {
        return File.Exists(GetSecretPath(vaultId));
    }

    /// <summary>
    /// Enrolls the Hello slot: wraps the VMK and stores the DPAPI-protected secret.
    /// Requires the caller to already hold the unlocked VMK.
    /// </summary>
    public static void Enroll(VaultSession session, VaultConfig config)
    {
        // 1. Generate random 32-byte Hello secret
        var helloSecret = new byte[32];
        RandomNumberGenerator.Fill(helloSecret);

        try
        {
            // 2. Derive a KEK from the secret
            var kek = DeriveKek(helloSecret, config.VaultId);
            try
            {
                // 3. Wrap the VMK
                var slotId = Guid.NewGuid();
                var nonce = new byte[12];
                RandomNumberGenerator.Fill(nonce);
                var aad = BuildAad(config.VaultId, slotId);
                var ciphertext = new byte[session.Vmk.Length];
                var tag = new byte[16];

                using var aes = new AesGcm(kek, 16);
                aes.Encrypt(nonce, session.Vmk, ciphertext, tag, aad);

                // 4. Add slot to config (or replace existing)
                config.Slots.RemoveAll(s => s.Type == "windows_hello");
                config.Slots.Add(new KeySlot
                {
                    Id = slotId,
                    Type = "windows_hello",
                    Nonce = Convert.ToBase64String(nonce),
                    WrappedVmk = Convert.ToBase64String(ciphertext),
                    Tag = Convert.ToBase64String(tag)
                });

                // 5. DPAPI-protect the secret and save it locally
                var entropy = GetEntropy(config.VaultId);
                var protectedSecret = ProtectedData.Protect(helloSecret, entropy, DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(HelloDir);
                File.WriteAllBytes(GetSecretPath(config.VaultId), protectedSecret);

                LogService.LogAction("Vault", $"Windows Hello slot enrolled for vault {config.VaultId}.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloSecret);
        }
    }

    /// <summary>
    /// Unlocks the vault using Windows Hello. Prompts the user for biometric/PIN.
    /// Returns the decrypted VMK on success. Throws on failure.
    /// </summary>
    public static async Task<byte[]> UnlockAsync(
        VaultRegistration reg,
        VaultConfig config,
        string message = "Authenticate to open your vault",
        CancellationToken ct = default)
    {
        // 1. Find hello slot in config
        var slot = config.Slots.Find(s => s.Type == "windows_hello");
        if (slot == null)
            throw new InvalidOperationException("No Windows Hello slot found in vault config.");

        // 2. Check DPAPI-protected secret exists on this machine
        var secretPath = GetSecretPath(config.VaultId);
        if (!File.Exists(secretPath))
            throw new InvalidOperationException("Windows Hello is not enrolled for this vault on this device.");

        // 3. Prompt Hello
        var helloService = new AppLock.Services.HelloService();
        bool verified = await helloService.RequestVerificationAsync(message, ct);
        if (!verified)
            throw new CryptographicException("Windows Hello verification failed or was cancelled.");

        ct.ThrowIfCancellationRequested();

        // 4. DPAPI-unprotect secret
        byte[] helloSecret;
        try
        {
            var protectedSecret = File.ReadAllBytes(secretPath);
            var entropy = GetEntropy(config.VaultId);
            helloSecret = ProtectedData.Unprotect(protectedSecret, entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException("Could not unprotect the Windows Hello key material. This can happen if Windows profile changed.", ex);
        }

        try
        {
            // 5. Derive KEK and unwrap VMK
            var kek = DeriveKek(helloSecret, config.VaultId);
            try
            {
                var nonce = Convert.FromBase64String(slot.Nonce);
                var ciphertext = Convert.FromBase64String(slot.WrappedVmk);
                var tag = Convert.FromBase64String(slot.Tag);
                var aad = BuildAad(config.VaultId, slot.Id);

                var vmk = new byte[ciphertext.Length];
                using var aes = new AesGcm(kek, 16);
                aes.Decrypt(nonce, ciphertext, tag, vmk, aad);
                return vmk;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloSecret);
        }
    }

    /// <summary>
    /// Removes the Hello slot from both the config and the local DPAPI secret file.
    /// </summary>
    public static void Revoke(Guid vaultId, VaultConfig config)
    {
        config.Slots.RemoveAll(s => s.Type == "windows_hello");

        var path = GetSecretPath(vaultId);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }

        LogService.LogAction("Vault", $"Windows Hello slot revoked for vault {vaultId}.");
    }

    // ---- Helpers ----------------------------------------------------------

    private static byte[] DeriveKek(byte[] helloSecret, Guid vaultId)
    {
        var info = System.Text.Encoding.UTF8.GetBytes($"hello-kek:{vaultId:N}");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, helloSecret, outputLength: 32, info: info);
    }

    private static byte[] BuildAad(Guid vaultId, Guid slotId)
    {
        return System.Text.Encoding.UTF8.GetBytes($"windows_hello:{vaultId:N}:{slotId:N}");
    }

    private static byte[] GetEntropy(Guid vaultId)
    {
        // Combine app entropy with vault ID for per-vault DPAPI binding
        using var ms = new MemoryStream();
        ms.Write(AppEntropy);
        ms.Write(vaultId.ToByteArray());
        return ms.ToArray();
    }

    private static string GetSecretPath(Guid vaultId) =>
        Path.Combine(HelloDir, $"vault-hello-{vaultId:N}.bin");
}
