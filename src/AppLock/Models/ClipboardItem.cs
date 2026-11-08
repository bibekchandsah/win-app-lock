using System;

namespace AppLock.Models;

public sealed class ClipboardItem
{
    public long Id { get; set; }
    public string Type { get; set; } = "text"; // 'text' | 'image' | 'files'
    public byte[]? Content { get; set; }
    public byte[]? Preview { get; set; }
    public byte[]? Html { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public string? ThumbPath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long SizeBytes { get; set; }
    public string? SourceProcess { get; set; }
    public string? SourceTitle { get; set; }
    public long CreatedAt { get; set; } // Unix ms
    public long LastUsedAt { get; set; } // Unix ms
    public int UseCount { get; set; }
    public bool Pinned { get; set; }
    public bool IsSnippet { get; set; }
    public string? Label { get; set; }
    public bool IsSensitive { get; set; }
    public long? ExpiresAt { get; set; }

    // Helpers to access unencrypted / raw string if plain text
    public string? ContentString
    {
        get => Content != null ? System.Text.Encoding.UTF8.GetString(Content) : null;
        set => Content = value != null ? System.Text.Encoding.UTF8.GetBytes(value) : null;
    }

    public string? PreviewString
    {
        get => Preview != null ? System.Text.Encoding.UTF8.GetString(Preview) : null;
        set => Preview = value != null ? System.Text.Encoding.UTF8.GetBytes(value) : null;
    }

    public string? HtmlString
    {
        get => Html != null ? System.Text.Encoding.UTF8.GetString(Html) : null;
        set => Html = value != null ? System.Text.Encoding.UTF8.GetBytes(value) : null;
    }
}

