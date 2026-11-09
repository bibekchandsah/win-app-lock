using Windows.UI;

namespace AppLock.Services.RingLight;

/// <summary>
/// Converts a color temperature in Kelvin to an RGB <see cref="Color"/>,
/// using Tanner Helland's algorithm (clamped to 2700–6500 K).
/// Brightness (0–100) is applied as a luminance scale.
/// </summary>
public static class ColorTemperature
{
    public static Color ToColor(int kelvin, int brightnessPercent = 100)
    {
        kelvin = System.Math.Clamp(kelvin, 2700, 6500);
        double temp = kelvin / 100.0;

        double r, g, b;

        // Red
        if (temp <= 66)
            r = 255;
        else
        {
            r = temp - 60;
            r = 329.698727446 * System.Math.Pow(r, -0.1332047592);
            r = System.Math.Clamp(r, 0, 255);
        }

        // Green
        if (temp <= 66)
        {
            g = temp;
            g = 99.4708025861 * System.Math.Log(g) - 161.1195681661;
        }
        else
        {
            g = temp - 60;
            g = 288.1221695283 * System.Math.Pow(g, -0.0755148492);
        }
        g = System.Math.Clamp(g, 0, 255);

        // Blue
        if (temp >= 66)
            b = 255;
        else if (temp <= 19)
            b = 0;
        else
        {
            b = temp - 10;
            b = 138.5177312231 * System.Math.Log(b) - 305.0447927307;
            b = System.Math.Clamp(b, 0, 255);
        }

        float scale = System.Math.Clamp(brightnessPercent, 0, 100) / 100f;
        return Color.FromArgb(255,
            (byte)(r * scale),
            (byte)(g * scale),
            (byte)(b * scale));
    }
}

