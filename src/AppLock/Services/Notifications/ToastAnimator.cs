using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using AppLock.Models;

namespace AppLock.Services.Notifications;

/// <summary>
/// Animates toast notifications (Strategy A: drive the real window).
///
/// Enter:
///   1. Move toast to start pose (off-edge or short-offset), set alpha=0.
///   2. Each frame: lerp position + alpha from start→rest over EnterMs.
///   3. Finish at rest pose with user's configured opacity.
///
/// Exit:
///   The shell often hides the window before our event fires, so exit
///   uses a "ghost" layered window (Strategy B) that is captured from the
///   toast's last visible appearance and animated out independently.
///   If the toast window is still alive we drive it directly first.
/// </summary>
public class ToastAnimator : IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────
    private const uint WS_EX_LAYERED    = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE  = 0x08000000;
    private const uint LWA_ALPHA         = 0x00000002;
    private const int  GWL_EXSTYLE       = -20;
    private const int  GWL_STYLE         = -16;
    private const uint WS_POPUP          = 0x80000000;

    private const int WatchdogMs = 2000;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint hdc, int x, int y);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint STM_SETIMAGE = 0x0172;
    private const uint SS_BITMAP = 0x0000000E;

    public static void DeleteObjectPublic(nint ho)
    {
        if (ho != nint.Zero) DeleteObject(ho);
    }

    /// <summary>
    /// Captures a pixel-perfect snapshot of the toast window while it is resting on screen.
    /// This snapshot is used for the ghost window exit animation so Windows' native slide-out is replaced.
    /// </summary>
    public static nint CaptureWindowSnapshot(HWND hwnd, Rectangle rect)
    {
        nint hBmp = nint.Zero;
        nint screenDC = nint.Zero;
        nint memDC = nint.Zero;

        try
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return nint.Zero;

            screenDC = GetDC(nint.Zero);
            if (screenDC == nint.Zero) return nint.Zero;

            memDC = CreateCompatibleDC(screenDC);
            if (memDC == nint.Zero) return nint.Zero;

            hBmp = CreateCompatibleBitmap(screenDC, rect.Width, rect.Height);
            if (hBmp == nint.Zero) return nint.Zero;

            var oldBmp = SelectObject(memDC, hBmp);

            bool captured = false;
            if (PInvoke.IsWindow(hwnd))
            {
                captured = PrintWindow(hwnd.Value, memDC, 2 /* PW_RENDERFULLCONTENT */);
            }

            if (captured)
            {
                uint centerPixel = GetPixel(memDC, rect.Width / 2, rect.Height / 2);
                if (centerPixel == 0) captured = false;
            }

            if (!captured)
            {
                BitBlt(memDC, 0, 0, rect.Width, rect.Height, screenDC, rect.X, rect.Y, SRCCOPY);
            }

            SelectObject(memDC, oldBmp);
            return hBmp;
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"CaptureWindowSnapshot error: {ex.Message}");
            if (hBmp != nint.Zero) { DeleteObject(hBmp); hBmp = nint.Zero; }
            return nint.Zero;
        }
        finally
        {
            if (memDC != nint.Zero) DeleteDC(memDC);
            if (screenDC != nint.Zero) ReleaseDC(nint.Zero, screenDC);
        }
    }

    /// <summary>
    /// Disables Windows DWM native window transition animations (the default slide from right)
    /// on the toast window so our custom animation loop has complete control without visual conflict.
    /// </summary>
    public static void DisableTransitions(HWND hwnd)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd)) return;
            int forceDisable = 1;
            DwmSetWindowAttribute(hwnd.Value, DWMWA_TRANSITIONS_FORCEDISABLED, ref forceDisable, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Restores Windows native DWM transitions when done.
    /// </summary>
    public static void EnableTransitions(HWND hwnd)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd)) return;
            int forceDisable = 0;
            DwmSetWindowAttribute(hwnd.Value, DWMWA_TRANSITIONS_FORCEDISABLED, ref forceDisable, sizeof(int));
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        nint hWnd, nint hdcDst, ref POINT pptDst, ref SIZE psize,
        nint hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE  { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER  = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    // ── State ─────────────────────────────────────────────────────────────────
    private bool   _disposed;
    private uint   _originalExStyle;
    private bool   _styleModified;
    private System.Threading.Timer? _watchdog;

    // ── Enter animation ───────────────────────────────────────────────────────

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
                $"AnimateEnter: 0x{hwnd.Value:X} dir={settings.EnterFrom} dur={settings.EnterMs}ms");

            if (settings.EnterFrom == "None")
            {
                // Instant position, no animation
                SafeSetWindowPos(hwnd, restRect.X, restRect.Y);
                ApplyFinalOpacity(hwnd, settings.OpacityPercent);
                return;
            }

            // Force Windows to suppress native DWM transitions so our custom enter animation isn't overridden
            DisableTransitions(hwnd);

            var startRect = ToastMotion.GetEnterStartRect(restRect, monitorBounds, settings);

            // Save & modify extended style so we can set alpha
            _originalExStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
            if ((_originalExStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd.Value, GWL_EXSTYLE, _originalExStyle | WS_EX_LAYERED);
                _styleModified = true;
                // Force Windows to apply the new style before we set alpha
                PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            }

            // Move to start pose, fully transparent, before first frame is visible
            SetLayeredWindowAttributes(hwnd.Value, 0, 0, LWA_ALPHA);
            PInvoke.SetWindowPos(hwnd, HWND.Null, startRect.X, startRect.Y, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

            // Brief yield so the compositor processes the move before animation begins
            await Task.Delay(8, ct);

            StartWatchdog(hwnd, settings.OpacityPercent);

            var effectiveDir = ToastMotion.GetEffectiveEnterDirection(restRect, monitorBounds, settings.EnterFrom);
            await RunLoopAsync(hwnd, startRect, restRect, settings.EnterMs,
                settings.Easing, effectiveDir, targetOpacityPercent: settings.OpacityPercent,
                isEnter: true, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"AnimateEnter error: {ex.Message}");
        }
        finally
        {
            StopWatchdog();
            // Always ensure window ends at correct position/opacity
            if (!ct.IsCancellationRequested && PInvoke.IsWindow(hwnd))
            {
                SafeSetWindowPos(hwnd, restRect.X, restRect.Y);
                ApplyFinalOpacity(hwnd, settings.OpacityPercent);
            }
            else
            {
                // Cancelled (toast disappeared) — restore style
                RestoreExStyle(hwnd);
            }
        }
    }

    // ── Exit animation (dedicated STA thread) ────────────────────────────────

    /// <summary>
    /// Animates the toast exit using a lightweight ghost window on an isolated STA background thread.
    /// This completely avoids threadpool task blocking and prevents freezing the main application UI.
    /// </summary>
    public static void AnimateExit(
        nint hSnapshotBmp,
        Rectangle restRect,
        Rectangle monitorBounds,
        NotificationCustomSettings settings,
        CancellationToken ct)
    {
        if (settings.ExitTo == "None")
        {
            if (hSnapshotBmp != nint.Zero) DeleteObject(hSnapshotBmp);
            return;
        }

        var thread = new System.Threading.Thread(() =>
        {
            nint ghost = nint.Zero;
            try
            {
                var exitDir = ToastMotion.GetEffectiveExitDirection(restRect, monitorBounds, settings);
                var endRect = ToastMotion.GetExitEndRect(restRect, monitorBounds, settings);

                const string ClassName = "STATIC";
                uint ghostExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | 0x00000008 /* WS_EX_TOPMOST */;
                uint ghostStyle = WS_POPUP | SS_BITMAP | 0x10000000 /* WS_VISIBLE */;

                ghost = CreateWindowEx(
                    ghostExStyle, ClassName, "", ghostStyle,
                    restRect.X, restRect.Y, restRect.Width, restRect.Height,
                    nint.Zero, nint.Zero, nint.Zero, nint.Zero);

                if (ghost == nint.Zero)
                {
                    LogService.LogAction("notifications", "Ghost window creation failed — skipping exit animation");
                    return;
                }

                var ghostHwnd = new HWND(ghost);
                DisableTransitions(ghostHwnd);

                if (hSnapshotBmp != nint.Zero)
                {
                    SendMessage(ghost, STM_SETIMAGE, nint.Zero, hSnapshotBmp);
                }

                // Make ghost topmost and show at restRect
                PInvoke.SetWindowPos(ghostHwnd, new HWND(new nint(-1)), // HWND_TOPMOST
                    restRect.X, restRect.Y, restRect.Width, restRect.Height,
                    SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

                byte startAlpha = (byte)Math.Max(1, Math.Min(255, settings.OpacityPercent / 100.0 * 255));
                SetLayeredWindowAttributes(ghost, 0, startAlpha, LWA_ALPHA);

                var sw = Stopwatch.StartNew();
                int durationMs = Math.Max(50, settings.ExitMs);

                while (!ct.IsCancellationRequested)
                {
                    double progress = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
                    double eased    = ToastMotion.ApplyEasing(progress, settings.Easing);

                    var curRect = ToastMotion.LerpRect(restRect, endRect, eased);
                    double opacityFraction = GetExitOpacity(eased, exitDir, settings.OpacityPercent);
                    byte alpha = (byte)Math.Max(0, Math.Min(255, opacityFraction * 255));

                    PInvoke.SetWindowPos(ghostHwnd, HWND.Null,
                        curRect.X, curRect.Y, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                    SetLayeredWindowAttributes(ghost, 0, alpha, LWA_ALPHA);

                    if (progress >= 1.0) break;

                    System.Threading.Thread.Sleep(16);
                }

                LogService.LogAction("notifications", $"Exit animation finished cleanly in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                LogService.LogAction("notifications", $"Ghost exit error: {ex.Message}");
            }
            finally
            {
                if (ghost != nint.Zero) DestroyWindow(ghost);
                if (hSnapshotBmp != nint.Zero) DeleteObject(hSnapshotBmp);
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Name = "ToastExitAnimationThread";
        thread.Start();
    }

    // ── Core animation loop ───────────────────────────────────────────────────

    /// <param name="isEnter">true = alpha goes 0→target; false = alpha goes target→0.</param>
    private async Task RunLoopAsync(
        HWND hwnd,
        Rectangle fromRect,
        Rectangle toRect,
        int durationMs,
        string easing,
        string direction,
        int targetOpacityPercent,
        bool isEnter,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            if (!PInvoke.IsWindow(hwnd)) break;

            double progress = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
            double eased    = ToastMotion.ApplyEasing(progress, easing);

            var curRect = ToastMotion.LerpRect(fromRect, toRect, eased);

            // Alpha
            double opacityFraction;
            if (isEnter)
                opacityFraction = GetEnterOpacity(eased, direction, targetOpacityPercent);
            else
                opacityFraction = GetExitOpacity(eased, direction, targetOpacityPercent);

            byte alpha = (byte)Math.Max(0, Math.Min(255, opacityFraction * 255));

            PInvoke.SetWindowPos(hwnd, HWND.Null, curRect.X, curRect.Y, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            SetLayeredWindowAttributes(hwnd.Value, 0, alpha, LWA_ALPHA);

            if (progress >= 1.0) break;

            // ~60 fps pacing with Task.Delay (DwmFlush was blocking the thread)
            await Task.Delay(16, ct).ConfigureAwait(false);
        }
    }

    // ── Opacity helpers ───────────────────────────────────────────────────────

    /// <summary>Alpha for enter frame: starts at 0, ends at targetOpacityPercent/100.</summary>
    private static double GetEnterOpacity(double eased, string direction, int targetOpacityPct)
    {
        double target = targetOpacityPct / 100.0;

        // Fade-only: opacity directly tracks progress
        if (direction is "Fade" or "ScaleFade")
            return eased * target;

        // Slide: quick fade-in during first 40% of travel, then full opacity
        double fadeIn = Math.Min(1.0, eased / 0.4);
        return fadeIn * target;
    }

    /// <summary>Alpha for exit frame: starts at targetOpacityPercent/100, ends at 0.</summary>
    private static double GetExitOpacity(double eased, string direction, int targetOpacityPct)
    {
        double start = targetOpacityPct / 100.0;

        // Fade-only: opacity directly tracks progress (inverted)
        if (direction is "Fade" or "ScaleFade")
            return start * (1.0 - eased);

        // Slide: fade out during last 40% of travel
        double fadeOut = Math.Min(1.0, (eased - 0.6) / 0.4);
        fadeOut = Math.Max(0, fadeOut);
        return start * (1.0 - fadeOut);
    }

    // ── Utility helpers ───────────────────────────────────────────────────────

    private static void SafeSetWindowPos(HWND hwnd, int x, int y)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd)) return;
            PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        catch { }
    }

    private void ApplyFinalOpacity(HWND hwnd, int opacityPercent)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd)) return;

            if (opacityPercent >= 100)
            {
                // Remove layered style so the window renders normally
                RestoreExStyle(hwnd);
                return;
            }

            var exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            }

            byte alpha = (byte)Math.Max(1, Math.Min(255, opacityPercent / 100.0 * 255));
            SetLayeredWindowAttributes(hwnd.Value, 0, alpha, LWA_ALPHA);
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"ApplyFinalOpacity error: {ex.Message}");
        }
    }

    private void RestoreExStyle(HWND hwnd)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd)) return;
            if (!_styleModified || _originalExStyle == 0) return;

            SetWindowLong(hwnd.Value, GWL_EXSTYLE, _originalExStyle);
            PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            _styleModified = false;
        }
        catch { }
    }

    // ── Watchdog ──────────────────────────────────────────────────────────────

    private void StartWatchdog(HWND hwnd, int targetOpacity)
    {
        StopWatchdog();
        _watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                LogService.LogAction("notifications",
                    $"Watchdog: restoring 0x{hwnd.Value:X}");
                if (PInvoke.IsWindow(hwnd))
                    ApplyFinalOpacity(hwnd, targetOpacity);
            }
            catch { }
        }, null, WatchdogMs, Timeout.Infinite);
    }

    private void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        StopWatchdog();
        _disposed = true;
    }
}
