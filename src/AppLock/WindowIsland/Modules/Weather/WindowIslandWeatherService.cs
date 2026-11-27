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
    private readonly Dictionary<string, (WeatherForecastData Data, DateTime FetchedUtc)> _memoryCaches = new();

    public event EventHandler<WeatherForecastData?>? WeatherUpdated;
    public event EventHandler? WeatherCitiesChanged;
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
        var city = settings.GetActiveCity();
        if (city.Latitude == 0.0 && city.Longitude == 0.0)
        {
            return CurrentWeather;
        }

        if (city.UtcOffsetSeconds == int.MinValue)
        {
            _ = TryFetchCityUtcOffsetAsync(city);
        }

        string cacheKey = $"{city.Latitude:F2}_{city.Longitude:F2}";

        // Respect 30-minute refresh rate limit unless forced
        if (!force && _memoryCaches.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow - entry.FetchedUtc < TimeSpan.FromMinutes(30))
        {
            lock (_lock)
            {
                _cachedData = entry.Data;
            }
            return entry.Data;
        }

        try
        {
            var fresh = await _provider.GetForecastAsync(city.Latitude, city.Longitude).ConfigureAwait(false);
            if (fresh != null)
            {
                fresh.CityName = city.Name;
                _lastFetchUtc = DateTime.UtcNow;

                lock (_lock)
                {
                    _cachedData = fresh;
                    _memoryCaches[cacheKey] = (fresh, DateTime.UtcNow);
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

    public async Task AddCityAsync(string cityName, double lat, double lon, string country = "")
    {
        var s = SettingsProvider();
        var list = s.GetWeatherCities();
        int existing = list.FindIndex(c => Math.Abs(c.Latitude - lat) < 0.05 && Math.Abs(c.Longitude - lon) < 0.05);
        if (existing >= 0)
        {
            s.ActiveWeatherCityIndex = existing;
        }
        else
        {
            list.Add(new WeatherCity(cityName, Math.Round(lat, 4), Math.Round(lon, 4), country));
            s.ActiveWeatherCityIndex = list.Count - 1;
        }

        var active = list[s.ActiveWeatherCityIndex];
        s.WeatherCity = active.Name;
        s.WeatherLatitude = active.Latitude;
        s.WeatherLongitude = active.Longitude;
        SettingsSaver?.Invoke(s);

        _ = TryFetchCityUtcOffsetAsync(active);
        WeatherCitiesChanged?.Invoke(this, EventArgs.Empty);

        await RefreshAsync(force: true).ConfigureAwait(false);
    }

    public static TimeSpan GetCityUtcOffset(WeatherCity city)
    {
        if (city.UtcOffsetSeconds != int.MinValue)
        {
            return TimeSpan.FromSeconds(city.UtcOffsetSeconds);
        }

        // Check if within local machine timezone range (~1.5 hours)
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        double approxSolarHours = city.Longitude / 15.0;
        if (Math.Abs(localOffset.TotalHours - approxSolarHours) <= 1.5)
        {
            return localOffset;
        }

        // 15-minute standard interval fallback (e.g. Nepal +5:45, India +5:30)
        double rounded15MinHours = Math.Round(approxSolarHours * 4.0) / 4.0;
        return TimeSpan.FromHours(Math.Clamp(rounded15MinHours, -12, 14));
    }

    public static async Task TryFetchCityUtcOffsetAsync(WeatherCity city)
    {
        try
        {
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={city.Latitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}&longitude={city.Longitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m&timezone=auto";
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string json = await client.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("utc_offset_seconds", out var prop))
            {
                city.UtcOffsetSeconds = prop.GetInt32();
                var s = SettingsProvider();
                SettingsSaver?.Invoke(s);
            }
        }
        catch { }
    }

    public async Task SelectCityAsync(int index)
    {
        var s = SettingsProvider();
        var list = s.GetWeatherCities();
        if (index < 0 || index >= list.Count) return;

        s.ActiveWeatherCityIndex = index;
        var active = list[index];
        s.WeatherCity = active.Name;
        s.WeatherLatitude = active.Latitude;
        s.WeatherLongitude = active.Longitude;
        SettingsSaver?.Invoke(s);
        WeatherCitiesChanged?.Invoke(this, EventArgs.Empty);

        await RefreshAsync(force: false).ConfigureAwait(false);
    }

    public async Task RemoveCityAsync(int index)
    {
        var s = SettingsProvider();
        var list = s.GetWeatherCities();
        if (list.Count <= 1 || index < 0 || index >= list.Count) return;

        list.RemoveAt(index);
        if (s.ActiveWeatherCityIndex >= list.Count)
        {
            s.ActiveWeatherCityIndex = list.Count - 1;
        }
        var active = list[s.ActiveWeatherCityIndex];
        s.WeatherCity = active.Name;
        s.WeatherLatitude = active.Latitude;
        s.WeatherLongitude = active.Longitude;
        SettingsSaver?.Invoke(s);
        WeatherCitiesChanged?.Invoke(this, EventArgs.Empty);

        await RefreshAsync(force: false).ConfigureAwait(false);
    }

    public async Task SetLocationAsync(string cityName, double lat, double lon)
    {
        await AddCityAsync(cityName, lat, lon).ConfigureAwait(false);
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
                    string icon = isSnow ? "\u2744" : "\u2614";
                    string detail = pt.PrecipitationMm > 0
                        ? $"{eventType} expected in ~{(int)Math.Max(15, Math.Round(diff.TotalMinutes))} min ({pt.PrecipitationMm:0.0} mm)"
                        : $"{eventType} expected in ~{(int)Math.Max(15, Math.Round(diff.TotalMinutes))} min";

                    RainWarningTriggered?.Invoke(this, new WindowIslandCard(
                        Kind: "weather-rain",
                        Icon: icon,
                        Title: $"{eventType} Alert",
                        Detail: detail,
                        AccentHex: "#60CDFF",
                        Priority: WindowIslandCardPriority.Rain,
                        StayMs: 5000,
                        IsStacked: true));

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
