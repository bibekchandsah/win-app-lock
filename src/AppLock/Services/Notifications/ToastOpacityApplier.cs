using System;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32;
using AppLock.Models;

namespace AppLock.Services.Notifications;

/// <summary>
/// Applies opacity/transparency to toast notification windows using WS_EX_LAYERED.
/// </summary>
public static class ToastOpacityApplier
{
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>Applies opacity from settings to a toast window.</summary>
    public static void ApplyOpacity(HWND toastHwnd, NotificationCustomSettings settings)
    {
        try
        {
            if (!PInvoke.IsWindow(toastHwnd))
                return;

            if (settings.OpacityPercent >= 100)
            {
                RemoveOpacity(toastHwnd);
                return;
            }

            var exStyle = GetWindowLong(toastHwnd.Value, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                exStyle |= WS_EX_LAYERED;
                SetWindowLong(toastHwnd.Value, GWL_EXSTYLE, exStyle);
                PInvoke.SetWindowPos(toastHwnd, HWND.Null, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            }

            byte alpha = (byte)Math.Max(1, Math.Min(255, (settings.OpacityPercent / 100.0) * 255));
            SetLayeredWindowAttributes(toastHwnd.Value, 0, alpha, LWA_ALPHA);

            LogService.LogAction("notifications",
                $"ApplyOpacity: 0x{toastHwnd.Value:X} → {settings.OpacityPercent}% (alpha={alpha})");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"ApplyOpacity error: {ex.Message}");
        }
    }

    /// <summary>Removes layered transparency, restoring toast to fully opaque.</summary>
    public static void RemoveOpacity(HWND toastHwnd)
    {
        try
        {
            if (!PInvoke.IsWindow(toastHwnd))
                return;

            var exStyle = GetWindowLong(toastHwnd.Value, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
                return;

            // First set alpha to 255 so the window is fully visible before stripping the flag
            SetLayeredWindowAttributes(toastHwnd.Value, 0, 255, LWA_ALPHA);

            exStyle &= ~WS_EX_LAYERED;
            SetWindowLong(toastHwnd.Value, GWL_EXSTYLE, exStyle);
            PInvoke.SetWindowPos(toastHwnd, HWND.Null, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

            LogService.LogAction("notifications", $"RemoveOpacity: 0x{toastHwnd.Value:X} restored to full opacity");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"RemoveOpacity error: {ex.Message}");
        }
    }
}
