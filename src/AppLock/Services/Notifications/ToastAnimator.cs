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
///   The ghost runs its own Win32 message pump on an STA thread.
/// </summary>
public class ToastAnimator : IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────
    private const uint WS_EX_LAYERED    = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE  = 0x08000000;
    private const uint WS_EX_TOPMOST     = 0x00000008;
    private const uint LWA_ALPHA         = 0x00000002;
    private const int  GWL_EXSTYLE       = -20;
    private const uint WS_POPUP          = 0x80000000;
    private const uint WS_VISIBLE        = 0x10000000;

    private const int WatchdogMs = 2000;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint hdc, int x, int y);

    private const uint SRCCOPY = 0x00CC0020;

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
                // Fallback: grab from screen at the toast's location
                BitBlt(memDC, 0, 0, rect.Width, rect.Height, screenDC, rect.X, rect.Y, SRCCOPY);
            }

            SelectObject(memDC, oldBmp);
            LogService.LogAction("notifications",
                $"CaptureWindowSnapshot for 0x{hwnd.Value:X} completed (pwCaptured={captured}): {rect.Width}x{rect.Height}, handle=0x{hBmp:X}");
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint    cbSize;
        public uint    style;
        public nint    lpfnWndProc;
        public int     cbClsExtra;
        public int     cbWndExtra;
        public nint    hInstance;
        public nint    hIcon;
        public nint    hCursor;
        public nint    hbrBackground;
        public string? lpszMenuName;
        public string  lpszClassName;
        public nint    hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint uMsg, nint wParam, nint lParam);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint hWnd, nint hdcDst, ref POINT pptDst, ref SIZE psize,
        nint hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE  { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    private const uint ULW_ALPHA     = 0x00000002;
    private const byte AC_SRC_OVER  = 0x00;
    private const uint WM_DESTROY   = 0x0002;
    private const uint WM_QUIT      = 0x0012;

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

    /// <summary>
    /// Animates the REAL toast window out (Strategy A).
    /// Lerps position from restRect toward endRect while fading opacity from targetOpacity to 0.
    /// Runs directly before Windows shell can start its native slide-out, providing a 100% native-rendered,
    /// artifact-free exit animation with zero black borders or stuttering.
    /// </summary>
    public async Task AnimateExitRealWindowAsync(
        HWND hwnd,
        Rectangle restRect,
        Rectangle monitorBounds,
        NotificationCustomSettings settings,
        CancellationToken ct)
    {
        if (settings.ExitTo == "None") return;

        try
        {
            var exitDir   = ToastMotion.GetEffectiveExitDirection(restRect, monitorBounds, settings);
            var endRect   = ToastMotion.GetExitEndRect(restRect, monitorBounds, settings);
            int durationMs  = Math.Max(50, settings.ExitMs);

            LogService.LogAction("notifications",
                $"AnimateExitRealWindow: 0x{hwnd.Value:X} dir={exitDir} dur={durationMs}ms");

            DisableTransitions(hwnd);

            // Ensure window has WS_EX_LAYERED so we can modulate its alpha down to 0
            var exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            }

            await RunLoopAsync(hwnd, restRect, endRect, durationMs,
                settings.Easing, exitDir, targetOpacityPercent: settings.OpacityPercent,
                isEnter: false, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"AnimateExitRealWindow error: {ex.Message}");
        }
    }

    // ── Exit animation (dedicated STA thread with message pump) ───────────────

    // We keep a static WNDPROC delegate alive so it doesn't get GC'd
    private delegate nint WndProcDelegate(nint hWnd, uint uMsg, nint wParam, nint lParam);
    private static readonly WndProcDelegate _ghostWndProc = GhostWndProc;
    private static nint GhostWndProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
        => DefWindowProcW(hWnd, uMsg, wParam, lParam);

    private static readonly object _classRegLock = new();
    private static bool _classRegistered;
    private const string GhostClassName = "AppLockToastGhost";

    private static void EnsureGhostClassRegistered()
    {
        if (_classRegistered) return;
        lock (_classRegLock)
        {
            if (_classRegistered) return;

            var hInstance = GetModuleHandleW(null);
            var wc = new WNDCLASSEXW
            {
                cbSize        = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style         = 0,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_ghostWndProc),
                cbClsExtra    = 0,
                cbWndExtra    = 0,
                hInstance     = hInstance,
                hIcon         = nint.Zero,
                hCursor       = nint.Zero,
                hbrBackground = nint.Zero,
                lpszMenuName  = null,
                lpszClassName = GhostClassName,
                hIconSm       = nint.Zero
            };

            ushort atom = RegisterClassExW(ref wc);
            int err = Marshal.GetLastWin32Error();
            if (atom == 0 && err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
            {
                LogService.LogAction("notifications", $"RegisterClassExW failed: err={err}");
            }
            else
            {
                LogService.LogAction("notifications", $"Ghost window class registered successfully (atom=0x{atom:X})");
            }
            _classRegistered = true;
        }
    }

    /// <summary>
    /// Animates the toast exit using a layered ghost window driven by UpdateLayeredWindow.
    /// Runs on an isolated STA thread with its own Win32 message pump.
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

        // Capture these on calling thread before handing off
        var exitDir   = ToastMotion.GetEffectiveExitDirection(restRect, monitorBounds, settings);
        var endRect   = ToastMotion.GetExitEndRect(restRect, monitorBounds, settings);
        int durationMs  = Math.Max(50, settings.ExitMs);
        string easing   = settings.Easing;
        int opacityPercent = settings.OpacityPercent;
        int w = restRect.Width;
        int h = restRect.Height;

        var thread = new Thread(() =>
        {
            nint ghost    = nint.Zero;
            nint screenDC = nint.Zero;
            nint memDC    = nint.Zero;
            nint drawBmp  = nint.Zero;
            nint oldBmp   = nint.Zero;

            try
            {
                EnsureGhostClassRegistered();

                // Create a WS_EX_LAYERED window using our custom class
                uint ghostExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                uint ghostStyle   = WS_POPUP;

                var hInst = GetModuleHandleW(null);
                ghost = CreateWindowExW(
                    ghostExStyle, GhostClassName, "", ghostStyle,
                    restRect.X, restRect.Y, w, h,
                    nint.Zero, nint.Zero, hInst, nint.Zero);

                if (ghost == nint.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    LogService.LogAction("notifications", $"Ghost CreateWindowExW failed: err={err} — skipping exit animation");
                    return;
                }

                // Suppress DWM transitions on the ghost too
                DisableTransitions(new HWND(ghost));

                // Build a memory DC with the snapshot bitmap
                screenDC = GetDC(nint.Zero);
                memDC    = CreateCompatibleDC(screenDC);
                drawBmp  = CreateCompatibleBitmap(screenDC, w, h);
                oldBmp   = SelectObject(memDC, drawBmp);

                // Copy snapshot into drawBmp via memDC
                if (hSnapshotBmp != nint.Zero)
                {
                    nint snapDC  = CreateCompatibleDC(screenDC);
                    nint prevBmp = SelectObject(snapDC, hSnapshotBmp);
                    BitBlt(memDC, 0, 0, w, h, snapDC, 0, 0, SRCCOPY);
                    SelectObject(snapDC, prevBmp);
                    DeleteDC(snapDC);
                    LogService.LogAction("notifications", "Ghost using snapshot bitmap");
                }
                else
                {
                    // Fallback: capture current screen at restRect
                    BitBlt(memDC, 0, 0, w, h, screenDC, restRect.X, restRect.Y, SRCCOPY);
                    LogService.LogAction("notifications", "Ghost using screen capture fallback");
                }

                // Initial alpha for the ghost
                byte startAlpha = (byte)Math.Max(1, Math.Min(255, opacityPercent / 100.0 * 255));

                var ptDst  = new POINT { x = restRect.X, y = restRect.Y };
                var size   = new SIZE  { cx = w, cy = h };
                var ptSrc  = new POINT { x = 0, y = 0 };
                var blend  = new BLENDFUNCTION
                {
                    BlendOp             = AC_SRC_OVER,
                    BlendFlags          = 0,
                    SourceConstantAlpha = startAlpha,
                    AlphaFormat         = 0  // no per-pixel alpha in our bitmap
                };

                // Set the layered content BEFORE showing the window (ULW_ALPHA | ULW_COLORKEY makes RGB 0,0,0 transparent)
                bool ulwOk = UpdateLayeredWindow(ghost, screenDC, ref ptDst, ref size, memDC, ref ptSrc, 0 /* black is transparent */, ref blend, ULW_ALPHA | 0x00000001 /* ULW_COLORKEY */);
                if (!ulwOk)
                    LogService.LogAction("notifications", $"ULW initial call failed: err={Marshal.GetLastWin32Error()}");

                // Now make the ghost visible (topmost, no activation)
                PInvoke.SetWindowPos(new HWND(ghost), new HWND(new nint(-1) /* HWND_TOPMOST */),
                    restRect.X, restRect.Y, w, h,
                    SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

                LogService.LogAction("notifications",
                    $"Ghost shown at ({restRect.X},{restRect.Y}) {w}x{h}, alpha={startAlpha}, ulw={ulwOk}");

                // Animation loop — drive via timer/sleep, peek message to stay responsive
                var sw = Stopwatch.StartNew();

                while (!ct.IsCancellationRequested)
                {
                    double progress = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
                    double eased    = ToastMotion.ApplyEasing(progress, easing);

                    var curRect = ToastMotion.LerpRect(restRect, endRect, eased);
                    double opacityFraction = GetExitOpacity(eased, exitDir, opacityPercent);
                    byte alpha = (byte)Math.Max(0, Math.Min(255, opacityFraction * 255));

                    var pDst = new POINT { x = curRect.X, y = curRect.Y };
                    blend.SourceConstantAlpha = alpha;
                    UpdateLayeredWindow(ghost, screenDC, ref pDst, ref size, memDC, ref ptSrc, 0, ref blend, ULW_ALPHA | 0x00000001 /* ULW_COLORKEY */);

                    if (progress >= 1.0) break;

                    // Pump messages so the window stays responsive
                    while (PeekMessage(out var msg, ghost, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }

                    // Sleep ~16ms for ~60fps
                    Thread.Sleep(16);
                }

                LogService.LogAction("notifications", $"Exit animation finished cleanly in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                LogService.LogAction("notifications", $"Ghost exit error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // Hide immediately so there's no leftover ghost on screen
                if (ghost != nint.Zero)
                {
                    PInvoke.ShowWindow(new HWND(ghost), SHOW_WINDOW_CMD.SW_HIDE);
                }

                if (oldBmp  != nint.Zero && memDC != nint.Zero) SelectObject(memDC, oldBmp);
                if (drawBmp != nint.Zero) DeleteObject(drawBmp);
                if (memDC   != nint.Zero) DeleteDC(memDC);
                if (screenDC != nint.Zero) ReleaseDC(nint.Zero, screenDC);
                if (hSnapshotBmp != nint.Zero) DeleteObject(hSnapshotBmp);

                // DestroyWindow must be called from the same thread that created the window
                if (ghost != nint.Zero) DestroyWindow(ghost);
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
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

            // ~60 fps pacing
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
