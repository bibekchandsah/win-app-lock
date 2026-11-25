using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.WindowIsland.Events;
using AppLock.WindowIsland.Modules.Weather;
using Xunit;

namespace AppLock.Tests;

public class WindowIslandWeatherTests
{
    [Fact]
    public void NoaaSolarCalculator_ComputesRealisticSunriseSunset()
    {
        // Oslo: Lat 59.91, Lon 10.75 on vernal equinox (March 20)
        var date = new DateTime(2026, 3, 20);
        var (sunrise, sunset) = NoaaSolarCalculator.CalculateSunriseSunset(59.91, 10.75, date);

        Assert.NotNull(sunrise);
        Assert.NotNull(sunset);
        Assert.True(sunrise!.Value < sunset!.Value);
        // Around equinox, sunrise is approximately 6:00 and sunset 18:00 local time (+/- 1.5h)
        Assert.InRange(sunrise.Value.Hour, 4, 8);
        Assert.InRange(sunset.Value.Hour, 17, 21);
    }

    [Fact]
    public void MetNorwayParser_ParsesValidJsonCorrectly()
    {
        string t1 = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string t2 = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        string sampleJson = $@"{{
            ""properties"": {{
                ""timeseries"": [
                    {{
                        ""time"": ""{t1}"",
                        ""data"": {{
                            ""instant"": {{
                                ""details"": {{
                                    ""air_temperature"": 18.5,
                                    ""wind_speed"": 4.2,
                                    ""wind_from_direction"": 180.0,
                                    ""relative_humidity"": 65.0,
                                    ""air_pressure_at_sea_level"": 1015.0,
                                    ""dew_point_temperature"": 11.8
                                }}
                            }},
                            ""next_1_hours"": {{
                                ""summary"": {{ ""symbol_code"": ""partlycloudy_day"" }},
                                ""details"": {{ ""precipitation_amount"": 0.2 }}
                            }}
                        }}
                    }},
                    {{
                        ""time"": ""{t2}"",
                        ""data"": {{
                            ""instant"": {{
                                ""details"": {{
                                    ""air_temperature"": 19.0,
                                    ""wind_speed"": 4.5
                                }}
                            }},
                            ""next_1_hours"": {{
                                ""summary"": {{ ""symbol_code"": ""rain"" }},
                                ""details"": {{ ""precipitation_amount"": 1.5 }}
                            }}
                        }}
                    }}
                ]
            }}
        }}";

        var data = MetNorwayWeatherProvider.ParseMetNorwayJson(sampleJson, 51.5074, -0.1278);
        Assert.NotNull(data);
        Assert.Equal(18.5, data!.CurrentTempC);
        Assert.Equal("partlycloudy_day", data.SymbolCode);
        Assert.Equal("Partly Cloudy", data.ConditionText);
        Assert.Equal(4.2, data.WindSpeedMps);
        Assert.Equal("S", data.WindDirectionCompass); // 180 deg = South
        Assert.Equal(65.0, data.HumidityPercent);
        Assert.Equal(1015.0, data.PressureHpa);
        Assert.NotEmpty(data.Hourly);
        Assert.NotEmpty(data.Daily);
    }

    [Theory]
    [InlineData("clearsky_day", "\u2600", "Clear Sky")]
    [InlineData("clearsky_night", "\uE708", "Clear Sky")]
    [InlineData("rain", "\u2614", "Rain")]
    [InlineData("heavyrain", "\u2614", "Heavy Rain")]
    [InlineData("snow", "\u2744", "Snow")]
    [InlineData("thunderstorm", "\u26C8", "Thunderstorm")]
    [InlineData("fog", "\u2601", "Foggy")]
    [InlineData("partlycloudy_day", "\u26C5", "Partly Cloudy")]
    public void SymbolMapping_MapsKnownSymbolsCorrectly(string symbol, string expectedGlyph, string expectedCondition)
    {
        string glyph = MetNorwayWeatherProvider.SymbolToGlyph(symbol);
        string condition = MetNorwayWeatherProvider.SymbolToConditionText(symbol);

        Assert.Equal(expectedGlyph, glyph);
        Assert.Equal(expectedCondition, condition);
    }

    [Fact]
    public void WeatherFormatting_ConvertsMetricAndImperialCorrectly()
    {
        // Metric temperature
        Assert.Equal("20°", WindowIslandWeatherService.FormatTemp(20.4, "metric"));
        // Imperial temperature (20°C -> 68°F)
        Assert.Equal("68°", WindowIslandWeatherService.FormatTemp(20.0, "imperial"));

        // Metric speed
        Assert.Equal("14.4 km/h", WindowIslandWeatherService.FormatSpeed(4.0, "metric")); // 4 m/s * 3.6 = 14.4 km/h
        // Imperial speed
        Assert.Equal("8.9 mph", WindowIslandWeatherService.FormatSpeed(4.0, "imperial")); // 4 m/s * 2.23694 ≈ 8.9 mph
    }

    [Fact]
    public void RainWarning_FiresWhenUpcomingRainIsDetected()
    {
        var mockProvider = new FakeWeatherProvider();
        var service = new WindowIslandWeatherService(mockProvider);

        bool warningFired = false;
        WindowIslandCard? receivedCard = null;
        service.RainWarningTriggered += (_, card) =>
        {
            warningFired = true;
            receivedCard = card;
        };

        var forecast = new WeatherForecastData
        {
            Hourly = new List<WeatherHourlyPoint>
            {
                new WeatherHourlyPoint
                {
                    Time = DateTime.Now.AddMinutes(45), // Within 1 hour
                    TempC = 12,
                    SymbolCode = "heavyrain",
                    IconGlyph = "\uE754",
                    PrecipitationMm = 2.4
                }
            }
        };

        service.CheckRainSnowWarning(forecast);

        Assert.True(warningFired);
        Assert.NotNull(receivedCard);
        Assert.Equal("weather-rain", receivedCard!.Kind);
        Assert.Equal(WindowIslandCardPriority.Rain, receivedCard.Priority);
        Assert.Contains("Rain Alert", receivedCard.Title);
        Assert.Contains("2.4 mm", receivedCard.Detail);
    }

    [Fact]
    public void WindowIslandSettings_WeatherCities_InitializesAndClones()
    {
        var settings = new WindowIslandSettings();
        var cities = settings.GetWeatherCities();
        Assert.NotEmpty(cities);
        Assert.Equal("Kathmandu", cities[0].Name);

        // Add a new city
        cities.Add(new WeatherCity("London", 51.5074, -0.1278, "United Kingdom"));
        settings.ActiveWeatherCityIndex = 1;
        Assert.Equal("London", settings.GetActiveCity().Name);

        var clone = settings.Clone();
        Assert.Equal(2, clone.WeatherCities.Count);
        Assert.Equal("London", clone.WeatherCities[1].Name);
        Assert.Equal(1, clone.ActiveWeatherCityIndex);

        // Mutating clone does not mutate original
        clone.WeatherCities[0].Name = "Tokyo";
        Assert.Equal("Kathmandu", settings.WeatherCities[0].Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task WeatherService_MultiCity_AddSelectRemoveWorks()
    {
        var settings = new WindowIslandSettings();
        WindowIslandSettings? savedSettings = null;
        WindowIslandWeatherService.SettingsProvider = () => settings;
        WindowIslandWeatherService.SettingsSaver = s => savedSettings = s;

        var mockProvider = new FakeWeatherProvider();
        var service = new WindowIslandWeatherService(mockProvider);

        // Add London
        await service.AddCityAsync("London", 51.5074, -0.1278, "UK");
        Assert.Equal("London", settings.WeatherCity);
        Assert.Equal(51.5074, settings.WeatherLatitude);
        Assert.Equal(-0.1278, settings.WeatherLongitude);
        Assert.Equal(2, settings.WeatherCities.Count); // Kathmandu + London
        Assert.Equal(1, settings.ActiveWeatherCityIndex);

        // Select Kathmandu (index 0)
        await service.SelectCityAsync(0);
        Assert.Equal(0, settings.ActiveWeatherCityIndex);
        Assert.Equal("Kathmandu", settings.WeatherCity);

        // Remove Kathmandu (index 0)
        await service.RemoveCityAsync(0);
        Assert.Single(settings.WeatherCities);
        Assert.Equal("London", settings.WeatherCities[0].Name);
        Assert.Equal(0, settings.ActiveWeatherCityIndex);
        Assert.Equal("London", settings.WeatherCity);

        // Prevent removing the last city
        await service.RemoveCityAsync(0);
        Assert.Single(settings.WeatherCities);
    }

    [Fact]
    public void WeatherService_GetCityUtcOffset_ReturnsAccurateOffset()
    {
        // Explicit offset stored
        var tokyo = new WeatherCity("Tokyo", 35.6762, 139.6503, "Japan", utcOffsetSeconds: 32400); // +9h
        Assert.Equal(TimeSpan.FromHours(9), WindowIslandWeatherService.GetCityUtcOffset(tokyo));

        // 15-minute longitude fallback for Kathmandu
        var ktm = new WeatherCity("Kathmandu", 27.7172, 85.3240, "Nepal");
        var offset = WindowIslandWeatherService.GetCityUtcOffset(ktm);
        Assert.True(offset.TotalHours >= 5.5 && offset.TotalHours <= 6.0);
    }

    private class FakeWeatherProvider : IWeatherProvider
    {
        public System.Threading.Tasks.Task<WeatherForecastData?> GetForecastAsync(double lat, double lon, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult<WeatherForecastData?>(null);

        public System.Threading.Tasks.Task<IReadOnlyList<WeatherLocationResult>> SearchLocationsAsync(string query, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<WeatherLocationResult>>(Array.Empty<WeatherLocationResult>());
    }
}
