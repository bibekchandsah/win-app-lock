using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppLock.Models;

namespace AppLock.WindowIsland.Modules.Controls;

public static class QuickCardCatalog
{
    private static List<QuickCard>? _cachedCatalog;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<QuickCard> GetCatalogCards()
    {
        if (_cachedCatalog != null) return _cachedCatalog;

        try
        {
            // Search paths for controls-cards.json
            string[] possiblePaths =
            [
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "island", "controls-cards.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "controls-cards.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "island", "controls-cards.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "controls-cards.json")
            ];

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<List<QuickCard>>(json, _jsonOptions);
                    if (loaded != null && loaded.Count > 0)
                    {
                        _cachedCatalog = loaded;
                        return _cachedCatalog;
                    }
                }
            }
        }
        catch { }

        _cachedCatalog = GetDefaultCatalog();
        return _cachedCatalog;
    }

    public static List<QuickCard> GetDefaultCatalog() => new()
    {
        new QuickCard { Id = "lock", Name = "Lock screen", IconGlyph = "\uE72E", Kind = QuickCardKind.Builtin },
        new QuickCard { Id = "desktop", Name = "Show desktop", IconGlyph = "\uE7F4", Kind = QuickCardKind.Builtin },
        new QuickCard { Id = "taskview", Name = "Task view", IconGlyph = "\uE7C4", Kind = QuickCardKind.Builtin },
        new QuickCard { Id = "calculator", Name = "Calculator", IconGlyph = "\uE8EF", Kind = QuickCardKind.Builtin, Command = "calc.exe" },
        new QuickCard { Id = "settings", Name = "Settings", IconGlyph = "\uE713", Kind = QuickCardKind.Uri, Command = "ms-settings:" },
        new QuickCard { Id = "taskmanager", Name = "Task Manager", IconGlyph = "\uE9F9", Kind = QuickCardKind.Executable, Command = "taskmgr.exe" },
        new QuickCard { Id = "explorer", Name = "File Explorer", IconGlyph = "\uEC50", Kind = QuickCardKind.Executable, Command = "explorer.exe" },
        new QuickCard { Id = "terminal", Name = "Terminal", IconGlyph = "\uE756", Kind = QuickCardKind.Executable, Command = "wt.exe" },
        new QuickCard { Id = "screenshot", Name = "Screenshot", IconGlyph = "\uF272", Kind = QuickCardKind.Builtin, Command = "ms-screenclip:" },
        new QuickCard { Id = "sound", Name = "Sound", IconGlyph = "\uE767", Kind = QuickCardKind.Uri, Command = "ms-settings:sound" },
        new QuickCard { Id = "display", Name = "Display", IconGlyph = "\uE7F4", Kind = QuickCardKind.Uri, Command = "ms-settings:display" },
        new QuickCard { Id = "nightlight", Name = "Night light", IconGlyph = "\uE708", Kind = QuickCardKind.Uri, Command = "ms-settings:nightlight" },
        new QuickCard { Id = "windowsupdate", Name = "Windows Update", IconGlyph = "\uE895", Kind = QuickCardKind.Uri, Command = "ms-settings:windowsupdate" },
        new QuickCard { Id = "bluetoothsettings", Name = "Bluetooth", IconGlyph = "\uE702", Kind = QuickCardKind.Uri, Command = "ms-settings:bluetooth" },
        new QuickCard { Id = "devicemanager", Name = "Device Manager", IconGlyph = "\uE7F8", Kind = QuickCardKind.Executable, Command = "devmgmt.msc" },
        new QuickCard { Id = "defaultapps", Name = "Default apps", IconGlyph = "\uE71D", Kind = QuickCardKind.Uri, Command = "ms-settings:defaultapps" },
        new QuickCard { Id = "startupapps", Name = "Startup apps", IconGlyph = "\uE7B5", Kind = QuickCardKind.Uri, Command = "ms-settings:startupapps" },
        new QuickCard { Id = "installedapps", Name = "Installed apps", IconGlyph = "\uE71D", Kind = QuickCardKind.Uri, Command = "ms-settings:appsfeatures" },
        new QuickCard { Id = "network", Name = "Network", IconGlyph = "\uE774", Kind = QuickCardKind.Uri, Command = "ms-settings:network" },
        new QuickCard { Id = "battery", Name = "Battery settings", IconGlyph = "\uE83F", Kind = QuickCardKind.Uri, Command = "ms-settings:batterysaver" }
    };

    public static (bool IsValid, string ErrorMessage) ValidateCard(string name, string command)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Card name is required.");

        if (name.Trim().Length > 24)
            return (false, "Card name cannot exceed 24 characters.");

        if (string.IsNullOrWhiteSpace(command))
            return (false, "Command or URI is required.");

        string trimmedCmd = command.Trim();

        // Check if command looks like a URI
        if (trimmedCmd.Contains("://") || trimmedCmd.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
            trimmedCmd.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmedCmd.StartsWith("calculator:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmedCmd, UriKind.Absolute, out _))
            {
                return (false, "Invalid URI format.");
            }
        }

        return (true, string.Empty);
    }

    public static bool TryParseHexGlyph(string input, out string glyph, out string hexCode)
    {
        glyph = string.Empty;
        hexCode = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string cleaned = input.Trim();
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[2..];
        else if (cleaned.StartsWith("\\u", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[2..];
        else if (cleaned.StartsWith("&#x", StringComparison.OrdinalIgnoreCase) && cleaned.EndsWith(";"))
            cleaned = cleaned[3..^1];
        else if (cleaned.StartsWith("u+", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[2..];

        if ((cleaned.Length == 4 || cleaned.Length == 5) && int.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out int codePoint))
        {
            if (codePoint > 0 && codePoint <= 0xFFFF)
            {
                glyph = ((char)codePoint).ToString();
                hexCode = cleaned.ToUpperInvariant();
                return true;
            }
        }
        return false;
    }

    public static List<(string Name, string Glyph)> GetCuratedIcons() => new()
    {
        ("App", "\uE71D"),
        ("Lock", "\uE72E"),
        ("Desktop", "\uE7F4"),
        ("Window", "\uE7C4"),
        ("Calculator", "\uE8EF"),
        ("Settings", "\uE713"),
        ("Terminal", "\uE756"),
        ("Task Manager", "\uE9F9"),
        ("Folder", "\uEC50"),
        ("Screenshot", "\uF272"),
        ("Camera", "\uE722"),
        ("Sound", "\uE767"),
        ("Mute", "\uE74F"),
        ("Microphone", "\uE720"),
        ("Bluetooth", "\uE702"),
        ("Wi-Fi", "\uE701"),
        ("Web", "\uE774"),
        ("Mail", "\uE715"),
        ("Calendar", "\uE787"),
        ("Clock", "\uE823"),
        ("Battery", "\uE83F"),
        ("Night light", "\uE708"),
        ("Brightness", "\uE706"),
        ("Refresh", "\uE72C"),
        ("Star", "\uE734"),
        ("Heart", "\uEB51"),
        ("Music", "\uE8D6"),
        ("Media Play", "\uE768"),
        ("Game", "\uE7FC"),
        ("Code", "\uE943"),
        ("Search", "\uE721"),
        ("Document", "\uE8A5"),
        ("Download", "\uE896"),
        ("Upload", "\uE898"),
        ("Cloud", "\uE753"),
        ("Shield", "\uEA18"),
        ("Key", "\uE8D7"),
        ("User", "\uE77B"),
        ("Lightbulb", "\uEA80"),
        ("Pin", "\uE718"),
        ("Bookmark", "\uE8A4"),
        ("Flag", "\uE7C1"),
        ("Tag", "\uE8EC"),
        ("Checkmark", "\uE73E"),
        ("Power", "\uE7E8"),
        ("Restart", "\uE777"),
        ("Store", "\uE719"),
        ("Chat", "\uE8BD"),
        ("Phone", "\uE717"),
        ("Paint", "\uE790"),
        ("Trash", "\uE74D"),
        ("Edit", "\uE70F"),
        ("Link", "\uE71B"),
        ("Share", "\uE72D"),
        ("Info", "\uE897"),
        ("Warning", "\uE7BA"),
        ("Print", "\uE749"),
        ("Airplane", "\uE709"),
        ("Home", "\uE80F"),
        ("Keyboard", "\uE765"),
        ("Mouse", "\uE962")
    };
}
