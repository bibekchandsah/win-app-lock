using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker;

/// <summary>
/// Saves compact snapshots of the collected data under AppData and compares two of them. Identifiers are
/// stored as salted hashes, never as plain text, and transient live values are left out so a comparison is
/// about configuration rather than whatever was happening at that second.
/// </summary>
public static class SnapshotService
{
    private const int FormatVersion = 1;
    private static readonly string[] IgnoredSections = { "performance" };
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("AppLock.PcChecker.Snapshot.v1");

    public sealed record SnapshotInfo(string Path, DateTime Created, int ItemCount)
    {
        public string DisplayName => $"{Created.ToString("g", CultureInfo.CurrentCulture)} · {ItemCount} values";
    }

    public sealed record Change(string Label, string Section, string? Before, string? After);

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppLock", "PcChecker", "snapshots");

    public static string? Save(IEnumerable<InfoSection> sections)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var items = new List<Entry>();
            foreach (var section in sections)
            {
                if (IgnoredSections.Contains(section.Id, StringComparer.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(section.Error)) continue;

                foreach (var group in section.Groups)
                {
                    foreach (var item in group.Items)
                    {
                        items.Add(new Entry
                        {
                            Section = section.Id,
                            Group = group.Title,
                            Key = item.Key,
                            Label = item.Label,
                            Value = SnapshotValue(item)
                        });
                    }
                }
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(new SnapshotFile
            {
                Version = FormatVersion,
                Created = DateTime.Now,
                Items = items
            });

            var name = $"pc-checker-{DateTime.Now:yyyy-MM-dd-HHmmss}.json.gz";
            var path = Path.Combine(Folder, name);

            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal);
            gzip.Write(payload);

            PruneOld();
            return path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Snapshot save failed: {ex.Message}");
            return null;
        }
    }

    public static List<SnapshotInfo> List()
    {
        try
        {
            if (!Directory.Exists(Folder)) return new List<SnapshotInfo>();

            var list = new List<SnapshotInfo>();
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json.gz"))
            {
                var snapshot = Read(path);
                if (snapshot == null) continue;
                list.Add(new SnapshotInfo(path, snapshot.Created, snapshot.Items.Count));
            }

            return list.OrderByDescending(s => s.Created).ToList();
        }
        catch
        {
            return new List<SnapshotInfo>();
        }
    }

    /// <summary>Compares the newest snapshot with the one before it.</summary>
    public static (SnapshotInfo? Before, SnapshotInfo? After, List<Change> Changes) CompareLatest()
    {
        var snapshots = List();
        if (snapshots.Count < 2) return (null, null, new List<Change>());

        var after = snapshots[0];
        var before = snapshots[1];
        return (before, after, Compare(before, after));
    }

    public static List<Change> Compare(SnapshotInfo before, SnapshotInfo after)
    {
        var a = Read(before.Path);
        var b = Read(after.Path);
        if (a == null || b == null) return new List<Change>();

        var beforeMap = a.Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        var afterMap = b.Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());

        var changes = new List<Change>();

        foreach (var (key, older) in beforeMap)
        {
            if (!afterMap.TryGetValue(key, out var newer))
            {
                changes.Add(new Change(older.Label, older.Section, older.Value, null));
                continue;
            }

            if (!string.Equals(older.Value, newer.Value, StringComparison.Ordinal))
            {
                changes.Add(new Change(older.Label, older.Section, older.Value, newer.Value));
            }
        }

        foreach (var (key, newer) in afterMap)
        {
            if (!beforeMap.ContainsKey(key)) changes.Add(new Change(newer.Label, newer.Section, null, newer.Value));
        }

        return changes.OrderBy(c => c.Section).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static SnapshotFile? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var snapshot = JsonSerializer.Deserialize<SnapshotFile>(gzip);
            return snapshot?.Version == FormatVersion ? snapshot : null;
        }
        catch
        {
            return null;
        }
    }

    private static void PruneOld()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles("*.json.gz")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(20)
                .ToList();

            foreach (var file in files) file.Delete();
        }
        catch { }
    }

    /// <summary>Identifiers are hashed; everything else is stored as shown, with long values capped.</summary>
    private static string SnapshotValue(InfoItem item)
    {
        if (item.Sensitivity == InfoSensitivity.Identifier && !string.IsNullOrWhiteSpace(item.Value))
        {
            return "hash:" + Hash(item.Value!);
        }

        var value = item.Value ?? InfoItem.ReasonText(item.Missing);
        return ReportExporter.Sanitize(value).Length > 300
            ? ReportExporter.Sanitize(value)[..300]
            : ReportExporter.Sanitize(value);
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value).Concat(Salt).ToArray());
        return Convert.ToHexString(bytes)[..16];
    }

    private sealed class SnapshotFile
    {
        public int Version { get; set; }
        public DateTime Created { get; set; }
        public List<Entry> Items { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Section { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
