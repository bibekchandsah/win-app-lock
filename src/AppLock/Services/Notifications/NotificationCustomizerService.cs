using System;
using System.Collections.Generic;
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
                // Cancel any previous hold loop for this HWND (re-used window)
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

            // Apply immediately, then start the hold loop on a background thread
            ApplyPosition(hwnd);
            StartHoldLoop(hwnd, cts.Token);
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
    /// Runs a tight re-assert loop for <see cref="HoldLoopMs"/> milliseconds
    /// on a background thread to beat the shell's native slide-in animation.
    /// </summary>
    private void StartHoldLoop(HWND hwnd, CancellationToken ct)
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

                    ApplyPosition(hwnd);
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

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
