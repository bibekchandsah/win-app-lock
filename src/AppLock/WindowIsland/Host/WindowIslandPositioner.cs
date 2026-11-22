using System;
using System.Runtime.InteropServices;
using AppLock.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Positions the Window Island window on the active monitor, honouring per-monitor DPI,
/// multi-monitor target rules (Primary, Cursor, Specific), top taskbar docking, and custom Y offsets.
/// </summary>
public static class WindowIslandPositioner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    public static DisplayArea GetTargetDisplayArea(AppWindow? appWindow, WindowIslandSettings settings)
    {
        if (string.Equals(settings.TargetMonitorMode, "Cursor", StringComparison.OrdinalIgnoreCase))
        {
            if (GetCursorPos(out var pt))
            {
                return DisplayArea.GetFromPoint(new PointInt32(pt.x, pt.y), DisplayAreaFallback.Primary);
            }
        }
        else if (string.Equals(settings.TargetMonitorMode, "Specific", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrEmpty(settings.TargetMonitorDeviceName))
        {
            try
            {
                var areas = DisplayArea.FindAll();
                foreach (var a in areas)
                {
                    if (string.Equals(a.DisplayId.Value.ToString(), settings.TargetMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return a;
                    }
                }
            }
            catch { }
        }

        if (appWindow != null)
        {
            return DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        }
        return DisplayArea.Primary;
    }

    public static RectInt32 Compute(AppWindow? appWindow, WindowIslandSettings settings, float windowWidthDip, float windowHeightDip, double dpiScale)
    {
        int widthPx = (int)Math.Round(windowWidthDip * dpiScale);
        int heightPx = (int)Math.Round(windowHeightDip * dpiScale);

        DisplayArea area = GetTargetDisplayArea(appWindow, settings);

        var outer = area.OuterBounds;
        var work = area.WorkArea;

        int x = settings.Position switch
        {
            "TopLeft"  => outer.X,
            "TopRight" => outer.X + outer.Width - widthPx,
            _          => outer.X + ((outer.Width - widthPx) / 2) // TopCenter
        };

        // If taskbar is docked at the top of this monitor, WorkArea.Y will be below OuterBounds.Y
        int topEdgePx = settings.UnderTopTaskbar
            ? Math.Max(outer.Y, work.Y)
            : outer.Y;

        // Capsule floats slightly below edge; notch sits flush
        int shapeOffsetPx = string.Equals(settings.Shape, "Capsule", StringComparison.OrdinalIgnoreCase)
            ? (int)Math.Round(6.0 * dpiScale)
            : 0;

        int customYOffsetPx = (int)Math.Round(settings.YOffsetDip * dpiScale);

        int y = topEdgePx + shapeOffsetPx + customYOffsetPx;

        return new RectInt32(x, y, widthPx, heightPx);
    }
}
