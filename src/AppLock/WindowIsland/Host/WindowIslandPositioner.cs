using System;
using System.Runtime.InteropServices;
using AppLock.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Positions the Window Island window on the active monitor, honouring per-monitor DPI,
/// top taskbar docking (WorkArea vs OuterBounds), and custom Y offsets.
/// </summary>
public static class WindowIslandPositioner
{
    public static RectInt32 Compute(AppWindow? appWindow, WindowIslandSettings settings, float windowWidthDip, float windowHeightDip, double dpiScale)
    {
        int widthPx = (int)Math.Round(windowWidthDip * dpiScale);
        int heightPx = (int)Math.Round(windowHeightDip * dpiScale);

        DisplayArea area = appWindow != null
            ? DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
            : DisplayArea.Primary;

        var outer = area.OuterBounds;
        var work = area.WorkArea;

        int x = settings.Position switch
        {
            "TopLeft"  => outer.X,
            "TopRight" => outer.X + outer.Width - widthPx,
            _          => outer.X + ((outer.Width - widthPx) / 2) // TopCenter
        };

        // If taskbar is docked at the top of this monitor, WorkArea.Y will be below OuterBounds.Y
        int topEdgePx = Math.Max(outer.Y, work.Y);

        // Capsule floats slightly below edge; notch sits flush
        int shapeOffsetPx = string.Equals(settings.Shape, "Capsule", StringComparison.OrdinalIgnoreCase)
            ? (int)Math.Round(6.0 * dpiScale)
            : 0;

        int customYOffsetPx = (int)Math.Round(settings.YOffsetDip * dpiScale);

        int y = topEdgePx + shapeOffsetPx + customYOffsetPx;

        return new RectInt32(x, y, widthPx, heightPx);
    }
}
