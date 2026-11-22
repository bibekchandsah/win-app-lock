using System.Collections.Generic;
using AppLock.Models;
using AppLock.WindowIsland.Services;
using Xunit;

namespace AppLock.Tests.WindowIsland;

/// <summary>
/// Milestone 15 — Pure-logic unit tests for WindowIslandVisibilityService.Evaluate()
/// and its static helper methods.  No WinUI runtime required.
/// </summary>
public sealed class M15_VisibilityTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static WindowIslandSettings DefaultSettings() => new()
    {
        HideInFullScreen           = true,
        EdgeDwellEnabled           = true,
        EdgeDwellMs                = 500,
        AutoHide                   = false,
        AutoHideStayOnDesktop      = true,
        AutoHideOnlyWhenOverlapped = false,
        ChosenAppsMode             = "Disabled",
        ChosenApps                 = new List<string>(),
    };

    private static VisibilityContext DefaultCtx() => new()
    {
        IsFullScreen             = false,
        IsDesktop                = false,
        IsWindowOverlapping      = false,
        ForegroundProcessName    = null,
        IsTemporaryRevealActive  = false,
        IsEdgeDwellActive        = false,
    };

    // ── Evaluate: priority overrides ────────────────────────────────────────────

    [Fact]
    public void Evaluate_TemporaryReveal_OverridesAllHidingRules()
    {
        var s   = DefaultSettings();
        s.HideInFullScreen = true;
        s.AutoHide         = true;

        var ctx = DefaultCtx();
        ctx.IsFullScreen            = true;
        ctx.IsTemporaryRevealActive = true;

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.False(result.ShouldHide);
        Assert.Equal("TemporaryReveal", result.Reason);
    }

    [Fact]
    public void Evaluate_EdgeDwell_OverridesFullScreen()
    {
        var s   = DefaultSettings();
        var ctx = DefaultCtx();
        ctx.IsFullScreen    = true;
        ctx.IsEdgeDwellActive = true;

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.False(result.ShouldHide);
        Assert.Equal("EdgeDwell", result.Reason);
    }

    // ── Evaluate: fullscreen ────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_FullScreen_HidesWhenSettingEnabled()
    {
        var s   = DefaultSettings();
        var ctx = DefaultCtx();
        ctx.IsFullScreen = true;

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.True(result.ShouldHide);
        Assert.Equal("FullScreen", result.Reason);
    }

    [Fact]
    public void Evaluate_FullScreen_NotHiddenWhenSettingDisabled()
    {
        var s   = DefaultSettings();
        s.HideInFullScreen = false;
        var ctx = DefaultCtx();
        ctx.IsFullScreen = true;

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.False(result.ShouldHide);
    }

    // ── Evaluate: chosen apps ───────────────────────────────────────────────────

    [Fact]
    public void Evaluate_ChosenApps_HideMode_HidesWhenForegroundInList()
    {
        var s = DefaultSettings();
        s.ChosenAppsMode = "Hide";
        s.ChosenApps     = new List<string> { "notepad", "mspaint" };

        var ctx = DefaultCtx();
        ctx.ForegroundProcessName = "notepad";

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.True(result.ShouldHide);
        Assert.Equal("ChosenAppHide", result.Reason);
    }

    [Fact]
    public void Evaluate_ChosenApps_HideMode_NotHiddenWhenForegroundNotInList()
    {
        var s = DefaultSettings();
        s.ChosenAppsMode = "Hide";
        s.ChosenApps     = new List<string> { "notepad" };

        var ctx = DefaultCtx();
        ctx.ForegroundProcessName = "chrome";

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.False(result.ShouldHide);
    }

    [Fact]
    public void Evaluate_ChosenApps_KeepMode_HidesWhenForegroundNotInList()
    {
        var s = DefaultSettings();
        s.ChosenAppsMode = "Keep";
        s.ChosenApps     = new List<string> { "notepad" };

        var ctx = DefaultCtx();
        ctx.ForegroundProcessName = "chrome";

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.True(result.ShouldHide);
        Assert.Equal("ChosenAppKeep", result.Reason);
    }

    [Fact]
    public void Evaluate_ChosenApps_KeepMode_NotHiddenWhenForegroundInList()
    {
        var s = DefaultSettings();
        s.ChosenAppsMode = "Keep";
        s.ChosenApps     = new List<string> { "notepad" };

        var ctx = DefaultCtx();
        ctx.ForegroundProcessName = "notepad";

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.False(result.ShouldHide);
    }

    [Fact]
    public void Evaluate_ChosenApps_CaseInsensitiveMatch()
    {
        var s = DefaultSettings();
        s.ChosenAppsMode = "Hide";
        s.ChosenApps     = new List<string> { "Notepad" };

        var ctx = DefaultCtx();
        ctx.ForegroundProcessName = "NOTEPAD";

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.True(result.ShouldHide);
    }

    // ── Evaluate: auto-hide ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_AutoHide_DefaultHides()
    {
        var s = DefaultSettings();
        s.AutoHide                   = true;
        s.AutoHideStayOnDesktop      = false;
        s.AutoHideOnlyWhenOverlapped = false;

        var ctx = DefaultCtx();
        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.True(result.ShouldHide);
        Assert.Equal("AutoHide", result.Reason);
    }

    [Fact]
    public void Evaluate_AutoHide_StayOnDesktop_NotHiddenWhenDesktopIsForeground()
    {
        var s = DefaultSettings();
        s.AutoHide              = true;
        s.AutoHideStayOnDesktop = true;

        var ctx = DefaultCtx();
        ctx.IsDesktop = true;

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.False(result.ShouldHide);
        Assert.Equal("DesktopVisible", result.Reason);
    }

    [Fact]
    public void Evaluate_AutoHide_OnlyWhenOverlapped_HidesWhenOverlapping()
    {
        var s = DefaultSettings();
        s.AutoHide                   = true;
        s.AutoHideStayOnDesktop      = false;
        s.AutoHideOnlyWhenOverlapped = true;

        var ctx = DefaultCtx();
        ctx.IsWindowOverlapping = true;

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.True(result.ShouldHide);
        Assert.Equal("WindowOverlapping", result.Reason);
    }

    [Fact]
    public void Evaluate_AutoHide_OnlyWhenOverlapped_NotHiddenWhenNotOverlapping()
    {
        var s = DefaultSettings();
        s.AutoHide                   = true;
        s.AutoHideStayOnDesktop      = false;
        s.AutoHideOnlyWhenOverlapped = true;

        var ctx = DefaultCtx();
        ctx.IsWindowOverlapping = false;

        var result = WindowIslandVisibilityService.Evaluate(s, ctx);
        Assert.False(result.ShouldHide);
        Assert.Equal("NotOverlapping", result.Reason);
    }

    [Fact]
    public void Evaluate_NothingActive_IsVisible()
    {
        var result = WindowIslandVisibilityService.Evaluate(DefaultSettings(), DefaultCtx());
        Assert.False(result.ShouldHide);
        Assert.Equal("Visible", result.Reason);
    }

    // ── IsWindowOverlapping ─────────────────────────────────────────────────────

    [Fact]
    public void IsWindowOverlapping_ReturnsTrueWhenRectsCross()
    {
        var island = new RECT(100, 0, 500, 40);
        var window = new RECT(200, 0, 400, 300);
        Assert.True(WindowIslandVisibilityService.IsWindowOverlapping(window, island));
    }

    [Fact]
    public void IsWindowOverlapping_ReturnsFalseWhenWindowIsBelow()
    {
        var island = new RECT(100, 0, 500, 40);
        var window = new RECT(100, 50, 400, 500);
        Assert.False(WindowIslandVisibilityService.IsWindowOverlapping(window, island));
    }

    [Fact]
    public void IsWindowOverlapping_ReturnsFalseWhenWindowIsToTheLeft()
    {
        var island = new RECT(400, 0, 800, 40);
        var window = new RECT(0, 0, 399, 1080);
        Assert.False(WindowIslandVisibilityService.IsWindowOverlapping(window, island));
    }

    [Fact]
    public void IsWindowOverlapping_ReturnsTrueWhenWindowCoversIsland()
    {
        var island = new RECT(100, 0, 500, 40);
        var window = new RECT(0, 0, 1920, 1080);
        Assert.True(WindowIslandVisibilityService.IsWindowOverlapping(window, island));
    }

    // ── IsDesktopWindow ─────────────────────────────────────────────────────────

    [Fact]
    public void IsDesktopWindow_Progman_ReturnsTrue()
        => Assert.True(WindowIslandVisibilityService.IsDesktopWindow("Progman"));

    [Fact]
    public void IsDesktopWindow_WorkerW_ReturnsTrue()
        => Assert.True(WindowIslandVisibilityService.IsDesktopWindow("WorkerW"));

    [Fact]
    public void IsDesktopWindow_ShellTrayWnd_ReturnsTrue()
        => Assert.True(WindowIslandVisibilityService.IsDesktopWindow("Shell_TrayWnd"));

    [Fact]
    public void IsDesktopWindow_Chrome_ReturnsFalse()
        => Assert.False(WindowIslandVisibilityService.IsDesktopWindow("Chrome_WidgetWin_1"));

    [Fact]
    public void IsDesktopWindow_Null_ReturnsFalse()
        => Assert.False(WindowIslandVisibilityService.IsDesktopWindow(null));

    [Fact]
    public void IsDesktopWindow_Empty_ReturnsFalse()
        => Assert.False(WindowIslandVisibilityService.IsDesktopWindow(string.Empty));

    // ── IsFullScreenState ───────────────────────────────────────────────────────

    [Fact]
    public void IsFullScreenState_D3DFullScreen_ReturnsTrue()
        => Assert.True(WindowIslandVisibilityService.IsFullScreenState(
               QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN));

    [Fact]
    public void IsFullScreenState_PresentationMode_ReturnsTrue()
        => Assert.True(WindowIslandVisibilityService.IsFullScreenState(
               QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE));

    [Fact]
    public void IsFullScreenState_AcceptsNotifications_ReturnsFalse()
        => Assert.False(WindowIslandVisibilityService.IsFullScreenState(
               QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS));

    [Fact]
    public void IsFullScreenState_Busy_ReturnsFalse()
        => Assert.False(WindowIslandVisibilityService.IsFullScreenState(
               QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY));
}
