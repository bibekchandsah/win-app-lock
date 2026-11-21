using System;
using AppLock.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AppLock.Island.Host;

/// <summary>Places the island page on the chosen monitor, honouring per-monitor DPI.</summary>
public static class IslandPositioner
{
    public static RectInt32 Compute(AppWindow appWindow, IslandSettings settings, int pageWidth, int pageHeight, double dpiScale)
    {
        int width = (int)Math.Round(pageWidth * dpiScale);
        int height = (int)Math.Round(pageHeight * dpiScale);

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var outer = area.OuterBounds;

        int x = settings.Position switch
        {
            "TopLeft" => outer.X - (int)Math.Round(IslandLayout.SideMargin * dpiScale),
            "TopRight" => outer.X + outer.Width - width + (int)Math.Round(IslandLayout.SideMargin * dpiScale),
            _ => outer.X + ((outer.Width - width) / 2),
        };

        // A capsule floats below the top edge; the notch sits flush with it.
        int topOffset = string.Equals(settings.Shape, "Capsule", StringComparison.OrdinalIgnoreCase)
            ? (int)Math.Round(8 * dpiScale)
            : 0;

        return new RectInt32(x, outer.Y + topOffset, width, height);
    }
}
