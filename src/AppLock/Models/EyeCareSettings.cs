using System;

namespace AppLock.Models;

public sealed class EyeCareSettings
{
    public bool Enabled { get; set; } = false;
    public int IntervalMinutes { get; set; } = 20;
    public string Message { get; set; } = "";
    public bool ScheduleEnabled { get; set; } = false;
    public TimeSpan StartTime { get; set; } = new(9, 0, 0);
    public TimeSpan EndTime { get; set; } = new(21, 0, 0);
    public int DurationSeconds { get; set; } = 20;
    public string? TemplatePath { get; set; }

    public const string DefaultMessage = 
        "Have a look far away from your current screen to protect your eyes";

    /// <summary>
    /// Validates and clamps settings to acceptable ranges.
    /// </summary>
    public void Validate()
    {
        if (IntervalMinutes < 1) IntervalMinutes = 1;
        if (IntervalMinutes > 1440) IntervalMinutes = 1440;

        if (DurationSeconds < 5) DurationSeconds = 5;
        if (DurationSeconds > 300) DurationSeconds = 300;

        // Validate template path existence
        if (!string.IsNullOrEmpty(TemplatePath) && !System.IO.File.Exists(TemplatePath))
        {
            TemplatePath = null;
        }
    }

    /// <summary>
    /// Checks if the current time is within the scheduled window.
    /// Handles cross-midnight schedules (e.g., 22:00 to 06:00).
    /// </summary>
    public bool IsWithinSchedule(TimeSpan currentTime)
    {
        if (!ScheduleEnabled)
            return true;

        if (StartTime == EndTime)
            return true; // Always inside

        if (StartTime < EndTime)
        {
            // Normal window: 09:00 to 21:00
            return currentTime >= StartTime && currentTime < EndTime;
        }
        else
        {
            // Cross-midnight window: 22:00 to 06:00
            return currentTime >= StartTime || currentTime < EndTime;
        }
    }
}
