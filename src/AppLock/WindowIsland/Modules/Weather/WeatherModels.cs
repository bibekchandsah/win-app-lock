using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.WindowIsland.Modules.Weather;

public sealed class WeatherLocationResult
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Country { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
}

public sealed class WeatherHourlyPoint
{
    public DateTime Time { get; set; }
    public double TempC { get; set; }
    public string SymbolCode { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "\uE706";
    public string ConditionText { get; set; } = string.Empty;
    public double PrecipitationMm { get; set; }
}

public sealed class WeatherDailyPoint
{
    public DateTime Date { get; set; }
    public string DayName { get; set; } = string.Empty;
    public double TempHighC { get; set; }
    public double TempLowC { get; set; }
    public string SymbolCode { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "\uE706";
    public string ConditionText { get; set; } = string.Empty;
    public double PrecipitationMm { get; set; }
}

public sealed class WeatherForecastData
{
    public string CityName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double CurrentTempC { get; set; }
    public double FeelsLikeC { get; set; }
    public string ConditionText { get; set; } = "Clear";
    public string SymbolCode { get; set; } = "clearsky_day";
    public string IconGlyph { get; set; } = "\uE706";
    public double HighC { get; set; }
    public double LowC { get; set; }
    public string SummaryOutlook { get; set; } = string.Empty;

    public double WindSpeedMps { get; set; }
    public double WindDirectionDeg { get; set; }
    public string WindDirectionCompass { get; set; } = "N";

    public double HumidityPercent { get; set; }
    public double DewPointC { get; set; }

    public double PressureHpa { get; set; }
    public string PressureTrend { get; set; } = "Steady";

    public DateTime Sunrise { get; set; }
    public DateTime Sunset { get; set; }

    public double PrecipitationTodayMm { get; set; }
    public double UvIndex { get; set; }
    public string UvLevel { get; set; } = "Low";

    public DateTime UpdatedTime { get; set; } = DateTime.UtcNow;

    public List<WeatherHourlyPoint> Hourly { get; set; } = new();
    public List<WeatherDailyPoint> Daily { get; set; } = new();
}
