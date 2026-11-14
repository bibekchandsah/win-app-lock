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
    public Microsoft.UI.Xaml.Media.Brush PinBrush => Pinned
        ? (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
    public Microsoft.UI.Xaml.Visibility PinVisibility => Pinned ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public void SetPinned(bool pinned)
    {
        _item.Pinned = pinned;
        OnPropertyChanged(nameof(Pinned));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinGlyph));
        OnPropertyChanged(nameof(PinToolTip));
        OnPropertyChanged(nameof(PinBrush));
        OnPropertyChanged(nameof(PinVisibility));
    }

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

    /// <summary>Compact age of the item, so long timestamps don't widen every row.</summary>
    public string DisplayTime => GetRelativeTime(_item.LastUsedAt);

    /// <summary>
    /// Exact local date and time, offered on hover only once the item is more than a day old
    /// ("3d ago" alone is vague, while "12m ago" is precise enough without a tooltip).
    /// </summary>
    public string? TimeToolTip
    {
        get
        {
            if (_item.LastUsedAt <= 0) return null;

            var dt = DateTimeOffset.FromUnixTimeMilliseconds(_item.LastUsedAt).ToLocalTime();
            return (DateTimeOffset.Now - dt).TotalHours >= 24 ? FormatTimestamp(_item.LastUsedAt) : null;
        }
    }

    public bool IsLink => !string.IsNullOrEmpty(PreviewText) && 
                          (PreviewText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                           PreviewText.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                           PreviewText.StartsWith("www.", StringComparison.OrdinalIgnoreCase));

    public Microsoft.UI.Xaml.Visibility LinkVisibility => IsLink ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility NonLinkSearchVisibility => (!IsLink && (IsText || IsImage)) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool IsSnippet => _item.IsSnippet;
    public Microsoft.UI.Xaml.Visibility SnippetBadgeVisibility => IsSnippet ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string SnippetTitle => _item.Label ?? string.Empty;
    public string SnippetAbbreviation => _item.SourceTitle ?? string.Empty;
    public Microsoft.UI.Xaml.Visibility AbbreviationVisibility => !string.IsNullOrWhiteSpace(SnippetAbbreviation) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private Microsoft.UI.Xaml.Media.SolidColorBrush? _colorBrush;
    private bool _colorChecked;

    public bool HasColorSwatch
    {
        get
        {
            EnsureColorChecked();
            return _colorBrush != null;
        }
    }

    public Microsoft.UI.Xaml.Visibility ColorSwatchVisibility => HasColorSwatch ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Media.SolidColorBrush? ColorBrush
    {
        get
        {
            EnsureColorChecked();
            return _colorBrush;
        }
    }

    private void EnsureColorChecked()
    {
        if (_colorChecked) return;
        _colorChecked = true;

        if (!IsText || string.IsNullOrWhiteSpace(PreviewText)) return;
        string trimmed = PreviewText.Trim();

        if (trimmed.StartsWith('#') && (trimmed.Length == 4 || trimmed.Length == 7 || trimmed.Length == 9))
        {
            try
            {
                string hex = trimmed[1..];
                byte a = 255, r = 0, g = 0, b = 0;
                if (hex.Length == 3)
                {
                    r = Convert.ToByte(new string(hex[0], 2), 16);
                    g = Convert.ToByte(new string(hex[1], 2), 16);
                    b = Convert.ToByte(new string(hex[2], 2), 16);
                }
                else if (hex.Length == 6)
                {
                    r = Convert.ToByte(hex[..2], 16);
                    g = Convert.ToByte(hex[2..4], 16);
                    b = Convert.ToByte(hex[4..6], 16);
                }
                else if (hex.Length == 8)
                {
                    a = Convert.ToByte(hex[..2], 16);
                    r = Convert.ToByte(hex[2..4], 16);
                    g = Convert.ToByte(hex[4..6], 16);
                    b = Convert.ToByte(hex[6..8], 16);
                }

                _colorBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
            }
            catch { }
        }
    }

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

    public Microsoft.UI.Xaml.Media.FontFamily PreviewFontFamily => IsCodeLike 
        ? new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas, Courier New") 
        : new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI");

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

    /// <summary>Formats a Unix timestamp as the exact local date and time, following the user's locale.</summary>
    public static string FormatTimestamp(long unixMs)
    {
        if (unixMs <= 0) return "";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return dt.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    }
}
