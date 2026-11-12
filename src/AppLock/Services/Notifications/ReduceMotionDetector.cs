using System;
using System.Runtime.InteropServices;

namespace AppLock.Services.Notifications;

/// <summary>
/// Detects if Windows "Animation effects" accessibility setting is disabled.
/// </summary>
public static class ReduceMotionDetector
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref bool pvParam,
        uint fWinIni);

    /// <summary>
    /// Check if Windows animation effects are disabled (reduce motion).
    /// </summary>
    public static bool IsReduceMotionEnabled()
    {
        try
        {
            bool animationsEnabled = true;
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref animationsEnabled, 0))
            {
                return !animationsEnabled; // Reduce motion = animations disabled
            }
            return false; // Default to animations enabled
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Error detecting reduce motion: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apply reduce motion defaults to settings if needed.
    /// </summary>
    public static void ApplyReduceMotionDefaults(Models.NotificationCustomSettings settings, bool respectUserOverride = true)
    {
        if (!IsReduceMotionEnabled())
            return;

        LogService.LogAction("notifications", "Reduce motion detected, applying accessibility defaults");

        // Only apply if user hasn't explicitly set values (or override is disabled)
        if (settings.EnterFrom == "Auto" || !respectUserOverride)
        {
            settings.EnterFrom = "Fade";
        }

        if (settings.ExitTo == "Reverse" || !respectUserOverride)
        {
            settings.ExitTo = "Fade";
        }

        // Reduce durations for faster, less distracting animations
        if (settings.EnterMs > 200 || !respectUserOverride)
        {
            settings.EnterMs = 150;
        }

        if (settings.ExitMs > 200 || !respectUserOverride)
        {
            settings.ExitMs = 150;
        }
    }

    /// <summary>
    /// Get a friendly message for UI display.
    /// </summary>
    public static string GetReduceMotionMessage()
    {
        return "Windows \"Animation effects\" is disabled. Animations default to fade for accessibility.";
    }
}
