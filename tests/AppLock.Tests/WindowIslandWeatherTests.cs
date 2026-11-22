using System;
using System.Collections.Generic;
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
        string sampleJson = @"{
            ""properties"": {
                ""timeseries"": [
                    {
                        ""time"": ""2026-11-23T12:00:00Z"",
                        ""data"": {
                            ""instant"": {
                                ""details"": {
                                    ""air_temperature"": 18.5,
                                    ""wind_speed"": 4.2,
                                    ""wind_from_direction"": 180.0,
                                    ""relative_humidity"": 65.0,
                                    ""air_pressure_at_sea_level"": 1015.0,
                                    ""dew_point_temperature"": 11.8
                                }
                            },
                            ""next_1_hours"": {
                                ""summary"": { ""symbol_code"": ""partlycloudy_day"" },
                                ""details"": { ""precipitation_amount"": 0.2 }
                            }
                        }
                    },
                    {
                        ""time"": ""2026-11-23T13:00:00Z"",
                        ""data"": {
                            ""instant"": {
                                ""details"": {
                                    ""air_temperature"": 19.0,
                                    ""wind_speed"": 4.5
                                }
                            },
                            ""next_1_hours"": {
                                ""summary"": { ""symbol_code"": ""rain"" },
                                ""details"": { ""precipitation_amount"": 1.5 }
                            }
                        }
                    }
                ]
            }
        }";

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
    [InlineData("clearsky_day", "\uE706", "Clear Sky")]
    [InlineData("rain", "\uE754", "Rain")]
    [InlineData("heavyrain", "\uE754", "Heavy Rain")]
    [InlineData("snow", "\uE774", "Snow")]
    [InlineData("thunderstorm", "\uE755", "Thunderstorm")]
    [InlineData("fog", "\uE752", "Foggy")]
    [InlineData("partlycloudy_day", "\uE70C", "Partly Cloudy")]
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

    private class FakeWeatherProvider : IWeatherProvider
    {
        public System.Threading.Tasks.Task<WeatherForecastData?> GetForecastAsync(double lat, double lon, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult<WeatherForecastData?>(null);

        public System.Threading.Tasks.Task<IReadOnlyList<WeatherLocationResult>> SearchLocationsAsync(string query, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<WeatherLocationResult>>(Array.Empty<WeatherLocationResult>());
    }
}
