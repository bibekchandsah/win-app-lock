using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using AppLock.Models;

namespace AppLock.WindowIsland.Services;

public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

public struct POINT
{
    public int X;
    public int Y;
}

public enum QUERY_USER_NOTIFICATION_STATE
{
    QUNS_NOT_PRESENT = 1,
    QUNS_BUSY = 2,
    QUNS_RUNNING_D3D_FULL_SCREEN = 3,
    QUNS_PRESENTATION_MODE = 4,
    QUNS_ACCEPTS_NOTIFICATIONS = 5,
    QUNS_QUIET_TIME = 6,
    QUNS_APP = 7
}

public sealed record VisibilityEvaluation(bool ShouldHide, string Reason);

public sealed class VisibilityContext
{
    public bool IsFullScreen { get; set; }
    public bool IsDesktop { get; set; }
    public bool IsWindowOverlapping { get; set; }
    public string? ForegroundProcessName { get; set; }
    public bool IsTemporaryRevealActive { get; set; }
    public bool IsEdgeDwellActive { get; set; }
}

/// <summary>
/// Milestone 15: Visibility Rules Service for Window Island.
/// Handles fullscreen detection (SHQueryUserNotificationState + window rects),
/// auto-hide rules, desktop foreground bypass, chosen apps filtering,
/// and 30 Hz top-edge cursor dwell polling when hidden.
/// </summary>
public sealed class WindowIslandVisibilityService : IDisposable
{
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly WindowIslandSettings _settings;
#pragma warning disable CS0649
    private readonly System.Timers.Timer? _evalTimer;
    private readonly System.Timers.Timer? _dwellTimer;
#pragma warning restore CS0649
    private readonly Stopwatch _dwellStopwatch = new();

    private bool _isCurrentlyHidden;
    private bool _edgeDwellTriggered;
    private bool _temporaryRevealActive;
    private DateTime _temporaryRevealExpiry = DateTime.MinValue;

    public event Action<bool, string>? VisibilityChanged;
    public event Action? EdgeDwellTriggered;

    public bool IsCurrentlyHidden => _isCurrentlyHidden;

    /// <summary>
    /// Production constructor — pass a DispatcherQueue to marshal callbacks onto the UI thread.
    /// </summary>
#if !TEST
    public WindowIslandVisibilityService(WindowIslandSettings settings,
        Microsoft.UI.Dispatching.DispatcherQueue? dq = null)
    {
        _settings = settings;

        if (dq != null)
        {
            // 500 ms background eval (fullscreen / foreground changes)
            _evalTimer          = new System.Timers.Timer(500);
            _evalTimer.Elapsed += (_, _) => dq.TryEnqueue(() => EvaluateState());
            _evalTimer.AutoReset = true;

            // 30 Hz cursor-dwell poll — only runs while hidden
            _dwellTimer          = new System.Timers.Timer(33);
            _dwellTimer.Elapsed += (_, _) => dq.TryEnqueue(() => OnDwellTimerTick());
            _dwellTimer.AutoReset = true;
        }
    }
#else
    // Test-only constructor — no dispatcher needed; timers are null
    public WindowIslandVisibilityService(WindowIslandSettings settings)
    {
        _settings = settings;
    }
#endif

    public void Start()
    {
        _evalTimer?.Start();
        EvaluateState();
    }

    public void Stop()
    {
        _evalTimer?.Stop();
        _dwellTimer?.Stop();
        _dwellStopwatch.Reset();
    }

    public void Dispose()
    {
        Stop();
        _evalTimer?.Dispose();
        _dwellTimer?.Dispose();
    }

    /// <summary>
    /// Temporarily brings the island into view (e.g. for card notifications, reminders, timer alerts).
    /// </summary>
    public void RequestTemporaryReveal(int durationMs = 4000)
    {
        _temporaryRevealActive = true;
        _temporaryRevealExpiry = DateTime.UtcNow.AddMilliseconds(durationMs);
        EvaluateState();
    }

    /// <summary>
    /// Resets edge dwell state when the island closes or cursor leaves.
    /// </summary>
    public void ResetEdgeDwell()
    {
        _edgeDwellTriggered = false;
        _dwellStopwatch.Reset();
    }

    // ── Pure Logic (Unit-Testable) ──────────────────────────────────────────

    public static VisibilityEvaluation Evaluate(WindowIslandSettings settings, VisibilityContext ctx)
    {
        // 1. Temporary reveal overrides all hiding rules (for alerts, reminders, cards)
        if (ctx.IsTemporaryRevealActive)
        {
            return new VisibilityEvaluation(false, "TemporaryReveal");
        }

        // 2. Edge dwell brings the island out over full-screen apps or auto-hide
        if (ctx.IsEdgeDwellActive)
        {
            return new VisibilityEvaluation(false, "EdgeDwell");
        }

        // 3. Full-screen app rule
        if (settings.HideInFullScreen && ctx.IsFullScreen)
        {
            return new VisibilityEvaluation(true, "FullScreen");
        }

        // 4. Chosen apps rule
        if (!string.Equals(settings.ChosenAppsMode, "Disabled", StringComparison.OrdinalIgnoreCase) &&
            settings.ChosenApps != null && settings.ChosenApps.Count > 0)
        {
            bool inList = !string.IsNullOrEmpty(ctx.ForegroundProcessName) &&
                          settings.ChosenApps.Any(a => string.Equals(a, ctx.ForegroundProcessName, StringComparison.OrdinalIgnoreCase));

            if (string.Equals(settings.ChosenAppsMode, "Hide", StringComparison.OrdinalIgnoreCase) && inList)
            {
                return new VisibilityEvaluation(true, "ChosenAppHide");
            }
            if (string.Equals(settings.ChosenAppsMode, "Keep", StringComparison.OrdinalIgnoreCase) && !inList)
            {
                return new VisibilityEvaluation(true, "ChosenAppKeep");
            }
        }

        // 5. Auto-hide rules
        if (settings.AutoHide)
        {
            // Stay in sight on desktop if enabled
            if (settings.AutoHideStayOnDesktop && ctx.IsDesktop)
            {
                return new VisibilityEvaluation(false, "DesktopVisible");
            }

            // Hide only while a window is under the island
            if (settings.AutoHideOnlyWhenOverlapped)
            {
                if (ctx.IsWindowOverlapping)
                {
                    return new VisibilityEvaluation(true, "WindowOverlapping");
                }
                return new VisibilityEvaluation(false, "NotOverlapping");
            }

            // Default auto-hide hides until needed
            return new VisibilityEvaluation(true, "AutoHide");
        }

        return new VisibilityEvaluation(false, "Visible");
    }

    public static bool IsWindowOverlapping(RECT windowRect, RECT islandRect)
    {
        return !(windowRect.Right <= islandRect.Left ||
                 windowRect.Left >= islandRect.Right ||
                 windowRect.Bottom <= islandRect.Top ||
                 windowRect.Top >= islandRect.Bottom);
    }

    public static bool IsDesktopWindow(string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFullScreenState(QUERY_USER_NOTIFICATION_STATE quns)
    {
        return quns == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
               quns == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
    }

    // ── Live Evaluation ──────────────────────────────────────────────────────

    public void EvaluateState(RECT? currentIslandScreenRect = null, RECT? currentMonitorRect = null)
    {
        // Check temporary reveal expiry
        if (_temporaryRevealActive && DateTime.UtcNow > _temporaryRevealExpiry)
        {
            _temporaryRevealActive = false;
        }

        var ctx = GatherLiveContext(currentIslandScreenRect, currentMonitorRect);
        ctx.IsTemporaryRevealActive = _temporaryRevealActive;
        ctx.IsEdgeDwellActive = _edgeDwellTriggered;

        var result = Evaluate(_settings, ctx);

        bool shouldHide = result.ShouldHide;
        if (shouldHide != _isCurrentlyHidden)
        {
            _isCurrentlyHidden = shouldHide;

            if (_isCurrentlyHidden)
            {
                // Start 30 Hz dwell timer while hidden
                if (_settings.EdgeDwellEnabled)
                {
                    _dwellStopwatch.Reset();
                    _dwellTimer?.Start();
                }
            }
            else
            {
                // Stop 30 Hz dwell timer while visible to preserve CPU
                _dwellTimer?.Stop();
                _dwellStopwatch.Reset();
            }

            VisibilityChanged?.Invoke(_isCurrentlyHidden, result.Reason);
        }
    }

    private VisibilityContext GatherLiveContext(RECT? islandRect, RECT? monitorRect)
    {
        var ctx = new VisibilityContext();

        // 1. Shell user notification state
        int hr = SHQueryUserNotificationState(out var quns);
        if (hr == 0 && IsFullScreenState(quns))
        {
            ctx.IsFullScreen = true;
        }

        // 2. Foreground window inspection
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var sb = new StringBuilder(128);
            GetClassName(fg, sb, sb.Capacity);
            string className = sb.ToString();

            ctx.IsDesktop = IsDesktopWindow(className);

            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    ctx.ForegroundProcessName = proc.ProcessName;
                }
                catch { }
            }

            // Window covering monitor check for fullscreen apps
            if (GetWindowRect(fg, out var fgRect) && !ctx.IsDesktop)
            {
                if (monitorRect.HasValue)
                {
                    var mon = monitorRect.Value;
                    if (fgRect.Left <= mon.Left && fgRect.Top <= mon.Top &&
                        fgRect.Right >= mon.Right && fgRect.Bottom >= mon.Bottom)
                    {
                        ctx.IsFullScreen = true;
                    }
                }

                if (islandRect.HasValue)
                {
                    ctx.IsWindowOverlapping = IsWindowOverlapping(fgRect, islandRect.Value);
                }
            }
        }

        return ctx;
    }

    private void OnDwellTimerTick()
    {
        if (!_isCurrentlyHidden || !_settings.EdgeDwellEnabled)
        {
            _dwellTimer?.Stop();
            _dwellStopwatch.Reset();
            return;
        }

        if (GetCursorPos(out var pt))
        {
            // Cursor at top edge of any monitor (Y <= 2)
            if (pt.Y <= 2)
            {
                if (!_dwellStopwatch.IsRunning)
                {
                    _dwellStopwatch.Restart();
                }
                else if (_dwellStopwatch.ElapsedMilliseconds >= _settings.EdgeDwellMs)
                {
                    _edgeDwellTriggered = true;
                    _dwellStopwatch.Reset();
                    _dwellTimer?.Stop();
                    EdgeDwellTriggered?.Invoke();
                    EvaluateState();
                }
            }
            else
            {
                // Cursor moved away from top edge
                _dwellStopwatch.Reset();
            }
        }
    }
}
