namespace AppLock.Models;

/// <summary>
/// Persisted settings for the native Window Island (WinUI 3 + Composition API).
/// Completely separate from the WebView2 Island.
/// </summary>
public sealed class WindowIslandSettings
{
    /// <summary>Whether Window Island is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Global hotkey to toggle Window Island, e.g. "Ctrl+Alt+W" or "Ctrl+Shift+I".</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+W";

    /// <summary>Position: TopCenter | TopLeft | TopRight.</summary>
    public string Position { get; set; } = "TopCenter";

    /// <summary>Shape: Notch (flush with top screen edge) or Capsule (floating pill).</summary>
    public string Shape { get; set; } = "Notch";

    /// <summary>Theme: System | Light | Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Scale in percent (100 = default size).</summary>
    public int SizePercent { get; set; } = 100;

    /// <summary>Opacity in percent (e.g. 95).</summary>
    public int OpacityPercent { get; set; } = 95;

    /// <summary>Show clock in folded notch.</summary>
    public bool ShowClock { get; set; } = true;

    /// <summary>Use 24-hour time format.</summary>
    public bool TimeFormat24H { get; set; } = true;

    /// <summary>Show seconds in clock.</summary>
    public bool ShowSeconds { get; set; } = false;

    /// <summary>Show battery in folded notch.</summary>
    public bool ShowBattery { get; set; } = true;

    /// <summary>Vertical offset in DIPs from top of screen.</summary>
    public int YOffsetDip { get; set; } = 0;

    public WindowIslandSettings Clone() => (WindowIslandSettings)MemberwiseClone();
}
