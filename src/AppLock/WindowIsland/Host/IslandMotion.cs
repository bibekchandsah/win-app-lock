using System;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// All Window Island animation timing constants in one place.
/// Tune here; nothing else hard-codes durations or spring parameters.
/// </summary>
public static class IslandMotion
{
    // ── Shape springs ──────────────────────────────────────────────────────────
    public const float OpenDampingRatio  = 0.72f;
    public const int   OpenPeriodMs      = 320;

    public const float CloseDampingRatio = 0.90f;
    public const int   ClosePeriodMs     = 260;

    public const float CardInDampingRatio  = 0.70f;
    public const int   CardInPeriodMs      = 300;

    // ── Tab switch ─────────────────────────────────────────────────────────────
    public const int   TabSwitchMs         = 160;    // crossfade + 8 DIP slide
    public const float TabSlideDip         = 8f;

    // ── Content fades ──────────────────────────────────────────────────────────
    public const int   ContentFadeInMs     = 140;
    public const int   ContentFadeInDelayMs = 80;
    public const int   ContentFadeOutMs    = 90;

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
    public static readonly (float W, float H) TabSizeWeather   = (660f, 360f);
    public static readonly (float W, float H) TabSizeControls  = (660f, 400f);
    public static readonly (float W, float H) CommandBarSize   = (660f, 380f);
    public static float WindowWidthForCommand => MathF.Max(CommandBarSize.W + 40f, 560f);

    public static (float W, float H) TabSize(int tabIndex) => tabIndex switch
    {
        0 => TabSizeHome,
        1 => TabSizeClipboard,
        2 => TabSizeShelf,
        3 => TabSizeText,
        4 => TabSizeChat,
        5 => TabSizeNote,
        6 => TabSizeWeather,
        7 => TabSizeControls,
        _ => TabSizeHome
    };

    // Window is always slightly wider than the tab content for the composition layer
    public static float WindowWidthForTab(int tabIndex)
        => MathF.Max(TabSize(tabIndex).W + 40f, 560f);
}
