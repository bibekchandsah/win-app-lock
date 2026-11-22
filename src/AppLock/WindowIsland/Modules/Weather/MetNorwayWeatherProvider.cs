using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.WindowIsland.Modules.Weather;

public sealed class MetNorwayWeatherProvider : IWeatherProvider
{
    private static readonly HttpClient _httpClient;
    private static DateTime _lastNominatimCallUtc = DateTime.MinValue;
    private static readonly object _nominatimLock = new();

    static MetNorwayWeatherProvider()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AppLock/1.0 (https://github.com/applock/applock; contact@applock.local)");
    }

    public async Task<WeatherForecastData?> GetForecastAsync(double lat, double lon, CancellationToken ct = default)
    {
        try
        {
            // MET Norway requires lat/lon rounded to max 4 decimal places
            string latStr = lat.ToString("F4", CultureInfo.InvariantCulture);
            string lonStr = lon.ToString("F4", CultureInfo.InvariantCulture);
            string url = $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={latStr}&lon={lonStr}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseMetNorwayJson(json, lat, lon);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WeatherLocationResult>> SearchLocationsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<WeatherLocationResult>();

        // Rate limit Nominatim: max 1 request per second
        lock (_nominatimLock)
        {
            var elapsed = DateTime.UtcNow - _lastNominatimCallUtc;
            if (elapsed < TimeSpan.FromSeconds(1))
            {
                Thread.Sleep(TimeSpan.FromSeconds(1) - elapsed);
            }
            _lastNominatimCallUtc = DateTime.UtcNow;
        }

        try
        {
            string encoded = Uri.EscapeDataString(query.Trim());
            string url = $"https://nominatim.openstreetmap.org/search?q={encoded}&format=json&limit=5&addressdetails=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<WeatherLocationResult>();

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var list = new List<WeatherLocationResult>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string name = item.TryGetProperty("name", out var n) && !string.IsNullOrEmpty(n.GetString())
                    ? n.GetString()!
                    : (item.TryGetProperty("display_name", out var dn) ? dn.GetString()!.Split(',')[0].Trim() : query);

                string displayName = item.TryGetProperty("display_name", out var dnp) ? dnp.GetString() ?? name : name;
                string latStr = item.TryGetProperty("lat", out var lt) ? lt.GetString() ?? "0" : "0";
                string lonStr = item.TryGetProperty("lon", out var ln) ? ln.GetString() ?? "0" : "0";

                double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat);
                double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon);

                string country = string.Empty;
                string countryCode = string.Empty;
                if (item.TryGetProperty("address", out var addr))
                {
                    if (addr.TryGetProperty("country", out var c)) country = c.GetString() ?? string.Empty;
                    if (addr.TryGetProperty("country_code", out var cc)) countryCode = cc.GetString()?.ToUpperInvariant() ?? string.Empty;
                }

                list.Add(new WeatherLocationResult
                {
                    Name = name,
                    DisplayName = displayName,
                    Latitude = lat,
                    Longitude = lon,
                    Country = country,
                    CountryCode = countryCode
                });
            }

            return list;
        }
        catch
        {
            return Array.Empty<WeatherLocationResult>();
        }
    }

    public static WeatherForecastData? ParseMetNorwayJson(string json, double lat, double lon)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("properties", out var props) ||
                !props.TryGetProperty("timeseries", out var timeseries))
            {
                return null;
            }

            var points = timeseries.EnumerateArray();
            if (!points.MoveNext()) return null;

            var firstPoint = points.Current;
            var dataElem = firstPoint.GetProperty("data");
            var instant = dataElem.GetProperty("instant").GetProperty("details");

            double currentTemp = instant.GetProperty("air_temperature").GetDouble();
            double windSpeed   = instant.TryGetProperty("wind_speed", out var ws) ? ws.GetDouble() : 0.0;
            double windDir     = instant.TryGetProperty("wind_from_direction", out var wd) ? wd.GetDouble() : 0.0;
            double humidity    = instant.TryGetProperty("relative_humidity", out var rh) ? rh.GetDouble() : 50.0;
            double pressure    = instant.TryGetProperty("air_pressure_at_sea_level", out var pr) ? pr.GetDouble() : 1013.25;
            double dewPoint    = instant.TryGetProperty("dew_point_temperature", out var dp) ? dp.GetDouble() : CalculateDewPoint(currentTemp, humidity);

            // Symbol code from next_1_hours or next_6_hours
            string symbolCode = "clearsky_day";
            double precipNext1h = 0.0;
            if (dataElem.TryGetProperty("next_1_hours", out var n1))
            {
                if (n1.TryGetProperty("summary", out var sm1) && sm1.TryGetProperty("symbol_code", out var sc1))
                    symbolCode = sc1.GetString() ?? "clearsky_day";
                if (n1.TryGetProperty("details", out var dt1) && dt1.TryGetProperty("precipitation_amount", out var pa1))
                    precipNext1h = pa1.GetDouble();
            }
            else if (dataElem.TryGetProperty("next_6_hours", out var n6))
            {
                if (n6.TryGetProperty("summary", out var sm6) && sm6.TryGetProperty("symbol_code", out var sc6))
                    symbolCode = sc6.GetString() ?? "clearsky_day";
            }

            var result = new WeatherForecastData
            {
                Latitude = lat,
                Longitude = lon,
                CurrentTempC = currentTemp,
                FeelsLikeC = CalculateWindChillOrHeatIndex(currentTemp, windSpeed, humidity),
                SymbolCode = symbolCode,
                IconGlyph = SymbolToGlyph(symbolCode),
                ConditionText = SymbolToConditionText(symbolCode),
                WindSpeedMps = windSpeed,
                WindDirectionDeg = windDir,
                WindDirectionCompass = DegreesToCompass(windDir),
                HumidityPercent = humidity,
                DewPointC = dewPoint,
                PressureHpa = pressure,
                UpdatedTime = DateTime.UtcNow
            };

            // NOAA Sunrise / Sunset calculation
            var (sunrise, sunset) = NoaaSolarCalculator.CalculateSunriseSunset(lat, lon, DateTime.Today);
            result.Sunrise = sunrise ?? DateTime.Today.AddHours(6);
            result.Sunset  = sunset  ?? DateTime.Today.AddHours(18);

            // UV index calculation based on solar elevation
            result.UvIndex = CalculateEstimatedUv(lat, lon, DateTime.UtcNow);
            result.UvLevel = GetUvLevelName(result.UvIndex);

            // Parse Hourly & Daily points
            var hourlyList = new List<WeatherHourlyPoint>();
            var dailyMap   = new Dictionary<DateTime, List<(double temp, string sym, double precip)>>();

            double todayHigh = currentTemp;
            double todayLow  = currentTemp;
            double todayPrecip = precipNext1h;

            DateTime now = DateTime.UtcNow;
            DateTime prevPressureTime = DateTime.MinValue;
            double prevPressureVal = pressure;

            foreach (var pt in timeseries.EnumerateArray())
            {
                if (!pt.TryGetProperty("time", out var timeProp) ||
                    !DateTime.TryParse(timeProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime ptTimeUtc))
                {
                    continue;
                }

                var d = pt.GetProperty("data");
                var inst = d.GetProperty("instant").GetProperty("details");
                double temp = inst.GetProperty("air_temperature").GetDouble();

                string sym = symbolCode;
                double precip = 0.0;
                if (d.TryGetProperty("next_1_hours", out var nx1))
                {
                    if (nx1.TryGetProperty("summary", out var s1) && s1.TryGetProperty("symbol_code", out var sc1))
                        sym = sc1.GetString() ?? sym;
                    if (nx1.TryGetProperty("details", out var dt) && dt.TryGetProperty("precipitation_amount", out var pamt))
                        precip = pamt.GetDouble();
                }
                else if (d.TryGetProperty("next_6_hours", out var nx6))
                {
                    if (nx6.TryGetProperty("summary", out var s6) && s6.TryGetProperty("symbol_code", out var sc6))
                        sym = sc6.GetString() ?? sym;
                    if (nx6.TryGetProperty("details", out var dt6) && dt6.TryGetProperty("precipitation_amount", out var pamt6))
                        precip = pamt6.GetDouble();
                }

                // Check pressure trend around 3 hours ago
                if (inst.TryGetProperty("air_pressure_at_sea_level", out var prVal))
                {
                    double p = prVal.GetDouble();
                    if (prevPressureTime == DateTime.MinValue && (ptTimeUtc - now).TotalHours >= 2.5 && (ptTimeUtc - now).TotalHours <= 4.0)
                    {
                        prevPressureTime = ptTimeUtc;
                        prevPressureVal = p;
                    }
                }

                DateTime localTime = ptTimeUtc.ToLocalTime();
                DateTime localDate = localTime.Date;

                // Today's high/low & precip
                if (localDate == DateTime.Today)
                {
                    if (temp > todayHigh) todayHigh = temp;
                    if (temp < todayLow)  todayLow  = temp;
                    todayPrecip += precip;
                }

                // Collect hourly points (up to next 24h)
                if (hourlyList.Count < 24 && ptTimeUtc >= now.AddMinutes(-30))
                {
                    hourlyList.Add(new WeatherHourlyPoint
                    {
                        Time = localTime,
                        TempC = temp,
                        SymbolCode = sym,
                        IconGlyph = SymbolToGlyph(sym),
                        ConditionText = SymbolToConditionText(sym),
                        PrecipitationMm = precip
                    });
                }

                // Group by day for 7-day outlook
                if (!dailyMap.TryGetValue(localDate, out var dayPts))
                {
                    dayPts = new List<(double temp, string sym, double precip)>();
                    dailyMap[localDate] = dayPts;
                }
                dayPts.Add((temp, sym, precip));
            }

            result.HighC = todayHigh;
            result.LowC = todayLow;
            result.PrecipitationTodayMm = Math.Round(todayPrecip, 1);
            result.Hourly = hourlyList;

            // Pressure trend
            double pDiff = pressure - prevPressureVal;
            result.PressureTrend = pDiff > 1.5 ? "Rising" : (pDiff < -1.5 ? "Falling" : "Steady");

            // Process daily points (next 7 days)
            var dailyList = new List<WeatherDailyPoint>();
            foreach (var kvp in dailyMap)
            {
                DateTime d = kvp.Key;
                var pts = kvp.Value;
                if (pts.Count == 0) continue;

                double dHigh = double.MinValue;
                double dLow = double.MaxValue;
                double dPrecip = 0.0;
                string dSym = symbolCode;

                foreach (var p in pts)
                {
                    if (p.temp > dHigh) dHigh = p.temp;
                    if (p.temp < dLow)  dLow  = p.temp;
                    dPrecip += p.precip;
                    if (!string.IsNullOrEmpty(p.sym)) dSym = p.sym;
                }

                string dayName = d == DateTime.Today ? "Today" : d.ToString("ddd");
                dailyList.Add(new WeatherDailyPoint
                {
                    Date = d,
                    DayName = dayName,
                    TempHighC = dHigh,
                    TempLowC = dLow,
                    SymbolCode = dSym,
                    IconGlyph = SymbolToGlyph(dSym),
                    ConditionText = SymbolToConditionText(dSym),
                    PrecipitationMm = Math.Round(dPrecip, 1)
                });

                if (dailyList.Count >= 7) break;
            }
            result.Daily = dailyList;

            // Summary outlook
            result.SummaryOutlook = GenerateSummaryOutlook(result);

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateSummaryOutlook(WeatherForecastData data)
    {
        string cond = data.ConditionText.ToLowerInvariant();
        int high = (int)Math.Round(data.HighC);
        int low = (int)Math.Round(data.LowC);

        if (data.PrecipitationTodayMm > 0.5)
        {
            return $"{data.ConditionText} with {data.PrecipitationTodayMm:0.0} mm precipitation, high of {high}°, dropping to {low}° at night.";
        }
        return $"{data.ConditionText} throughout the day, high of {high}°, down to {low}° tonight.";
    }

    public static string SymbolToGlyph(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return "\uE706";
        string s = symbol.ToLowerInvariant();

        if (s.Contains("thunder")) return "\uE755";
        if (s.Contains("heavyrain")) return "\uE754";
        if (s.Contains("sleet")) return "\uE776";
        if (s.Contains("snow")) return "\uE774";
        if (s.Contains("rain")) return "\uE754";
        if (s.Contains("fog")) return "\uE752";
        if (s.Contains("partlycloudy")) return "\uE70C";
        if (s.Contains("fair")) return "\uE70C";
        if (s.Contains("cloudy")) return "\uE753";
        if (s.Contains("clear")) return s.Contains("night") ? "\uE708" : "\uE706";

        return "\uE706";
    }

    public static string SymbolToConditionText(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return "Clear";
        string s = symbol.ToLowerInvariant();

        if (s.Contains("heavyrainshowers_and_thunder") || s.Contains("heavyrain_and_thunder")) return "Heavy Rain & Thunder";
        if (s.Contains("thunder")) return "Thunderstorm";
        if (s.Contains("heavyrain")) return "Heavy Rain";
        if (s.Contains("rainshowers")) return "Rain Showers";
        if (s.Contains("rain")) return "Rain";
        if (s.Contains("heavysnow")) return "Heavy Snow";
        if (s.Contains("snowshowers")) return "Snow Showers";
        if (s.Contains("snow")) return "Snow";
        if (s.Contains("sleet")) return "Sleet";
        if (s.Contains("fog")) return "Foggy";
        if (s.Contains("partlycloudy")) return "Partly Cloudy";
        if (s.Contains("fair")) return "Fair Sky";
        if (s.Contains("cloudy")) return "Cloudy";
        if (s.Contains("clearsky")) return "Clear Sky";

        return "Clear";
    }

    private static double CalculateDewPoint(double t, double rh)
    {
        // Magnus formula approximation
        double a = 17.27;
        double b = 237.7;
        double alpha = ((a * t) / (b + t)) + Math.Log(Math.Max(rh, 1) / 100.0);
        return Math.Round((b * alpha) / (a - alpha), 1);
    }

    private static double CalculateWindChillOrHeatIndex(double t, double windMps, double rh)
    {
        // For cold weather with wind: Wind Chill (Environment Canada / NOAA)
        double windKmh = windMps * 3.6;
        if (t <= 10.0 && windKmh >= 4.8)
        {
            double wc = 13.12 + (0.6215 * t) - (11.37 * Math.Pow(windKmh, 0.16)) + (0.3965 * t * Math.Pow(windKmh, 0.16));
            return Math.Round(wc, 1);
        }

        // For hot humid weather: Heat Index approximation
        if (t >= 26.0 && rh >= 40.0)
        {
            double tf = (t * 9.0 / 5.0) + 32.0;
            double hi = 0.5 * (tf + 61.0 + ((tf - 68.0) * 1.2) + (rh * 0.094));
            if (hi >= 80.0)
            {
                hi = -42.379 + 2.04901523 * tf + 10.14333127 * rh
                     - 0.22475541 * tf * rh - 0.00683783 * tf * tf
                     - 0.05481717 * rh * rh + 0.00122874 * tf * tf * rh
                     + 0.00085282 * tf * rh * rh - 0.00000199 * tf * tf * rh * rh;
            }
            double hic = (hi - 32.0) * 5.0 / 9.0;
            return Math.Round(hic, 1);
        }

        return Math.Round(t, 1);
    }

    private static string DegreesToCompass(double deg)
    {
        string[] car = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        int idx = (int)Math.Round((deg % 360.0) / 22.5) % 16;
        return car[idx];
    }

    private static double CalculateEstimatedUv(double lat, double lon, DateTime utc)
    {
        // Solar elevation angle approximation for clear sky UV index
        int d = utc.DayOfYear;
        double dec = 23.45 * Math.Sin(Math.PI / 180.0 * (360.0 / 365.0 * (d - 81)));
        double hourAngle = 15.0 * (utc.Hour + (utc.Minute / 60.0) - 12.0) + lon;

        double sinAlt = Math.Sin(Math.PI / 180.0 * lat) * Math.Sin(Math.PI / 180.0 * dec) +
                        Math.Cos(Math.PI / 180.0 * lat) * Math.Cos(Math.PI / 180.0 * dec) * Math.Cos(Math.PI / 180.0 * hourAngle);

        double alt = Math.Asin(Math.Max(-1.0, Math.Min(1.0, sinAlt))) * 180.0 / Math.PI;
        if (alt <= 0) return 0.0;

        // Peak summer solar elevation ~65-90 deg corresponds to UV 8-12
        double uv = Math.Sin(alt * Math.PI / 180.0) * 10.5;
        return Math.Round(Math.Max(0.0, uv), 1);
    }

    private static string GetUvLevelName(double uv) => uv switch
    {
        < 3.0 => "Low",
        < 6.0 => "Moderate",
        < 8.0 => "High",
        < 11.0 => "Very High",
        _ => "Extreme"
    };
}
