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
/// Main service that customizes Windows notification toast positions.
/// Uses a short "hold" loop after first detection to beat the shell's
/// native slide-in animation (which keeps resetting the window position).
/// </summary>
public class NotificationCustomizerService : IDisposable
{
    private readonly NotificationCustomSettings _settings;
    private ToastWinEventHook? _hook;
    private readonly ToastClickThroughManager _clickThroughManager = new();

    // Tracks active toasts and their hold-loop cancellation tokens
    private readonly Dictionary<nint, CancellationTokenSource> _managedToasts = new();
    private readonly object _lock = new();
    private bool _disposed;

    // Duration in ms to keep asserting position after the toast appears.
    // The shell's native slide-in animation is typically 300–400 ms.
    private const int HoldLoopMs = 800;
    // How often (ms) to re-assert position during the hold loop.
    private const int HoldIntervalMs = 16; // ~60 fps

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

            CancelAllHoldLoops();

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
                // Cancel any previous animation/hold for this HWND (re-used window)
                if (_managedToasts.TryGetValue(hwnd.Value, out var old))
                {
                    old.Cancel();
                    old.Dispose();
                }
                cts = new CancellationTokenSource();
                _managedToasts[hwnd.Value] = cts;
            }

            LogService.LogAction("notifications",
                $"Toast shown: 0x{hwnd.Value:X}, Anchor: {_settings.Anchor}");

            // Read fresh settings to get latest values
            var currentSettings = ServiceContainer.Settings.NotificationCustom;

            // Get monitor bounds for animation
            var monitor = ToastPositioner.GetTargetMonitor(hwnd, currentSettings);
            var monitorInfo = GetMonitorInfo(monitor);
            var monitorBounds = new System.Drawing.Rectangle(
                monitorInfo.rcMonitor.left,
                monitorInfo.rcMonitor.top,
                monitorInfo.rcMonitor.right - monitorInfo.rcMonitor.left,
                monitorInfo.rcMonitor.bottom - monitorInfo.rcMonitor.top
            );

            // Calculate rest position (where the toast will sit)
            var targetRect = ToastPositioner.GetTargetRect(hwnd, currentSettings);
            var restRect = new System.Drawing.Rectangle(
                targetRect.left,
                targetRect.top,
                targetRect.right - targetRect.left,
                targetRect.bottom - targetRect.top
            );

            // Start animation on background thread
            StartEnterAnimation(hwnd, restRect, monitorBounds, currentSettings, cts.Token);

            // Apply click-through if enabled
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
        // The hold loop already re-asserts position every 16 ms, so we only
        // need to react to moves that happen AFTER the hold loop has ended
        // (e.g. the shell nudges the window during dismiss).
        bool isManaged;
        lock (_lock)
        {
            isManaged = _managedToasts.ContainsKey(hwnd.Value);
        }

        if (isManaged)
        {
            ApplyPosition(hwnd);
        }
    }

    private void OnToastHidden(object? sender, HWND hwnd)
    {
        try
        {
            CancellationTokenSource? cts = null;
            lock (_lock)
            {
                if (_managedToasts.Remove(hwnd.Value, out cts))
                {
                    // Also clear from the hook's seen-set so a re-used HWND is
                    // treated as a brand-new toast next time it shows.
                    _hook?.ForgetToast(hwnd);
                }
            }

            // Start exit animation before cancelling the hold loop
            if (cts != null && !cts.IsCancellationRequested)
            {
                StartExitAnimation(hwnd, cts.Token);
            }

            // Remove click-through
            _clickThroughManager.RemoveClickThrough(hwnd);

            // Cancel and dispose after starting exit animation
            cts?.Cancel();
            cts?.Dispose();

            LogService.LogAction("notifications", $"Toast hidden: 0x{hwnd.Value:X}");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications",
                $"Error handling toast hide: {ex.Message}");
        }
    }

    // ── Animation and positioning ────────────────────────────────────────────

    /// <summary>
    /// Start the enter animation for a toast.
    /// </summary>
    private void StartEnterAnimation(
        HWND hwnd,
        System.Drawing.Rectangle restRect,
        System.Drawing.Rectangle monitorBounds,
        NotificationCustomSettings settings,
        CancellationToken ct)
    {
        Task.Run(async () =>
        {
            try
            {
                var animator = new ToastAnimator();
                await animator.AnimateEnterAsync(hwnd, restRect, monitorBounds, settings, ct);
                animator.Dispose();

                // After animation, hold position
                StartHoldLoop(hwnd, restRect, ct);
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                LogService.LogAction("notifications",
                    $"Enter animation task error: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// Start the exit animation for a toast.
    /// </summary>
    private void StartExitAnimation(
        HWND hwnd,
        CancellationToken ct)
    {
        Task.Run(async () =>
        {
            try
            {
                // Read current settings
                var currentSettings = ServiceContainer.Settings.NotificationCustom;

                // Get window rect to determine rest position
                if (!Windows.Win32.PInvoke.GetWindowRect(hwnd, out var rect))
                    return;

                var restRect = new System.Drawing.Rectangle(
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top
                );

                // Get monitor bounds
                var monitor = ToastPositioner.GetTargetMonitor(hwnd, currentSettings);
                var monitorInfo = GetMonitorInfo(monitor);
                var monitorBounds = new System.Drawing.Rectangle(
                    monitorInfo.rcMonitor.left,
                    monitorInfo.rcMonitor.top,
                    monitorInfo.rcMonitor.right - monitorInfo.rcMonitor.left,
                    monitorInfo.rcMonitor.bottom - monitorInfo.rcMonitor.top
                );

                // Animate exit
                var animator = new ToastAnimator();
                await animator.AnimateExitAsync(hwnd, restRect, monitorBounds, currentSettings, ct);
                animator.Dispose();

                LogService.LogAction("notifications", $"Exit animation complete for 0x{hwnd.Value:X}");
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                LogService.LogAction("notifications",
                    $"Exit animation task error: {ex.Message}");
            }
        }, ct);
    }

    // ── Live settings reapplication ───────────────────────────────────────────

    /// <summary>
    /// Re-applies the current opacity from settings to every toast window that
    /// is currently visible and managed by this service.  Call this whenever
    /// OpacityPercent changes in settings so the effect is instant without
    /// needing to toggle the service off/on.
    /// </summary>
    public void ReapplyOpacityToActiveToasts()
    {
        // Snapshot the managed HWNDs under the lock, then work outside it
        List<HWND> active;
        lock (_lock)
        {
            if (_managedToasts.Count == 0)
                return;
            active = new List<HWND>(_managedToasts.Count);
            foreach (var key in _managedToasts.Keys)
                active.Add(new HWND((nint)key));
        }

        var currentSettings = ServiceContainer.Settings.NotificationCustom;
        foreach (var hwnd in active)
        {
            if (!Windows.Win32.PInvoke.IsWindow(hwnd))
                continue;

            if (currentSettings.OpacityPercent >= 100)
            {
                // Restore: remove WS_EX_LAYERED so the window is fully opaque again
                ToastOpacityApplier.RemoveOpacity(hwnd);
            }
            else
            {
                ToastOpacityApplier.ApplyOpacity(hwnd, currentSettings);
            }
        }

        LogService.LogAction("notifications",
            $"ReapplyOpacity: updated {active.Count} active toast(s) → {currentSettings.OpacityPercent}%");
    }

    // ── Position helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Move the toast window to the configured anchor position.
    /// SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE so we never steal focus
    /// or change z-order, and never resize the toast.
    /// </summary>
    private void ApplyPosition(HWND hwnd)
    {
        try
        {
            if (!Windows.Win32.PInvoke.IsWindow(hwnd))
                return;

            var target = ToastPositioner.GetTargetRect(hwnd, _settings);

            unsafe
            {
                Windows.Win32.PInvoke.SetWindowPos(
                    hwnd,
                    HWND.Null,
                    target.left,
                    target.top,
                    0,
                    0,
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING // suppress WM_WINDOWPOSCHANGING on the target
                );
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"ApplyPosition error: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a hold loop to keep the toast at rest position after animation completes.
    /// </summary>
    private void StartHoldLoop(HWND hwnd, System.Drawing.Rectangle restRect, CancellationToken ct)
    {
        Task.Run(async () =>
        {
            try
            {
                var deadline = Environment.TickCount64 + HoldLoopMs;
                while (!ct.IsCancellationRequested &&
                       Environment.TickCount64 < deadline)
                {
                    await Task.Delay(HoldIntervalMs, ct).ConfigureAwait(false);

                    if (ct.IsCancellationRequested)
                        break;

                    if (!Windows.Win32.PInvoke.IsWindow(hwnd))
                        break;

                    // Just hold position, opacity was set by animator
                    Windows.Win32.PInvoke.SetWindowPos(hwnd, HWND.Null, restRect.X, restRect.Y, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE | 
                        SET_WINDOW_POS_FLAGS.SWP_NOZORDER | 
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                }

                LogService.LogAction("notifications",
                    $"Hold loop ended for 0x{hwnd.Value:X}");
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                LogService.LogAction("notifications",
                    $"Hold loop error: {ex.Message}");
            }
        }, ct);
    }

    private void CancelAllHoldLoops()
    {
        lock (_lock)
        {
            foreach (var cts in _managedToasts.Values)
            {
                try { cts.Cancel(); cts.Dispose(); }
                catch { /* ignore */ }
            }
            _managedToasts.Clear();
        }
    }

    private static Windows.Win32.Graphics.Gdi.MONITORINFO GetMonitorInfo(Windows.Win32.Graphics.Gdi.HMONITOR monitor)
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
