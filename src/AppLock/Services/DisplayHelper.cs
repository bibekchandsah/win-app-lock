using System;
using System.Collections.Generic;
using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace AppLock.Services;

/// <summary>
/// Helper service for multi-monitor and DPI awareness.
/// Provides utilities for per-monitor DPI v2 support and window positioning across monitors.
/// </summary>
public sealed class DisplayHelper
{
    public DisplayHelper()
    {
        // Ensure DPI awareness is set (should be in manifest, but verify at runtime)
        EnsureDpiAwareness();
    }

    /// <summary>
    /// Ensure the process is DPI aware (PerMonitorV2 preferred).
    /// </summary>
    private void EnsureDpiAwareness()
    {
        try
        {
            unsafe
            {
                var awareness = PInvoke.GetAwarenessFromDpiAwarenessContext(
                    PInvoke.GetThreadDpiAwarenessContext());
                
                System.Diagnostics.Debug.WriteLine($"Current DPI awareness: {awareness}");
                
                // If not already PerMonitorV2, try to set it
                if (awareness != DPI_AWARENESS.DPI_AWARENESS_PER_MONITOR_AWARE)
                {
                    // This typically needs to be set via manifest or before any Windows are created
                    // Just log it here
                    System.Diagnostics.Debug.WriteLine("DPI awareness should be PerMonitorV2 in manifest");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking DPI awareness: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the DPI for a specific window.
    /// </summary>
    public uint GetDpiForWindow(HWND hwnd)
    {
        if (hwnd.IsNull) return 96; // Default DPI

        try
        {
            return PInvoke.GetDpiForWindow(hwnd);
        }
        catch
        {
            return 96;
        }
    }

    /// <summary>
    /// Get the scale factor for a window (1.0 = 100%, 1.5 = 150%, etc.).
    /// </summary>
    public double GetScaleFactorForWindow(HWND hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi / 96.0; // 96 is the default DPI (100%)
    }

    /// <summary>
    /// Scale a rectangle based on DPI.
    /// </summary>
    public RECT ScaleRect(RECT rect, uint dpi)
    {
        var scale = dpi / 96.0;
        
        return new RECT
        {
            left = (int)(rect.left * scale),
            top = (int)(rect.top * scale),
            right = (int)(rect.right * scale),
            bottom = (int)(rect.bottom * scale)
        };
    }

    /// <summary>
    /// Get the monitor that contains a window.
    /// </summary>
    public HMONITOR GetMonitorFromWindow(HWND hwnd)
    {
        if (hwnd.IsNull) return default;

        try
        {
            return PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Get monitor info including work area and DPI.
    /// </summary>
    public MonitorInfo? GetMonitorInfo(HMONITOR monitor)
    {
        if (monitor.Value == IntPtr.Zero) return null;

        try
        {
            unsafe
            {
                MONITORINFO info = new()
                {
                    cbSize = (uint)sizeof(MONITORINFO)
                };

                if (!PInvoke.GetMonitorInfo(monitor, &info))
                    return null;

                // Get DPI for this monitor
                uint dpiX = 96, dpiY = 96;
                PInvoke.GetDpiForMonitor(
                    monitor,
                    MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                    &dpiX,
                    &dpiY);

                return new MonitorInfo
                {
                    Monitor = monitor,
                    MonitorRect = info.rcMonitor,
                    WorkRect = info.rcWork,
                    IsPrimary = (info.dwFlags & 1) != 0,
                    DpiX = dpiX,
                    DpiY = dpiY,
                    ScaleFactor = dpiX / 96.0
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting monitor info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get all monitors in the system.
    /// </summary>
    public List<MonitorInfo> GetAllMonitors()
    {
        var monitors = new List<MonitorInfo>();

        unsafe
        {
            PInvoke.EnumDisplayMonitors(
                default(HDC),
                (RECT?)null,
                (monitor, hdc, rect, lParam) =>
                {
                    var info = GetMonitorInfo(monitor);
                    if (info != null)
                    {
                        monitors.Add(info);
                    }
                    return true;
                },
                default);
        }

        return monitors;
    }

    /// <summary>
    /// Check if a point is within a rectangle.
    /// </summary>
    public bool IsPointInRect(System.Drawing.Point point, RECT rect)
    {
        return point.X >= rect.left &&
               point.X < rect.right &&
               point.Y >= rect.top &&
               point.Y < rect.bottom;
    }

    /// <summary>
    /// Get the monitor that contains a specific point.
    /// </summary>
    public HMONITOR GetMonitorFromPoint(System.Drawing.Point point)
    {
        try
        {
            return PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Adjust window rectangle to account for DWM extended frame bounds.
    /// This gives the actual visible window area, excluding shadows.
    /// </summary>
    public RECT GetWindowBoundsWithoutShadow(HWND hwnd)
    {
        if (hwnd.IsNull)
            return default;

        try
        {
            unsafe
            {
                RECT rect;
                var result = PInvoke.DwmGetWindowAttribute(
                    hwnd,
                    Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                    &rect,
                    (uint)sizeof(RECT));

                if (result.Succeeded)
                    return rect;

                // Fallback to regular GetWindowRect
                PInvoke.GetWindowRect(hwnd, out rect);
                return rect;
            }
        }
        catch
        {
            PInvoke.GetWindowRect(hwnd, out var rect);
            return rect;
        }
    }

    /// <summary>
    /// Clamp a rectangle to fit within monitor bounds.
    /// </summary>
    public RECT ClampToMonitor(RECT rect, HMONITOR monitor)
    {
        var monitorInfo = GetMonitorInfo(monitor);
        if (monitorInfo == null) return rect;

        var workRect = monitorInfo.WorkRect;

        return new RECT
        {
            left = Math.Max(rect.left, workRect.left),
            top = Math.Max(rect.top, workRect.top),
            right = Math.Min(rect.right, workRect.right),
            bottom = Math.Min(rect.bottom, workRect.bottom)
        };
    }
}

/// <summary>
/// Information about a monitor.
/// </summary>
public class MonitorInfo
{
    public HMONITOR Monitor { get; set; }
    public RECT MonitorRect { get; set; }
    public RECT WorkRect { get; set; }
    public bool IsPrimary { get; set; }
    public uint DpiX { get; set; }
    public uint DpiY { get; set; }
    public double ScaleFactor { get; set; }

    public int Width => MonitorRect.right - MonitorRect.left;
    public int Height => MonitorRect.bottom - MonitorRect.top;
    public int WorkWidth => WorkRect.right - WorkRect.left;
    public int WorkHeight => WorkRect.bottom - WorkRect.top;
}
