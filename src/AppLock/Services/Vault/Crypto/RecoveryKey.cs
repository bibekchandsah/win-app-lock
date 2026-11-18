using System;
using System.Security.Cryptography;
using System.Text;

namespace AppLock.Services.Vault.Crypto;

public static class RecoveryKey
{
    // Crockford Base32 alphabet: 32 chars, omitting I, L, O, U to prevent reading confusion
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly byte[] CharToVal = new byte[128];

    static RecoveryKey()
    {
        for (int i = 0; i < CharToVal.Length; i++) CharToVal[i] = 0xFF;
        for (int i = 0; i < Alphabet.Length; i++)
        {
            char c = Alphabet[i];
            CharToVal[c] = (byte)i;
            CharToVal[char.ToLowerInvariant(c)] = (byte)i;
        }
        // Friendly aliases: 'O' -> 0, 'I'/'L' -> 1
        CharToVal['O'] = 0;
        CharToVal['o'] = 0;
        CharToVal['I'] = 1;
        CharToVal['i'] = 1;
        CharToVal['L'] = 1;
        CharToVal['l'] = 1;
    }

    /// <summary>
    /// Generates a random 160-bit recovery key and its 8x5 formatted string with checksum.
    /// </summary>
    public static (byte[] RawKey, string FormattedKey) Generate()
    {
        var rawKey = new byte[20]; // 160 bits
        RandomNumberGenerator.Fill(rawKey);
        var formatted = Format(rawKey);
        return (rawKey, formatted);
    }

    /// <summary>
    /// Formats 20 raw bytes into 8 groups of 5 characters (40 chars) including a 5-byte SHA-256 checksum.
    /// </summary>
    public static string Format(byte[] rawKey)
    {
        if (rawKey == null || rawKey.Length != 20)
            throw new ArgumentException("Recovery key raw bytes must be exactly 20 bytes (160 bits).", nameof(rawKey));

        // Compute 5-byte checksum from SHA-256
        var hash = SHA256.HashData(rawKey);
        var combined = new byte[25]; // 20 bytes key + 5 bytes checksum = 200 bits = 40 base32 chars
        Buffer.BlockCopy(rawKey, 0, combined, 0, 20);
        Buffer.BlockCopy(hash, 0, combined, 20, 5);

        var chars = EncodeBase32(combined); // 40 chars
        var sb = new StringBuilder(47); // 40 chars + 7 hyphens
        for (int i = 0; i < 8; i++)
        {
            if (i > 0) sb.Append('-');
            sb.Append(chars, i * 5, 5);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Validates and parses the 8x5 recovery key string, verifying the checksum.
    /// </summary>
    public static bool TryParse(string formattedKey, out byte[] rawKey, out string? errorMessage)
    {
        rawKey = Array.Empty<byte>();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(formattedKey))
        {
            errorMessage = "Recovery key cannot be empty.";
            return false;
        }

        // Clean out whitespace, hyphens
        var clean = formattedKey.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        if (clean.Length != 40)
        {
            errorMessage = $"Recovery key must be 40 characters (8 groups of 5). Got {clean.Length}.";
            return false;
        }

        byte[] combined;
        try
        {
            combined = DecodeBase32(clean);
        }
        catch
        {
            errorMessage = "Recovery key contains invalid characters.";
            return false;
        }

        if (combined.Length != 25)
        {
            errorMessage = "Invalid recovery key data length.";
            return false;
        }

        var keyBytes = new byte[20];
        var checksum = new byte[5];
        Buffer.BlockCopy(combined, 0, keyBytes, 0, 20);
        Buffer.BlockCopy(combined, 20, checksum, 0, 5);

        var expectedHash = SHA256.HashData(keyBytes);
        var expectedChecksum = new byte[5];
        Buffer.BlockCopy(expectedHash, 0, expectedChecksum, 0, 5);

        if (!CryptographicOperations.FixedTimeEquals(checksum, expectedChecksum))
        {
            errorMessage = "Recovery key checksum verification failed. Please check for typing mistakes.";
            return false;
        }

        rawKey = keyBytes;
        return true;
    }

    /// <summary>
    /// Expands the 160-bit recovery key into a 256-bit KEK using HKDF-SHA256.
    /// </summary>
    public static byte[] ExpandToKek(byte[] rawKey, byte[] salt)
    {
        return HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: rawKey,
            outputLength: 32,
            salt: salt,
            info: "vault-recovery-kek"u8.ToArray());
    }

    private static string EncodeBase32(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 8 / 5);
        int bitBuffer = 0;
        int bitCount = 0;

        foreach (byte b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                int index = (bitBuffer >> bitCount) & 0x1F;
                sb.Append(Alphabet[index]);
            }
        }

        if (bitCount > 0)
        {
            int index = (bitBuffer << (5 - bitCount)) & 0x1F;
            sb.Append(Alphabet[index]);
        }

        return sb.ToString();
    }

    private static byte[] DecodeBase32(string text)
    {
        var output = new byte[text.Length * 5 / 8];
        int bitBuffer = 0;
        int bitCount = 0;
        int outIndex = 0;

        foreach (char c in text)
        {
            if (c >= CharToVal.Length || CharToVal[c] == 0xFF)
                throw new FormatException($"Invalid Base32 character '{c}'.");

            bitBuffer = (bitBuffer << 5) | CharToVal[c];
            bitCount += 5;

            if (bitCount >= 8)
            {
                bitCount -= 8;
                output[outIndex++] = (byte)((bitBuffer >> bitCount) & 0xFF);
            }
        }

        return output;
    }
}
