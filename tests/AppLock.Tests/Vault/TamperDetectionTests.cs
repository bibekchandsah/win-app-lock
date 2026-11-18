using System;
using System.IO;
using System.Security.Cryptography;
using AppLock.Models.Vault;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;
using Xunit;

namespace AppLock.Tests.Vault;

public class TamperDetectionTests
{
    private readonly byte[] _fekWrapKey = new byte[32];
    private readonly byte[] _indexKey = new byte[32];

    public TamperDetectionTests()
    {
        RandomNumberGenerator.Fill(_fekWrapKey);
        RandomNumberGenerator.Fill(_indexKey);
    }

    [Fact]
    public void FileHeader_BitFlip_RejectsImmediately()
    {
        var data = new byte[500];
        RandomNumberGenerator.Fill(data);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, Guid.NewGuid(), 256);

        var encBytes = encStream.ToArray();

        // Flip bit in header (offset 15: inside fileId)
        encBytes[15] ^= 0x01;

        using var tamperedStream = new MemoryStream(encBytes);
        using var outStream = new MemoryStream();

        Assert.ThrowsAny<CryptographicException>(() =>
            FileObjectReader.DecryptAll(tamperedStream, outStream, _fekWrapKey));
    }

    [Fact]
    public void FileChunk_CiphertextBitFlip_RejectsDecryption()
    {
        var data = new byte[500];
        RandomNumberGenerator.Fill(data);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, Guid.NewGuid(), 256);

        var encBytes = encStream.ToArray();

        // Flip bit in chunk ciphertext (after 114-byte header)
        encBytes[114 + 10] ^= 0x01;

        using var tamperedStream = new MemoryStream(encBytes);
        using var outStream = new MemoryStream();

        Assert.ThrowsAny<CryptographicException>(() =>
            FileObjectReader.DecryptAll(tamperedStream, outStream, _fekWrapKey));
    }

    [Fact]
    public void FileChunk_TagBitFlip_RejectsDecryption()
    {
        var data = new byte[100];
        RandomNumberGenerator.Fill(data);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, Guid.NewGuid(), 256);

        var encBytes = encStream.ToArray();

        // Tag is the last 16 bytes
        encBytes[encBytes.Length - 5] ^= 0x01;

        using var tamperedStream = new MemoryStream(encBytes);
        using var outStream = new MemoryStream();

        Assert.ThrowsAny<CryptographicException>(() =>
            FileObjectReader.DecryptAll(tamperedStream, outStream, _fekWrapKey));
    }

    [Fact]
    public void FileObject_Truncation_FailsClosed()
    {
        var data = new byte[1000];
        RandomNumberGenerator.Fill(data);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, Guid.NewGuid(), 256);

        var encBytes = encStream.ToArray();

        // Truncate 30 bytes off the end
        var truncated = new byte[encBytes.Length - 30];
        Buffer.BlockCopy(encBytes, 0, truncated, 0, truncated.Length);

        using var tamperedStream = new MemoryStream(truncated);
        using var outStream = new MemoryStream();

        Assert.ThrowsAny<Exception>(() =>
            FileObjectReader.DecryptAll(tamperedStream, outStream, _fekWrapKey));
    }

    [Fact]
    public void FileObject_ChunkReordering_FailsAuthentication()
    {
        // 2 chunks of 100 bytes each
        var data = new byte[200];
        RandomNumberGenerator.Fill(data);

        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();
        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, Guid.NewGuid(), 100);

        var encBytes = encStream.ToArray();

        // Header is 114 bytes
        // Chunk 0: 100 bytes ciphertext + 16 bytes tag = 116 bytes (offset 114 to 230)
        // Chunk 1: 100 bytes ciphertext + 16 bytes tag = 116 bytes (offset 230 to 346)
        var chunk0 = new byte[116];
        var chunk1 = new byte[116];
        Buffer.BlockCopy(encBytes, 114, chunk0, 0, 116);
        Buffer.BlockCopy(encBytes, 230, chunk1, 0, 116);

        // Swap chunks in output
        var swapped = new byte[encBytes.Length];
        Buffer.BlockCopy(encBytes, 0, swapped, 0, 114);
        Buffer.BlockCopy(chunk1, 0, swapped, 114, 116);
        Buffer.BlockCopy(chunk0, 0, swapped, 230, 116);

        using var tamperedStream = new MemoryStream(swapped);
        using var outStream = new MemoryStream();

        Assert.ThrowsAny<CryptographicException>(() =>
            FileObjectReader.DecryptAll(tamperedStream, outStream, _fekWrapKey));
    }

    [Fact]
    public void Index_BitFlip_FailsDecryption()
    {
        var vaultId = Guid.NewGuid();
        var index = new VaultIndex(vaultId);
        index.AddItem(new IndexItem { Name = "confidential.pdf", Size = 1234 });

        var encIndex = VaultIndex.EncryptIndex(index.Data, _indexKey);

        // Flip bit in ciphertext
        encIndex[30] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            VaultIndex.DecryptIndex(encIndex, _indexKey, vaultId));
    }

    [Fact]
    public void Index_RollbackReplay_ThrowsRevisionError()
    {
        var vaultId = Guid.NewGuid();
        var index = new VaultIndex(vaultId);
        index.AddItem(new IndexItem { Name = "doc1.txt" });
        index.Data.Revision = 2; // Old revision

        var oldEnc = VaultIndex.EncryptIndex(index.Data, _indexKey);

        // Expecting minimum revision 5 (we previously saw revision 5 on this PC)
        Assert.Throws<CryptographicException>(() =>
            VaultIndex.DecryptIndex(oldEnc, _indexKey, vaultId, expectedMinRevision: 5));
    }
}
