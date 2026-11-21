using System;
using System.IO;
using System.Linq;
using AppLock.Models;
using AppLock.Services.ScreenTime;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

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

/// <summary>Vibrant, high-contrast fixed palette; the index is a stable hash of AppKey.</summary>
public static class UsagePalette
{
    // Carefully selected high-contrast colors with strong visibility (contrast >= 3:1)
    // on both pure white (#FFFFFF) light-theme cards and dark (#202020) surfaces.
    // Avoids Windows theme keys like SystemAccentColorLight1/2 which turn white in light mode.
    private static readonly Windows.UI.Color[] PaletteColors =
    [
        Windows.UI.Color.FromArgb(255, 79, 107, 237),  // 0 Royal Blue / Indigo
        Windows.UI.Color.FromArgb(255, 224, 83, 83),   // 1 Coral Red
        Windows.UI.Color.FromArgb(255, 34, 160, 107),  // 2 Emerald Green
        Windows.UI.Color.FromArgb(255, 230, 115, 0),   // 3 Vivid Orange
        Windows.UI.Color.FromArgb(255, 142, 68, 173),  // 4 Purple / Violet
        Windows.UI.Color.FromArgb(255, 0, 151, 167),   // 5 Teal / Cyan
        Windows.UI.Color.FromArgb(255, 216, 27, 96),   // 6 Raspberry / Magenta
        Windows.UI.Color.FromArgb(255, 217, 119, 6),   // 7 Amber / Dark Gold
    ];

    public static Brush BrushFor(int index)
    {
        var color = PaletteColors[Math.Abs(index) % PaletteColors.Length];
        return new SolidColorBrush(color);
    }

    public static Windows.UI.Color ColorFor(int index)
    {
        return PaletteColors[Math.Abs(index) % PaletteColors.Length];
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

/// <summary>
/// One app line in the Screen Time list. Rows are updated in place by AppKey so a refresh does not
/// rebuild the whole list and replay entrance animations over every row.
/// </summary>
public sealed partial class AppUsageRowViewModel : ObservableObject
{
    private readonly string _displayName;

    public AppUsageRowViewModel(
        AppUsageRow row,
        StoredApp? stored,
        LockRule? rule,
        ScreenTimeSettings settings,
        DateOnly today,
        long rangeTotalSeconds,
        byte[]? iconPng = null)
    {
        AppKey = row.AppKey;
        _displayName = string.IsNullOrWhiteSpace(row.DisplayName) ? row.AppKey : row.DisplayName;
        Initial = string.IsNullOrEmpty(_displayName) ? "?" : _displayName.Substring(0, 1).ToUpperInvariant();
        ExePath = stored?.ExePath;
        Aumid = stored?.Aumid;
        ColorIndex = ScreenTimeStore.StableColorIndex(row.AppKey);
        Icon = CreateImage(iconPng);

        Update(row, rule, settings, today, rangeTotalSeconds, iconPng);
    }

    public string AppKey { get; }
    public string DisplayName => _displayName;
    public string Initial { get; }
    public string? ExePath { get; }
    public string? Aumid { get; }
    public int ColorIndex { get; }

    [ObservableProperty] private ImageSource? _icon;

    [ObservableProperty] private long _seconds;
    [ObservableProperty] private string _timeText = "0m";
    [ObservableProperty] private string _opensText = string.Empty;
    [ObservableProperty] private double _sharePercent;
    [ObservableProperty] private LockRule? _rule;
    [ObservableProperty] private bool _isHidden;

    [ObservableProperty] private bool _hasLimit;
    [ObservableProperty] private int _limitMinutes;
    [ObservableProperty] private string _limitText = string.Empty;
    [ObservableProperty] private string _limitStateText = string.Empty;
    [ObservableProperty] private double _limitProgress;
    [ObservableProperty] private Brush _limitBrush = UsagePalette.SecondaryText;
    [ObservableProperty] private string _extraText = string.Empty;
    [ObservableProperty] private bool _hasExtra;

    public double Share => SharePercent / 100.0;
    public Brush ColorBrush => UsagePalette.BrushFor(ColorIndex);
    public Brush NeutralBrush => UsagePalette.Neutral;

    /// <summary>True when the app has an admin limit, so excluding it would be a loophole.</summary>
    public bool CanExclude => !HasLimit;

    partial void OnHasLimitChanged(bool value) => OnPropertyChanged(nameof(CanExclude));

    partial void OnSharePercentChanged(double value) => OnPropertyChanged(nameof(Share));

    public void Update(AppUsageRow row, LockRule? rule, ScreenTimeSettings settings, DateOnly today, long rangeTotalSeconds, byte[]? iconPng = null)
    {
        if (Icon == null && iconPng is { Length: > 0 })
            Icon = CreateImage(iconPng);

        Seconds = row.Seconds;
        TimeText = UsageFormat.Duration(row.Seconds);
        OpensText = row.Opens == 1 ? "1 open" : $"{row.Opens} opens";
        SharePercent = rangeTotalSeconds > 0
            ? Math.Clamp(row.Seconds / (double)rangeTotalSeconds * 100.0, 0, 100)
            : 0;

        Rule = rule;
        IsHidden = settings.HiddenAppKeys.Any(k => string.Equals(k, AppKey, StringComparison.OrdinalIgnoreCase));

        if (rule?.SelfDiscipline is { QuotaEnabled: true } sd)
        {
            HasLimit = true;
            LimitMinutes = sd.DailyLimitMinutes;
            LimitText = $"{UsageFormat.Duration(sd.DailyLimitMinutes * 60L)} / day";

            int used = sd.QuotaDate == today ? sd.UsedSecondsToday : 0;
            int limit = Math.Max(1, sd.DailyLimitMinutes) * 60;
            int effective = limit + Math.Max(0, sd.ExtraSecondsGrantedToday);

            LimitProgress = Math.Clamp(used / (double)limit * 100.0, 0, 100);

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

            ExtraText = sd.ExtraSecondsGrantedToday > 0
                ? $"+{UsageFormat.ShortDuration(sd.ExtraSecondsGrantedToday)} extra"
                : string.Empty;
            HasExtra = ExtraText.Length > 0;
        }
        else
        {
            HasLimit = false;
            LimitMinutes = 0;
            LimitText = string.Empty;
            LimitStateText = string.Empty;
            LimitProgress = 0;
            LimitBrush = UsagePalette.SecondaryText;
            ExtraText = string.Empty;
            HasExtra = false;
        }
    }

    private static ImageSource? CreateImage(byte[]? png)
    {
        if (png == null || png.Length == 0) return null;

        try
        {
            var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            bitmap.SetSource(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
