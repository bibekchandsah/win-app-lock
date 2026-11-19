using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models.Vault;

namespace AppLock.ViewModels.Vault;

public partial class VaultItemViewModel : ObservableObject
{
    public IndexItem Model { get; }

    public Guid Id => Model.Id;
    public Guid? ParentId => Model.ParentId;
    public bool IsDirectory => Model.IsDirectory;
    public long Size => Model.Size;
    public DateTime CreatedTimeUtc => Model.CreatedTimeUtc;
    public DateTime ModifiedTimeUtc => Model.ModifiedTimeUtc;
    public string? ObjectId => Model.ObjectId;
    public string? Sha256 => Model.Sha256;
    public bool IsTrash => Model.IsTrash;
    public DateTime? DeletedTimeUtc => Model.DeletedTimeUtc;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _searchSnippet = string.Empty;

    [ObservableProperty]
    private bool _hasSearchSnippet;

    public string DisplaySize => IsDirectory ? "--" : FormatFileSize(Size);

    public string DisplayModified => ModifiedTimeUtc.ToLocalTime().ToString("g");

    public string DisplayCreated => CreatedTimeUtc.ToLocalTime().ToString("g");

    public string DisplayDeleted => DeletedTimeUtc.HasValue ? DeletedTimeUtc.Value.ToLocalTime().ToString("g") : "--";

    public string Glyph => IsDirectory ? "\uE8B7" : GetGlyphForExtension(Path.GetExtension(Name));

    public string TypeDescription => IsDirectory ? "File folder" : GetTypeDescription(Path.GetExtension(Name));

    public VaultItemViewModel(IndexItem model)
    {
        Model = model;
        Name = model.Name;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private static string GetGlyphForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".svg" or ".ico" => "\uEB9F", // Image
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".wma" => "\uEC4F", // Audio
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" => "\uE714", // Video
        ".pdf" => "\uEA90", // PDF / Document
        ".doc" or ".docx" or ".rtf" or ".odt" => "\uE8A5", // Document
        ".xls" or ".xlsx" or ".csv" => "\uE8A5", // Spreadsheet
        ".ppt" or ".pptx" => "\uE8A5", // Presentation
        ".txt" or ".md" or ".json" or ".xml" or ".yaml" or ".yml" or ".log" => "\uE8C1", // Text
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uF012", // Archive
        ".cs" or ".cpp" or ".c" or ".h" or ".py" or ".js" or ".ts" or ".html" or ".css" => "\uE943", // Code
        _ => "\uE7C3" // Generic file
    };

    private static string GetTypeDescription(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => "Image",
        ".mp3" or ".wav" or ".flac" or ".m4a" => "Audio",
        ".mp4" or ".mkv" or ".avi" or ".mov" => "Video",
        ".pdf" => "PDF Document",
        ".doc" or ".docx" => "Word Document",
        ".xls" or ".xlsx" => "Excel Spreadsheet",
        ".txt" => "Text Document",
        ".md" => "Markdown File",
        ".zip" or ".7z" or ".rar" => "Compressed Archive",
        _ => string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File"
    };
}
