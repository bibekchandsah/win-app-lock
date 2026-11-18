using System;
using System.Security.Cryptography;
using System.Text;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;
using Xunit;

namespace AppLock.Tests.Vault;

public class VaultCryptoCoreTests
{
    private readonly IKdf _fastKdf = new FastTestKdf();

    [Fact]
    public void VmkGeneration_Produces256Bits()
    {
        var vmk = VaultCrypto.GenerateVmk();
        Assert.NotNull(vmk);
        Assert.Equal(32, vmk.Length);
        Assert.False(CryptographicOperations.FixedTimeEquals(vmk, new byte[32]));
    }

    [Fact]
    public void DerivedKeys_AreUniqueAndDeterministic()
    {
        var vmk = VaultCrypto.GenerateVmk();
        var vaultId = Guid.NewGuid();

        var indexKey1 = VaultCrypto.DeriveIndexKey(vmk, vaultId);
        var indexKey2 = VaultCrypto.DeriveIndexKey(vmk, vaultId);
        var fekWrapKey = VaultCrypto.DeriveFekWrapKey(vmk, vaultId);
        var configMacKey = VaultCrypto.DeriveConfigMacKey(vmk, vaultId);
        var verifierKey = VaultCrypto.DeriveVerifierKey(vmk, vaultId);

        // Deterministic
        Assert.True(CryptographicOperations.FixedTimeEquals(indexKey1, indexKey2));

        // Distinct from each other
        Assert.False(CryptographicOperations.FixedTimeEquals(indexKey1, fekWrapKey));
        Assert.False(CryptographicOperations.FixedTimeEquals(indexKey1, configMacKey));
        Assert.False(CryptographicOperations.FixedTimeEquals(fekWrapKey, verifierKey));
    }

    [Fact]
    public void Verifier_ValidVmkSucceeds_InvalidVmkFails()
    {
        var vmk = VaultCrypto.GenerateVmk();
        var vaultId = Guid.NewGuid();

        var verifier = VaultCrypto.CreateVerifier(vmk, vaultId);
        Assert.NotNull(verifier);

        // Correct VMK
        Assert.True(VaultCrypto.CheckVerifier(vmk, vaultId, verifier));

        // Incorrect VMK
        var wrongVmk = VaultCrypto.GenerateVmk();
        Assert.False(VaultCrypto.CheckVerifier(wrongVmk, vaultId, verifier));
    }

    [Fact]
    public void PasswordSlot_CreateAndUnlock_SucceedsWithCorrectPassword()
    {
        var vmk = VaultCrypto.GenerateVmk();
        var vaultId = Guid.NewGuid();
        var password = "Correct-Horse-Battery-Staple-2026!";

        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        var kdfParams = new KdfParams
        {
            Algorithm = "fast_test",
            MemorySizeKb = 65536,
            Iterations = 3,
            Parallelism = 1,
            Salt = Convert.ToBase64String(salt)
        };

        var slot = KeySlots.CreatePasswordSlot(vmk, vaultId, password, kdfParams, _fastKdf);
        Assert.Equal("password", slot.Type);

        // Unlock with right password
        var unlockedVmk = KeySlots.UnlockPasswordSlot(slot, vaultId, password, _fastKdf, allowFastTest: true);
        Assert.True(CryptographicOperations.FixedTimeEquals(vmk, unlockedVmk));

        // Unlock with wrong password throws
        Assert.ThrowsAny<CryptographicException>(() =>
            KeySlots.UnlockPasswordSlot(slot, vaultId, "WrongPassword!", _fastKdf, allowFastTest: true));
    }

    [Fact]
    public void RecoveryKey_Generate_Format_Validate_AndUnlock()
    {
        var vmk = VaultCrypto.GenerateVmk();
        var vaultId = Guid.NewGuid();

        var (rawKey, formatted) = RecoveryKey.Generate();
        Assert.Equal(20, rawKey.Length);
        Assert.Equal(47, formatted.Length); // 8x5 chars + 7 hyphens

        // Verify parsing formatted string
        Assert.True(RecoveryKey.TryParse(formatted, out var parsedRawKey, out var error));
        Assert.Null(error);
        Assert.True(CryptographicOperations.FixedTimeEquals(rawKey, parsedRawKey));

        // Create recovery slot
        var slot = KeySlots.CreateRecoverySlot(vmk, vaultId, rawKey);
        Assert.Equal("recovery", slot.Type);

        // Unlock recovery slot with parsed key
        var unlockedVmk = KeySlots.UnlockRecoverySlot(slot, vaultId, parsedRawKey);
        Assert.True(CryptographicOperations.FixedTimeEquals(vmk, unlockedVmk));

        // Wrong recovery key fails
        var (wrongRawKey, _) = RecoveryKey.Generate();
        Assert.ThrowsAny<CryptographicException>(() =>
            KeySlots.UnlockRecoverySlot(slot, vaultId, wrongRawKey));
    }

    [Fact]
    public void RecoveryKey_TamperedCharacter_FailsChecksum()
    {
        var (_, formatted) = RecoveryKey.Generate();

        // Alter one character in formatted string
        var chars = formatted.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        Assert.False(RecoveryKey.TryParse(tampered, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("checksum", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KdfFloors_EnforcedAgainstDowngradedConfig()
    {
        // Memory below 64 MB
        var weakMemory = new KdfParams { Algorithm = "argon2id", MemorySizeKb = 32768, Iterations = 3, Parallelism = 1 };
        Assert.Throws<CryptographicException>(() => VaultCrypto.EnforceKdfFloors(weakMemory));

        // Iterations below 3
        var weakIterations = new KdfParams { Algorithm = "argon2id", MemorySizeKb = 65536, Iterations = 2, Parallelism = 1 };
        Assert.Throws<CryptographicException>(() => VaultCrypto.EnforceKdfFloors(weakIterations));

        // Unknown algorithm
        var badAlgo = new KdfParams { Algorithm = "md5", MemorySizeKb = 65536, Iterations = 3, Parallelism = 1 };
        Assert.Throws<CryptographicException>(() => VaultCrypto.EnforceKdfFloors(badAlgo));

        // Valid floors pass
        var valid = new KdfParams { Algorithm = "argon2id", MemorySizeKb = 65536, Iterations = 3, Parallelism = 4 };
        VaultCrypto.EnforceKdfFloors(valid);
    }

    [Fact]
    public void ConfigMac_AuthenticatesConfigIntegrity()
    {
        var config = new VaultConfig
        {
            VaultId = Guid.NewGuid(),
            Name = "Secret Vault",
            ChunkSize = 262144
        };
        var macKey = new byte[32];
        RandomNumberGenerator.Fill(macKey);

        config.ConfigMac = VaultCrypto.ComputeConfigMac(config, macKey);
        Assert.True(VaultCrypto.VerifyConfigMac(config, macKey));

        // Tamper with config name
        config.Name = "Hacked Vault";
        Assert.False(VaultCrypto.VerifyConfigMac(config, macKey));
    }

    [Fact]
    public void Hkdf_Rfc5869_TestVector1_MatchesKnownOutput()
    {
        // RFC 5869 Test Case 1: Hash = SHA-256
        var ikm = Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        int length = 42;
        var expectedOkm = Convert.FromHexString("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info);
        Assert.Equal(expectedOkm, okm);
    }

    [Fact]
    public void PasswordChange_ReWrap_LeavesFileDataUntouched()
    {
        var vmk = VaultCrypto.GenerateVmk();
        var vaultId = Guid.NewGuid();
        var fekWrapKey = VaultCrypto.DeriveFekWrapKey(vmk, vaultId);

        // 1. Encrypt sample file data with original VMK derived fekWrapKey
        var plaintext = Encoding.UTF8.GetBytes("Super secret document content!");
        using var inStream = new MemoryStream(plaintext);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, fekWrapKey, Guid.NewGuid());

        // 2. Old password slot
        var oldSalt = new byte[16];
        RandomNumberGenerator.Fill(oldSalt);
        var oldKdf = new KdfParams { Algorithm = "fast_test", MemorySizeKb = 65536, Iterations = 3, Salt = Convert.ToBase64String(oldSalt) };
        var oldSlot = KeySlots.CreatePasswordSlot(vmk, vaultId, "OldPassword-1234", oldKdf, _fastKdf);

        // 3. User changes password: old slot unwrapped, new slot created with new password
        var unwrappedVmk = KeySlots.UnlockPasswordSlot(oldSlot, vaultId, "OldPassword-1234", _fastKdf, allowFastTest: true);

        var newSalt = new byte[16];
        RandomNumberGenerator.Fill(newSalt);
        var newKdf = new KdfParams { Algorithm = "fast_test", MemorySizeKb = 65536, Iterations = 3, Salt = Convert.ToBase64String(newSalt) };
        var newSlot = KeySlots.CreatePasswordSlot(unwrappedVmk, vaultId, "NewPassword-5678", newKdf, _fastKdf);

        // 4. Verify new slot unlocks the SAME VMK
        var verifiedVmk = KeySlots.UnlockPasswordSlot(newSlot, vaultId, "NewPassword-5678", _fastKdf, allowFastTest: true);
        Assert.True(CryptographicOperations.FixedTimeEquals(vmk, verifiedVmk));

        // 5. Decrypt the file data using fekWrapKey derived from verifiedVmk: file data is untouched!
        var verifiedFekWrapKey = VaultCrypto.DeriveFekWrapKey(verifiedVmk, vaultId);
        encStream.Position = 0;
        using var decStream = new MemoryStream();
        FileObjectReader.DecryptAll(encStream, decStream, verifiedFekWrapKey);

        Assert.Equal(plaintext, decStream.ToArray());
    }

    [Fact]
    public void Argon2Kdf_RealDerivation_ProducesValidOutput()
    {
        var argon2 = new Argon2Kdf();
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        // Real Argon2id derivation with minimal test time (16 MB, 1 iteration, 1 thread)
        var key = argon2.DeriveKey("TestPassphrase-2026", salt, memorySizeKb: 16384, iterations: 1, parallelism: 1, keyLengthBytes: 32);
        Assert.NotNull(key);
        Assert.Equal(32, key.Length);
        Assert.False(CryptographicOperations.FixedTimeEquals(key, new byte[32]));
    }
}
