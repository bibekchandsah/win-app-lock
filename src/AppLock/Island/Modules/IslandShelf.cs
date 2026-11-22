using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using AppLock.Island.Host;

namespace AppLock.Island.Modules;

/// <summary>One file on the Shelf as the page draws it.</summary>
public sealed record ShelfRow(string Path, string Name, string Icon, bool Exists);

/// <summary>On-disk shape of `shelf.json` (plain get/set so the serializer stays boring).</summary>
public sealed class ShelfFile
{
    public string Path { get; set; } = string.Empty;
    public DateTime AddedUtc { get; set; }
}

/// <summary>
/// The Shelf tab (island.md 9.2): a small list of files the user parked there while working.
/// Entries are links to the originals (or copies, when the setting says so) stored in
/// `%LOCALAPPDATA%\AppLock\island\shelf.json`. Icons are rendered once per path into the island
/// cache and loaded by the page from there.
/// </summary>
public sealed class IslandShelf
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint fileAttributes, ref SHFILEINFO info, uint infoSize, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff",
    };

    private readonly string _file;
    private readonly List<ShelfFile> _entries = new();
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);

    public IslandShelf()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "island");
        Directory.CreateDirectory(root);
        _file = Path.Combine(root, "shelf.json");

        try
        {
            Directory.CreateDirectory(IconFolder);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] shelf icon folder failed: {ex.Message}");
        }

        Load();
        IslandSpikeLog.Write($"shelf: loaded {_entries.Count} item(s) from {_file}");
    }

    /// <summary>Relative to the island cache host (`https://island.cache/shelf/…`).</summary>
    public static string IconFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "island", "cache", "shelf");

    public event Action? Changed;

    public int Count => _entries.Count;

    public IReadOnlyList<ShelfRow> Rows()
    {
        var rows = new List<ShelfRow>(_entries.Count);
        foreach (ShelfFile entry in _entries)
        {
            bool isDirectory = Directory.Exists(entry.Path);
            bool exists = isDirectory || File.Exists(entry.Path);
            string name = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = entry.Path;

            rows.Add(new ShelfRow(entry.Path, name, Icon(entry.Path, exists, isDirectory), exists));
        }

        return rows;
    }

    /// <summary>Adds files, skipping ones already on the shelf. Returns how many were added.</summary>
    public int Add(IEnumerable<string> paths, bool copyInto = false)
    {
        int added = 0;

        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string path;
            try
            {
                path = Path.GetFullPath(raw);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (_entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            if (copyInto && File.Exists(path))
            {
                string? copy = Copy(path);
                if (copy == null) continue;
                path = copy;
            }

            _entries.Add(new ShelfFile { Path = path, AddedUtc = DateTime.UtcNow });
            added++;
        }

        if (added == 0) return 0;

        Save();
        Changed?.Invoke();
        return added;
    }

    public bool Remove(string path)
    {
        int removed = _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        Save();
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;

        _entries.Clear();
        Save();
        Changed?.Invoke();
    }

    /// <summary>True when the path is on the shelf; the page may only remove what it was given.</summary>
    public bool Contains(string path) =>
        _entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    private static string? Copy(string path)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AppLock", "island", "shelf");
            Directory.CreateDirectory(folder);

            string target = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(path)}");
            File.Copy(path, target, overwrite: false);
            return target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] shelf copy failed: {ex.Message}");
            return null;
        }
    }

    // ── Icons ────────────────────────────────────────────────────────────────

    private string Icon(string path, bool exists, bool isDirectory)
    {
        if (_icons.TryGetValue(path, out string? cached)) return cached;

        string name = $"shelf-{Hash(path):X8}.png";
        string file = Path.Combine(IconFolder, name);

        if (!File.Exists(file))
        {
            try
            {
                using Bitmap? bitmap = ImageIcon(path, exists, isDirectory);
                bitmap?.Save(file, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Island] shelf icon failed for {path}: {ex.Message}");
            }
        }

        if (!File.Exists(file)) return string.Empty;

        _icons[path] = name;
        return name;
    }

    private static Bitmap? ImageIcon(string path, bool exists, bool isDirectory)
    {
        // A picture is more useful as a thumbnail than as the image viewer's icon.
        if (exists && !isDirectory && ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            using FileStream stream = File.OpenRead(path);
            using Image image = Image.FromStream(stream);

            var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.Clear(Color.Transparent);

            float scale = Math.Min(48f / image.Width, 48f / image.Height);
            int width = Math.Max(1, (int)(image.Width * scale));
            int height = Math.Max(1, (int)(image.Height * scale));
            graphics.DrawImage(image, (48 - width) / 2, (48 - height) / 2, width, height);
            return bitmap;
        }

        uint attributes = exists
            ? (isDirectory ? FILE_ATTRIBUTE_DIRECTORY : 0u)
            : FILE_ATTRIBUTE_NORMAL;
        uint flags = SHGFI_ICON | SHGFI_LARGEICON | (exists ? 0u : SHGFI_USEFILEATTRIBUTES);

        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            using System.Drawing.Icon icon = System.Drawing.Icon.FromHandle(info.hIcon);
            return icon.ToBitmap();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static uint Hash(string value)
    {
        // FNV-1a: stable across runs, which is all a cache file name needs.
        uint hash = 2166136261;
        foreach (char c in value.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    // ── Storage ──────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;

            var entries = JsonSerializer.Deserialize<List<ShelfFile>>(File.ReadAllText(_file));
            if (entries == null) return;

            _entries.AddRange(entries.Where(e => !string.IsNullOrWhiteSpace(e.Path)));
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"shelf: load failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] shelf save failed: {ex.Message}");
        }
    }
}
