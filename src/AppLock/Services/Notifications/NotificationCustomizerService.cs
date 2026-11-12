using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AppLock.Services.Notifications;

/// <summary>
/// Main service that customizes Windows notification toast positions and animations.
/// 
/// Lifecycle per toast:
///   SHOW  → StartEnterAnimation (hold loop included at end)
///   HIDE  → StartExitAnimation  (uses its OWN cts, not the hold-loop one)
///
/// The _managedToasts dictionary maps HWND → the hold-loop CTS.
/// A separate _exitCts dictionary holds a CTS for each in-progress exit animation.
/// </summary>
public class NotificationCustomizerService : IDisposable
{
    private readonly NotificationCustomSettings _settings;
    private ToastWinEventHook? _hook;
    private readonly ToastClickThroughManager _clickThroughManager = new();

    // Tracks active toasts: HWND → CTS for the enter-animation + hold loop
    private readonly Dictionary<nint, CancellationTokenSource> _managedToasts = new();
    // Tracks toasts currently executing enter animation (so OnToastMoved does not interfere)
    private readonly HashSet<nint> _animatingToasts = new();
    // Tracks in-flight exit animations: HWND → CTS (independent lifetime)
    private readonly Dictionary<nint, CancellationTokenSource> _exitAnimations = new();
    // Remembers the confirmed rest rect per HWND so exit uses the right start position
    private readonly Dictionary<nint, System.Drawing.Rectangle> _restRects = new();
    // Remembers the captured snapshot bitmap per HWND for ghost exit animation
    private readonly Dictionary<nint, nint> _snapshots = new();

    private readonly object _lock = new();
    private bool _disposed;

    // Duration in ms to keep asserting position after enter animation completes.
    private const int HoldLoopMs    = 800;
    private const int HoldIntervalMs = 16;  // ~60 fps

    public bool IsRunning => _hook != null;

    public NotificationCustomizerService(NotificationCustomSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (!_settings.Enabled || _hook != null)
        {
            LogService.LogAction("notifications",
                $"Start called but not starting: Enabled={_settings.Enabled}, Hook exists={_hook != null}");
            return;
        }

        try
        {
            _hook = new ToastWinEventHook();
            _hook.ToastShown  += OnToastShown;
            _hook.ToastMoved  += OnToastMoved;
            _hook.ToastHidden += OnToastHidden;
            _hook.Start();

            LogService.LogAction("notifications",
                $"NotificationCustomizerService started. Anchor: {_settings.Anchor}");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications",
                $"Failed to start NotificationCustomizerService: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_hook == null)
            return;

        try
        {
            _hook.ToastShown  -= OnToastShown;
            _hook.ToastMoved  -= OnToastMoved;
            _hook.ToastHidden -= OnToastHidden;
            _hook.Stop();
            _hook.Dispose();
            _hook = null;

            CancelAll();

            LogService.LogAction("notifications", "NotificationCustomizerService stopped");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications",
                $"Error stopping NotificationCustomizerService: {ex.Message}");
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnToastShown(object? sender, HWND hwnd)
    {
        try
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                // Cancel any existing hold loop for this HWND (recycled window)
                if (_managedToasts.TryGetValue(hwnd.Value, out var old))
                {
                    old.Cancel();
                    old.Dispose();
                }
                // Also cancel any stale exit animation for this HWND
                if (_exitAnimations.TryGetValue(hwnd.Value, out var oldExit))
                {
                    oldExit.Cancel();
                    oldExit.Dispose();
                    _exitAnimations.Remove(hwnd.Value);
                }
                // Clean up any stale snapshot
                if (_snapshots.Remove(hwnd.Value, out var oldSnap) && oldSnap != nint.Zero)
                {
                    ToastAnimator.DeleteObjectPublic(oldSnap);
                }

                cts = new CancellationTokenSource();
                _managedToasts[hwnd.Value] = cts;
            }

            LogService.LogAction("notifications",
                $"Toast shown: 0x{hwnd.Value:X}, Anchor: {_settings.Anchor}");

            var currentSettings = ServiceContainer.Settings.NotificationCustom;

            var monitor     = ToastPositioner.GetTargetMonitor(hwnd, currentSettings);
            var monitorInfo = GetMonitorInfo(monitor);
            var monitorBounds = new System.Drawing.Rectangle(
                monitorInfo.rcMonitor.left, monitorInfo.rcMonitor.top,
                monitorInfo.rcMonitor.right  - monitorInfo.rcMonitor.left,
                monitorInfo.rcMonitor.bottom - monitorInfo.rcMonitor.top);

            var targetRect = ToastPositioner.GetTargetRect(hwnd, currentSettings);
            var restRect   = new System.Drawing.Rectangle(
                targetRect.left, targetRect.top,
                targetRect.right  - targetRect.left,
                targetRect.bottom - targetRect.top);

            // Remember rest rect for exit animation
            lock (_lock) { _restRects[hwnd.Value] = restRect; }

            StartToastLifecycle(hwnd, restRect, monitorBounds, currentSettings, cts.Token);

            _clickThroughManager.ApplyClickThrough(hwnd, currentSettings, cts.Token);
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications",
                $"Error on toast show: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void OnToastMoved(object? sender, HWND hwnd)
    {
        // Do not touch window while enter animation is in progress
        bool isAnimating;
        lock (_lock) { isAnimating = _animatingToasts.Contains(hwnd.Value); }
        if (isAnimating)
            return;

        // If an exit animation is already running, do NOT fight it
        bool hasExitRunning;
        lock (_lock) { hasExitRunning = _exitAnimations.ContainsKey(hwnd.Value); }
        if (hasExitRunning)
            return;

        bool isManaged;
        System.Drawing.Rectangle restRect;
        lock (_lock)
        {
            isManaged = _managedToasts.ContainsKey(hwnd.Value);
            _restRects.TryGetValue(hwnd.Value, out restRect);
        }
        if (!isManaged)
            return;

        // Check if the shell is attempting to dismiss the toast by moving it away from restRect
        if (restRect != default && Windows.Win32.PInvoke.GetWindowRect(hwnd, out var wr))
        {
            int dx = Math.Abs(wr.left - restRect.X);
            int dy = Math.Abs(wr.top - restRect.Y);
            if (dx > 10 || dy > 10)
            {
                // Shell is moving the window away -> this is a dismissal gesture or animation!
                LogService.LogAction("notifications",
                    $"Shell dismissal detected via movement (dx={dx}, dy={dy}) for 0x{hwnd.Value:X}");
                OnToastHidden(sender, hwnd);
                return;
            }
        }

        ApplyPosition(hwnd);
    }

    private void OnToastHidden(object? sender, HWND hwnd)
    {
        try
        {
            CancellationTokenSource? holdCts = null;
            nint snapshot = nint.Zero;

            lock (_lock)
            {
                _animatingToasts.Remove(hwnd.Value);

                if (!_managedToasts.Remove(hwnd.Value, out holdCts))
                {
                    // Already dismissed or our real-window exit animation already completed!
                    _hook?.ForgetToast(hwnd);
                    return;
                }

                _hook?.ForgetToast(hwnd);
                _snapshots.Remove(hwnd.Value, out snapshot);
                _restRects.Remove(hwnd.Value);
            }

            holdCts?.Cancel();
            holdCts?.Dispose();

            // Hide and move the real window offscreen immediately so the shell's native slide-out is completely invisible
            try
            {
                if (Windows.Win32.PInvoke.IsWindow(hwnd))
                {
                    Windows.Win32.PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                    Windows.Win32.PInvoke.SetWindowPos(hwnd, HWND.Null, -10000, -10000, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                        SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                        SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW);
                }
            }
            catch { }

            if (snapshot != nint.Zero)
            {
                ToastAnimator.DeleteObjectPublic(snapshot);
            }

            // Remove click-through
            _clickThroughManager.RemoveClickThrough(hwnd);

            LogService.LogAction("notifications", $"Toast hidden: 0x{hwnd.Value:X}");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications",
                $"Error handling toast hide: {ex.Message}");
        }
    }

    // ── Animation helpers ─────────────────────────────────────────────────────

    private void StartToastLifecycle(
        HWND hwnd,
        System.Drawing.Rectangle restRect,
        System.Drawing.Rectangle monitorBounds,
        NotificationCustomSettings settings,
        CancellationToken ct)
    {
        Task.Run(async () =>
        {
            var animator = new ToastAnimator();
            lock (_lock) { _animatingToasts.Add(hwnd.Value); }
            try
            {
                // 1. Enter animation (Strategy A: drive the real window)
                await animator.AnimateEnterAsync(hwnd, restRect, monitorBounds, settings, ct);

                lock (_lock) { _animatingToasts.Remove(hwnd.Value); }

                if (ct.IsCancellationRequested) return;

                // 2. Hold at restRect for the display duration, while monitoring hover
                int displayDurationMs = GetSystemMessageDurationMs();
                int holdMs = Math.Max(1000, displayDurationMs - settings.ExitMs - 150);

                var deadline = Environment.TickCount64 + holdMs;
                while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
                {
                    await Task.Delay(HoldIntervalMs, ct).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) break;
                    if (!Windows.Win32.PInvoke.IsWindow(hwnd)) break;

                    // If user is hovering over the toast, extend the hold deadline so it doesn't dismiss while being read
                    if (Windows.Win32.PInvoke.GetCursorPos(out var pt))
                    {
                        if (restRect.Contains(pt.X, pt.Y))
                        {
                            deadline = Environment.TickCount64 + 2000;
                        }
                    }

                    // Re-assert rest position during hold
                    Windows.Win32.PInvoke.SetWindowPos(hwnd, HWND.Null, restRect.X, restRect.Y, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                        SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                }

                if (ct.IsCancellationRequested) return;
                if (!Windows.Win32.PInvoke.IsWindow(hwnd)) return;

                // 3. Exit animation on the REAL WINDOW (Strategy A)
                // Runs BEFORE Windows initiates its native slide-out, providing 100% native rendering without black borders!
                LogService.LogAction("notifications", $"Starting real-window exit animation for 0x{hwnd.Value:X}");
                lock (_lock) { _animatingToasts.Add(hwnd.Value); }
                await animator.AnimateExitRealWindowAsync(hwnd, restRect, monitorBounds, settings, ct);

                // 4. Hide real window and move far offscreen so Windows dismisses it silently offscreen
                if (Windows.Win32.PInvoke.IsWindow(hwnd))
                {
                    Windows.Win32.PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                    Windows.Win32.PInvoke.SetWindowPos(hwnd, HWND.Null, -10000, -10000, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                        SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                        SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW);
                }

                LogService.LogAction("notifications", $"Real-window exit animation finished for 0x{hwnd.Value:X}");

                // Remove from managed toasts so OnToastHidden knows the exit animation has already completed
                lock (_lock)
                {
                    _managedToasts.Remove(hwnd.Value);
                    if (_snapshots.Remove(hwnd.Value, out var s) && s != nint.Zero)
                        ToastAnimator.DeleteObjectPublic(s);
                    _restRects.Remove(hwnd.Value);
                }
            }
            catch (OperationCanceledException) { /* expected when dismissed early by user */ }
            catch (Exception ex)
            {
                LogService.LogAction("notifications", $"Toast lifecycle error: {ex.Message}");
            }
            finally
            {
                lock (_lock) { _animatingToasts.Remove(hwnd.Value); }
                animator.Dispose();
            }
        }, ct);
    }

    private static int GetSystemMessageDurationMs()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility");
            if (key?.GetValue("MessageDuration") is int seconds && seconds > 0)
            {
                return seconds * 1000;
            }
        }
        catch { }
        return 5000;
    }

    // ── Live settings reapplication ───────────────────────────────────────────

    /// <summary>
    /// Re-applies the current opacity from settings to every visible managed toast.
    /// Call this when OpacityPercent changes so the effect is immediate.
    /// </summary>
    public void ReapplyOpacityToActiveToasts()
    {
        List<HWND> active;
        lock (_lock)
        {
            if (_managedToasts.Count == 0) return;
            active = new List<HWND>(_managedToasts.Count);
            foreach (var key in _managedToasts.Keys)
                active.Add(new HWND((nint)key));
        }

        var currentSettings = ServiceContainer.Settings.NotificationCustom;
        foreach (var hwnd in active)
        {
            if (!Windows.Win32.PInvoke.IsWindow(hwnd)) continue;
            if (currentSettings.OpacityPercent >= 100)
                ToastOpacityApplier.RemoveOpacity(hwnd);
            else
                ToastOpacityApplier.ApplyOpacity(hwnd, currentSettings);
        }

        LogService.LogAction("notifications",
            $"ReapplyOpacity: updated {active.Count} active toast(s) → {currentSettings.OpacityPercent}%");
    }

    /// <summary>
    /// Re-applies the target anchor/position to every currently-managed toast.
    /// Call this when Anchor or Custom position changes so the effect is immediate in realtime.
    /// </summary>
    public void ReapplyPositionToActiveToasts()
    {
        List<HWND> active;
        lock (_lock)
        {
            if (_managedToasts.Count == 0) return;
            active = new List<HWND>(_managedToasts.Count);
            foreach (var key in _managedToasts.Keys)
                active.Add(new HWND((nint)key));
        }

        var currentSettings = ServiceContainer.Settings.NotificationCustom;
        foreach (var hwnd in active)
        {
            if (!Windows.Win32.PInvoke.IsWindow(hwnd)) continue;

            var target = ToastPositioner.GetTargetRect(hwnd, currentSettings);
            var newRestRect = new System.Drawing.Rectangle(target.left, target.top,
                target.right - target.left, target.bottom - target.top);

            lock (_lock) { _restRects[hwnd.Value] = newRestRect; }

            Windows.Win32.PInvoke.SetWindowPos(hwnd, HWND.Null,
                newRestRect.X, newRestRect.Y, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

            // Update snapshot at new position
            var newSnap = ToastAnimator.CaptureWindowSnapshot(hwnd, newRestRect);
            lock (_lock)
            {
                if (_snapshots.Remove(hwnd.Value, out var prev) && prev != nint.Zero)
                    ToastAnimator.DeleteObjectPublic(prev);
                _snapshots[hwnd.Value] = newSnap;
            }
        }

        LogService.LogAction("notifications", $"ReapplyPosition: updated {active.Count} active toast(s)");
    }

    // ── Position helpers ──────────────────────────────────────────────────────

    private void ApplyPosition(HWND hwnd)
    {
        try
        {
            if (!Windows.Win32.PInvoke.IsWindow(hwnd)) return;
            var currentSettings = ServiceContainer.Settings.NotificationCustom;
            var target = ToastPositioner.GetTargetRect(hwnd, currentSettings);

            unsafe
            {
                Windows.Win32.PInvoke.SetWindowPos(hwnd, HWND.Null,
                    target.left, target.top, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING);
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"ApplyPosition error: {ex.Message}");
        }
    }

    private void CancelAll()
    {
        lock (_lock)
        {
            foreach (var cts in _managedToasts.Values)
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            _managedToasts.Clear();
            _animatingToasts.Clear();

            foreach (var cts in _exitAnimations.Values)
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            _exitAnimations.Clear();
            _restRects.Clear();

            foreach (var snap in _snapshots.Values)
            {
                if (snap != nint.Zero) ToastAnimator.DeleteObjectPublic(snap);
            }
            _snapshots.Clear();
        }
    }

    private static Windows.Win32.Graphics.Gdi.MONITORINFO GetMonitorInfo(
        Windows.Win32.Graphics.Gdi.HMONITOR monitor)
    {
        var info = new Windows.Win32.Graphics.Gdi.MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<Windows.Win32.Graphics.Gdi.MONITORINFO>()
        };
        Windows.Win32.PInvoke.GetMonitorInfo(monitor, ref info);
        return info;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _clickThroughManager.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
