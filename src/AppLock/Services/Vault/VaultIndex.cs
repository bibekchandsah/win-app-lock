using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AppLock.Models.Vault;

namespace AppLock.Services.Vault;

public class VaultIndex
{
    public static readonly byte[] Magic = "ALIX"u8.ToArray();
    public const ushort Version = 1;

    public VaultIndexData Data { get; private set; }

    public VaultIndex(Guid vaultId)
    {
        Data = new VaultIndexData
        {
            VaultId = vaultId,
            Revision = 1,
            Updated = DateTime.UtcNow,
            Items = new List<IndexItem>()
        };
    }

    public VaultIndex(VaultIndexData data)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public static byte[] EncryptIndex(VaultIndexData indexData, byte[] indexKey)
    {
        if (indexData == null) throw new ArgumentNullException(nameof(indexData));
        if (indexKey == null || indexKey.Length != 32)
            throw new ArgumentException("Index key must be 32 bytes.", nameof(indexKey));

        // 1. Serialize JSON and compress with GZip
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                JsonSerializer.Serialize(gzip, indexData);
            }
            compressed = ms.ToArray();
        }

        // 2. Encrypt with AES-256-GCM
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var aad = new byte[24]; // vaultId(16) + revision(8)
        Buffer.BlockCopy(indexData.VaultId.ToByteArray(), 0, aad, 0, 16);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(16, 8), indexData.Revision);

        var ciphertext = new byte[compressed.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(indexKey, 16))
        {
            aes.Encrypt(nonce, compressed, ciphertext, tag, aad);
        }

        // Output: Magic(4) + Version(2) + Revision(8) + Nonce(12) + Ciphertext + Tag(16)
        var totalLength = 4 + 2 + 8 + 12 + ciphertext.Length + 16;
        var result = new byte[totalLength];
        Buffer.BlockCopy(Magic, 0, result, 0, 4);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(6, 8), indexData.Revision);
        Buffer.BlockCopy(nonce, 0, result, 14, 12);
        Buffer.BlockCopy(ciphertext, 0, result, 26, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, 26 + ciphertext.Length, 16);

        return result;
    }

    public static VaultIndexData DecryptIndex(byte[] encryptedBytes, byte[] indexKey, Guid vaultId, long? expectedMinRevision = null)
    {
        if (encryptedBytes == null || encryptedBytes.Length < 42)
            throw new InvalidDataException("Encrypted index blob is too short.");
        if (indexKey == null || indexKey.Length != 32)
            throw new ArgumentException("Index key must be 32 bytes.", nameof(indexKey));

        // Verify magic
        if (encryptedBytes[0] != Magic[0] ||
            encryptedBytes[1] != Magic[1] ||
            encryptedBytes[2] != Magic[2] ||
            encryptedBytes[3] != Magic[3])
        {
            throw new InvalidDataException("Invalid index magic signature.");
        }

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(encryptedBytes.AsSpan(4, 2));
        if (version != 1)
        {
            throw new NotSupportedException($"Unsupported index version: {version}");
        }

        long revision = BinaryPrimitives.ReadInt64BigEndian(encryptedBytes.AsSpan(6, 8));
        if (expectedMinRevision.HasValue && revision < expectedMinRevision.Value)
        {
            // Rollback warning
            throw new CryptographicException($"Index rollback detected: loaded revision {revision} is older than previously recorded revision {expectedMinRevision.Value}.");
        }

        var nonce = new byte[12];
        Buffer.BlockCopy(encryptedBytes, 14, nonce, 0, 12);

        int cipherLen = encryptedBytes.Length - 26 - 16;
        if (cipherLen < 0) throw new InvalidDataException("Invalid index length.");

        var ciphertext = new byte[cipherLen];
        var tag = new byte[16];
        Buffer.BlockCopy(encryptedBytes, 26, ciphertext, 0, cipherLen);
        Buffer.BlockCopy(encryptedBytes, 26 + cipherLen, tag, 0, 16);

        var aad = new byte[24];
        Buffer.BlockCopy(vaultId.ToByteArray(), 0, aad, 0, 16);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(16, 8), revision);

        var compressed = new byte[cipherLen];
        using (var aes = new AesGcm(indexKey, 16))
        {
            try
            {
                aes.Decrypt(nonce, ciphertext, tag, compressed, aad);
            }
            catch (CryptographicException)
            {
                throw new CryptographicException("Index decryption/authentication failed (index blob tampered or corrupt).");
            }
        }

        // Decompress and deserialize
        using var ms = new MemoryStream(compressed);
        using var gzip = new GZipStream(ms, CompressionMode.Decompress);
        var data = JsonSerializer.Deserialize<VaultIndexData>(gzip);
        if (data == null) throw new InvalidDataException("Deserialized index was null.");

        return data;
    }

    public void AddItem(IndexItem item)
    {
        Data.Items.Add(item);
        Data.Revision++;
        Data.Updated = DateTime.UtcNow;
    }

    public bool RemoveItem(Guid itemId)
    {
        int count = Data.Items.RemoveAll(i => i.Id == itemId);
        if (count > 0)
        {
            Data.Revision++;
            Data.Updated = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    public IndexItem? GetItem(Guid itemId) =>
        Data.Items.FirstOrDefault(i => i.Id == itemId);

    public IEnumerable<IndexItem> GetItemsInFolder(Guid? parentId) =>
        Data.Items.Where(i => i.ParentId == parentId && !i.IsTrash);

    public IEnumerable<IndexItem> GetTrashItems() =>
        Data.Items.Where(i => i.IsTrash);

    public void IncrementRevision()
    {
        Data.Revision++;
        Data.Updated = DateTime.UtcNow;
    }
}
