using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace AppLock.Services.Vault.Crypto;

public class ChunkedCryptoStream : Stream
{
    private readonly Stream _baseStream;
    private readonly bool _leaveOpen;
    private readonly ParsedFileHeader _header;
    private readonly AesGcm _aes;
    private readonly byte[] _fileIdBytes;
    private readonly long _plainLength;

    private long _position;
    private long _cachedChunkIndex = -1;
    private byte[] _cachedChunkPlaintext = Array.Empty<byte>();
    private bool _isDisposed;

    public ChunkedCryptoStream(Stream baseStream, byte[] fekWrapKey, long? knownPlaintextLength = null, bool leaveOpen = false)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        if (!_baseStream.CanSeek)
            throw new ArgumentException("Base stream must be seekable for random access.", nameof(baseStream));

        _leaveOpen = leaveOpen;
        _baseStream.Seek(0, SeekOrigin.Begin);
        _header = FileObjectReader.ReadAndVerifyHeader(_baseStream, fekWrapKey);
        _aes = new AesGcm(_header.Fek, 16);
        _fileIdBytes = _header.FileId.ToByteArray();

        if (knownPlaintextLength.HasValue)
        {
            _plainLength = knownPlaintextLength.Value;
        }
        else
        {
            // Compute plain length from file size
            long totalFileLength = _baseStream.Length;
            long dataLength = totalFileLength - FileObjectWriter.HeaderSize;
            if (dataLength < 16)
            {
                _plainLength = 0;
            }
            else
            {
                int fullChunkStoredSize = _header.ChunkSize + 16;
                long fullChunks = dataLength / fullChunkStoredSize;
                long remainder = dataLength % fullChunkStoredSize;
                if (remainder == 0 && fullChunks > 0)
                {
                    // Last chunk is exactly full size
                    _plainLength = fullChunks * _header.ChunkSize;
                }
                else
                {
                    long lastChunkPlain = Math.Max(0, remainder - 16);
                    _plainLength = fullChunks * _header.ChunkSize + lastChunkPlain;
                }
            }
        }
    }

    public override bool CanRead => !_isDisposed;
    public override bool CanSeek => !_isDisposed;
    public override bool CanWrite => false;
    public override long Length => _plainLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        CheckDisposed();
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _plainLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPos < 0) throw new IOException("An attempt was made to move the file pointer before the beginning of the file.");
        _position = newPos;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        CheckDisposed();
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || buffer.Length - offset < count)
            throw new ArgumentException("Invalid buffer range.");

        if (count == 0 || _position >= _plainLength)
            return 0;

        int totalRead = 0;

        while (count > 0 && _position < _plainLength)
        {
            long chunkIndex = _position / _header.ChunkSize;
            int offsetInChunk = (int)(_position % _header.ChunkSize);

            EnsureChunkLoaded(chunkIndex);

            int availableInChunk = _cachedChunkPlaintext.Length - offsetInChunk;
            if (availableInChunk <= 0) break;

            int toCopy = Math.Min(count, availableInChunk);
            Buffer.BlockCopy(_cachedChunkPlaintext, offsetInChunk, buffer, offset + totalRead, toCopy);

            totalRead += toCopy;
            _position += toCopy;
            count -= toCopy;
        }

        return totalRead;
    }

    private void EnsureChunkLoaded(long chunkIndex)
    {
        if (_cachedChunkIndex == chunkIndex)
            return;

        int chunkSize = _header.ChunkSize;
        long chunkFileOffset = FileObjectWriter.HeaderSize + chunkIndex * (chunkSize + 16);

        _baseStream.Seek(chunkFileOffset, SeekOrigin.Begin);

        // Determine if this chunk is the final chunk
        long totalDataBytes = _baseStream.Length - FileObjectWriter.HeaderSize;
        int fullChunkStoredSize = chunkSize + 16;
        long totalChunks = (totalDataBytes + fullChunkStoredSize - 1) / fullChunkStoredSize;
        if (totalChunks == 0) totalChunks = 1;
        bool isFinal = (chunkIndex + 1) >= totalChunks;

        long remainingBytes = _baseStream.Length - chunkFileOffset;
        if (remainingBytes < 16)
        {
            throw new InvalidDataException($"Corrupted chunk {chunkIndex}: fewer than 16 bytes available.");
        }

        int chunkStoredLen = isFinal ? (int)remainingBytes : fullChunkStoredSize;
        int cipherLen = chunkStoredLen - 16;

        var storedBuffer = new byte[chunkStoredLen];
        int read = 0;
        while (read < chunkStoredLen)
        {
            int r = _baseStream.Read(storedBuffer, read, chunkStoredLen - read);
            if (r == 0) break;
            read += r;
        }

        if (read != chunkStoredLen)
        {
            throw new EndOfStreamException($"Failed to read expected {chunkStoredLen} bytes for chunk {chunkIndex}.");
        }

        var tag = new byte[16];
        Buffer.BlockCopy(storedBuffer, cipherLen, tag, 0, 16);

        var chunkNonce = new byte[12];
        Buffer.BlockCopy(_header.NoncePrefix, 0, chunkNonce, 0, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunkNonce.AsSpan(8, 4), (uint)chunkIndex);

        var chunkAad = new byte[21];
        Buffer.BlockCopy(_fileIdBytes, 0, chunkAad, 0, 16);
        BinaryPrimitives.WriteUInt32BigEndian(chunkAad.AsSpan(16, 4), (uint)chunkIndex);
        chunkAad[20] = (byte)(isFinal ? 1 : 0);

        var plainBuffer = new byte[cipherLen];
        try
        {
            _aes.Decrypt(
                chunkNonce,
                storedBuffer.AsSpan(0, cipherLen),
                tag,
                plainBuffer,
                chunkAad);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException($"Chunk {chunkIndex} authentication failed (tampered data or corrupted storage).");
        }

        // Cache this decrypted chunk
        if (_cachedChunkPlaintext.Length > 0)
        {
            CryptographicOperations.ZeroMemory(_cachedChunkPlaintext);
        }
        _cachedChunkPlaintext = plainBuffer;
        _cachedChunkIndex = chunkIndex;
    }

    private void CheckDisposed()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ChunkedCryptoStream));
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (disposing)
            {
                if (_cachedChunkPlaintext.Length > 0)
                {
                    CryptographicOperations.ZeroMemory(_cachedChunkPlaintext);
                    _cachedChunkPlaintext = Array.Empty<byte>();
                }
                _aes.Dispose();
                _header.Dispose();
                if (!_leaveOpen)
                {
                    _baseStream.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
