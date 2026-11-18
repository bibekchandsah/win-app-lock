using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace AppLock.Services.Vault.Crypto;

public static class FileObjectWriter
{
    public static readonly byte[] Magic = "ALV1"u8.ToArray();
    public const ushort Version = 1;
    public const ushort CipherIdAes256Gcm = 1;
    public const int DefaultChunkSize = 262144; // 256 KiB
    public const int HeaderSize = 114; // 4 + 2 + 2 + 4 + 16 + 2 + 60 + 8 + 16

    /// <summary>
    /// Encrypts input plaintext stream into vault chunked object format.
    /// Computes SHA-256 hash and plaintext length while streaming.
    /// </summary>
    public static (long PlaintextLength, string Sha256Hex) WriteObject(
        Stream inputStream,
        Stream outputStream,
        byte[] fekWrapKey,
        Guid fileId,
        int chunkSize = DefaultChunkSize)
    {
        if (fekWrapKey == null || fekWrapKey.Length != 32)
            throw new ArgumentException("FEK wrap key must be 32 bytes.", nameof(fekWrapKey));
        if (chunkSize <= 0)
            throw new ArgumentException("Chunk size must be positive.", nameof(chunkSize));

        // 1. Generate random 256-bit FEK
        var fek = new byte[32];
        RandomNumberGenerator.Fill(fek);

        // 2. Wrap FEK with fek-wrap-key
        var fekNonce = new byte[12];
        RandomNumberGenerator.Fill(fekNonce);
        var wrappedFekCiphertext = new byte[32];
        var wrappedFekTag = new byte[16];
        var fileIdBytes = fileId.ToByteArray();

        using (var wrapAes = new AesGcm(fekWrapKey, 16))
        {
            wrapAes.Encrypt(fekNonce, fek, wrappedFekCiphertext, wrappedFekTag, fileIdBytes);
        }

        var wrappedFekTotal = new byte[60]; // 12 + 32 + 16
        Buffer.BlockCopy(fekNonce, 0, wrappedFekTotal, 0, 12);
        Buffer.BlockCopy(wrappedFekCiphertext, 0, wrappedFekTotal, 12, 32);
        Buffer.BlockCopy(wrappedFekTag, 0, wrappedFekTotal, 44, 16);

        // 3. Generate random 64-bit nonce prefix
        var noncePrefix = new byte[8];
        RandomNumberGenerator.Fill(noncePrefix);

        // 4. Construct unauthenticated header (98 bytes)
        var headerAad = new byte[98];
        Buffer.BlockCopy(Magic, 0, headerAad, 0, 4);
        BinaryPrimitives.WriteUInt16BigEndian(headerAad.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt16BigEndian(headerAad.AsSpan(6, 2), CipherIdAes256Gcm);
        BinaryPrimitives.WriteUInt32BigEndian(headerAad.AsSpan(8, 4), (uint)chunkSize);
        Buffer.BlockCopy(fileIdBytes, 0, headerAad, 12, 16);
        BinaryPrimitives.WriteUInt16BigEndian(headerAad.AsSpan(28, 2), (ushort)wrappedFekTotal.Length);
        Buffer.BlockCopy(wrappedFekTotal, 0, headerAad, 30, 60);
        Buffer.BlockCopy(noncePrefix, 0, headerAad, 90, 8);

        // 5. Authenticate header with fek-wrap-key
        var headerTag = new byte[16];
        var headerNonce = new byte[12];
        Buffer.BlockCopy(noncePrefix, 0, headerNonce, 0, 8); // Remaining 4 bytes are 0
        using (var wrapAes = new AesGcm(fekWrapKey, 16))
        {
            wrapAes.Encrypt(headerNonce, Array.Empty<byte>(), Array.Empty<byte>(), headerTag, headerAad);
        }

        // Write complete header (114 bytes)
        outputStream.Write(headerAad, 0, headerAad.Length);
        outputStream.Write(headerTag, 0, headerTag.Length);

        // 6. Stream chunks
        long totalPlaintextLength = 0;
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fekAes = new AesGcm(fek, 16);

        var chunkBuffer = new byte[chunkSize];
        var ciphertextBuffer = new byte[chunkSize];
        var tagBuffer = new byte[16];
        var chunkNonce = new byte[12];
        Buffer.BlockCopy(noncePrefix, 0, chunkNonce, 0, 8);
        var chunkAad = new byte[21]; // fileId(16) + chunkIndex(4) + finalFlag(1)
        Buffer.BlockCopy(fileIdBytes, 0, chunkAad, 0, 16);

        uint chunkIndex = 0;
        bool done = false;
        int carryByte = -1;

        try
        {
            while (!done)
            {
                int bytesRead = 0;
                if (carryByte != -1)
                {
                    chunkBuffer[0] = (byte)carryByte;
                    bytesRead = 1;
                    carryByte = -1;
                }

                while (bytesRead < chunkSize)
                {
                    int r = inputStream.Read(chunkBuffer, bytesRead, chunkSize - bytesRead);
                    if (r == 0) break;
                    bytesRead += r;
                }

                // Peek if stream has more bytes to know if this is the final chunk
                int nextByte = inputStream.ReadByte();
                bool isFinal = nextByte == -1;
                if (!isFinal)
                {
                    carryByte = nextByte;
                }

                byte finalFlag = (byte)(isFinal ? 1 : 0);
                BinaryPrimitives.WriteUInt32BigEndian(chunkNonce.AsSpan(8, 4), chunkIndex);
                BinaryPrimitives.WriteUInt32BigEndian(chunkAad.AsSpan(16, 4), chunkIndex);
                chunkAad[20] = finalFlag;

                var plaintextSpan = chunkBuffer.AsSpan(0, bytesRead);
                var cipherSpan = ciphertextBuffer.AsSpan(0, bytesRead);

                fekAes.Encrypt(chunkNonce, plaintextSpan, cipherSpan, tagBuffer, chunkAad);

                // Update hash & totals
                if (bytesRead > 0)
                {
                    sha256.AppendData(chunkBuffer, 0, bytesRead);
                    totalPlaintextLength += bytesRead;
                }

                // Write chunk ciphertext + tag
                outputStream.Write(ciphertextBuffer, 0, bytesRead);
                outputStream.Write(tagBuffer, 0, tagBuffer.Length);

                if (isFinal)
                {
                    done = true;
                }
                else
                {
                    chunkIndex++;
                }
            }

            var hashBytes = sha256.GetHashAndReset();
            return (totalPlaintextLength, Convert.ToHexString(hashBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fek);
            CryptographicOperations.ZeroMemory(chunkBuffer);
        }
    }
}
