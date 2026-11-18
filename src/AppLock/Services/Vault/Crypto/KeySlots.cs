using System;
using System.Security.Cryptography;
using System.Text;
using AppLock.Models.Vault;

namespace AppLock.Services.Vault.Crypto;

public static class KeySlots
{
    public static KeySlot CreatePasswordSlot(
        byte[] vmk,
        Guid vaultId,
        string password,
        KdfParams kdfParams,
        IKdf kdfService)
    {
        var slotId = Guid.NewGuid();
        var salt = Convert.FromBase64String(kdfParams.Salt);

        var kek = kdfService.DeriveKey(
            password,
            salt,
            kdfParams.MemorySizeKb,
            kdfParams.Iterations,
            kdfParams.Parallelism,
            keyLengthBytes: 32);

        try
        {
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var aad = BuildAad(vaultId, "password", slotId);
            var ciphertext = new byte[vmk.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(kek, 16);
            aes.Encrypt(nonce, vmk, ciphertext, tag, aad);

            return new KeySlot
            {
                Id = slotId,
                Type = "password",
                Kdf = kdfParams,
                Nonce = Convert.ToBase64String(nonce),
                WrappedVmk = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static byte[] UnlockPasswordSlot(
        KeySlot slot,
        Guid vaultId,
        string password,
        IKdf kdfService,
        bool allowFastTest = false)
    {
        if (slot.Kdf == null) throw new CryptographicException("Slot is missing KDF parameters.");
        VaultCrypto.EnforceKdfFloors(slot.Kdf, allowFastTest);

        var salt = Convert.FromBase64String(slot.Kdf.Salt);
        var kek = kdfService.DeriveKey(
            password,
            salt,
            slot.Kdf.MemorySizeKb,
            slot.Kdf.Iterations,
            slot.Kdf.Parallelism,
            keyLengthBytes: 32);

        try
        {
            var nonce = Convert.FromBase64String(slot.Nonce);
            var ciphertext = Convert.FromBase64String(slot.WrappedVmk);
            var tag = Convert.FromBase64String(slot.Tag);
            var aad = BuildAad(vaultId, "password", slot.Id);

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

    public static KeySlot CreateRecoverySlot(
        byte[] vmk,
        Guid vaultId,
        byte[] rawRecoveryKey)
    {
        var slotId = Guid.NewGuid();
        var hkdfSalt = new byte[16];
        RandomNumberGenerator.Fill(hkdfSalt);

        var kek = RecoveryKey.ExpandToKek(rawRecoveryKey, hkdfSalt);
        try
        {
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var aad = BuildAad(vaultId, "recovery", slotId);
            var ciphertext = new byte[vmk.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(kek, 16);
            aes.Encrypt(nonce, vmk, ciphertext, tag, aad);

            return new KeySlot
            {
                Id = slotId,
                Type = "recovery",
                HkdfSalt = Convert.ToBase64String(hkdfSalt),
                Nonce = Convert.ToBase64String(nonce),
                WrappedVmk = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static byte[] UnlockRecoverySlot(
        KeySlot slot,
        Guid vaultId,
        byte[] rawRecoveryKey)
    {
        if (string.IsNullOrEmpty(slot.HkdfSalt))
            throw new CryptographicException("Recovery slot is missing HKDF salt.");

        var hkdfSalt = Convert.FromBase64String(slot.HkdfSalt);
        var kek = RecoveryKey.ExpandToKek(rawRecoveryKey, hkdfSalt);

        try
        {
            var nonce = Convert.FromBase64String(slot.Nonce);
            var ciphertext = Convert.FromBase64String(slot.WrappedVmk);
            var tag = Convert.FromBase64String(slot.Tag);
            var aad = BuildAad(vaultId, "recovery", slot.Id);

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

    private static byte[] BuildAad(Guid vaultId, string slotType, Guid slotId)
    {
        var vaultBytes = vaultId.ToByteArray();
        var typeBytes = Encoding.UTF8.GetBytes(slotType);
        var slotBytes = slotId.ToByteArray();

        var aad = new byte[vaultBytes.Length + typeBytes.Length + slotBytes.Length];
        Buffer.BlockCopy(vaultBytes, 0, aad, 0, vaultBytes.Length);
        Buffer.BlockCopy(typeBytes, 0, aad, vaultBytes.Length, typeBytes.Length);
        Buffer.BlockCopy(slotBytes, 0, aad, vaultBytes.Length + typeBytes.Length, slotBytes.Length);
        return aad;
    }
}
