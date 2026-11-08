using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services;

public class ClipboardExportItemDto
{
    public string Type { get; set; } = "text";
    public string? ContentString { get; set; }
    public string? PreviewString { get; set; }
    public string? HtmlString { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string? ImageFileName { get; set; }
    public string? ThumbFileName { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long SizeBytes { get; set; }
    public string? SourceProcess { get; set; }
    public string? SourceTitle { get; set; }
    public long CreatedAt { get; set; }
    public long LastUsedAt { get; set; }
    public int UseCount { get; set; }
    public bool Pinned { get; set; }
    public bool IsSnippet { get; set; }
    public string? Label { get; set; }
    public bool IsSensitive { get; set; }
}

public class ClipboardExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Exports all clipboard items, snippets, and images into a ZIP archive.
    /// </summary>
    public async Task<bool> ExportToZipAsync(string zipFilePath)
    {
        try
        {
            if (File.Exists(zipFilePath))
            {
                File.Delete(zipFilePath);
            }

            var items = await ServiceContainer.ClipboardStore.GetItemsAsync(limit: 5000);
            var snippets = await ServiceContainer.ClipboardStore.GetSnippetsAsync();
            var allItems = items.Concat(snippets).GroupBy(x => x.Id).Select(g => g.First()).ToList();

            var dtos = new List<ClipboardExportItemDto>();
            var imageFilesToZip = new List<(string SourcePath, string ZipEntryName)>();

            foreach (var item in allItems)
            {
                var dto = new ClipboardExportItemDto
                {
                    Type = item.Type,
                    ContentString = item.ContentString,
                    PreviewString = item.PreviewString,
                    HtmlString = item.HtmlString,
                    Hash = item.Hash,
                    Width = item.Width,
                    Height = item.Height,
                    SizeBytes = item.SizeBytes,
                    SourceProcess = item.SourceProcess,
                    SourceTitle = item.SourceTitle,
                    CreatedAt = item.CreatedAt,
                    LastUsedAt = item.LastUsedAt,
                    UseCount = item.UseCount,
                    Pinned = item.Pinned,
                    IsSnippet = item.IsSnippet,
                    Label = item.Label,
                    IsSensitive = item.IsSensitive
                };

                if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    string fname = Path.GetFileName(item.ImagePath);
                    dto.ImageFileName = fname;
                    imageFilesToZip.Add((item.ImagePath, $"images/{fname}"));
                }

                if (!string.IsNullOrEmpty(item.ThumbPath) && File.Exists(item.ThumbPath))
                {
                    string fname = Path.GetFileName(item.ThumbPath);
                    dto.ThumbFileName = fname;
                    imageFilesToZip.Add((item.ThumbPath, $"thumbs/{fname}"));
                }

                dtos.Add(dto);
            }

            string json = JsonSerializer.Serialize(dtos, JsonOptions);

            using (var zipStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                // Write items.json
                var jsonEntry = archive.CreateEntry("clipboard_history.json", CompressionLevel.Optimal);
                using (var entryStream = jsonEntry.Open())
                using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
                {
                    await writer.WriteAsync(json);
                }

                // Add images and thumbnails
                foreach (var img in imageFilesToZip)
                {
                    if (File.Exists(img.SourcePath))
                    {
                        var imgEntry = archive.CreateEntry(img.ZipEntryName, CompressionLevel.Optimal);
                        using var entryStream = imgEntry.Open();
                        using var fileStream = File.OpenRead(img.SourcePath);
                        await fileStream.CopyToAsync(entryStream);
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardExportService] Export error: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Imports items and images from a ZIP archive into the database.
    /// </summary>
    public async Task<int> ImportFromZipAsync(string zipFilePath)
    {
        int importedCount = 0;
        try
        {
            if (!File.Exists(zipFilePath)) return 0;

            string baseDir = ServiceContainer.ClipboardStore.StoragePath;
            string imagesDir = Path.Combine(baseDir, "images");
            string thumbsDir = Path.Combine(baseDir, "thumbs");
            Directory.CreateDirectory(imagesDir);
            Directory.CreateDirectory(thumbsDir);

            using var zipStream = File.OpenRead(zipFilePath);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            // 1. Extract image and thumb files
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("images/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name))
                {
                    string target = Path.Combine(imagesDir, entry.Name);
                    entry.ExtractToFile(target, overwrite: true);
                }
                else if (entry.FullName.StartsWith("thumbs/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name))
                {
                    string target = Path.Combine(thumbsDir, entry.Name);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            // 2. Read clipboard_history.json
            var jsonEntry = archive.GetEntry("clipboard_history.json");
            if (jsonEntry == null) return 0;

            string json;
            using (var stream = jsonEntry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                json = await reader.ReadToEndAsync();
            }

            var dtos = JsonSerializer.Deserialize<List<ClipboardExportItemDto>>(json, JsonOptions);
            if (dtos == null || dtos.Count == 0) return 0;

            foreach (var dto in dtos)
            {
                var item = new ClipboardItem
                {
                    Type = dto.Type,
                    ContentString = dto.ContentString,
                    PreviewString = dto.PreviewString,
                    HtmlString = dto.HtmlString,
                    Hash = dto.Hash,
                    ImagePath = !string.IsNullOrEmpty(dto.ImageFileName) ? Path.Combine(imagesDir, dto.ImageFileName) : null,
                    ThumbPath = !string.IsNullOrEmpty(dto.ThumbFileName) ? Path.Combine(thumbsDir, dto.ThumbFileName) : null,
                    Width = dto.Width,
                    Height = dto.Height,
                    SizeBytes = dto.SizeBytes,
                    SourceProcess = dto.SourceProcess,
                    SourceTitle = dto.SourceTitle,
                    CreatedAt = dto.CreatedAt,
                    LastUsedAt = dto.LastUsedAt,
                    UseCount = dto.UseCount,
                    Pinned = dto.Pinned,
                    IsSnippet = dto.IsSnippet,
                    Label = dto.Label,
                    IsSensitive = dto.IsSensitive
                };

                await ServiceContainer.ClipboardStore.InsertOrUpdateAsync(item, 5000);
                importedCount++;
            }

            return importedCount;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardExportService] Import error: {ex}");
            return importedCount;
        }
    }
}

