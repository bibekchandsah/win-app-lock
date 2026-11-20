using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace AppLock.WebFilter.Engine;

/// <summary>
/// An immutable, sorted array of 64-bit XxHash64 domain hashes. A lookup walks the query name's
/// suffixes and binary-searches each one, so <c>example.com</c> matches <c>a.example.com</c> too.
/// The file is memory-mapped and never held in a managed array, which keeps a million domains at
/// roughly 8 MB of mapped pages. The per-install seed is stored in the file so a snapshot stays
/// self-describing; the tiny 64-bit collision probability is accepted (documented in NOTES).
/// </summary>
public sealed class ListSnapshot : IDisposable
{
    public const string Magic = "ALWFLST1";
    public const int Version = 1;
    public const int HeaderSize = 28;
    public const int MaxDomains = 5_000_000;

    private readonly ulong[]? _memory;
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly FileStream? _stream;
    private bool _disposed;

    private ListSnapshot(ulong seed, int count, ulong[] memory)
    {
        Seed = seed;
        Count = count;
        _memory = memory;
    }

    private ListSnapshot(ulong seed, int count, FileStream stream, MemoryMappedFile map, MemoryMappedViewAccessor view)
    {
        Seed = seed;
        Count = count;
        _stream = stream;
        _map = map;
        _view = view;
    }

    public ulong Seed { get; }

    public int Count { get; }

    public bool IsEmpty => Count == 0;

    /// <summary>Builds an in-memory snapshot from domain names. Empty or invalid names are skipped.</summary>
    public static ListSnapshot FromDomains(IEnumerable<string> domains, ulong seed)
    {
        var hashes = new HashSet<ulong>();

        foreach (var domain in domains)
        {
            if (DomainName.TryNormalize(domain, out var normalized))
            {
                hashes.Add(Hash(normalized, seed));
            }
        }

        var array = hashes.ToArray();
        Array.Sort(array);
        return new ListSnapshot(seed, array.Length, array);
    }

    /// <summary>Opens a compiled snapshot. Throws <see cref="InvalidDataException"/> when it is corrupt.</summary>
    public static ListSnapshot Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        try
        {
            if (stream.Length < HeaderSize) throw new InvalidDataException("Snapshot is too short");

            var header = new byte[HeaderSize];
            int read = 0;
            while (read < header.Length)
            {
                int chunk = stream.Read(header, read, header.Length - read);
                if (chunk <= 0) throw new InvalidDataException("Snapshot header is truncated");
                read += chunk;
            }

            if (!header.AsSpan(0, 8).SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            {
                throw new InvalidDataException("Snapshot magic is wrong");
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
            if (version != Version) throw new InvalidDataException($"Unsupported snapshot version {version}");

            ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12, 8));
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20, 4));

            if (count < 0 || count > MaxDomains) throw new InvalidDataException("Snapshot count is out of range");
            if (stream.Length != HeaderSize + (long)count * sizeof(ulong))
            {
                throw new InvalidDataException("Snapshot length does not match its count");
            }

            var map = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);

            var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new ListSnapshot(seed, count, stream, map, view);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Writes a compiled snapshot to a stream. The caller is responsible for atomic replacement.</summary>
    public static void Write(IEnumerable<string> domains, ulong seed, Stream destination)
    {
        var snapshot = FromDomains(domains, seed);

        Span<byte> header = stackalloc byte[HeaderSize];
        Encoding.ASCII.GetBytes(Magic).AsSpan().CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], seed);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], snapshot.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 0);

        destination.Write(header);

        var buffer = new byte[8192];
        int used = 0;

        for (int i = 0; i < snapshot.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(used, 8), snapshot.ReadHash(i));
            used += 8;

            if (used == buffer.Length)
            {
                destination.Write(buffer);
                used = 0;
            }
        }

        if (used > 0)
        {
            destination.Write(buffer, 0, used);
        }
    }

    /// <summary>Finds the most specific listed suffix of <paramref name="normalizedName"/>.</summary>
    public bool TryMatch(string normalizedName, out string matched)
    {
        matched = string.Empty;

        foreach (var suffix in DomainName.EnumerateSuffixes(normalizedName))
        {
            if (ContainsHash(Hash(suffix, Seed)))
            {
                matched = suffix;
                return true;
            }
        }

        return false;
    }

    public bool Contains(string normalizedName) => TryMatch(normalizedName, out _);

    private bool ContainsHash(ulong hash)
    {
        int low = 0;
        int high = Count - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            ulong value = ReadHash(middle);

            if (value == hash) return true;
            if (value < hash) low = middle + 1;
            else high = middle - 1;
        }

        return false;
    }

    private ulong ReadHash(int index)
    {
        if (_memory != null) return _memory[index];

        long offset = HeaderSize + (long)index * sizeof(ulong);
        return _view!.ReadUInt64(offset);
    }

    private static ulong Hash(string name, ulong seed)
    {
        Span<byte> buffer = stackalloc byte[256];
        int length = Encoding.ASCII.GetBytes(name, buffer);
        return XxHash64.HashToUInt64(buffer[..length], (long)seed);
    }

    /// <summary>A fresh per-install seed. Not a secret; it only spreads the hash table per install.</summary>
    public static ulong NewSeed()
        => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(sizeof(ulong)));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _view?.Dispose();
        _map?.Dispose();
        _stream?.Dispose();
    }
}
