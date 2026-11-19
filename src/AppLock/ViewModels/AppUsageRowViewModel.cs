using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AppLock.ViewModels;

public static class UsageFormat
{
    public static string Duration(long totalSeconds)
    {
        if (totalSeconds <= 0) return "0m";
        if (totalSeconds < 60) return "<1m";

        long minutes = totalSeconds / 60;
        long hours = minutes / 60;
        long rest = minutes % 60;

        if (hours <= 0) return $"{rest}m";
        return rest == 0 ? $"{hours}h" : $"{hours}h {rest:00}m";
    }

    public static string ShortDuration(long totalSeconds)
    {
        if (totalSeconds <= 0) return "0m";
        if (totalSeconds < 60) return "<1m";
        long minutes = totalSeconds / 60;
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60:00}m";
    }
}

/// <summary>Theme-aware fixed palette; the index is a stable hash of AppKey.</summary>
public static class UsagePalette
{
    private static readonly string[] ColorKeys =
    {
        "SystemAccentColor",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorDark1",
        "SystemFillColorSuccess",
        "SystemFillColorCaution",
        "SystemFillColorAttention",
        "SystemFillColorCritical"
    };

    public static Brush BrushFor(int index)
    {
        try
        {
            var key = ColorKeys[Math.Abs(index) % ColorKeys.Length];
            if (Application.Current.Resources[key] is Windows.UI.Color color)
                return new SolidColorBrush(color);
        }
        catch
        {
            // Fall through to the neutral brush.
        }

        return Neutral;
    }

    public static Brush Neutral => TryBrush("TextFillColorTertiaryBrush", Microsoft.UI.Colors.Gray);

    public static Brush SecondaryText => TryBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);

    public static Brush Warning => TryBrush("SystemFillColorCautionBrush", Microsoft.UI.Colors.Orange);

    public static Brush Danger => TryBrush("SystemFillColorCriticalBrush", Microsoft.UI.Colors.IndianRed);

    private static Brush TryBrush(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush)
                return brush;
        }
        catch { }

        return new SolidColorBrush(fallback);
    }
}

/// <summary>One app line in the Screen Time list, including its live limit chip.</summary>
public sealed class AppUsageRowViewModel
{
    public AppUsageRowViewModel(
        AppUsageRow row,
        StoredApp? stored,
        LockRule? rule,
        ScreenTimeSettings settings,
        DateOnly today,
        long rangeTotalSeconds)
    {
        AppKey = row.AppKey;
        DisplayName = string.IsNullOrWhiteSpace(row.DisplayName) ? row.AppKey : row.DisplayName;
        Seconds = row.Seconds;
        Opens = row.Opens;
        ExePath = stored?.ExePath;
        Aumid = stored?.Aumid;
        Rule = rule;
        ColorIndex = Services.ScreenTime.ScreenTimeStore.StableColorIndex(row.AppKey);
        Share = rangeTotalSeconds > 0 ? Math.Clamp(row.Seconds / (double)rangeTotalSeconds, 0, 1) : 0;
        IsHidden = settings.HiddenAppKeys.Any(k => string.Equals(k, AppKey, StringComparison.OrdinalIgnoreCase));

        if (rule?.SelfDiscipline is { QuotaEnabled: true } sd)
        {
            HasLimit = true;
            LimitMinutes = sd.DailyLimitMinutes;
            LimitText = $"{UsageFormat.Duration(sd.DailyLimitMinutes * 60L)} / day";

            int used = sd.QuotaDate == today ? sd.UsedSecondsToday : 0;
            int limit = Math.Max(1, sd.DailyLimitMinutes) * 60;
            LimitProgress = Math.Clamp(used / (double)limit * 100.0, 0, 100);
            UsedText = UsageFormat.ShortDuration(used);

            bool hasExtra = sd.ExtraSecondsGrantedToday > 0;
            int effective = limit + Math.Max(0, sd.ExtraSecondsGrantedToday);

            if (used >= effective)
            {
                LimitStateText = "Limit reached";
                LimitBrush = UsagePalette.Danger;
            }
            else if (used >= effective * 0.9)
            {
                LimitStateText = $"{UsageFormat.ShortDuration(used)} / {UsageFormat.ShortDuration(effective)}";
                LimitBrush = UsagePalette.Warning;
            }
            else
            {
                LimitStateText = $"{UsageFormat.ShortDuration(used)} / {UsageFormat.ShortDuration(effective)}";
                LimitBrush = UsagePalette.SecondaryText;
            }

            if (hasExtra)
                ExtraText = $"+{UsageFormat.ShortDuration(sd.ExtraSecondsGrantedToday)} extra";
        }
    }

    public string AppKey { get; }
    public string DisplayName { get; }
    public long Seconds { get; }
    public int Opens { get; }
    public string? ExePath { get; }
    public string? Aumid { get; }
    public LockRule? Rule { get; }
    public int ColorIndex { get; }
    public double Share { get; }
    public bool IsHidden { get; }

    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName.Substring(0, 1).ToUpperInvariant();
    public string TimeText => UsageFormat.Duration(Seconds);
    public string OpensText => Opens == 1 ? "1 open" : $"{Opens} opens";
    public double SharePercent => Share * 100.0;
    public Brush ColorBrush => UsagePalette.BrushFor(ColorIndex);
    public Brush NeutralBrush => UsagePalette.Neutral;

    public bool HasLimit { get; }
    public int LimitMinutes { get; }
    public string LimitText { get; } = string.Empty;
    public string LimitStateText { get; } = string.Empty;
    public string UsedText { get; } = string.Empty;
    public string ExtraText { get; } = string.Empty;
    public double LimitProgress { get; }
    public Brush LimitBrush { get; } = UsagePalette.SecondaryText;
    public bool HasExtra => !string.IsNullOrEmpty(ExtraText);

    /// <summary>True when the app has an admin limit, so excluding it would be a loophole.</summary>
    public bool CanExclude => !HasLimit;
}
