using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
#if !APPLOCK_TESTS
using AppLock.Services;
#endif
using AppLock.WindowIsland.Events;

namespace AppLock.WindowIsland.Modules.Weather;

public sealed class WindowIslandWeatherService
{
    private static readonly Lazy<WindowIslandWeatherService> _instance = new(() => new WindowIslandWeatherService());
    public static WindowIslandWeatherService Instance => _instance.Value;

#if APPLOCK_TESTS
    public static Func<WindowIslandSettings> SettingsProvider { get; set; } = () => new WindowIslandSettings();
    public static Action<WindowIslandSettings>? SettingsSaver { get; set; } = _ => { };
#else
    public static Func<WindowIslandSettings> SettingsProvider { get; set; } = () => ServiceContainer.Settings.WindowIsland;
    public static Action<WindowIslandSettings>? SettingsSaver { get; set; } = _ => ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
#endif

    private readonly IWeatherProvider _provider;
    private readonly string _cacheFilePath;
    private readonly System.Threading.Timer _refreshTimer;
    private readonly object _lock = new();

    private WeatherForecastData? _cachedData;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private DateTime _lastRainWarningTime = DateTime.MinValue;

    public event EventHandler<WeatherForecastData?>? WeatherUpdated;
    public event EventHandler<WindowIslandCard>? RainWarningTriggered;

    public WeatherForecastData? CurrentWeather
    {
        get
        {
            lock (_lock) return _cachedData;
        }
    }

    public WindowIslandWeatherService() : this(new MetNorwayWeatherProvider())
    {
    }

    public WindowIslandWeatherService(IWeatherProvider provider)
    {
        _provider = provider;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "island");
        Directory.CreateDirectory(dir);
        _cacheFilePath = Path.Combine(dir, "weather_cache.json");

        LoadCacheFromDisk();

        // Check weather every 30 minutes
        _refreshTimer = new System.Threading.Timer(OnTimerTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30));
    }

    private void OnTimerTick(object? state)
    {
        _ = RefreshAsync(force: false);
    }

    public void LoadCacheFromDisk()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                string json = File.ReadAllText(_cacheFilePath);
                var data = JsonSerializer.Deserialize<WeatherForecastData>(json);
                if (data != null)
                {
                    lock (_lock)
                    {
                        _cachedData = data;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveCacheToDisk(WeatherForecastData data)
    {
        try
        {
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheFilePath, json);
        }
        catch { }
    }

    public async Task<WeatherForecastData?> RefreshAsync(bool force = false)
    {
        var settings = SettingsProvider();
        if (settings.WeatherLatitude == 0.0 && settings.WeatherLongitude == 0.0)
        {
            return CurrentWeather;
        }

        // Respect 30-minute refresh rate limit unless forced
        if (!force && DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromMinutes(30) && _cachedData != null)
        {
            return _cachedData;
        }

        try
        {
            var fresh = await _provider.GetForecastAsync(settings.WeatherLatitude, settings.WeatherLongitude).ConfigureAwait(false);
            if (fresh != null)
            {
                fresh.CityName = settings.WeatherCity;
                _lastFetchUtc = DateTime.UtcNow;

                lock (_lock)
                {
                    _cachedData = fresh;
                }
                SaveCacheToDisk(fresh);

                CheckRainSnowWarning(fresh);
                WeatherUpdated?.Invoke(this, fresh);
                return fresh;
            }
        }
        catch { }

        return CurrentWeather;
    }

    public async Task<IReadOnlyList<WeatherLocationResult>> SearchCitiesAsync(string query, CancellationToken ct = default)
    {
        return await _provider.SearchLocationsAsync(query, ct).ConfigureAwait(false);
    }

    public async Task SetLocationAsync(string cityName, double lat, double lon)
    {
        var s = SettingsProvider();
        s.WeatherCity = cityName;
        s.WeatherLatitude = Math.Round(lat, 4);
        s.WeatherLongitude = Math.Round(lon, 4);
        SettingsSaver?.Invoke(s);

        await RefreshAsync(force: true).ConfigureAwait(false);
    }

    public void CheckRainSnowWarning(WeatherForecastData data)
    {
        var s = SettingsProvider();
        if (!s.WeatherRainWarning || data.Hourly == null || data.Hourly.Count == 0)
            return;

        // Check if rain or snow is predicted within the next 1-2 hours
        DateTime now = DateTime.Now;
        foreach (var pt in data.Hourly)
        {
            var diff = pt.Time - now;
            if (diff.TotalHours >= 0.25 && diff.TotalHours <= 1.75)
            {
                bool isSnow = pt.SymbolCode.Contains("snow") || pt.SymbolCode.Contains("sleet");
                bool isRain = pt.SymbolCode.Contains("rain") || pt.PrecipitationMm >= 0.2;

                if (isSnow || isRain)
                {
                    // Avoid repeating warning for the same event
                    if ((now - _lastRainWarningTime).TotalHours < 3)
                        return;

                    _lastRainWarningTime = now;
                    string eventType = isSnow ? "Snow" : "Rain";
                    string icon = isSnow ? "\uE774" : "\uE754";
                    string detail = pt.PrecipitationMm > 0
                        ? $"{eventType} expected in ~{(int)Math.Max(15, Math.Round(diff.TotalMinutes))} min ({pt.PrecipitationMm:0.0} mm)"
                        : $"{eventType} expected in ~{(int)Math.Max(15, Math.Round(diff.TotalMinutes))} min";

                    RainWarningTriggered?.Invoke(this, new WindowIslandCard(
                        Kind: "weather-rain",
                        Icon: icon,
                        Title: $"{eventType} Alert",
                        Detail: detail,
                        AccentHex: isSnow ? "#60CDFF" : "#60CDFF",
                        Priority: WindowIslandCardPriority.Rain,
                        StayMs: 5000));

                    break;
                }
            }
        }
    }

    // ── Formatting Helpers ─────────────────────────────────────────────────────

    public static string FormatTemp(double tempC, string units)
    {
        if (string.Equals(units, "imperial", StringComparison.OrdinalIgnoreCase))
        {
            double f = (tempC * 9.0 / 5.0) + 32.0;
            return $"{Math.Round(f)}°";
        }
        return $"{Math.Round(tempC)}°";
    }

    public static string FormatSpeed(double mps, string units)
    {
        if (string.Equals(units, "imperial", StringComparison.OrdinalIgnoreCase))
        {
            double mph = mps * 2.23694;
            return $"{Math.Round(mph, 1)} mph";
        }
        double kmh = mps * 3.6;
        return $"{Math.Round(kmh, 1)} km/h";
    }
}
