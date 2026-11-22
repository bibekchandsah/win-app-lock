using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.WindowIsland.Modules;

/// <summary>One file, folder, or application indexed in memory.</summary>
public sealed record IndexedItem(
    string Name,
    string Path,
    bool IsDirectory,
    bool IsApp,
    string Ext);

/// <summary>
/// In-memory index of files, folders, and Start Menu apps (island.md Section 8).
/// Built asynchronously on first open in a low-priority background task,
/// kept fresh with FileSystemWatchers, never touches disk for index persistence,
/// and searches in < 15ms.
/// </summary>
public sealed class WindowIslandFileIndex : IDisposable
{
    private const int MaxEntries = 60000;
    private readonly List<IndexedItem> _items = new(8192);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _lock = new();
    private bool _indexingStarted;
    private bool _disposed;

    public int Count
    {
        get
        {
            lock (_lock) return _items.Count;
        }
    }

    public void StartBackgroundIndexing()
    {
        if (_indexingStarted || _disposed) return;
        _indexingStarted = true;

        Task.Run(RunIndexingAsync);
    }

    private void RunIndexingAsync()
    {
        try
        {
            var targetFolders = new List<string>();

            AddIfExists(targetFolders, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            AddIfExists(targetFolders, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddIfExists(targetFolders, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            AddIfExists(targetFolders, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            AddIfExists(targetFolders, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            AddIfExists(targetFolders, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

            var appFolders = new List<string>();
            AddIfExists(appFolders, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs");
            AddIfExists(appFolders, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs");

            var collected = new List<IndexedItem>(8192);

            // 1. Index Start Menu Apps first (highest relevance)
            foreach (var folder in appFolders)
            {
                ScanDirectory(folder, isAppFolder: true, maxDepth: 4, collected);
            }

            // 2. Index User Folders
            foreach (var folder in targetFolders)
            {
                ScanDirectory(folder, isAppFolder: false, maxDepth: 3, collected);
            }

            lock (_lock)
            {
                _items.Clear();
                _items.AddRange(collected.Take(MaxEntries));
            }

            Debug.WriteLine($"[WindowIslandFileIndex] Indexed {_items.Count} items in background.");

            // 3. Set up watchers on high-churn directories
            SetupWatchers(new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandFileIndex] Indexing error: {ex.Message}");
        }
    }

    private static void AddIfExists(List<string> list, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            list.Add(path);
    }

    private static void ScanDirectory(string root, bool isAppFolder, int maxDepth, List<IndexedItem> list)
    {
        try
        {
            var opt = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = maxDepth,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            foreach (var file in Directory.EnumerateFiles(root, "*", opt))
            {
                if (list.Count >= MaxEntries) return;

                string ext = Path.GetExtension(file).ToLowerInvariant();
                // Exclude noisy temp files
                if (ext is ".tmp" or ".ini" or ".log" or ".bak") continue;

                string name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;

                bool isApp = isAppFolder || ext is ".exe" or ".lnk" or ".bat" or ".cmd";
                list.Add(new IndexedItem(name, file, IsDirectory: false, IsApp: isApp, Ext: ext));
            }

            // Also index direct subdirectories
            foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false }))
            {
                if (list.Count >= MaxEntries) return;
                string dirName = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(dirName))
                {
                    list.Add(new IndexedItem(dirName, dir, IsDirectory: true, IsApp: false, Ext: string.Empty));
                }
            }
        }
        catch { }
    }

    private void SetupWatchers(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemDeleted;
                watcher.Renamed += OnFileSystemRenamed;

                lock (_watchers)
                {
                    _watchers.Add(watcher);
                }
            }
            catch { }
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(e.FullPath);
            bool isDir = Directory.Exists(e.FullPath);
            var item = new IndexedItem(name, e.FullPath, isDir, ext is ".exe" or ".lnk", ext);

            lock (_lock)
            {
                _items.RemoveAll(i => string.Equals(i.Path, e.FullPath, StringComparison.OrdinalIgnoreCase));
                _items.Insert(0, item);
            }
        }
        catch { }
    }

    private void OnFileSystemDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => string.Equals(i.Path, e.FullPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(e.FullPath);
            bool isDir = Directory.Exists(e.FullPath);
            var item = new IndexedItem(name, e.FullPath, isDir, ext is ".exe" or ".lnk", ext);

            lock (_lock)
            {
                _items.RemoveAll(i => string.Equals(i.Path, e.OldFullPath, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(i.Path, e.FullPath, StringComparison.OrdinalIgnoreCase));
                _items.Insert(0, item);
            }
        }
        catch { }
    }

    /// <summary>
    /// Searches indexed files and apps using fuzzy ranking:
    /// Prefix (100) > Word-start (80) > Substring (60) > Subsequence (40).
    /// </summary>
    public List<IndexedItem> Search(string query, int maxResults = 12)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<IndexedItem>();

        string q = query.Trim().ToLowerInvariant();
        var scored = new List<(IndexedItem Item, int Score)>();

        List<IndexedItem> snapshot;
        lock (_lock)
        {
            snapshot = _items.ToList();
        }

        foreach (var item in snapshot)
        {
            string nameLower = item.Name.ToLowerInvariant();
            int score = CalculateMatchScore(nameLower, q);
            if (score > 0)
            {
                // App bonus
                if (item.IsApp) score += 10;
                scored.Add((item, score));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(maxResults)
            .Select(s => s.Item)
            .ToList();
    }

    private static int CalculateMatchScore(string name, string q)
    {
        if (name.Equals(q, StringComparison.Ordinal)) return 110;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 100;

        int wordIdx = name.IndexOf(" " + q, StringComparison.Ordinal);
        if (wordIdx >= 0) return 85;

        int subIdx = name.IndexOf(q, StringComparison.Ordinal);
        if (subIdx >= 0) return 65;

        // Subsequence match (e.g. "calc" in "calculator")
        int qIdx = 0;
        for (int i = 0; i < name.Length && qIdx < q.Length; i++)
        {
            if (name[i] == q[qIdx]) qIdx++;
        }
        if (qIdx == q.Length) return 40;

        return 0;
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_watchers)
        {
            foreach (var w in _watchers)
            {
                try { w.Dispose(); } catch { }
            }
            _watchers.Clear();
        }
    }
}
