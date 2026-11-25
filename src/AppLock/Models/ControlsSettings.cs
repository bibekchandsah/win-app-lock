using System;
using System.Collections.Generic;

namespace AppLock.Models;

public class ControlsSettings
{
    public ControlsTileVisibility Tiles { get; set; } = new();
    public bool ShowBluetoothBatteryCard { get; set; } = true;
    public bool HideBatteryCardWhenNone { get; set; } = false;
    public bool ShowQuickCards { get; set; } = true;
    public bool ShowBrightness { get; set; } = true;
    public bool ShowMicrophone { get; set; } = true;
    public bool ShowPowerRow { get; set; } = true;
    public bool CloseIslandAfterCard { get; set; } = true;
    private List<QuickCard> _cards = QuickCard.Defaults();
    public List<QuickCard> Cards
    {
        get => (_cards == null || _cards.Count == 0) ? (_cards = QuickCard.Defaults()) : _cards;
        set => _cards = (value == null || value.Count == 0) ? QuickCard.Defaults() : value;
    }

    public bool WifiShowUnsaved { get; set; } = true;
    public int WifiScanSeconds { get; set; } = 15;
    public bool WifiWarnOpen { get; set; } = true;

    public bool BtShowUnnamed { get; set; } = false;
    public bool BtLowBatteryAlert { get; set; } = true;
    public int BtLowBatteryPercent { get; set; } = 20;

    /// <summary>Battery warning thresholds (% below which a warning card is shown, e.g. 80, 50, 20, 10).</summary>
    public List<int> BtWarningThresholds { get; set; } = new() { 80, 50, 20, 10 };

    public List<int> GetBtWarningThresholds()
    {
        if (BtWarningThresholds == null || BtWarningThresholds.Count == 0)
        {
            BtWarningThresholds = new List<int> { 80, 50, 20, 10 };
        }
        return BtWarningThresholds;
    }

    public static List<int> ParseThresholds(string? text)
    {
        var result = new SortedSet<int>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int val) && val >= 1 && val <= 99)
                {
                    result.Add(val);
                }
            }
        }
        var list = new List<int>(result);
        list.Sort((a, b) => b.CompareTo(a)); // descending
        return list;
    }

    /// <summary>Show connected Bluetooth device battery in the folded island.</summary>
    public bool ShowBtBatteryOnIsland { get; set; } = true;

    /// <summary>User consent to show multiple connected Bluetooth devices in the folded island.</summary>
    public bool ShowMultipleBtBatteriesOnIsland { get; set; } = false;

    /// <summary>Display style on folded island: "IconAndPercent" | "IconAndBattery" | "IconBatteryAndPercent".</summary>
    public string BtBatteryIslandStyle { get; set; } = "IconAndPercent";

    /// <summary>Also send Bluetooth battery warnings as Windows notifications.</summary>
    public bool BtWarningWindowsNotification { get; set; } = true;
}

public class ControlsTileVisibility
{
    public bool Wifi { get; set; } = true;
    public bool Bluetooth { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    public bool Battery { get; set; } = true;
    public bool Vpn { get; set; } = true;
}

public enum QuickCardKind
{
    Builtin,
    Uri,
    Executable,
    Path
}

public class QuickCard
{
    public string Id { get; set; } = string.Empty;
    public bool IsCustom { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public QuickCardKind Kind { get; set; } = QuickCardKind.Builtin;
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool RunAsAdmin { get; set; }

    public static List<QuickCard> Defaults() => new()
    {
        new QuickCard { Id = "lock", Name = "Lock screen", IconGlyph = "\uE72E", Kind = QuickCardKind.Builtin },
        new QuickCard { Id = "desktop", Name = "Show desktop", IconGlyph = "\uE7F4", Kind = QuickCardKind.Builtin },
        new QuickCard { Id = "taskview", Name = "Task view", IconGlyph = "\uE7C4", Kind = QuickCardKind.Builtin },
        new QuickCard { Id = "calculator", Name = "Calculator", IconGlyph = "\uE8EF", Kind = QuickCardKind.Builtin, Command = "calc.exe" },
        new QuickCard { Id = "defaultapps", Name = "Default apps", IconGlyph = "\uE71D", Kind = QuickCardKind.Uri, Command = "ms-settings:defaultapps" },
        new QuickCard { Id = "startupapps", Name = "Startup apps", IconGlyph = "\uE7B5", Kind = QuickCardKind.Uri, Command = "ms-settings:startupapps" },
    };
}
