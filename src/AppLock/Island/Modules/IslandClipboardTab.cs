using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using AppLock.Island.Host;
using AppLock.Models;
using AppLock.Services;

namespace AppLock.Island.Modules;

/// <summary>One row of the Clipboard tab, already reduced to what the page draws.</summary>
public sealed record ClipboardRow(
    string Id,
    string Type,
    string Preview,
    string Source,
    string Age,
    int Chars,
    bool Pinned,
    string Thumb);

/// <summary>
/// The Clipboard tab over the app's own clipboard history (island.md 9.1): the same
/// <see cref="ClipboardStore"/> and <see cref="ClipboardPasteService"/> the Clipboard page uses,
/// so there is exactly one monitor and one history. Nothing is written to disk here.
/// </summary>
public sealed class IslandClipboardTab
{
    private const int Limit = 60;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public async Task<IReadOnlyList<ClipboardRow>> ListAsync(string? search)
    {
        try
        {
            List<ClipboardItem> items = await ServiceContainer.ClipboardStore.GetItemsAsync(
                Limit, "All", string.IsNullOrWhiteSpace(search) ? null : search);

            return items.Select(ToRow).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] clipboard list failed: {ex.Message}");
            return Array.Empty<ClipboardRow>();
        }
    }

    /// <summary>Pastes (or only copies) the item into the app that had focus. True when it worked.</summary>
    public async Task<bool> PasteAsync(string id, bool copyOnly)
    {
        ClipboardItem? item = await FindAsync(id);
        if (item == null) return false;

        // The island never activates for mouse clicks, so the foreground window is still the app the
        // user was in — exactly the window the paste has to go to.
        IntPtr target = GetForegroundWindow();
        var result = await ServiceContainer.ClipboardPasteService.PasteItemAsync(
            item, target, plainTextOnly: false, copyOnly: copyOnly);

        IslandSpikeLog.Write($"clipboard: paste {id} copyOnly={copyOnly} -> {result.Success} ({result.Message})");
        return result.Success;
    }

    public async Task<bool> PinAsync(string id)
    {
        if (!long.TryParse(id, out long value)) return false;

        try
        {
            bool pinned = await ServiceContainer.ClipboardStore.TogglePinAsync(value);
            IslandSpikeLog.Write($"clipboard: pin {id} -> {pinned}");
            return true;
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"clipboard: pin failed ({ex.GetType().Name})");
            return false;
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await ServiceContainer.ClipboardStore.ClearHistoryAsync(includePinned: false);
            IslandSpikeLog.Write("clipboard: history cleared");
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"clipboard: clear failed ({ex.GetType().Name})");
        }
    }

    private static async Task<ClipboardItem?> FindAsync(string id)
    {
        if (!long.TryParse(id, out long value)) return null;

        List<ClipboardItem> items = await ServiceContainer.ClipboardStore.GetItemsAsync(200, "All", null);
        return items.FirstOrDefault(item => item.Id == value);
    }

    private static ClipboardRow ToRow(ClipboardItem item)
    {
        string preview = item.PreviewString?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;

        if (item.Type == "image")
        {
            preview = item.Width is int width && item.Height is int height ? $"{width}\u00D7{height}" : "Image";
        }
        else if (item.Type == "files")
        {
            preview = FilePreview(item.ContentString);
        }

        if (preview.Length == 0) preview = item.Type == "text" ? "Empty" : "Item";

        string age = AgeText(item.CreatedAt);
        int chars = item.Type == "text" ? item.ContentString?.Length ?? 0 : 0;

        return new ClipboardRow(
            item.Id.ToString(),
            item.Type,
            preview.Length > 160 ? preview[..160] : preview,
            item.SourceProcess ?? string.Empty,
            age,
            chars,
            item.Pinned,
            Thumb(item));
    }

    /// <summary>Relative path under the clipboard folder, served by the page through `island.clips`.</summary>
    private static string Thumb(ClipboardItem item)
    {
        if (!string.IsNullOrEmpty(item.ThumbPath))
        {
            string name = Path.GetFileName(item.ThumbPath);
            if (name.Length > 0) return "thumbs/" + name;
        }

        if (!string.IsNullOrEmpty(item.ImagePath))
        {
            string name = Path.GetFileName(item.ImagePath);
            if (name.Length > 0) return "images/" + name;
        }

        return string.Empty;
    }

    private static string FilePreview(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "Files";

        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(json);
            if (paths == null || paths.Count == 0) return "Files";

            string first = Path.GetFileName(paths[0]);
            return paths.Count == 1 ? first : $"{first} +{paths.Count - 1} more";
        }
        catch
        {
            return "Files";
        }
    }

    private static string AgeText(long createdAtMs)
    {
        if (createdAtMs <= 0) return string.Empty;

        TimeSpan age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs);
        if (age.TotalSeconds < 45) return "now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }
}
