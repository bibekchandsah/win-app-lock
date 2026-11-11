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
            LogService.LogAction("notifications", 
                $"ApplyOpacity called: HWND=0x{toastHwnd.Value:X}, OpacityPercent={settings.OpacityPercent}");

            // Only apply if opacity is less than 100%
            if (settings.OpacityPercent >= 100)
            {
                LogService.LogAction("notifications", "Opacity is 100%, skipping (leaving window normal)");
                return;
            }

            // Validate window exists
            if (!PInvoke.IsWindow(toastHwnd))
            {
                LogService.LogAction("notifications", "Window is invalid, skipping opacity");
                return;
            }

            // Get current window style
            var exStyle = GetWindowLong(toastHwnd.Value, GWL_EXSTYLE);
            LogService.LogAction("notifications", 
                $"Current exStyle: 0x{exStyle:X8}, Has WS_EX_LAYERED: {(exStyle & WS_EX_LAYERED) != 0}");

            // Add WS_EX_LAYERED if not already present
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                exStyle |= WS_EX_LAYERED;
                var result = SetWindowLong(toastHwnd.Value, GWL_EXSTYLE, exStyle);
                LogService.LogAction("notifications", 
                    $"Added WS_EX_LAYERED, SetWindowLong result: {result}, New exStyle: 0x{exStyle:X8}");
                
                // Force window to redraw with new style
                PInvoke.SetWindowPos(toastHwnd, HWND.Null, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | 
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE | 
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | 
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            }

            // Calculate alpha value (0-255, where 255 is fully opaque)
            byte alpha = (byte)Math.Max(1, Math.Min(255, (settings.OpacityPercent / 100.0) * 255));
            LogService.LogAction("notifications", 
                $"Setting alpha to {alpha} (from {settings.OpacityPercent}%)");

            // Apply the alpha transparency
            bool success = SetLayeredWindowAttributes(toastHwnd.Value, 0, alpha, LWA_ALPHA);
            LogService.LogAction("notifications", 
                $"SetLayeredWindowAttributes result: {success}, LastError: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Failed to apply opacity: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
