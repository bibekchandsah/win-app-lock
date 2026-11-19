using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AppLock.Services.Vault.Crypto;
using CryptoPaddingMode = AppLock.Services.Vault.Crypto.PaddingMode;
using Xunit;

namespace AppLock.Tests.Vault;

/// <summary>
/// Tests for size-padding (Bucket64KiB mode) and transparent decryption via ChunkedCryptoStream.
/// </summary>
public class SizePaddingTests
{
    private readonly byte[] _fekWrapKey = new byte[32];

    public SizePaddingTests()
    {
        RandomNumberGenerator.Fill(_fekWrapKey);
    }

    [Theory]
    [InlineData(1)]         // 1 byte → padded to 64 KiB bucket
    [InlineData(23)]        // text "Original report content" size
    [InlineData(65535)]     // one byte less than a full 64 KiB bucket
    [InlineData(65536)]     // exactly one 64 KiB bucket (no padding needed)
    [InlineData(65537)]     // one byte over — pads to 128 KiB bucket
    [InlineData(131072)]    // exactly two 64 KiB buckets
    public void PaddedObject_DecryptsToExactOriginalPlaintext_ViaCryptoStream(int dataSize)
    {
        var original = new byte[dataSize];
        RandomNumberGenerator.Fill(original);
        var fileId = Guid.NewGuid();

        // Write with Bucket64KiB padding
        using var inStream = new MemoryStream(original);
        using var encStream = new MemoryStream();
        var (writtenLen, sha256Hex) = FileObjectWriter.WriteObject(
            inStream, encStream, _fekWrapKey, fileId, paddingMode: CryptoPaddingMode.Bucket64KiB);

        Assert.Equal(dataSize, writtenLen);
        // Verify SHA-256 matches original
        var expectedSha = Convert.ToHexString(SHA256.HashData(original));
        Assert.Equal(expectedSha, sha256Hex, StringComparer.OrdinalIgnoreCase);

        // Decrypt via ChunkedCryptoStream with knownPlaintextLength → no padding leaks
        encStream.Position = 0;
        using var cs = new ChunkedCryptoStream(encStream, _fekWrapKey, knownPlaintextLength: dataSize, leaveOpen: true);
        using var outStream = new MemoryStream();
        cs.CopyTo(outStream);

        Assert.Equal(dataSize, (int)outStream.Length);
        Assert.Equal(original, outStream.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    [InlineData(65535)]
    [InlineData(65537)]
    public void PaddedObject_DecryptsToExactOriginalPlaintext_ViaDecryptAll(int dataSize)
    {
        var original = new byte[dataSize];
        RandomNumberGenerator.Fill(original);
        var fileId = Guid.NewGuid();

        using var inStream = new MemoryStream(original);
        using var encStream = new MemoryStream();
        var (writtenLen, sha256Hex) = FileObjectWriter.WriteObject(
            inStream, encStream, _fekWrapKey, fileId, paddingMode: CryptoPaddingMode.Bucket64KiB);

        // Verify via DecryptAll with expectedPlaintextLength → strips padding
        encStream.Position = 0;
        using var outStream = new MemoryStream();
        long decLen = FileObjectReader.DecryptAll(encStream, outStream, _fekWrapKey, expectedPlaintextLength: writtenLen);

        Assert.Equal(dataSize, (int)decLen);
        Assert.Equal(original, outStream.ToArray());
    }

    [Fact]
    public void PaddedObject_OnDisk_IsLargerThanOriginal_ForSubBucketFile()
    {
        const int fileSize = 1000; // well under 64 KiB bucket
        var data = new byte[fileSize];
        RandomNumberGenerator.Fill(data);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, Guid.NewGuid(), paddingMode: CryptoPaddingMode.Bucket64KiB);

        // The on-disk chunk ciphertext should be padded to 64 KiB (+ 16 byte tag)
        long encSize = encStream.Length;
        long dataSection = encSize - FileObjectWriter.HeaderSize;
        long cipherLen = dataSection - 16; // subtract final tag

        Assert.True(cipherLen >= SizePadding.DefaultBucketSize,
            $"Expected padded ciphertext >= {SizePadding.DefaultBucketSize} but got {cipherLen}");
    }

    [Fact]
    public void NoPadding_ObjectSize_ReflectsExactPlaintext()
    {
        const int fileSize = 1000;
        var data = new byte[fileSize];
        RandomNumberGenerator.Fill(data);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, Guid.NewGuid(), paddingMode: CryptoPaddingMode.None);

        long encSize = encStream.Length;
        long dataSection = encSize - FileObjectWriter.HeaderSize;
        long cipherLen = dataSection - 16;

        Assert.Equal(fileSize, cipherLen);
    }

    [Fact]
    public void SizePadding_CalculatePaddedChunkSize_IsCorrect()
    {
        const int bucket = SizePadding.DefaultBucketSize; // 65536

        Assert.Equal(bucket, SizePadding.CalculatePaddedChunkSize(1, bucket));
        Assert.Equal(bucket, SizePadding.CalculatePaddedChunkSize(65535, bucket));
        Assert.Equal(bucket, SizePadding.CalculatePaddedChunkSize(65536, bucket));
        Assert.Equal(bucket * 2, SizePadding.CalculatePaddedChunkSize(65537, bucket));
        Assert.Equal(0, SizePadding.CalculatePaddedChunkSize(0, bucket));
    }

    [Fact]
    public void PaddedObject_TextContent_DecryptsAccurately_ViaCryptoStream()
    {
        const string content = "Hello from AppLock size padding test! Привет мир.";
        var data = Encoding.UTF8.GetBytes(content);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        var (writtenLen, _) = FileObjectWriter.WriteObject(
            inStream, encStream, _fekWrapKey, Guid.NewGuid(), paddingMode: CryptoPaddingMode.Bucket64KiB);

        encStream.Position = 0;
        using var cs = new ChunkedCryptoStream(encStream, _fekWrapKey, knownPlaintextLength: writtenLen, leaveOpen: true);
        using var reader = new StreamReader(cs, Encoding.UTF8);
        var decoded = reader.ReadToEnd();

        Assert.Equal(content, decoded);
    }
}
