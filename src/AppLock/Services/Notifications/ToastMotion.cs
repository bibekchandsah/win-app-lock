using System;
using System.Drawing;
using AppLock.Models;

namespace AppLock.Services.Notifications;

/// <summary>
/// Pure math for toast animation positions and easing.
/// Shared between live preview and real animator to ensure they match exactly.
/// </summary>
public static class ToastMotion
{
    /// <summary>
    /// Compute the start position for enter animation.
    /// </summary>
    public static Rectangle GetEnterStartRect(
        Rectangle restRect,
        Rectangle monitorBounds,
        NotificationCustomSettings settings)
    {
        var direction = GetEffectiveEnterDirection(restRect, monitorBounds, settings.EnterFrom);
        
        return direction switch
        {
            "Top" => settings.DistanceMode == "Edge" 
                ? new Rectangle(restRect.X, monitorBounds.Top - restRect.Height, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X, restRect.Y - settings.ShortDistancePx, restRect.Width, restRect.Height),
            
            "Bottom" => settings.DistanceMode == "Edge"
                ? new Rectangle(restRect.X, monitorBounds.Bottom, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X, restRect.Y + settings.ShortDistancePx, restRect.Width, restRect.Height),
            
            "Left" => settings.DistanceMode == "Edge"
                ? new Rectangle(monitorBounds.Left - restRect.Width, restRect.Y, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X - settings.ShortDistancePx, restRect.Y, restRect.Width, restRect.Height),
            
            "Right" => settings.DistanceMode == "Edge"
                ? new Rectangle(monitorBounds.Right, restRect.Y, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X + settings.ShortDistancePx, restRect.Y, restRect.Width, restRect.Height),
            
            "Fade" or "ScaleFade" or "None" => restRect, // Start at rest position
            
            _ => restRect
        };
    }

    /// <summary>
    /// Compute the exit end position for exit animation.
    /// </summary>
    public static Rectangle GetExitEndRect(
        Rectangle restRect,
        Rectangle monitorBounds,
        NotificationCustomSettings settings)
    {
        var direction = GetEffectiveExitDirection(restRect, monitorBounds, settings);
        
        return direction switch
        {
            "Top" => settings.DistanceMode == "Edge"
                ? new Rectangle(restRect.X, monitorBounds.Top - restRect.Height, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X, restRect.Y - settings.ShortDistancePx, restRect.Width, restRect.Height),
            
            "Bottom" => settings.DistanceMode == "Edge"
                ? new Rectangle(restRect.X, monitorBounds.Bottom, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X, restRect.Y + settings.ShortDistancePx, restRect.Width, restRect.Height),
            
            "Left" => settings.DistanceMode == "Edge"
                ? new Rectangle(monitorBounds.Left - restRect.Width, restRect.Y, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X - settings.ShortDistancePx, restRect.Y, restRect.Width, restRect.Height),
            
            "Right" => settings.DistanceMode == "Edge"
                ? new Rectangle(monitorBounds.Right, restRect.Y, restRect.Width, restRect.Height)
                : new Rectangle(restRect.X + settings.ShortDistancePx, restRect.Y, restRect.Width, restRect.Height),
            
            "Fade" or "None" => restRect, // End at rest position
            
            _ => restRect
        };
    }

    /// <summary>
    /// Resolve "Auto" enter direction to the nearest edge.
    /// </summary>
    public static string GetEffectiveEnterDirection(
        Rectangle restRect,
        Rectangle monitorBounds,
        string enterFrom)
    {
        if (enterFrom != "Auto")
            return enterFrom;

        // Calculate center of toast
        int centerX = restRect.Left + restRect.Width / 2;
        int centerY = restRect.Top + restRect.Height / 2;
        
        // Calculate center of monitor
        int monitorCenterX = monitorBounds.Left + monitorBounds.Width / 2;
        int monitorCenterY = monitorBounds.Top + monitorBounds.Height / 2;
        
        // Distance to each edge
        int distToTop = centerY - monitorBounds.Top;
        int distToBottom = monitorBounds.Bottom - centerY;
        int distToLeft = centerX - monitorBounds.Left;
        int distToRight = monitorBounds.Right - centerX;
        
        // If very centered (within 20% of center), use scale+fade
        bool nearlyCenteredX = Math.Abs(centerX - monitorCenterX) < monitorBounds.Width * 0.2;
        bool nearlyCenteredY = Math.Abs(centerY - monitorCenterY) < monitorBounds.Height * 0.2;
        
        if (nearlyCenteredX && nearlyCenteredY)
            return "ScaleFade";
        
        // Find nearest edge
        int minDist = Math.Min(Math.Min(distToTop, distToBottom), Math.Min(distToLeft, distToRight));
        
        if (minDist == distToTop) return "Top";
        if (minDist == distToBottom) return "Bottom";
        if (minDist == distToLeft) return "Left";
        return "Right";
    }

    /// <summary>
    /// Resolve exit direction (handles "Reverse").
    /// For "Reverse", the exit goes back toward the same edge the toast entered from,
    /// i.e. the toast exits in the SAME direction it came from (not opposite).
    /// </summary>
    public static string GetEffectiveExitDirection(
        Rectangle restRect,
        Rectangle monitorBounds,
        NotificationCustomSettings settings)
    {
        if (settings.ExitTo != "Reverse")
            return settings.ExitTo;
        
        // Mirror the enter direction for the exit:
        // If it entered from Top  → exit toward Top  (slides back up out)
        // If it entered from Right→ exit toward Right (slides back right out)
        var enterDir = GetEffectiveEnterDirection(restRect, monitorBounds, settings.EnterFrom);
        
        return enterDir switch
        {
            "Top"       => "Top",
            "Bottom"    => "Bottom",
            "Left"      => "Left",
            "Right"     => "Right",
            "ScaleFade" => "Fade",
            "Fade"      => "Fade",
            _           => "Fade"
        };
    }

    /// <summary>
    /// Apply easing function to linear progress (0.0 to 1.0).
    /// </summary>
    public static double ApplyEasing(double t, string easing)
    {
        // Clamp input
        t = Math.Max(0.0, Math.Min(1.0, t));
        
        return easing switch
        {
            "Linear" => t,
            
            "EaseOut" => 1.0 - Math.Pow(1.0 - t, 3.0), // Cubic ease out
            
            "EaseInOut" => t < 0.5
                ? 4.0 * t * t * t
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0,
            
            "Spring" => // Slight overshoot
                1.0 + Math.Sin((t - 1.0) * Math.PI) * 0.1 * (1.0 - t),
            
            "Bounce" => // Bounce at end
                t < 0.9
                    ? EaseOutQuad(t / 0.9)
                    : 1.0 + Math.Sin((t - 0.9) * Math.PI * 5) * 0.05,
            
            _ => t // Fallback to linear
        };
    }

    private static double EaseOutQuad(double t) => 1.0 - (1.0 - t) * (1.0 - t);

    /// <summary>
    /// Interpolate between two rectangles based on progress (0.0 to 1.0).
    /// </summary>
    public static Rectangle LerpRect(Rectangle from, Rectangle to, double progress)
    {
        int x = (int)(from.X + (to.X - from.X) * progress);
        int y = (int)(from.Y + (to.Y - from.Y) * progress);
        int width = (int)(from.Width + (to.Width - from.Width) * progress);
        int height = (int)(from.Height + (to.Height - from.Height) * progress);
        
        return new Rectangle(x, y, width, height);
    }

    /// <summary>
    /// Interpolate opacity based on progress and animation type.
    /// </summary>
    public static double GetOpacityForProgress(
        double progress,
        string direction,
        int targetOpacityPercent)
    {
        double baseOpacity = targetOpacityPercent / 100.0;
        
        // Fade animations modulate opacity
        if (direction == "Fade" || direction == "ScaleFade")
        {
            return progress * baseOpacity;
        }
        
        // Slide animations: fade in during first 30%, stay at target
        if (progress < 0.3)
        {
            return (progress / 0.3) * baseOpacity;
        }
        
        return baseOpacity;
    }
}
