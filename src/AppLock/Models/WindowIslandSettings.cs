namespace AppLock.Models;

/// <summary>
/// Persisted settings for the native Window Island (WinUI 3 + Composition API).
/// Completely separate from the WebView2 Island.
/// </summary>
public sealed class WindowIslandSettings
{
    /// <summary>Whether Window Island is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Last active category tag in WindowIslandSettingsPage so it persists across reload/restart.</summary>
    public string LastSettingsTab { get; set; } = "general";

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

    /// <summary>Date & Calendar tab clock style: "Digital" | "DigitalSeconds" | "Analog" | "AnalogSeconds".</summary>
    public string CalendarClockStyle { get; set; } = "DigitalSeconds";

    /// <summary>Analog clock face in the Date & Calendar tab: "Auto" | "Dial" | "Minimal" | "Bold" | "Light" | "Thin" | "Classic".</summary>
    public string CalendarClockFace { get; set; } = "Auto";

    /// <summary>Custom world clocks displayed as scrollable pills in the Date & Calendar tab.</summary>
    public System.Collections.Generic.List<CustomWorldClock> CustomClocks { get; set; } = new();

    /// <summary>Show battery in folded notch.</summary>
    public bool ShowBattery { get; set; } = true;

    /// <summary>Show system stats (icon + %) in folded notch.</summary>
    public bool ShowCpuInNotch  { get; set; } = false;
    public bool ShowRamInNotch  { get; set; } = false;
    public bool ShowDiskInNotch { get; set; } = false;
    public bool ShowGpuInNotch  { get; set; } = false;
    public bool ShowDownSpeedInNotch { get; set; } = false;
    public bool ShowUpSpeedInNotch   { get; set; } = false;

    /// <summary>Vertical offset in DIPs from top of screen.</summary>
    public int YOffsetDip { get; set; } = 0;

    /// <summary>Horizontal offset in DIPs from the configured edge/center.</summary>
    public int XOffsetDip { get; set; } = 0;

    // ── Milestone 4: Live Card Settings ───────────────────────────────────────
    /// <summary>Card stay duration: "1s" (1000ms), "Shorter" (2000ms), "Usual" (3500ms), "Longer" (6000ms), "Custom".</summary>
    public string CardStayDuration { get; set; } = "Usual";

    /// <summary>Custom card stay duration in milliseconds when CardStayDuration is "Custom" (500..30000 ms).</summary>
    public int CardStayCustomMs { get; set; } = 4000;

    public int GetCardStayMs() => CardStayDuration switch
    {
        "1s"      => 1000,
        "Shorter" => 2000,
        "Longer"  => 6000,
        "Custom"  => Math.Clamp(CardStayCustomMs > 0 ? CardStayCustomMs : 4000, 500, 30000),
        _         => 3500
    };

    public bool CardCapsLockEnabled   { get; set; } = true;
    public bool CardNumLockEnabled    { get; set; } = true;
    public bool CardDeleteKeyWarningEnabled { get; set; } = false;
    public System.Collections.Generic.List<string> CustomKeyWarnings { get; set; } = new();
    public bool CardVolumeEnabled     { get; set; } = true;
    public bool CardBrightnessEnabled { get; set; } = true;
    public bool CardClipboardEnabled  { get; set; } = true;
    public bool CardScreenshotEnabled { get; set; } = true;
    public bool CardDownloadsEnabled  { get; set; } = true;
    public bool CardFileCopyEnabled   { get; set; } = true;

    public static System.Collections.Generic.List<string> ParseCustomKeyWarnings(string? raw)
    {
        var result = new System.Collections.Generic.List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryResolveVirtualKey(part, out _, out string canonical) && seen.Add(canonical))
            {
                result.Add(canonical);
            }
        }
        return result;
    }

    public static bool TryResolveVirtualKey(string? name, out int vk, out string canonicalName)
    {
        vk = 0;
        canonicalName = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string trimmed = name.Trim();

        switch (trimmed.ToUpperInvariant())
        {
            case "DEL":
            case "DELETE":
                vk = 0x2E; canonicalName = "Delete"; return true;
            case "INS":
            case "INSERT":
                vk = 0x2D; canonicalName = "Insert"; return true;
            case "PRTSC":
            case "PRINTSCREEN":
            case "SNAPSHOT":
                vk = 0x2C; canonicalName = "PrintScreen"; return true;
            case "ESC":
            case "ESCAPE":
                vk = 0x1B; canonicalName = "Escape"; return true;
            case "PGUP":
            case "PAGEUP":
                vk = 0x21; canonicalName = "PageUp"; return true;
            case "PGDN":
            case "PAGEDOWN":
                vk = 0x22; canonicalName = "PageDown"; return true;
            case "SCROLL":
            case "SCROLLLOCK":
                vk = 0x91; canonicalName = "ScrollLock"; return true;
            case "PAUSE":
            case "BREAK":
                vk = 0x13; canonicalName = "Pause"; return true;
            case "BACK":
            case "BACKSPACE":
                vk = 0x08; canonicalName = "Backspace"; return true;
        }

        if (Enum.TryParse<Windows.System.VirtualKey>(trimmed, ignoreCase: true, out var parsed) && (int)parsed > 0)
        {
            vk = (int)parsed;
            canonicalName = parsed.ToString();
            return true;
        }
        return false;
    }

    // ── Milestone 8: Command Bar & Web Searches (@) ──────────────────────────
    /// <summary>Default search engine used for plain web searches in the Command Bar ("google", "bing", "duckduckgo", "custom").</summary>
    public string DefaultSearchEngine { get; set; } = "google";

    /// <summary>Custom search URL template (e.g. "https://example.com/search?q={query}") when DefaultSearchEngine is "custom".</summary>
    public string CustomDefaultSearchUrl { get; set; } = string.Empty;

    public (string Name, string Url, string AccentHex) ResolveDefaultSearchEngine()
    {
        return (DefaultSearchEngine ?? "google").ToLowerInvariant() switch
        {
            "bing"       => ("Bing",       "https://www.bing.com/search?q=",   "#008373"),
            "duckduckgo" => ("DuckDuckGo", "https://duckduckgo.com/?q=",        "#DE5833"),
            "custom" when !string.IsNullOrWhiteSpace(CustomDefaultSearchUrl)
                         => ("Custom",     CustomDefaultSearchUrl.Trim(),      "#60CDFF"),
            _            => ("Google",     "https://www.google.com/search?q=", "#4285F4")
        };
    }

    public string BuildDefaultSearchUrl(string query)
    {
        var (_, baseUrl, _) = ResolveDefaultSearchEngine();
        string encoded = Uri.EscapeDataString(query);
        if (baseUrl.Contains("{query}", StringComparison.OrdinalIgnoreCase))
            return baseUrl.Replace("{query}", encoded, StringComparison.OrdinalIgnoreCase);
        return baseUrl + encoded;
    }

    /// <summary>Configurable list of search engine shortcuts in the Command Bar (e.g. "@youtube nepali music").</summary>
    public System.Collections.Generic.List<CustomSearchCommand> CustomSearches { get; set; } = DefaultCustomSearches();

    public System.Collections.Generic.List<CustomSearchCommand> GetCustomSearches()
    {
        if (CustomSearches == null || CustomSearches.Count == 0)
        {
            CustomSearches = DefaultCustomSearches();
        }
        return CustomSearches;
    }

    public static System.Collections.Generic.List<CustomSearchCommand> DefaultCustomSearches() => new()
    {
        new CustomSearchCommand("@google", "Google", "https://www.google.com/search?q=", "https://www.google.com", "\uE721", "#4285F4"),
        new CustomSearchCommand("@youtube", "YouTube", "https://www.youtube.com/results?search_query=", "https://www.youtube.com", "\uE714", "#FF0000"),
        new CustomSearchCommand("@incognito", "Incognito Search", "https://duckduckgo.com/?q=", "https://duckduckgo.com", "\uE727", "#9E9E9E", isIncognito: true),
        new CustomSearchCommand("@maps", "Google Maps", "https://www.google.com/maps/search/", "https://www.google.com/maps", "\uE707", "#34A853"),
        new CustomSearchCommand("@images", "Google Images", "https://www.google.com/search?tbm=isch&q=", "https://images.google.com", "\uEB9F", "#FBBC05"),
        new CustomSearchCommand("@amazon", "Amazon", "https://www.amazon.com/s?k=", "https://www.amazon.com", "\uE719", "#FF9900"),
        new CustomSearchCommand("@flipkart", "Flipkart", "https://www.flipkart.com/search?q=", "https://www.flipkart.com", "\uE7BF", "#2874F0"),
        new CustomSearchCommand("@wiki", "Wikipedia", "https://en.wikipedia.org/wiki/Special:Search?search=", "https://en.wikipedia.org", "\uE82D", "#6B7280"),
        new CustomSearchCommand("@wikihow", "wikiHow", "https://www.wikihow.com/wikiHowTo?search=", "https://www.wikihow.com", "\uE82D", "#93B874"),
        new CustomSearchCommand("@github", "GitHub", "https://github.com/search?q=", "https://github.com", "\uE943", "#A371F7"),
        new CustomSearchCommand("@translate", "Google Translate", "https://translate.google.com/?sl=auto&tl=en&text=", "https://translate.google.com", "\uE8C1", "#1A73E8"),
        new CustomSearchCommand("@stackoverflow", "Stack Overflow", "https://stackoverflow.com/search?q=", "https://stackoverflow.com", "\uE7BA", "#F48024"),
        new CustomSearchCommand("@reddit", "Reddit", "https://www.reddit.com/search/?q=", "https://www.reddit.com", "\uE8BD", "#FF4500"),
        new CustomSearchCommand("@quora", "Quora", "https://www.quora.com/search?q=", "https://www.quora.com", "\uE76E", "#B92B27"),
        new CustomSearchCommand("@medium", "Medium", "https://medium.com/search?q=", "https://medium.com", "\uE70F", "#00AB6C"),
        new CustomSearchCommand("@linkedin", "LinkedIn", "https://www.linkedin.com/search/results/all/?keywords=", "https://www.linkedin.com", "\uE77B", "#0A66C2"),
        new CustomSearchCommand("@twitter", "Twitter / X", "https://x.com/search?q=", "https://x.com", "\uE735", "#1DA1F2"),
        new CustomSearchCommand("@x", "X", "https://x.com/search?q=", "https://x.com", "\uE735", "#E7E9EA"),
        new CustomSearchCommand("@news", "Google News", "https://news.google.com/search?q=", "https://news.google.com", "\uE8A1", "#EA4335"),
        new CustomSearchCommand("@scholar", "Google Scholar", "https://scholar.google.com/scholar?q=", "https://scholar.google.com", "\uE82D", "#4285F4"),
        new CustomSearchCommand("@weather", "Weather", "https://www.google.com/search?q=weather+", "https://www.google.com/search?q=weather", "\uE9CA", "#FFB900"),
        new CustomSearchCommand("@duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q=", "https://duckduckgo.com", "\uE774", "#DE5833"),
        new CustomSearchCommand("@spotify", "Spotify", "https://open.spotify.com/search/", "https://open.spotify.com", "\uE8D6", "#1DB954"),
        new CustomSearchCommand("@coursera", "Coursera", "https://www.coursera.org/search?query=", "https://www.coursera.org", "\uE7BE", "#0056D2"),
        new CustomSearchCommand("@udemy", "Udemy", "https://www.udemy.com/courses/search/?q=", "https://www.udemy.com", "\uE7BE", "#A435F0"),
        new CustomSearchCommand("@discord", "Discord", "https://discord.com/search?q=", "https://discord.com", "\uE8BD", "#5865F2"),
        new CustomSearchCommand("@pinterest", "Pinterest", "https://www.pinterest.com/search/pins/?q=", "https://www.pinterest.com", "\uE718", "#BD081C"),
        new CustomSearchCommand("@unsplash", "Unsplash", "https://unsplash.com/s/photos/", "https://unsplash.com", "\uEB9F", "#FFFFFF")
    };

    // ── Milestone 10: Weather Settings ───────────────────────────────────────
    public string WeatherCity { get; set; } = string.Empty;
    public double WeatherLatitude { get; set; } = 0.0;
    public double WeatherLongitude { get; set; } = 0.0;
    public string WeatherUnits { get; set; } = "metric"; // "metric" (°C, m/s) or "imperial" (°F, mph)
    public bool WeatherTempOnIsland { get; set; } = true;
    public bool WeatherRainWarning { get; set; } = true;
    public System.Collections.Generic.List<WeatherCity> WeatherCities { get; set; } = new();
    public int ActiveWeatherCityIndex { get; set; } = 0;

    public System.Collections.Generic.List<WeatherCity> GetWeatherCities()
    {
        if (WeatherCities == null || WeatherCities.Count == 0)
        {
            WeatherCities = new System.Collections.Generic.List<WeatherCity>();
            if (!string.IsNullOrWhiteSpace(WeatherCity) && (WeatherLatitude != 0 || WeatherLongitude != 0))
            {
                WeatherCities.Add(new WeatherCity(WeatherCity, WeatherLatitude, WeatherLongitude));
            }
            else
            {
                WeatherCities.Add(new WeatherCity("Kathmandu", 27.7172, 85.3240, "Nepal"));
            }
        }
        return WeatherCities;
    }

    public WeatherCity GetActiveCity()
    {
        var list = GetWeatherCities();
        int idx = Math.Clamp(ActiveWeatherCityIndex, 0, list.Count - 1);
        ActiveWeatherCityIndex = idx;
        return list[idx];
    }

    // ── Milestone 12: Notification / Alert Card Settings ─────────────────────
    /// <summary>Push a card when VPN connects or disconnects.</summary>
    public bool CardVpnEnabled        { get; set; } = true;

    // ── Controls Tab Add-on Settings ─────────────────────────────────────────
    public ControlsSettings Controls  { get; set; } = new();

    /// <summary>Push cards at 20 %, 10 %, 5 % battery while discharging.</summary>
    public bool CardBatteryLowEnabled { get; set; } = true;

    /// <summary>Custom battery percentage thresholds (1..99, descending) that trigger low battery alerts.</summary>
    public List<int> LowBatteryThresholds { get; set; } = new() { 20, 10, 5 };

    public List<int> GetEffectiveLowBatteryThresholds()
    {
        var list = (LowBatteryThresholds ?? new List<int> { 20, 10, 5 })
            .Where(p => p >= 1 && p <= 99)
            .Distinct()
            .OrderByDescending(p => p)
            .ToList();
        return list.Count > 0 ? list : new List<int> { 20, 10, 5 };
    }

    /// <summary>Push a card when charging starts or finishes.</summary>
    public bool CardChargingEnabled   { get; set; } = true;

    /// <summary>Show orange/green mic/camera indicator dots on the folded island.</summary>
    public bool MicCamDotsEnabled     { get; set; } = true;

    /// <summary>Show microphone indicator on the folded island.</summary>
    public bool MicDotEnabled         { get; set; } = true;

    /// <summary>Show camera indicator on the folded island.</summary>
    public bool CamDotEnabled         { get; set; } = true;

    /// <summary>Push a live card when an app starts using the microphone.</summary>
    public bool CardMicEnabled        { get; set; } = true;

    /// <summary>Push a live card when an app starts using the camera.</summary>
    public bool CardCamEnabled        { get; set; } = true;

    // ── Home tab media ────────────────────────────────────────────────────────
    /// <summary>Show the lyrics sheet beside the player when the current track has lyrics.</summary>
    public bool LyricsEnabled         { get; set; } = true;

    /// <summary>Home tab player visual style: "Compact", "Fill", or "SoftMirror".</summary>
    public string MediaPlayerStyle    { get; set; } = "Compact";
    public string MediaPillMode       { get; set; } = "Usual";

    // ── Milestone 13: Chat Tab (WebView2) ────────────────────────────────────
    /// <summary>List of AI chat sites available as sub-tabs (defaults: Gemini, DeepSeek, ChatGPT; max 6).</summary>
    public System.Collections.Generic.List<ChatSite> ChatSites { get; set; } = DefaultChatSites();

    /// <summary>Minutes of inactivity after which the WebView2 instance is released (default: 5 min).</summary>
    public int ChatIdleMinutes { get; set; } = 5;

    /// <summary>Index of currently active chat sub-tab.</summary>
    public int ActiveChatIndex { get; set; } = 0;

    /// <summary>Dynamic height in DIPs for the AI Chat tab (default: 440, min: 320, max: 720).</summary>
    public float ChatHeightDip { get; set; } = 440f;

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

    /// <summary>Through mode: folded island lets clicks pass through and ignores hover; hotkeys still open it.</summary>
    public bool ThroughMode { get; set; } = false;

    /// <summary>Optional global hotkey to toggle Through mode (e.g. "Ctrl+Shift+T").</summary>
    public string ThroughModeHotkey { get; set; } = string.Empty;

    /// <summary>Global hotkey to open/toggle the Window Island Command Bar (default: "Alt+Space").</summary>
    public string CommandBarHotkey { get; set; } = "Alt+Space";

    // ── Milestone 16: Settings Completion & Polish ───────────────────────────
    /// <summary>Lite mode: disable shadow, 100% opacity, 120ms transitions, 30 fps equalizer, 1 min clock.</summary>
    public bool LiteMode { get; set; } = false;

    /// <summary>Force reduced motion (80ms transitions, skip staggers & equalizer animations).</summary>
    public bool ReducedMotion { get; set; } = false;

    /// <summary>Notch open/tab animation duration in milliseconds (80..1200 ms, default: 240 ms).</summary>
    public int AnimationDurationMs { get; set; } = 240;

    /// <summary>Notch close animation duration in milliseconds (80..1200 ms, default: 220 ms).</summary>
    public int CloseAnimationDurationMs { get; set; } = 220;

    /// <summary>Exclude the island window from screenshots and screen recordings (WDA_EXCLUDEFROMCAPTURE).</summary>
    public bool ExcludeFromCapture { get; set; } = false;

    /// <summary>Island color swatch: "Default", "Navy", "Indigo", "Green", "Maroon", "Brown", "Cream", "Mint", "Lavender", or hex.</summary>
    public string IslandColor { get; set; } = "Default";

    /// <summary>Last custom hex color chosen by the user, preserved even when switching to a preset swatch.</summary>
    public string LastCustomIslandColor { get; set; } = "#1E293B";

    /// <summary>Hover open delay in milliseconds (default: 150 ms).</summary>
    public int OpenDelayMs { get; set; } = 150;

    /// <summary>Hover close delay in milliseconds (default: 350 ms).</summary>
    public int CloseDelayMs { get; set; } = 350;

    /// <summary>Show developer frame-stats overlay on the island.</summary>
    public bool ShowFrameStats { get; set; } = false;

    /// <summary>Custom folder path for saving screenshots (empty = default Pictures\Screenshots).</summary>
    public string ScreenshotsFolder { get; set; } = string.Empty;

    public string GetEffectiveScreenshotsFolder()
    {
        if (!string.IsNullOrWhiteSpace(ScreenshotsFolder))
        {
            try { return System.IO.Path.GetFullPath(ScreenshotsFolder.Trim()); } catch { }
        }
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

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
        clone.CustomSearches = new System.Collections.Generic.List<CustomSearchCommand>();
        if (CustomSearches != null)
        {
            foreach (var cs in CustomSearches)
            {
                clone.CustomSearches.Add(cs.Clone());
            }
        }
        clone.WeatherCities = new System.Collections.Generic.List<WeatherCity>();
        if (WeatherCities != null)
        {
            foreach (var c in WeatherCities)
            {
                clone.WeatherCities.Add(new WeatherCity(c.Name, c.Latitude, c.Longitude, c.Country, c.UtcOffsetSeconds));
            }
        }
        clone.CustomClocks = new System.Collections.Generic.List<CustomWorldClock>();
        if (CustomClocks != null)
        {
            foreach (var cc in CustomClocks)
            {
                clone.CustomClocks.Add(new CustomWorldClock(cc.PlaceName, cc.TimeZoneId));
            }
        }
        return clone;
    }
}

/// <summary>
/// Custom world clock entry for the Window Island Date & Calendar tab.
/// </summary>
public sealed class CustomWorldClock
{
    public string PlaceName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;

    public CustomWorldClock() { }

    public CustomWorldClock(string placeName, string timeZoneId)
    {
        PlaceName = placeName;
        TimeZoneId = timeZoneId;
    }
}

/// <summary>
/// Model for a saved city location in Window Island Weather tab.
/// </summary>
public sealed class WeatherCity
{
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Country { get; set; } = string.Empty;
    public int UtcOffsetSeconds { get; set; } = int.MinValue;

    public WeatherCity() { }

    public WeatherCity(string name, double lat, double lon, string country = "", int utcOffsetSeconds = int.MinValue)
    {
        Name = name;
        Latitude = lat;
        Longitude = lon;
        Country = country;
        UtcOffsetSeconds = utcOffsetSeconds;
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
