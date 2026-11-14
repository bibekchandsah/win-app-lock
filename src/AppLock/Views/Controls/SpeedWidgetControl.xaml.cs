using System;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AppLock.Views.Controls;

/// <summary>
/// The speed widget's content. Hosted by the floating window and by the settings preview,
/// so every appearance change is visible instantly without the widget being enabled.
/// </summary>
public sealed partial class SpeedWidgetControl : UserControl
{
    private static readonly Color DefaultBackground = Color.FromArgb(255, 0x20, 0x20, 0x20);
    private static readonly Color DefaultText = Colors.White;
    private static readonly Color DefaultDown = Color.FromArgb(255, 0x4A, 0xDE, 0x80);
    private static readonly Color DefaultUp = Color.FromArgb(255, 0x60, 0xA5, 0xFA);

    public SpeedWidgetControl()
    {
        InitializeComponent();
        ApplyAppearance(ServiceContainer.Settings.SpeedMeter);
    }

    /// <summary>Re-applies every appearance setting: preset, colors, opacity, scale, layout, font.</summary>
    public void ApplyAppearance(SpeedMeterSettings settings)
    {
        var preset = settings.ThemePreset ?? "Glass";
        var isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;

        var scale = Math.Clamp(settings.ScalePercent, 50, 250) / 100.0;
        var opacity = Math.Clamp(settings.BackgroundOpacityPercent, 0, 100) / 100.0;
        var radius = Math.Clamp(settings.CornerRadiusPx, 0, 40);

        // Background: the preset decides the base color, the opacity setting decides its alpha.
        var background = ParseColor(settings.BackgroundColor, DefaultBackground);
        var transparent = false;

        switch (preset)
        {
            case "Transparent":
                transparent = true;
                break;
            case "FollowWindows":
                background = isDark ? Color.FromArgb(255, 0x20, 0x20, 0x20) : Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
                break;
            case "Solid":
                opacity = 1.0;
                break;
        }

        Card.Background = transparent
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), background.R, background.G, background.B));

        var textColor = preset == "FollowWindows"
            ? (isDark ? Colors.White : Color.FromArgb(255, 0x1A, 0x1A, 0x1A))
            : ParseColor(settings.TextColor, DefaultText);

        var textBrush = new SolidColorBrush(textColor);
        var downBrush = new SolidColorBrush(ParseColor(settings.DownloadColor, DefaultDown));
        var upBrush = new SolidColorBrush(ParseColor(settings.UploadColor, DefaultUp));

        // Border follows the appearance toggle, tinted from the text color so it stays visible.
        var showBorder = settings.ShowBorder && !transparent;
        Card.BorderThickness = showBorder ? new Thickness(1) : new Thickness(0);
        Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, textColor.R, textColor.G, textColor.B));

        Card.CornerRadius = new CornerRadius(radius);
        Card.Padding = settings.Compact
            ? new Thickness(10 * scale, 6 * scale, 10 * scale, 6 * scale)
            : new Thickness(16 * scale, 10 * scale, 16 * scale, 10 * scale);

        Row.Orientation = string.Equals(settings.Layout, "Vertical", StringComparison.OrdinalIgnoreCase)
            ? Orientation.Vertical
            : Orientation.Horizontal;
        Row.Spacing = 16 * scale;
        DownBlock.Spacing = 6 * scale;
        UpBlock.Spacing = 6 * scale;

        var font = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI Variable" : settings.FontFamily);
        var weight = settings.FontWeight switch
        {
            "Bold" => Microsoft.UI.Text.FontWeights.Bold,
            "Normal" => Microsoft.UI.Text.FontWeights.Normal,
            _ => Microsoft.UI.Text.FontWeights.SemiBold
        };

        var fontSize = 14 * scale;
        var iconSize = 12 * scale;
        var numberWidth = 84 * scale;

        foreach (var block in new[] { DownText, UpText })
        {
            block.FontFamily = font;
            block.FontWeight = weight;
            block.FontSize = fontSize;
            block.Width = numberWidth;
            block.Foreground = textBrush;
        }

        foreach (var label in new[] { DownLabel, UpLabel })
        {
            label.FontFamily = font;
            label.FontWeight = weight;
            label.FontSize = 11 * scale;
            label.Visibility = settings.UseIcons ? Visibility.Collapsed : Visibility.Visible;
            label.Foreground = textBrush;
        }

        DownText.Foreground = textBrush;
        UpText.Foreground = textBrush;

        DownIcon.FontSize = iconSize;
        UpIcon.FontSize = iconSize;
        DownIcon.Foreground = downBrush;
        UpIcon.Foreground = upBrush;
        DownIcon.Visibility = settings.UseIcons ? Visibility.Visible : Visibility.Collapsed;
        UpIcon.Visibility = settings.UseIcons ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Sets the two rate labels; formatting is done by the caller.</summary>
    public void SetValues(string down, string up)
    {
        DownText.Text = down;
        UpText.Text = up;
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            var text = value.TrimStart('#');
            if (text.Length == 6)
            {
                return Color.FromArgb(
                    255,
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16));
            }

            if (text.Length == 8)
            {
                return Color.FromArgb(
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16),
                    Convert.ToByte(text.Substring(6, 2), 16));
            }
        }
        catch (Exception)
        {
            // Malformed hex: fall through to the default.
        }

        return fallback;
    }
}
