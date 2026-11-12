using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using AppLock.Models;

namespace AppLock.Services.Notifications;

/// <summary>
/// Animates toast notifications using Strategy A (drive the real window).
/// Runs on a dedicated high-priority thread paced with DwmFlush.
/// </summary>
public class ToastAnimator : IDisposable
{
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const int GWL_EXSTYLE = -20;
    
    // Watchdog: never leave a toast transparent longer than 2 seconds
    private const int WatchdogTimeoutMs = 2000;
    
    private bool _disposed;
    private uint _originalExStyle;
    private bool _styleModified;
    private System.Threading.Timer? _watchdogTimer;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

    /// <summary>
    /// Animate a toast window entering.
    /// </summary>
    public async Task AnimateEnterAsync(
        HWND hwnd,
        Rectangle restRect,
        Rectangle monitorBounds,
        NotificationCustomSettings settings,
        CancellationToken ct)
    {
        try
        {
            LogService.LogAction("notifications", 
                $"AnimateEnter: HWND=0x{hwnd.Value:X}, Direction={settings.EnterFrom}, Duration={settings.EnterMs}ms");

            // Animation disabled
            if (settings.EnterFrom == "None")
            {
                // Just position it
                PInvoke.SetWindowPos(hwnd, HWND.Null, restRect.X, restRect.Y, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE | 
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | 
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                
                // Apply target opacity
                ApplyOpacity(hwnd, settings.OpacityPercent);
                return;
            }

            // Compute start position
            var startRect = ToastMotion.GetEnterStartRect(restRect, monitorBounds, settings);
            
            LogService.LogAction("notifications", 
                $"Enter: Start={startRect.X},{startRect.Y}, Rest={restRect.X},{restRect.Y}");

            // Save original extended style
            _originalExStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
            _styleModified = false;

            // Make window layered for alpha control
            if ((_originalExStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd.Value, GWL_EXSTYLE, _originalExStyle | WS_EX_LAYERED);
                _styleModified = true;
            }

            // Move to start position and make fully transparent
            PInvoke.SetWindowPos(hwnd, HWND.Null, startRect.X, startRect.Y, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE | 
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | 
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            
            SetLayeredWindowAttributes(hwnd.Value, 0, 0, LWA_ALPHA);

            // Small delay to ensure window is positioned before animation starts
            await Task.Delay(10, ct);

            // Start watchdog timer to restore window if animation gets stuck
            StartWatchdog(hwnd, settings.OpacityPercent);

            // Run animation
            await RunAnimationLoopAsync(hwnd, startRect, restRect, settings.EnterMs, 
                settings.Easing, settings.EnterFrom, settings.OpacityPercent, ct);

            LogService.LogAction("notifications", "Enter animation complete");
        }
        catch (OperationCanceledException)
        {
            LogService.LogAction("notifications", "Enter animation cancelled");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Enter animation error: {ex.Message}");
        }
        finally
        {
            // Stop watchdog and ensure window is left at rest position with target opacity
            StopWatchdog();
            RestoreWindow(hwnd, settings.OpacityPercent);
        }
    }

    /// <summary>
    /// Animate a toast window exiting.
    /// </summary>
    public async Task AnimateExitAsync(
        HWND hwnd,
        Rectangle restRect,
        Rectangle monitorBounds,
        NotificationCustomSettings settings,
        CancellationToken ct)
    {
        try
        {
            LogService.LogAction("notifications", 
                $"AnimateExit: HWND=0x{hwnd.Value:X}, Direction={settings.ExitTo}, Duration={settings.ExitMs}ms");

            // Animation disabled
            if (settings.ExitTo == "None")
            {
                // Just hide it
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                return;
            }

            // Compute end position
            var endRect = ToastMotion.GetExitEndRect(restRect, monitorBounds, settings);
            
            LogService.LogAction("notifications", 
                $"Exit: Rest={restRect.X},{restRect.Y}, End={endRect.X},{endRect.Y}");

            // Ensure window is layered
            var exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }

            // Run animation
            var exitDirection = ToastMotion.GetEffectiveExitDirection(restRect, monitorBounds, settings);
            
            // Start watchdog
            StartWatchdog(hwnd, 0); // Exit to transparent
            
            await RunAnimationLoopAsync(hwnd, restRect, endRect, settings.ExitMs, 
                settings.Easing, exitDirection, 0, ct);

            LogService.LogAction("notifications", "Exit animation complete");
        }
        catch (OperationCanceledException)
        {
            LogService.LogAction("notifications", "Exit animation cancelled");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Exit animation error: {ex.Message}");
        }
        finally
        {
            StopWatchdog();
        }
    }

    /// <summary>
    /// Core animation loop paced with DwmFlush or high-resolution timer.
    /// </summary>
    private async Task RunAnimationLoopAsync(
        HWND hwnd,
        Rectangle fromRect,
        Rectangle toRect,
        int durationMs,
        string easing,
        string direction,
        int targetOpacityPercent,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var targetMs = durationMs;

        while (!ct.IsCancellationRequested)
        {
            var elapsedMs = sw.ElapsedMilliseconds;
            
            // Calculate linear progress
            double linearProgress = Math.Min(1.0, elapsedMs / (double)targetMs);
            
            // Apply easing
            double easedProgress = ToastMotion.ApplyEasing(linearProgress, easing);
            
            // Interpolate position
            var currentRect = ToastMotion.LerpRect(fromRect, toRect, easedProgress);
            
            // Interpolate opacity
            double opacity = ToastMotion.GetOpacityForProgress(easedProgress, direction, targetOpacityPercent);
            byte alpha = (byte)Math.Max(0, Math.Min(255, opacity * 255));
            
            // Apply position
            if (PInvoke.IsWindow(hwnd))
            {
                PInvoke.SetWindowPos(hwnd, HWND.Null, currentRect.X, currentRect.Y, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE | 
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | 
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                
                SetLayeredWindowAttributes(hwnd.Value, 0, alpha, LWA_ALPHA);
            }
            else
            {
                break; // Window destroyed
            }
            
            // Done?
            if (linearProgress >= 1.0)
                break;
            
            // Wait for next frame (~60fps)
            try
            {
                // Try to use DwmFlush for vsync-paced animation
                DwmFlush();
            }
            catch
            {
                // Fallback to 16ms delay if DwmFlush fails
                await Task.Delay(16, ct);
            }
        }
    }

    /// <summary>
    /// Apply opacity to a window without animation.
    /// </summary>
    private void ApplyOpacity(HWND hwnd, int opacityPercent)
    {
        if (opacityPercent >= 100)
            return;

        var exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
        if ((exStyle & WS_EX_LAYERED) == 0)
        {
            SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        }

        byte alpha = (byte)((opacityPercent / 100.0) * 255);
        SetLayeredWindowAttributes(hwnd.Value, 0, alpha, LWA_ALPHA);
    }

    /// <summary>
    /// Restore window to normal state after animation.
    /// </summary>
    private void RestoreWindow(HWND hwnd, int targetOpacityPercent)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd))
                return;

            // Apply final opacity
            ApplyOpacity(hwnd, targetOpacityPercent);

            // Restore original style if we modified it
            if (_styleModified && _originalExStyle != 0)
            {
                SetWindowLong(hwnd.Value, GWL_EXSTYLE, _originalExStyle);
                _styleModified = false;
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"RestoreWindow error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        
        StopWatchdog();
        _disposed = true;
    }

    /// <summary>
    /// Start a watchdog timer that restores window if animation gets stuck.
    /// </summary>
    private void StartWatchdog(HWND hwnd, int targetOpacity)
    {
        StopWatchdog();
        
        _watchdogTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                LogService.LogAction("notifications", 
                    $"Watchdog triggered for 0x{hwnd.Value:X}, restoring window");
                RestoreWindow(hwnd, targetOpacity);
            }
            catch (Exception ex)
            {
                LogService.LogAction("notifications", $"Watchdog error: {ex.Message}");
            }
        }, null, WatchdogTimeoutMs, Timeout.Infinite);
    }

    /// <summary>
    /// Stop the watchdog timer.
    /// </summary>
    private void StopWatchdog()
    {
        if (_watchdogTimer != null)
        {
            _watchdogTimer.Dispose();
            _watchdogTimer = null;
        }
    }
}
