using System;
using System.Collections.Generic;

namespace AppLock.Models;

public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = false;
    public int MaxItems { get; set; } = 100;            // 10..5000
    public string Hotkey { get; set; } = "Ctrl+Shift+V";
    public bool CaptureText { get; set; } = true;
    public bool CaptureImages { get; set; } = true;
    public bool CaptureFiles { get; set; } = false;
    public bool KeepRichText { get; set; } = true;
    public int MaxTextKb { get; set; } = 1024;          // skip larger text
    public int MaxImageMb { get; set; } = 10;           // skip larger images
    public int StorageCapMb { get; set; } = 500;
    public int AutoDeleteDays { get; set; } = 0;        // 0 = never
    public bool AutoPaste { get; set; } = true;         // false = copy only
    public string PopupPosition { get; set; } = "cursor"; // cursor | center | caret
    public bool EncryptHistory { get; set; } = true;
    public bool RequireHello { get; set; } = false;
    public bool DetectSensitive { get; set; } = true;
    public int SensitiveExpireSeconds { get; set; } = 60; // 0 = never expire
    public List<string> IgnoredApps { get; set; } = new();
    public bool OverrideWinV { get; set; } = false;     // experimental (section 5)
    public bool PlaySound { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string ThemeMode { get; set; } = "System";   // Light | Dark | System
    public bool ExcludeFromScreenCapture { get; set; } = false;
    public string CustomSearchUrl { get; set; } = "https://www.google.com/search?q={query}";

    public void Clamp()
    {
        MaxItems = Math.Clamp(MaxItems, 10, 5000);
        MaxTextKb = Math.Max(1, MaxTextKb);
        MaxImageMb = Math.Max(1, MaxImageMb);
        StorageCapMb = Math.Max(10, StorageCapMb);
        AutoDeleteDays = Math.Max(0, AutoDeleteDays);
        SensitiveExpireSeconds = Math.Max(0, SensitiveExpireSeconds);
        if (string.IsNullOrWhiteSpace(Hotkey))
        {
            Hotkey = "Ctrl+Shift+V";
        }
        if (PopupPosition != "cursor" && PopupPosition != "center" && PopupPosition != "caret")
        {
            PopupPosition = "cursor";
        }
        if (ThemeMode != "Light" && ThemeMode != "Dark" && ThemeMode != "System")
        {
            ThemeMode = "System";
        }
        IgnoredApps ??= new List<string>();
    }
}

