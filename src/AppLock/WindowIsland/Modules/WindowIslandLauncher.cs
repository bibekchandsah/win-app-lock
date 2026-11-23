using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace AppLock.WindowIsland.Modules;

/// <summary>One shortcut item in the Island Launcher.</summary>
public sealed class IslandShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? IconFile { get; set; }
    public bool UseAppIcon { get; set; } = true;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Manages launcher shortcuts (stored in shortcuts.json), launches processes/URLs,
/// and fetches icons (shell icons for files/apps, favicons for websites).
/// Max 24 items per island.md section 7.
/// </summary>
public sealed class WindowIslandLauncher
{
    private const int MaxItems = 24;

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
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref SHFILEINFO info, uint infoSize, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private readonly string _file;
    private readonly List<IslandShortcut> _items = new();
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    public event Action? Changed;

    public WindowIslandLauncher()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "island");
        Directory.CreateDirectory(root);
        _file = Path.Combine(root, "shortcuts.json");

        try { Directory.CreateDirectory(IconFolder); } catch { }

        Load();
    }

    public static string IconFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "island", "cache", "launcher");

    public IReadOnlyList<IslandShortcut> Items
    {
        get
        {
            lock (_items)
            {
                return _items.ToList();
            }
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var json = File.ReadAllText(_file);
            var loaded = JsonSerializer.Deserialize<List<IslandShortcut>>(json);
            if (loaded != null)
            {
                lock (_items)
                {
                    _items.Clear();
                    _items.AddRange(loaded.Take(MaxItems));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandLauncher] Load failed: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            List<IslandShortcut> copy;
            lock (_items)
            {
                copy = _items.ToList();
            }
            File.WriteAllText(_file, JsonSerializer.Serialize(copy));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandLauncher] Save failed: {ex.Message}");
        }
    }

    public async Task<bool> AddAsync(string rawTarget, string? rawName = null, bool useAppIcon = true)
    {
        if (string.IsNullOrWhiteSpace(rawTarget)) return false;
        string target = rawTarget.Trim();

        lock (_items)
        {
            if (_items.Count >= MaxItems) return false;
            if (_items.Any(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        string name = string.IsNullOrWhiteSpace(rawName) ? InferName(target) : rawName.Trim();

        var shortcut = new IslandShortcut
        {
            Target = target,
            Name = name,
            UseAppIcon = useAppIcon
        };

        // Cache icon in background
        shortcut.IconFile = await Task.Run(() => ExtractOrFetchIcon(target, useAppIcon));

        lock (_items)
        {
            if (_items.Count >= MaxItems) return false;
            _items.Add(shortcut);
        }

        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Applies an edit to an existing shortcut. The icon is re-resolved only when the target or the
    /// "use the app's icon" choice changed, so renaming an entry stays instant.
    /// </summary>
    public async Task<bool> UpdateAsync(string id, string rawTarget, string? rawName = null, bool useAppIcon = true)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(rawTarget)) return false;
        string target = rawTarget.Trim();

        IslandShortcut? item;
        lock (_items)
        {
            if (_items.Any(i => i.Id != id && string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)))
                return false;
            item = _items.FirstOrDefault(i => i.Id == id);
        }
        if (item == null) return false;

        bool refreshIcon = item.UseAppIcon != useAppIcon
                        || !string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrEmpty(item.IconFile);

        string? iconPath = refreshIcon
            ? await Task.Run(() => ExtractOrFetchIcon(target, useAppIcon))
            : item.IconFile;

        lock (_items)
        {
            item.Target = target;
            item.Name = string.IsNullOrWhiteSpace(rawName) ? InferName(target) : rawName.Trim();
            item.UseAppIcon = useAppIcon;
            item.IconFile = iconPath;
        }

        Save();
        Changed?.Invoke();
        return true;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_items)
        {
            removed = _items.RemoveAll(i => i.Id == id) > 0;
        }
        if (removed)
        {
            Save();
            Changed?.Invoke();
        }
        return removed;
    }

    public void Launch(string id, bool runAsAdmin = false)
    {
        IslandShortcut? item;
        lock (_items)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
        }
        if (item == null) return;

        try
        {
            var psi = new ProcessStartInfo(item.Target)
            {
                UseShellExecute = true
            };
            if (runAsAdmin)
            {
                psi.Verb = "runas";
            }
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandLauncher] Launch failed for {item.Target}: {ex.Message}");
        }
    }

    public void OpenFolder(string id)
    {
        IslandShortcut? item;
        lock (_items)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
        }
        if (item == null) return;

        try
        {
            string path = item.Target;
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandLauncher] OpenFolder failed: {ex.Message}");
        }
    }

    private static string InferName(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            string host = uri.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host[4..];
            return host;
        }

        try
        {
            string clean = target.Trim('"', ' ');
            if (File.Exists(clean) || Directory.Exists(clean))
            {
                string fn = Path.GetFileName(clean.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(fn)) return fn;
            }
        }
        catch { }

        return target.Length > 20 ? target[..20] : target;
    }

    private static string? ExtractOrFetchIcon(string target, bool useAppIcon)
    {
        if (!useAppIcon) return null;

        try
        {
            uint hash = Hash(target);
            string iconName = $"launcher-{hash:X8}.png";
            string iconPath = Path.Combine(IconFolder, iconName);
            if (File.Exists(iconPath)) return iconPath;

            // 1. Web URLs
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                string faviconUrl = $"{uri.Scheme}://{uri.Host}/favicon.ico";
                try
                {
                    using var response = _httpClient.GetAsync(faviconUrl).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        using var ms = new MemoryStream(bytes);
                        using var bmp = new Bitmap(ms);
                        using var targetBmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(targetBmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.Clear(Color.Transparent);
                            g.DrawImage(bmp, 0, 0, 64, 64);
                        }
                        targetBmp.Save(iconPath, ImageFormat.Png);
                        return iconPath;
                    }
                }
                catch { }

                return null;
            }

            // 2. Shell file / folder / app icon
            string clean = target.Trim('"', ' ');
            bool isDir = Directory.Exists(clean);
            bool exists = isDir || File.Exists(clean);

            uint attributes = exists ? (isDir ? FILE_ATTRIBUTE_DIRECTORY : 0u) : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_ICON | SHGFI_LARGEICON | (exists ? 0u : SHGFI_USEFILEATTRIBUTES);

            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(clean, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var icon = Icon.FromHandle(info.hIcon);
                    using var bmp = icon.ToBitmap();
                    using var targetBmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(targetBmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.Clear(Color.Transparent);
                        g.DrawImage(bmp, 0, 0, 64, 64);
                    }
                    targetBmp.Save(iconPath, ImageFormat.Png);
                    return iconPath;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandLauncher] ExtractOrFetchIcon failed: {ex.Message}");
        }

        return null;
    }

    private static uint Hash(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}
