using System;

namespace AppLock.Island.Host;

/// <summary>
/// Design geometry of the island in CSS pixels, mirrored in <c>Assets/island-ui/src/motion.ts</c>.
/// The page is the open island plus a small margin for the anti-aliased edge and a static shadow.
/// </summary>
public static class IslandLayout
{
    public const int FoldedWidth = 196;
    public const int FoldedHeight = 36;
    public const int FoldedRadius = 18;

    public const int OpenWidth = 420;
    public const int OpenHeight = 180;
    public const int OpenRadius = 24;

    public const int SideMargin = 8;
    public const int BottomMargin = 8;

    public static int PageWidth => OpenWidth + (SideMargin * 2);
    public static int PageHeight => OpenHeight + BottomMargin;

    /// <summary>Clamps the size setting to the range the settings page offers.</summary>
    public static double SizeFactor(int sizePercent) => Math.Clamp(sizePercent, 70, 150) / 100d;

    public static (int Width, int Height) PageSize(int sizePercent)
    {
        double factor = SizeFactor(sizePercent);
        return ((int)Math.Round(PageWidth * factor), (int)Math.Round(PageHeight * factor));
    }
}
