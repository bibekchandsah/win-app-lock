using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using AppLock.Models;
using AppLock.Services;

namespace AppLock.ViewModels;

public sealed partial class ClipboardItemViewModel : ObservableObject
{
    private readonly ClipboardItem _item;
    private BitmapImage? _sourceIcon;
    private bool _iconLoaded;
    private BitmapImage? _thumbnailImage;
    private bool _thumbnailLoaded;
    private List<string>? _filePaths;

    public ClipboardItem Model => _item;

    public long Id => _item.Id;
    public string Type => _item.Type;
    public bool IsText => Type == "text";
    public bool IsImage => Type == "image";
    public bool IsFiles => Type == "files";

    public Microsoft.UI.Xaml.Visibility TextVisibility => IsText ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ImageVisibility => IsImage ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility FilesVisibility => IsFiles ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private bool _isRevealed;
    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (_isRevealed != value)
            {
                _isRevealed = value;
                OnPropertyChanged(nameof(IsRevealed));
                OnPropertyChanged(nameof(PreviewText));
                OnPropertyChanged(nameof(RevealGlyph));
                OnPropertyChanged(nameof(RevealToolTip));
            }
        }
    }

    public void ToggleReveal()
    {
        IsRevealed = !IsRevealed;
    }

    public string PreviewText
    {
        get
        {
            if (IsSensitive && !_isRevealed)
            {
                return "•••••••••••••••• (Sensitive item masked)";
            }
            return _item.PreviewString ?? string.Empty;
        }
    }

    public string SourceProcess => _item.SourceProcess ?? "Unknown";
    public string SourceTitle => _item.SourceTitle ?? string.Empty;
    public long SizeBytes => _item.SizeBytes;
    public long CreatedAt => _item.CreatedAt;
    public long LastUsedAt => _item.LastUsedAt;
    public int UseCount => _item.UseCount;
    public bool Pinned => _item.Pinned;
    public bool IsPinned => _item.Pinned;
    public string PinGlyph => Pinned ? "\uE840" : "\uE718";
    public string PinToolTip => Pinned ? "Unpin (Ctrl+P)" : "Pin (Ctrl+P)";
    public Microsoft.UI.Xaml.Visibility PinVisibility => Pinned ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private string _quickPasteBadge = string.Empty;
    public string QuickPasteBadge
    {
        get => _quickPasteBadge;
        set
        {
            if (_quickPasteBadge != value)
            {
                _quickPasteBadge = value;
                OnPropertyChanged(nameof(QuickPasteBadge));
                OnPropertyChanged(nameof(QuickPasteVisibility));
            }
        }
    }
    public Microsoft.UI.Xaml.Visibility QuickPasteVisibility => !string.IsNullOrEmpty(_quickPasteBadge) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool IsSensitive => _item.IsSensitive;
    public long? ExpiresAt => _item.ExpiresAt;
    public Microsoft.UI.Xaml.Visibility LockVisibility => IsSensitive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility RevealButtonVisibility => (IsSensitive && IsText) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string RevealGlyph => _isRevealed ? "\uED1A" : "\uE7B3";
    public string RevealToolTip => _isRevealed ? "Hide" : "Reveal";

    public string ExpiresText
    {
        get
        {
            if (!ExpiresAt.HasValue) return string.Empty;
            long remainingMs = ExpiresAt.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remainingMs <= 0) return "Expired";
            int sec = (int)(remainingMs / 1000);
            return sec < 60 ? $"Expires in {sec}s" : $"Expires in {sec / 60}m";
        }
    }
    public Microsoft.UI.Xaml.Visibility ExpiresVisibility => (IsSensitive && ExpiresAt.HasValue) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string? ImagePath => _item.ImagePath;
    public string? ThumbPath => _item.ThumbPath;
    public int? Width => _item.Width;
    public int? Height => _item.Height;

    public string DimensionsText => (Width.HasValue && Height.HasValue) ? $"{Width} × {Height}" : string.Empty;

    public bool IsImageMissing => IsImage && 
        (!string.IsNullOrEmpty(ThumbPath) && !File.Exists(ThumbPath)) && 
        (!string.IsNullOrEmpty(ImagePath) && !File.Exists(ImagePath));

    public List<string> FilePaths
    {
        get
        {
            if (_filePaths == null && IsFiles && !string.IsNullOrEmpty(_item.ContentString))
            {
                try
                {
                    _filePaths = JsonSerializer.Deserialize<List<string>>(_item.ContentString) ?? new List<string>();
                }
                catch
                {
                    _filePaths = new List<string>();
                }
            }
            return _filePaths ??= new List<string>();
        }
    }

    public string FormattedSize
    {
        get
        {
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
            return $"{SizeBytes / (1024.0 * 1024.0):F2} MB";
        }
    }

    public string RelativeTime => GetRelativeTime(_item.LastUsedAt);

    public bool IsLink => !string.IsNullOrEmpty(PreviewText) && 
                          (PreviewText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                           PreviewText.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public bool IsCodeLike
    {
        get
        {
            if (string.IsNullOrEmpty(PreviewText)) return false;
            return PreviewText.Contains('{') && PreviewText.Contains('}') ||
                   PreviewText.Contains("function ") ||
                   PreviewText.Contains("var ") ||
                   PreviewText.Contains("const ") ||
                   PreviewText.Contains("<html") ||
                   PreviewText.Contains("public ") ||
                   PreviewText.StartsWith("    ") ||
                   PreviewText.StartsWith("\t");
        }
    }

    public BitmapImage? SourceIcon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                LoadSourceIcon();
            }
            return _sourceIcon;
        }
    }

    public BitmapImage? ThumbnailImage
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                LoadThumbnail();
            }
            return _thumbnailImage;
        }
    }

    public ClipboardItemViewModel(ClipboardItem item)
    {
        _item = item;
    }

    private void LoadThumbnail()
    {
        if (!IsImage) return;

        try
        {
            string? pathToLoad = null;
            if (!string.IsNullOrEmpty(ThumbPath) && File.Exists(ThumbPath))
            {
                pathToLoad = ThumbPath;
            }
            else if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
            {
                pathToLoad = ImagePath;
            }

            if (pathToLoad != null)
            {
                _thumbnailImage = new BitmapImage(new Uri(pathToLoad));
                OnPropertyChanged(nameof(ThumbnailImage));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardItemViewModel] Error loading thumbnail: {ex.Message}");
        }
    }

    private void LoadSourceIcon()
    {
        if (string.IsNullOrWhiteSpace(_item.SourceProcess) || _item.SourceProcess == "Unknown")
            return;

        try
        {
            string proc = _item.SourceProcess;
            string? fullPath = null;

            if (File.Exists(proc))
            {
                fullPath = proc;
            }
            else
            {
                string sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), proc);
                if (File.Exists(sys32))
                {
                    fullPath = sys32;
                }
                else
                {
                    string prog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), proc);
                    if (File.Exists(prog)) fullPath = prog;
                }
            }

            if (fullPath != null)
            {
                _sourceIcon = ServiceContainer.WindowEnumerator.ExtractIcon(fullPath);
                OnPropertyChanged(nameof(SourceIcon));
            }
        }
        catch { }
    }

    public static string GetRelativeTime(long unixMs)
    {
        if (unixMs <= 0) return "";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        var span = DateTimeOffset.Now - dt;

        if (span.TotalSeconds < 45) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return dt.ToString("M");
    }
}
