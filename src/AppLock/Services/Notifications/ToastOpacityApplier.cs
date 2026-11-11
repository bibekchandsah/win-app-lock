using System;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32;
using AppLock.Models;

namespace AppLock.Services.Notifications;

/// <summary>
/// Applies opacity/transparency to toast notification windows.
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

    /// <summary>
    /// Applies opacity to a toast window.
    /// </summary>
    /// <param name="toastHwnd">The toast window handle</param>
    /// <param name="settings">Notification settings containing OpacityPercent</param>
    public static void ApplyOpacity(HWND toastHwnd, NotificationCustomSettings settings)
    {
        try
        {
            // Only apply if opacity is less than 100%
            if (settings.OpacityPercent >= 100)
            {
                return;
            }

            // Get current window style
            var exStyle = GetWindowLong(toastHwnd.Value, GWL_EXSTYLE);

            // Add WS_EX_LAYERED if not already present
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                exStyle |= WS_EX_LAYERED;
                SetWindowLong(toastHwnd.Value, GWL_EXSTYLE, exStyle);
            }

            // Calculate alpha value (0-255, where 255 is fully opaque)
            byte alpha = (byte)((settings.OpacityPercent / 100.0) * 255);

            // Apply the alpha transparency
            SetLayeredWindowAttributes(toastHwnd.Value, 0, alpha, LWA_ALPHA);
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Failed to apply opacity: {ex.Message}");
        }
    }
}
