using System;

namespace AppLock.WindowIsland.Modules.Weather;

/// <summary>
/// Semantic visual theme for the Window Island Weather hero card based on
/// current weather conditions, wind speed, and local time of day.
/// </summary>
public enum WeatherThemeKind
{
    Sunshine,
    Raining,
    Cloudy,
    Thunderstorm,
    Wind,
    Morning,
    Evening,
    Sunrise,
    Sunset,
    Snow,
    Fog,
    Night
}

public sealed record WeatherCardPalette(
    WeatherThemeKind Kind,
    uint TopColor,
    uint MiddleColor,
    uint BottomColor,
    uint BorderColor,
    uint IconColor);

public static class WeatherCardTheme
{
    public static WeatherCardPalette Resolve(
        string? symbolCode,
        string? conditionText,
        double windSpeedMps,
        TimeSpan localTime,
        TimeSpan sunrise,
        TimeSpan sunset,
        bool isDaytime)
    {
        var kind = ResolveKind(symbolCode, conditionText, windSpeedMps, localTime, sunrise, sunset, isDaytime);
        return GetPalette(kind, isDaytime);
    }

    public static WeatherThemeKind ResolveKind(
        string? symbolCode,
        string? conditionText,
        double windSpeedMps,
        TimeSpan localTime,
        TimeSpan sunrise,
        TimeSpan sunset,
        bool isDaytime)
    {
        string sym = (symbolCode ?? string.Empty).ToLowerInvariant();
        string cond = (conditionText ?? string.Empty).ToLowerInvariant();

        // 1. High-priority weather phenomena
        if (sym.Contains("thunder") || cond.Contains("thunder") || cond.Contains("storm"))
            return WeatherThemeKind.Thunderstorm;

        if (sym.Contains("rain") || sym.Contains("drizzle") || sym.Contains("shower") ||
            cond.Contains("rain") || cond.Contains("drizzle") || cond.Contains("shower"))
            return WeatherThemeKind.Raining;

        if (sym.Contains("snow") || sym.Contains("sleet") ||
            cond.Contains("snow") || cond.Contains("sleet") || cond.Contains("blizzard"))
            return WeatherThemeKind.Snow;

        if (sym.Contains("fog") || sym.Contains("mist") ||
            cond.Contains("fog") || cond.Contains("mist") || cond.Contains("haze"))
            return WeatherThemeKind.Fog;

        // 2. Solar transitions (within 45 minutes of sunrise / sunset)
        if (sunset > sunrise)
        {
            if (Math.Abs((localTime - sunrise).TotalMinutes) <= 45)
                return WeatherThemeKind.Sunrise;

            if (Math.Abs((localTime - sunset).TotalMinutes) <= 45)
                return WeatherThemeKind.Sunset;
        }

        // 3. Strong wind
        if (windSpeedMps >= 9.0 || cond.Contains("wind"))
            return WeatherThemeKind.Wind;

        // 4. Cloudy / overcast / partly cloudy
        if (sym.Contains("cloud") || cond.Contains("cloud") || cond.Contains("overcast"))
            return WeatherThemeKind.Cloudy;

        // 5. Time-of-day clear themes
        if (isDaytime)
        {
            if (localTime.TotalHours < 11.5)
                return WeatherThemeKind.Morning;

            if (sunset > sunrise && (sunset - localTime).TotalMinutes <= 90)
                return WeatherThemeKind.Evening;

            return WeatherThemeKind.Sunshine;
        }

        if (sunset > sunrise && localTime > sunset && (localTime - sunset).TotalMinutes <= 120)
            return WeatherThemeKind.Evening;

        return WeatherThemeKind.Night;
    }

    public static WeatherCardPalette GetPalette(WeatherThemeKind kind, bool isDaytime = true) => kind switch
    {
        WeatherThemeKind.Thunderstorm => new(
            kind,
            0xFF231F3A, 0xFF392B58, 0xFF141222,
            0x40B39DDB, 0xFFFFD54F),

        WeatherThemeKind.Raining => new(
            kind,
            0xFF283E51, 0xFF1F364D, 0xFF12202E,
            0x3881D4FA, 0xFF64B5F6),

        WeatherThemeKind.Snow => new(
            kind,
            0xFF3C5A78, 0xFF2C455E, 0xFF1B2D3E,
            0x45E1F5FE, 0xFFE8F6FF),

        WeatherThemeKind.Fog => new(
            kind,
            0xFF4A5863, 0xFF36424B, 0xFF222B32,
            0x35CFD8DC, 0xFFCFD8DC),

        WeatherThemeKind.Sunrise => new(
            kind,
            0xFFD96B43, 0xFFA24B74, 0xFF3D2856,
            0x40FFCC80, 0xFFFFD166),

        WeatherThemeKind.Sunset => new(
            kind,
            0xFFDF5845, 0xFF8E3A6E, 0xFF2E1E47,
            0x40FFAB91, 0xFFFFAB40),

        WeatherThemeKind.Wind => new(
            kind,
            0xFF1F637A, 0xFF15425E, 0xFF0D273B,
            0x3880DEEA, 0xFF80DEEA),

        WeatherThemeKind.Cloudy => isDaytime
            ? new(kind, 0xFF4B637E, 0xFF364B63, 0xFF223142, 0x35FFFFFF, 0xFFE3ECF5)
            : new(kind, 0xFF26323F, 0xFF1B242E, 0xFF11171F, 0x28FFFFFF, 0xFFC5D1DE),

        WeatherThemeKind.Morning => new(
            kind,
            0xFF2F89C9, 0xFF236BA3, 0xFF174A75,
            0x38FFE082, 0xFFFFE066),

        WeatherThemeKind.Evening => new(
            kind,
            0xFF2F366E, 0xFF1F244D, 0xFF121530,
            0x359FA8DA, 0xFFC5CAE9),

        WeatherThemeKind.Sunshine => new(
            kind,
            0xFF1E88E5, 0xFF1565C0, 0xFF0D47A1,
            0x38FFFFFF, 0xFFFFEB3B),

        _ => new(
            WeatherThemeKind.Night,
            0xFF141E30, 0xFF0D1524, 0xFF070B14,
            0x28FFFFFF, 0xFFE2EAF8)
    };
}
