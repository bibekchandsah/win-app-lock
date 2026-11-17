using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Net.Codecrete.QrCodeGenerator;

namespace AppLock.Services;

/// <summary>
/// Turns a saved Wi-Fi network into the "WIFI:" QR payload phones understand (`WIFI:T:WPA;S:ssid;P:pass;;`)
/// and renders it as vector rectangles, so the code stays crisp and needs no image encoding.
/// </summary>
public static class WifiQrCode
{
    /// <summary>Builds the QR payload, escaping the characters the format reserves.</summary>
    public static string BuildPayload(string ssid, string? password, bool isOpen)
    {
        var authentication = isOpen || string.IsNullOrEmpty(password) ? "nopass" : "WPA";
        var payload = $"WIFI:T:{authentication};S:{Escape(ssid)};";

        if (authentication != "nopass")
        {
            payload += $"P:{Escape(password!)};";
        }

        return payload + ";";
    }

    /// <summary>
    /// A white card containing the QR code for <paramref name="payload"/>. White background and black modules
    /// are fixed on purpose: a QR code has to stay scannable in the app's dark theme too.
    /// </summary>
    public static FrameworkElement CreateVisual(string payload, double size = 260)
    {
        var code = QrCode.EncodeText(payload, QrCode.Ecc.Medium);

        const int quietZone = 4;
        var geometry = new GeometryGroup();

        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
            {
                if (!code.GetModule(x, y)) continue;
                geometry.Children.Add(new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(x + quietZone, y + quietZone, 1, 1)
                });
            }
        }

        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(Colors.Black),
            Width = size,
            Height = size,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        return new Border
        {
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = path
        };
    }

    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace(":", "\\:")
            .Replace("\"", "\\\"");
}
