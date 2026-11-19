using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

public enum PreviewCategory
{
    Image,
    Text,
    Pdf,
    Media,
    Unsupported
}

public static class PreviewService
{
    private static readonly System.Collections.Generic.HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico"
    };

    private static readonly System.Collections.Generic.HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".cs", ".js", ".ts", ".html", ".css",
        ".py", ".c", ".cpp", ".h", ".sql", ".sh", ".ps1", ".bat"
    };

    private static readonly System.Collections.Generic.HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".flac", ".aac", ".wma",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm"
    };

    public static PreviewCategory GetCategory(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return PreviewCategory.Unsupported;

        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            return PreviewCategory.Pdf;

        if (ImageExts.Contains(ext))
            return PreviewCategory.Image;

        if (TextExts.Contains(ext))
            return PreviewCategory.Text;

        if (MediaExts.Contains(ext))
            return PreviewCategory.Media;

        return PreviewCategory.Unsupported;
    }

    public static bool CanPreview(string fileName) =>
        GetCategory(fileName) != PreviewCategory.Unsupported;

    /// <summary>
    /// Opens a seekable ChunkedCryptoStream over the encrypted object file.
    /// Does NOT write any plaintext to disk.
    /// Caller must dispose the stream.
    /// </summary>
    public static ChunkedCryptoStream OpenStream(VaultSession session, IndexItem item)
    {
        if (string.IsNullOrEmpty(item.ObjectId))
            throw new ArgumentException("Item has no encrypted object ID.", nameof(item));

        var objPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, item.IsTrash);
        if (!File.Exists(objPath))
            throw new FileNotFoundException($"Encrypted object file for '{item.Name}' is missing on disk.", objPath);

        var fileStream = new FileStream(objPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        return new ChunkedCryptoStream(fileStream, session.FekWrapKey, item.Size, leaveOpen: false);
    }

    /// <summary>
    /// Reads text contents in memory up to maxBytes.
    /// </summary>
    public static async Task<(string Text, bool IsTruncated)> ReadTextAsync(
        VaultSession session,
        IndexItem item,
        int maxBytes = 4 * 1024 * 1024,
        CancellationToken ct = default)
    {
        using var stream = OpenStream(session, item);
        int bytesToRead = (int)Math.Min(stream.Length, maxBytes);
        bool isTruncated = stream.Length > maxBytes;

        var buffer = new byte[bytesToRead];
        int read = 0;
        while (read < bytesToRead)
        {
            ct.ThrowIfCancellationRequested();
            int r = await stream.ReadAsync(buffer.AsMemory(read, bytesToRead - read), ct);
            if (r == 0) break;
            read += r;
        }

        // Detect BOM or default UTF-8
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        return (text, isTruncated);
    }

    /// <summary>
    /// Reads decrypted object into an InMemoryRandomAccessStream for WinUI controls (zero disk writes).
    /// </summary>
    public static async Task<InMemoryRandomAccessStream> LoadToMemoryStreamAsync(
        VaultSession session,
        IndexItem item,
        CancellationToken ct = default)
    {
        using var cryptoStream = OpenStream(session, item);
        var inMemory = new InMemoryRandomAccessStream();

        using (var outNetStream = inMemory.AsStreamForWrite())
        {
            await cryptoStream.CopyToAsync(outNetStream, 65536, ct);
            await outNetStream.FlushAsync(ct);
        }

        inMemory.Seek(0);
        return inMemory;
    }
}
