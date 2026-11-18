using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppLock.Models.Vault;

namespace AppLock.Services.Vault.Crypto;

public static class VaultCrypto
{
    public const int MinMemorySizeKb = 65536; // 64 MiB
    public const int MinIterations = 3;
    public const int VmkLengthBytes = 32;

    private static readonly byte[] KnownVerifierPlaintext = "APPLOCK-VAULT-VERIFIER-V1\0\0\0\0\0\0\0"u8.ToArray();

    public static byte[] GenerateVmk()
    {
        var vmk = new byte[VmkLengthBytes];
        RandomNumberGenerator.Fill(vmk);
        return vmk;
    }

    public static byte[] DeriveIndexKey(byte[] vmk, Guid vaultId) =>
        DeriveSubKey(vmk, vaultId, "index-key"u8.ToArray());

    public static byte[] DeriveFekWrapKey(byte[] vmk, Guid vaultId) =>
        DeriveSubKey(vmk, vaultId, "fek-wrap-key"u8.ToArray());

    public static byte[] DeriveConfigMacKey(byte[] vmk, Guid vaultId) =>
        DeriveSubKey(vmk, vaultId, "config-mac-key"u8.ToArray());

    public static byte[] DeriveVerifierKey(byte[] vmk, Guid vaultId) =>
        DeriveSubKey(vmk, vaultId, "verifier-key"u8.ToArray());

    private static byte[] DeriveSubKey(byte[] vmk, Guid vaultId, byte[] info)
    {
        return HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: vmk,
            outputLength: 32,
            salt: vaultId.ToByteArray(),
            info: info);
    }

    public static VerifierData CreateVerifier(byte[] vmk, Guid vaultId)
    {
        var key = DeriveVerifierKey(vmk, vaultId);
        try
        {
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[KnownVerifierPlaintext.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, KnownVerifierPlaintext, ciphertext, tag, vaultId.ToByteArray());

            return new VerifierData
            {
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool CheckVerifier(byte[] vmk, Guid vaultId, VerifierData verifier)
    {
        if (verifier == null || string.IsNullOrEmpty(verifier.Nonce) ||
            string.IsNullOrEmpty(verifier.Ciphertext) || string.IsNullOrEmpty(verifier.Tag))
        {
            return false;
        }

        var key = DeriveVerifierKey(vmk, vaultId);
        try
        {
            var nonce = Convert.FromBase64String(verifier.Nonce);
            var ciphertext = Convert.FromBase64String(verifier.Ciphertext);
            var tag = Convert.FromBase64String(verifier.Tag);

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, vaultId.ToByteArray());

            return CryptographicOperations.FixedTimeEquals(plaintext, KnownVerifierPlaintext);
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void EnforceKdfFloors(KdfParams kdf, bool allowFastTest = false)
    {
        if (kdf == null) throw new CryptographicException("Missing KDF parameters in configuration.");

        if (allowFastTest && kdf.Algorithm == "fast_test")
        {
            return;
        }

        if (!kdf.Algorithm.Equals("argon2id", StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException($"Unsupported KDF algorithm: '{kdf.Algorithm}'. Only Argon2id is permitted.");
        }

        if (kdf.MemorySizeKb < MinMemorySizeKb)
        {
            throw new CryptographicException($"KDF memory size ({kdf.MemorySizeKb} KB) is below the security floor of {MinMemorySizeKb} KB.");
        }

        if (kdf.Iterations < MinIterations)
        {
            throw new CryptographicException($"KDF iterations count ({kdf.Iterations}) is below the security floor of {MinIterations}.");
        }
    }

    public static string ComputeConfigMac(VaultConfig config, byte[] configMacKey)
    {
        // Compute HMAC over canonical config payload (all fields except configMac)
        using var hmac = new HMACSHA256(configMacKey);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", config.FormatVersion);
            writer.WriteString("vaultId", config.VaultId.ToString());
            writer.WriteString("name", config.Name);
            writer.WriteString("cipherSuite", config.CipherSuite);
            writer.WriteNumber("chunkSize", config.ChunkSize);
            writer.WriteString("verifierNonce", config.Verifier.Nonce);
            writer.WriteString("verifierCiphertext", config.Verifier.Ciphertext);
            writer.WriteString("verifierTag", config.Verifier.Tag);

            writer.WriteStartArray("slots");
            foreach (var slot in config.Slots)
            {
                writer.WriteStartObject();
                writer.WriteString("id", slot.Id.ToString());
                writer.WriteString("type", slot.Type);
                writer.WriteString("nonce", slot.Nonce);
                writer.WriteString("wrappedVmk", slot.WrappedVmk);
                writer.WriteString("tag", slot.Tag);
                if (slot.Kdf != null)
                {
                    writer.WriteString("kdfAlgo", slot.Kdf.Algorithm);
                    writer.WriteNumber("kdfM", slot.Kdf.MemorySizeKb);
                    writer.WriteNumber("kdfT", slot.Kdf.Iterations);
                    writer.WriteNumber("kdfP", slot.Kdf.Parallelism);
                    writer.WriteString("kdfSalt", slot.Kdf.Salt);
                }
                if (slot.HkdfSalt != null) writer.WriteString("hkdfSalt", slot.HkdfSalt);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var mac = hmac.ComputeHash(ms.ToArray());
        return Convert.ToBase64String(mac);
    }

    public static bool VerifyConfigMac(VaultConfig config, byte[] configMacKey)
    {
        if (string.IsNullOrEmpty(config.ConfigMac)) return false;
        try
        {
            var expectedMac = Convert.FromBase64String(ComputeConfigMac(config, configMacKey));
            var actualMac = Convert.FromBase64String(config.ConfigMac);
            return CryptographicOperations.FixedTimeEquals(expectedMac, actualMac);
        }
        catch
        {
            return false;
        }
    }
}
