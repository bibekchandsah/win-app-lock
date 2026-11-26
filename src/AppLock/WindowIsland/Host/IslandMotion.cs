using System;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// All Window Island animation timing constants in one place.
/// Tune here; nothing else hard-codes durations or spring parameters.
/// </summary>
public static class IslandMotion
{
    // ── Configurable animation duration ───────────────────────────────────────
    public const int   DefaultAnimationDurationMs      = 240;
    public const int   DefaultCloseAnimationDurationMs = 220;
    public const int   MinAnimationDurationMs          = 80;
    public const int   MaxAnimationDurationMs          = 1200;

    public static int ResolveDurationMs(AppLock.Models.WindowIslandSettings? settings)
    {
        if (settings == null) return DefaultAnimationDurationMs;
        if (settings.ReducedMotion) return ReducedMotionFadeMs;
        int raw = settings.AnimationDurationMs > 0 ? settings.AnimationDurationMs : DefaultAnimationDurationMs;
        int clamped = Math.Clamp(raw, MinAnimationDurationMs, MaxAnimationDurationMs);
        return settings.LiteMode ? Math.Min(clamped, 120) : clamped;
    }

    public static int ResolveCloseDurationMs(AppLock.Models.WindowIslandSettings? settings)
    {
        if (settings == null) return DefaultCloseAnimationDurationMs;
        if (settings.ReducedMotion) return ReducedMotionFadeMs;
        int raw = settings.CloseAnimationDurationMs > 0 ? settings.CloseAnimationDurationMs : DefaultCloseAnimationDurationMs;
        int clamped = Math.Clamp(raw, MinAnimationDurationMs, MaxAnimationDurationMs);
        return settings.LiteMode ? Math.Min(clamped, 120) : clamped;
    }

    // ── Shape springs ──────────────────────────────────────────────────────────
    public const float OpenDampingRatio  = 0.88f;
    public const int   OpenPeriodMs      = 180;

    public const float CloseDampingRatio = 0.92f;
    public const int   ClosePeriodMs     = 170;

    public const float CardInDampingRatio  = 0.84f;
    public const int   CardInPeriodMs      = 190;

    // ── Tab switch ─────────────────────────────────────────────────────────────
    public const int   TabSwitchMs         = 130;    // crossfade + 8 DIP slide
    public const float TabSlideDip         = 8f;

    // ── Content fades ──────────────────────────────────────────────────────────
    public const int   ContentFadeInMs     = 140;
    public const int   ContentFadeInDelayMs = 30;
    public const int   ContentFadeOutMs    = 80;

    // ── Stagger ────────────────────────────────────────────────────────────────
    public const int   StaggerPerItemMs    = 18;
    public const int   StaggerMaxItems     = 8;

    // ── Hover delays ──────────────────────────────────────────────────────────
    public const int   HoverOpenDelayMs    = 150;
    public const int   HoverCloseDelayMs   = 350;
    public const int   HoverHysteresisDip  = 8;

    // ── Reduced motion (when UISettings.AnimationsEnabled == false) ───────────
    public const int   ReducedMotionFadeMs = 80;

    // ── Open shell dimensions per tab ─────────────────────────────────────────
    // (width, height) in DIPs
    public static readonly (float W, float H) TabSizeHome      = (660f, 370f);
    public static readonly (float W, float H) TabSizeClipboard = (660f, 344f);
    public static readonly (float W, float H) TabSizeShelf     = (660f, 344f);
    public static readonly (float W, float H) TabSizeText      = (660f, 344f);
    public static float ChatHeightDip { get; set; } = 440f;
    public static (float W, float H) TabSizeChat => (660f, ChatHeightDip);
    public static readonly (float W, float H) TabSizeNote      = (660f, 360f);
    public static readonly (float W, float H) TabSizeCalendar  = (660f, 360f);
    public static readonly (float W, float H) TabSizeWeather   = (660f, 360f);
    public static readonly (float W, float H) TabSizeControls  = (660f, 400f);
    public static readonly (float W, float H) CommandBarSize   = (660f, 380f);
    public static float WindowWidthForCommand => MathF.Max(CommandBarSize.W + 40f, 560f);

    /// <summary>
    /// Height of the tallest tab. The island window is sized for it so switching tabs never resizes
    /// the window — a resize makes Windows rebuild the window surface with the opaque theme colour,
    /// which is what turned the light-themed island solid white after the first tab switch.
    /// </summary>
    public static float MaxTabHeight => MathF.Max(ChatHeightDip, TabSizeControls.H);

    public static (float W, float H) TabSize(int tabIndex) => tabIndex switch
    {
        0 => TabSizeHome,
        1 => TabSizeClipboard,
        2 => TabSizeShelf,
        3 => TabSizeText,
        4 => TabSizeChat,
        5 => TabSizeNote,
        6 => TabSizeCalendar,
        7 => TabSizeWeather,
        8 => TabSizeControls,
        _ => TabSizeHome
    };

    // Window is always slightly wider than the tab content for the composition layer
    public static float WindowWidthForTab(int tabIndex)
        => MathF.Max(TabSize(tabIndex).W + 40f, 560f);
}
