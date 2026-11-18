using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace AppLock.Services.Vault.Crypto;

public class ParsedFileHeader : IDisposable
{
    public ushort Version { get; init; }
    public ushort CipherId { get; init; }
    public int ChunkSize { get; init; }
    public Guid FileId { get; init; }
    public byte[] NoncePrefix { get; init; } = new byte[8];
    public byte[] Fek { get; init; } = new byte[32];

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Fek);
    }
}

public static class FileObjectReader
{
    public static ParsedFileHeader ReadAndVerifyHeader(Stream stream, byte[] fekWrapKey)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (fekWrapKey == null || fekWrapKey.Length != 32)
            throw new ArgumentException("FEK wrap key must be 32 bytes.", nameof(fekWrapKey));

        var headerBytes = new byte[FileObjectWriter.HeaderSize];
        int read = 0;
        while (read < headerBytes.Length)
        {
            int r = stream.Read(headerBytes, read, headerBytes.Length - read);
            if (r == 0) throw new EndOfStreamException("Incomplete file object header.");
            read += r;
        }

        // Verify magic
        if (headerBytes[0] != FileObjectWriter.Magic[0] ||
            headerBytes[1] != FileObjectWriter.Magic[1] ||
            headerBytes[2] != FileObjectWriter.Magic[2] ||
            headerBytes[3] != FileObjectWriter.Magic[3])
        {
            throw new InvalidDataException("Invalid file object magic signature.");
        }

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.AsSpan(4, 2));
        if (version != 1)
        {
            throw new NotSupportedException($"Unsupported file object version: {version}");
        }

        ushort cipherId = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.AsSpan(6, 2));
        if (cipherId != FileObjectWriter.CipherIdAes256Gcm)
        {
            throw new NotSupportedException($"Unsupported cipher suite: {cipherId}");
        }

        int chunkSize = (int)BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(8, 4));
        if (chunkSize <= 0 || chunkSize > 64 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid chunk size: {chunkSize}");
        }

        var fileIdBytes = new byte[16];
        Buffer.BlockCopy(headerBytes, 12, fileIdBytes, 0, 16);
        var fileId = new Guid(fileIdBytes);

        ushort wrappedFekLen = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.AsSpan(28, 2));
        if (wrappedFekLen != 60)
        {
            throw new InvalidDataException($"Invalid wrapped FEK length: {wrappedFekLen}");
        }

        var wrappedFek = new byte[60];
        Buffer.BlockCopy(headerBytes, 30, wrappedFek, 0, 60);

        var noncePrefix = new byte[8];
        Buffer.BlockCopy(headerBytes, 90, noncePrefix, 0, 8);

        var headerTag = new byte[16];
        Buffer.BlockCopy(headerBytes, 98, headerTag, 0, 16);

        // Verify header tag using fek-wrap-key
        var headerAad = new byte[98];
        Buffer.BlockCopy(headerBytes, 0, headerAad, 0, 98);

        var headerNonce = new byte[12];
        Buffer.BlockCopy(noncePrefix, 0, headerNonce, 0, 8);

        using (var wrapAes = new AesGcm(fekWrapKey, 16))
        {
            try
            {
                wrapAes.Decrypt(headerNonce, Array.Empty<byte>(), headerTag, Array.Empty<byte>(), headerAad);
            }
            catch (CryptographicException)
            {
                throw new CryptographicException("File object header authentication failed (header has been tampered with).");
            }
        }

        // Unwrap FEK
        var fekNonce = new byte[12];
        var fekCiphertext = new byte[32];
        var fekTag = new byte[16];
        Buffer.BlockCopy(wrappedFek, 0, fekNonce, 0, 12);
        Buffer.BlockCopy(wrappedFek, 12, fekCiphertext, 0, 32);
        Buffer.BlockCopy(wrappedFek, 44, fekTag, 0, 16);

        var fek = new byte[32];
        using (var wrapAes = new AesGcm(fekWrapKey, 16))
        {
            try
            {
                wrapAes.Decrypt(fekNonce, fekCiphertext, fekTag, fek, fileIdBytes);
            }
            catch (CryptographicException)
            {
                throw new CryptographicException("Failed to unwrap file encryption key (tampered or incorrect key).");
            }
        }

        return new ParsedFileHeader
        {
            Version = version,
            CipherId = cipherId,
            ChunkSize = chunkSize,
            FileId = fileId,
            NoncePrefix = noncePrefix,
            Fek = fek
        };
    }

    /// <summary>
    /// Reads and decrypts all chunks from input stream to output stream.
    /// Verifies counter continuity, final-chunk flag, and chunk authentication tags.
    /// </summary>
    public static long DecryptAll(Stream inputStream, Stream outputStream, byte[] fekWrapKey)
    {
        using var header = ReadAndVerifyHeader(inputStream, fekWrapKey);
        using var fekAes = new AesGcm(header.Fek, 16);

        var fileIdBytes = header.FileId.ToByteArray();
        var chunkNonce = new byte[12];
        Buffer.BlockCopy(header.NoncePrefix, 0, chunkNonce, 0, 8);
        var chunkAad = new byte[21];
        Buffer.BlockCopy(fileIdBytes, 0, chunkAad, 0, 16);

        var cipherBuffer = new byte[header.ChunkSize];
        var plainBuffer = new byte[header.ChunkSize];
        var tagBuffer = new byte[16];

        long totalDecrypted = 0;
        uint chunkIndex = 0;
        bool sawFinal = false;

        while (!sawFinal)
        {
            // Read until either we have tag (minimum 16 bytes) and ciphertext
            // In a chunk, ciphertext length <= chunkSize. But we don't know exact chunk ciphertext length in advance
            // unless we check stream length or read chunks.
            // When streaming:
            // Notice: every chunk except the final one has exactly header.ChunkSize ciphertext bytes!
            // Let's verify: For all non-final chunks, bytesRead = header.ChunkSize + 16.
            // If remaining bytes in stream < header.ChunkSize + 16, this must be the final chunk!
            int bytesToRead = header.ChunkSize + 16;
            int totalRead = 0;
            var buffer = new byte[bytesToRead];

            while (totalRead < bytesToRead)
            {
                int r = inputStream.Read(buffer, totalRead, bytesToRead - totalRead);
                if (r == 0) break;
                totalRead += r;
            }

            if (totalRead < 16)
            {
                throw new InvalidDataException($"Truncated chunk at index {chunkIndex}: read {totalRead} bytes (expected at least 16-byte tag).");
            }

            int cipherLen = totalRead - 16;
            Buffer.BlockCopy(buffer, totalRead - 16, tagBuffer, 0, 16);
            Buffer.BlockCopy(buffer, 0, cipherBuffer, 0, cipherLen);

            // Peek next byte to see if this is the final chunk or if there are more
            int nextByte = inputStream.ReadByte();
            bool isFinal = nextByte == -1;
            if (!isFinal)
            {
                // Put byte back by seeking back if seekable, or we read chunk by chunk
                if (inputStream.CanSeek)
                {
                    inputStream.Seek(-1, SeekOrigin.Current);
                }
                else
                {
                    throw new NotSupportedException("Non-seekable stream with chunk peek not supported.");
                }
            }

            byte finalFlag = (byte)(isFinal ? 1 : 0);
            BinaryPrimitives.WriteUInt32BigEndian(chunkNonce.AsSpan(8, 4), chunkIndex);
            BinaryPrimitives.WriteUInt32BigEndian(chunkAad.AsSpan(16, 4), chunkIndex);
            chunkAad[20] = finalFlag;

            try
            {
                fekAes.Decrypt(
                    chunkNonce,
                    cipherBuffer.AsSpan(0, cipherLen),
                    tagBuffer,
                    plainBuffer.AsSpan(0, cipherLen),
                    chunkAad);
            }
            catch (CryptographicException)
            {
                throw new CryptographicException($"Chunk {chunkIndex} authentication failed (data corrupted, tampered, or out of order).");
            }

            if (cipherLen > 0)
            {
                outputStream.Write(plainBuffer, 0, cipherLen);
                totalDecrypted += cipherLen;
            }

            if (isFinal)
            {
                sawFinal = true;
            }
            chunkIndex++;
        }

        return totalDecrypted;
    }
}
