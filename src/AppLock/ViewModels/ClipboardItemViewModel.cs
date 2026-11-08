using System;
using System.IO;
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

    public ClipboardItem Model => _item;

    public long Id => _item.Id;
    public string Type => _item.Type;
    public string PreviewText => _item.PreviewString ?? string.Empty;
    public string SourceProcess => _item.SourceProcess ?? "Unknown";
    public string SourceTitle => _item.SourceTitle ?? string.Empty;
    public long SizeBytes => _item.SizeBytes;
    public long CreatedAt => _item.CreatedAt;
    public long LastUsedAt => _item.LastUsedAt;
    public int UseCount => _item.UseCount;
    public bool Pinned => _item.Pinned;
    public bool IsSensitive => _item.IsSensitive;

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

    public ClipboardItemViewModel(ClipboardItem item)
    {
        _item = item;
    }

    private void LoadSourceIcon()
    {
        if (string.IsNullOrWhiteSpace(_item.SourceProcess) || _item.SourceProcess == "Unknown")
            return;

        try
        {
            // Try resolving full path if available or search common paths
            string proc = _item.SourceProcess;
            string? fullPath = null;

            if (File.Exists(proc))
            {
                fullPath = proc;
            }
            else
            {
                // Check System32
                string sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), proc);
                if (File.Exists(sys32))
                {
                    fullPath = sys32;
                }
                else
                {
                    // Check ProgramFiles
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

