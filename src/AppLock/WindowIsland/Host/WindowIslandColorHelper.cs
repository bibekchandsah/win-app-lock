using System;
using Windows.UI;
using AppLock.Models;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Milestone 16: Pure color parsing, swatch resolution, and contrast calculations for Window Island.
/// </summary>
public static class WindowIslandColorHelper
{
    public static Color ResolveBaseColor(WindowIslandSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.IslandColor) &&
            !string.Equals(settings.IslandColor, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return settings.IslandColor.ToLowerInvariant() switch
            {
                "navy"     => Color.FromArgb(255, 15, 23, 42),
                "indigo"   => Color.FromArgb(255, 30, 27, 75),
                "green"    => Color.FromArgb(255, 20, 45, 30),
                "maroon"   => Color.FromArgb(255, 60, 15, 25),
                "brown"    => Color.FromArgb(255, 50, 35, 25),
                "cream"    => Color.FromArgb(255, 245, 240, 230),
                "mint"     => Color.FromArgb(255, 230, 245, 235),
                "lavender" => Color.FromArgb(255, 235, 230, 250),
                _ when settings.IslandColor.StartsWith("#") => HexToColor(settings.IslandColor),
                _ => settings.Theme == "Light" ? Color.FromArgb(255, 245, 245, 245) : Color.FromArgb(255, 20, 20, 20)
            };
        }

        return settings.Theme switch
        {
            "Light" => Color.FromArgb(255, 245, 245, 245),
            "Dark"  => Color.FromArgb(255, 20, 20, 20),
            _       => Color.FromArgb(255, 20, 20, 20)
        };
    }

    public static Color HexToColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Color.FromArgb(255, 20, 20, 20);
        hex = hex.TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return Color.FromArgb(255, r, g, b);
        }
        return Color.FromArgb(255, 20, 20, 20);
    }

    /// <summary>
    /// Returns true if the color is considered dark (relative luminance < 0.5), requiring light text.
    /// </summary>
    public static bool IsDarkColor(Color c)
    {
        // Standard relative luminance formula
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance < 0.5;
    }
}
