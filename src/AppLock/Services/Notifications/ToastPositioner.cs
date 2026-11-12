using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.Graphics.Gdi;
using AppLock.Models;
using System.Drawing;

namespace AppLock.Services.Notifications;

/// <summary>
/// Calculates and applies toast notification positions
/// </summary>
public class ToastPositioner
{
    public static RECT GetTargetRect(HWND toastHwnd, NotificationCustomSettings settings)
    {
        // Get toast window size
        RECT toastRect = default;
        unsafe
        {
            PInvoke.GetWindowRect(toastHwnd, &toastRect);
        }
        int toastWidth = toastRect.right - toastRect.left;
        int toastHeight = toastRect.bottom - toastRect.top;

        // Get target monitor
        var monitor = GetTargetMonitor(settings);
        var monitorInfo = GetMonitorInfo(monitor);
        
        RECT workArea = settings.RespectTaskbar ? monitorInfo.rcWork : monitorInfo.rcMonitor;

        // Calculate position based on anchor
        var (x, y) = CalculatePosition(
            settings.Anchor,
            workArea,
            toastWidth,
            toastHeight,
            settings.MarginPx,
            settings.CustomPercentX,
            settings.CustomPercentY
        );

        // Clamp to monitor bounds
        x = Math.Max(monitorInfo.rcMonitor.left, Math.Min(x, monitorInfo.rcMonitor.right - toastWidth));
        y = Math.Max(monitorInfo.rcMonitor.top, Math.Min(y, monitorInfo.rcMonitor.bottom - toastHeight));

        return new RECT
        {
            left = x,
            top = y,
            right = x + toastWidth,
            bottom = y + toastHeight
        };
    }

    public static void PositionToast(HWND toastHwnd, NotificationCustomSettings settings)
    {
        var targetRect = GetTargetRect(toastHwnd, settings);
        
        unsafe
        {
            PInvoke.SetWindowPos(
                toastHwnd,
                HWND.Null,
                targetRect.left,
                targetRect.top,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE | 
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | 
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            );
        }
    }

    private static (int x, int y) CalculatePosition(
        string anchor,
        RECT area,
        int width,
        int height,
        int margin,
        float customX,
        float customY)
    {
        int areaWidth = area.right - area.left;
        int areaHeight = area.bottom - area.top;

        return anchor switch
        {
            "TopLeft" => (area.left + margin, area.top + margin),
            "TopCenter" => (area.left + (areaWidth - width) / 2, area.top + margin),
            "TopRight" => (area.right - width - margin, area.top + margin),
            "MiddleLeft" => (area.left + margin, area.top + (areaHeight - height) / 2),
            "Center" => (area.left + (areaWidth - width) / 2, area.top + (areaHeight - height) / 2),
            "MiddleRight" => (area.right - width - margin, area.top + (areaHeight - height) / 2),
            "BottomLeft" => (area.left + margin, area.bottom - height - margin),
            "BottomCenter" => (area.left + (areaWidth - width) / 2, area.bottom - height - margin),
            "BottomRight" => (area.right - width - margin, area.bottom - height - margin),
            "Custom" => (
                area.left + (int)(areaWidth * customX),
                area.top + (int)(areaHeight * customY)
            ),
            _ => (area.right - width - margin, area.bottom - height - margin) // Default: BottomRight
        };
    }

    private static HMONITOR GetTargetMonitor(NotificationCustomSettings settings)
    {
        return settings.MonitorMode switch
        {
            "Primary" => PInvoke.MonitorFromPoint(new Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY),
            "Mouse" => GetMonitorFromCursor(),
            "ActiveWindow" => GetMonitorFromActiveWindow(),
            _ => PInvoke.MonitorFromPoint(new Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY)
        };
    }

    /// <summary>
    /// Get target monitor for a toast (public for animator).
    /// </summary>
    public static HMONITOR GetTargetMonitor(HWND toastHwnd, NotificationCustomSettings settings)
    {
        // For now, just use settings-based monitor selection
        // Could enhance to use toastHwnd's current monitor as a hint
        return GetTargetMonitor(settings);
    }

    private static HMONITOR GetMonitorFromCursor()
    {
        if (PInvoke.GetCursorPos(out var pt))
        {
            return PInvoke.MonitorFromPoint(new Point(pt.X, pt.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        }
        return PInvoke.MonitorFromPoint(new Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
    }

    private static HMONITOR GetMonitorFromActiveWindow()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.Value == IntPtr.Zero)
            return PInvoke.MonitorFromPoint(new Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
            
        return PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
    }

    private static MONITORINFO GetMonitorInfo(HMONITOR monitor)
    {
        MONITORINFO info = new()
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
        };
        
        unsafe
        {
            PInvoke.GetMonitorInfo(monitor, ref info);
        }
        
        return info;
    }
}
