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

    // ── Milestone 4: Live Card Settings ───────────────────────────────────────
    /// <summary>Card stay duration: "Shorter" (2000ms), "Usual" (3500ms), "Longer" (6000ms).</summary>
    public string CardStayDuration { get; set; } = "Usual";

    public int GetCardStayMs() => CardStayDuration switch
    {
        "Shorter" => 2000,
        "Longer"  => 6000,
        _         => 3500
    };

    public bool CardCapsLockEnabled   { get; set; } = true;
    public bool CardVolumeEnabled     { get; set; } = true;
    public bool CardClipboardEnabled  { get; set; } = true;
    public bool CardScreenshotEnabled { get; set; } = true;

    // ── Milestone 10: Weather Settings ───────────────────────────────────────
    public string WeatherCity { get; set; } = string.Empty;
    public double WeatherLatitude { get; set; } = 0.0;
    public double WeatherLongitude { get; set; } = 0.0;
    public string WeatherUnits { get; set; } = "metric"; // "metric" (°C, m/s) or "imperial" (°F, mph)
    public bool WeatherTempOnIsland { get; set; } = true;
    public bool WeatherRainWarning { get; set; } = true;

    // ── Milestone 12: Notification / Alert Card Settings ─────────────────────
    /// <summary>Push a card when VPN connects or disconnects.</summary>
    public bool CardVpnEnabled        { get; set; } = true;

    /// <summary>Push cards at 20 %, 10 %, 5 % battery while discharging.</summary>
    public bool CardBatteryLowEnabled { get; set; } = true;

    /// <summary>Push a card when charging starts or finishes.</summary>
    public bool CardChargingEnabled   { get; set; } = true;

    /// <summary>Show orange/green mic/camera indicator dots on the folded island.</summary>
    public bool MicCamDotsEnabled     { get; set; } = true;

    // ── Milestone 13: Chat Tab (WebView2) ────────────────────────────────────
    /// <summary>List of AI chat sites available as sub-tabs (defaults: Gemini, DeepSeek, ChatGPT; max 6).</summary>
    public System.Collections.Generic.List<ChatSite> ChatSites { get; set; } = DefaultChatSites();

    /// <summary>Minutes of inactivity after which the WebView2 instance is released (default: 5 min).</summary>
    public int ChatIdleMinutes { get; set; } = 5;

    /// <summary>Index of currently active chat sub-tab.</summary>
    public int ActiveChatIndex { get; set; } = 0;

    public static System.Collections.Generic.List<ChatSite> DefaultChatSites() => new()
    {
        new ChatSite("Gemini", "https://gemini.google.com", "\uE8BD"),
        new ChatSite("DeepSeek", "https://chat.deepseek.com", "\uE9CE"),
        new ChatSite("ChatGPT", "https://chatgpt.com", "\uE946")
    };

    // ── Milestone 15: Visibility Rules Settings ──────────────────────────────
    /// <summary>Hide the island when a full-screen app (game, video, presentation) is running.</summary>
    public bool HideInFullScreen { get; set; } = true;

    /// <summary>Come out over full-screen apps when cursor rests at top edge (500ms dwell).</summary>
    public bool EdgeDwellEnabled { get; set; } = true;

    /// <summary>Top-edge dwell duration in milliseconds (default: 500 ms).</summary>
    public int EdgeDwellMs { get; set; } = 500;

    /// <summary>Auto-hide folded island off-screen until needed or cursor hovers top edge.</summary>
    public bool AutoHide { get; set; } = false;

    /// <summary>When auto-hide is enabled, stay in sight while the Windows desktop is foreground.</summary>
    public bool AutoHideStayOnDesktop { get; set; } = true;

    /// <summary>When auto-hide is enabled, hide only while a window actually overlaps the island area.</summary>
    public bool AutoHideOnlyWhenOverlapped { get; set; } = false;

    /// <summary>Chosen apps rule: "Disabled" | "Hide" (hide while active) | "Keep" (only show while active).</summary>
    public string ChosenAppsMode { get; set; } = "Disabled";

    /// <summary>List of process names for the chosen apps rule (e.g. "chrome", "notepad").</summary>
    public System.Collections.Generic.List<string> ChosenApps { get; set; } = new();

    /// <summary>Target monitor: "Primary" | "Cursor" | "Specific".</summary>
    public string TargetMonitorMode { get; set; } = "Primary";

    /// <summary>Display device ID or name when TargetMonitorMode is "Specific".</summary>
    public string TargetMonitorDeviceName { get; set; } = string.Empty;

    /// <summary>Position island under top taskbar if taskbar is docked at top.</summary>
    public bool UnderTopTaskbar { get; set; } = true;

    // ── Milestone 16: Settings Completion & Polish ───────────────────────────
    /// <summary>Lite mode: disable shadow, 100% opacity, 120ms transitions, 30 fps equalizer, 1 min clock.</summary>
    public bool LiteMode { get; set; } = false;

    /// <summary>Force reduced motion (80ms transitions, skip staggers & equalizer animations).</summary>
    public bool ReducedMotion { get; set; } = false;

    /// <summary>Exclude the island window from screenshots and screen recordings (WDA_EXCLUDEFROMCAPTURE).</summary>
    public bool ExcludeFromCapture { get; set; } = false;

    /// <summary>Island color swatch: "Default", "Navy", "Indigo", "Green", "Maroon", "Brown", "Cream", "Mint", "Lavender", or hex.</summary>
    public string IslandColor { get; set; } = "Default";

    /// <summary>Hover open delay in milliseconds (default: 150 ms).</summary>
    public int OpenDelayMs { get; set; } = 150;

    /// <summary>Hover close delay in milliseconds (default: 350 ms).</summary>
    public int CloseDelayMs { get; set; } = 350;

    /// <summary>Show developer frame-stats overlay on the island.</summary>
    public bool ShowFrameStats { get; set; } = false;

    public WindowIslandSettings Clone()
    {
        var clone = (WindowIslandSettings)MemberwiseClone();
        clone.ChatSites = new System.Collections.Generic.List<ChatSite>();
        if (ChatSites != null)
        {
            foreach (var s in ChatSites)
            {
                clone.ChatSites.Add(new ChatSite(s.Name, s.Url, s.Icon));
            }
        }
        clone.ChosenApps = new System.Collections.Generic.List<string>(ChosenApps ?? new());
        return clone;
    }
}

/// <summary>
/// Model for an AI chat site sub-tab in Window Island Chat tab.
/// </summary>
public sealed class ChatSite
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Icon { get; set; } = "\uE8BD";

    public ChatSite() { }

    public ChatSite(string name, string url, string icon = "\uE8BD")
    {
        Name = name;
        Url = url;
        Icon = icon;
    }
}
