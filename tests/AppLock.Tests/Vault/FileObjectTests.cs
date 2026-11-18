using System;
using System.IO;
using System.Security.Cryptography;
using AppLock.Services.Vault.Crypto;
using Xunit;

namespace AppLock.Tests.Vault;

public class FileObjectTests
{
    private readonly byte[] _fekWrapKey = new byte[32];

    public FileObjectTests()
    {
        RandomNumberGenerator.Fill(_fekWrapKey);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(1, 1024)]
    [InlineData(1023, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1024)]
    [InlineData(5000, 1024)]
    [InlineData(262144, 262144)] // standard chunk size
    [InlineData(600000, 262144)] // multi-chunk standard
    public void FileObject_RoundTrip_MatchesOriginalExactly(int dataSize, int chunkSize)
    {
        var originalData = new byte[dataSize];
        if (dataSize > 0)
        {
            RandomNumberGenerator.Fill(originalData);
        }

        var fileId = Guid.NewGuid();

        using var inStream = new MemoryStream(originalData);
        using var encryptedStream = new MemoryStream();

        // 1. Write object
        var (writtenLen, sha256) = FileObjectWriter.WriteObject(
            inStream,
            encryptedStream,
            _fekWrapKey,
            fileId,
            chunkSize);

        Assert.Equal(dataSize, writtenLen);
        var expectedSha = Convert.ToHexString(SHA256.HashData(originalData));
        Assert.Equal(expectedSha, sha256);

        // 2. Read object back
        encryptedStream.Position = 0;
        using var decryptedStream = new MemoryStream();
        long decryptedLen = FileObjectReader.DecryptAll(encryptedStream, decryptedStream, _fekWrapKey);

        Assert.Equal(dataSize, decryptedLen);
        Assert.Equal(originalData, decryptedStream.ToArray());
    }

    [Fact]
    public void ChunkedCryptoStream_RandomAccessSeeking_ReturnsAccurateBytes()
    {
        const int chunkSize = 512;
        const int totalSize = 3500; // ~7 chunks
        var data = new byte[totalSize];
        for (int i = 0; i < totalSize; i++) data[i] = (byte)(i % 251);

        var fileId = Guid.NewGuid();
        using var inStream = new MemoryStream(data);
        using var encStream = new MemoryStream();

        FileObjectWriter.WriteObject(inStream, encStream, _fekWrapKey, fileId, chunkSize);

        encStream.Position = 0;
        using var chunkStream = new ChunkedCryptoStream(encStream, _fekWrapKey, knownPlaintextLength: totalSize, leaveOpen: true);

        Assert.Equal(totalSize, chunkStream.Length);

        // Test 1: Read first 100 bytes
        var buf1 = new byte[100];
        int r1 = chunkStream.Read(buf1, 0, 100);
        Assert.Equal(100, r1);
        Assert.Equal(data.AsSpan(0, 100).ToArray(), buf1);

        // Test 2: Seek into middle of chunk 3 (offset 1600) and read across chunk boundary (700 bytes)
        chunkStream.Seek(1600, SeekOrigin.Begin);
        Assert.Equal(1600, chunkStream.Position);

        var buf2 = new byte[700];
        int r2 = chunkStream.Read(buf2, 0, 700);
        Assert.Equal(700, r2);
        Assert.Equal(data.AsSpan(1600, 700).ToArray(), buf2);

        // Test 3: Seek backwards to offset 50
        chunkStream.Seek(50, SeekOrigin.Begin);
        var buf3 = new byte[20];
        int r3 = chunkStream.Read(buf3, 0, 20);
        Assert.Equal(20, r3);
        Assert.Equal(data.AsSpan(50, 20).ToArray(), buf3);

        // Test 4: Seek near end (totalSize - 10)
        chunkStream.Seek(-10, SeekOrigin.End);
        var buf4 = new byte[20];
        int r4 = chunkStream.Read(buf4, 0, 20);
        Assert.Equal(10, r4); // only 10 bytes remaining
        Assert.Equal(data.AsSpan(totalSize - 10, 10).ToArray(), buf4.AsSpan(0, 10).ToArray());

        // EOF read returns 0
        int rEof = chunkStream.Read(buf4, 0, 10);
        Assert.Equal(0, rEof);
    }
}
