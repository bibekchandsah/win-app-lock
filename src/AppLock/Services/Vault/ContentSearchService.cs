using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

public class ContentSearchResult
{
    public IndexItem Item { get; init; } = null!;
    public int MatchCount { get; init; }
    public string Snippet { get; init; } = string.Empty;
    public int FirstMatchLine { get; init; }
}

/// <summary>
/// Searches inside encrypted text and code document contents while a vault is unlocked.
/// Uses in-memory ChunkedCryptoStream; never writes unencrypted document content to disk.
/// </summary>
public static class ContentSearchService
{
    private static readonly HashSet<string> SearchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".cs", ".js", ".ts", ".html", ".htm", ".css",
        ".py", ".c", ".cpp", ".h", ".hpp", ".sql", ".sh", ".ps1", ".bat",
        ".rtf", ".tex", ".env", ".toml"
    };

    // Maximum file size to scan for full-text search (10 MiB) to keep responsiveness high
    public const long MaxSearchableFileSizeBytes = 10 * 1024 * 1024;

    public static bool IsSearchable(IndexItem item)
    {
        if (item.IsDirectory || item.IsTrash || string.IsNullOrEmpty(item.ObjectId))
            return false;

        if (item.Size > MaxSearchableFileSizeBytes)
            return false;

        var ext = Path.GetExtension(item.Name);
        return !string.IsNullOrEmpty(ext) && SearchableExtensions.Contains(ext);
    }

    /// <summary>
    /// Searches all searchable files in the unlocked vault session matching the query.
    /// </summary>
    public static async Task<List<ContentSearchResult>> SearchAsync(
        VaultSession session,
        string query,
        Guid? inFolderId = null,
        CancellationToken ct = default)
    {
        if (session == null || string.IsNullOrWhiteSpace(query))
            return new List<ContentSearchResult>();

        var trimmed = query.Trim();
        List<IndexItem> candidateItems;
        lock (session)
        {
            candidateItems = session.Index.Items
                .Where(i => IsSearchable(i) && (!inFolderId.HasValue || i.ParentId == inFolderId.Value))
                .ToList();
        }

        var results = new List<ContentSearchResult>();

        await Task.Run(() =>
        {
            foreach (var item in candidateItems)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var match = SearchItemContent(session, item, trimmed, ct);
                    if (match != null)
                    {
                        lock (results)
                        {
                            results.Add(match);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.LogAction("Vault", $"Content search skipped '{item.Name}': {ex.Message}");
                }
            }
        }, ct);

        return results;
    }

    private static ContentSearchResult? SearchItemContent(
        VaultSession session,
        IndexItem item,
        string query,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.ObjectId)) return null;

        var objPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, item.IsTrash);
        if (!File.Exists(objPath)) return null;

        using var fs = new FileStream(objPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var chunkStream = new ChunkedCryptoStream(fs, session.FekWrapKey, item.Size, leaveOpen: false);
        using var reader = new StreamReader(chunkStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);

        string? line;
        int lineNumber = 0;
        int totalMatches = 0;
        string firstSnippet = string.Empty;
        int firstMatchLine = 0;

        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            lineNumber++;

            int idx = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                totalMatches++;
                if (firstMatchLine == 0)
                {
                    firstMatchLine = lineNumber;
                    firstSnippet = BuildSnippet(line, idx, query.Length);
                }
            }
        }

        if (totalMatches > 0)
        {
            return new ContentSearchResult
            {
                Item = item,
                MatchCount = totalMatches,
                Snippet = firstSnippet,
                FirstMatchLine = firstMatchLine
            };
        }

        return null;
    }

    private static string BuildSnippet(string line, int matchIndex, int matchLength)
    {
        const int contextChars = 40;
        int start = Math.Max(0, matchIndex - contextChars);
        int end = Math.Min(line.Length, matchIndex + matchLength + contextChars);

        string prefix = start > 0 ? "…" : "";
        string suffix = end < line.Length ? "…" : "";

        string rawSnippet = line.Substring(start, end - start).Trim();
        return $"{prefix}{rawSnippet}{suffix}";
    }
}
