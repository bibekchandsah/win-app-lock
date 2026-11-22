namespace AppLock.Models;

/// <summary>How long a live card stays on screen (island.md 5.2).</summary>
public enum IslandCardDuration
{
    Shorter = 0,
    Usual = 1,
    Longer = 2,
}

/// <summary>Persisted Island settings. The island UI renders from these over the bridge; the host owns
/// the window, position, scale and visibility.</summary>
public sealed class IslandSettings
{
    /// <summary>Show the notch at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Global hotkey that shows/hides the island, e.g. "Ctrl+Alt+I".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+I";

    /// <summary>TopCenter | TopLeft | TopRight.</summary>
    public string Position { get; set; } = "TopCenter";

    /// <summary>Notch (flush with the screen edge) or Capsule (fully rounded, floating).</summary>
    public string Shape { get; set; } = "Notch";

    /// <summary>System | Light | Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Island scale in percent (100 = the design size from the spec).</summary>
    public int SizePercent { get; set; } = 100;

    /// <summary>Background opacity of the island body in percent.</summary>
    public int OpacityPercent { get; set; } = 95;

    /// <summary>Off passes --disable-gpu to the WebView2 process; takes effect on the next start.</summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>Folded notch shows the time.</summary>
    public bool ShowClock { get; set; } = true;

    /// <summary>Folded notch shows the battery level.</summary>
    public bool ShowBattery { get; set; } = true;

    /// <summary>How long a live card stays before it collapses again.</summary>
    public IslandCardDuration CardDuration { get; set; } = IslandCardDuration.Usual;

    /// <summary>Live events: each card can be turned off (island.md 12).</summary>
    public bool ShowVolumeCard { get; set; } = true;

    public bool ShowCapsLockCard { get; set; } = true;

    public bool ShowClipboardCard { get; set; } = true;

    public bool ShowScreenshotCard { get; set; } = true;

    /// <summary>Screenshots folder to watch; empty means Pictures\Screenshots.</summary>
    public string ScreenshotsFolder { get; set; } = string.Empty;

    /// <summary>Shelf entries point at the original files; this copies the files into the shelf instead.</summary>
    public bool ShelfCopyFiles { get; set; }

    public IslandSettings Clone() => (IslandSettings)MemberwiseClone();

    /// <summary>Stay time of a card in milliseconds.</summary>
    public int CardStayMs => CardDuration switch
    {
        IslandCardDuration.Shorter => 2000,
        IslandCardDuration.Longer => 6000,
        _ => 3500,
    };
}
