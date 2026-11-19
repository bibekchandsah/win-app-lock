using System;

namespace AppLock.Services.Vault.Crypto;

public enum PaddingMode
{
    None = 0,
    /// <summary>
    /// Pads to the next power of two or bucket boundary (e.g. 64 KiB) up to chunk boundary.
    /// Obfuscates exact file lengths against storage and traffic analysis.
    /// </summary>
    Bucket64KiB = 1
}

public static class SizePadding
{
    public const int DefaultBucketSize = 65536; // 64 KiB

    /// <summary>
    /// Calculates the padded length for a given plaintext size.
    /// Files are padded up to the next bucket size (minimum 1 byte padding when padding is enabled,
    /// or padded to bucket multiple).
    /// </summary>
    public static int CalculatePaddedChunkSize(int originalLength, int bucketSize = DefaultBucketSize)
    {
        if (originalLength <= 0) return 0;
        if (bucketSize <= 0) return originalLength;

        int remainder = originalLength % bucketSize;
        if (remainder == 0) return originalLength;

        return originalLength + (bucketSize - remainder);
    }
}
