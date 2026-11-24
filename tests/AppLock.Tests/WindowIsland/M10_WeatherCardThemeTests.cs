using System;
using AppLock.WindowIsland.Modules.Weather;
using Xunit;

namespace AppLock.Tests.WindowIsland;

public sealed class M10_WeatherCardThemeTests
{
    private static readonly TimeSpan Sunrise = TimeSpan.FromHours(6);
    private static readonly TimeSpan Sunset = TimeSpan.FromHours(18);

    [Theory]
    [InlineData("thunderstorm", "Thunderstorm", 4.0, 14.0, true, WeatherThemeKind.Thunderstorm)]
    [InlineData("heavyrain", "Heavy Rain", 3.0, 14.0, true, WeatherThemeKind.Raining)]
    [InlineData("snow", "Light Snow", 2.0, 14.0, true, WeatherThemeKind.Snow)]
    [InlineData("fog", "Foggy", 1.0, 9.0, true, WeatherThemeKind.Fog)]
    [InlineData("clearsky_day", "Clear", 3.0, 6.25, true, WeatherThemeKind.Sunrise)]
    [InlineData("clearsky_day", "Clear", 3.0, 18.25, false, WeatherThemeKind.Sunset)]
    [InlineData("clearsky_day", "Windy", 11.0, 14.0, true, WeatherThemeKind.Wind)]
    [InlineData("partlycloudy_day", "Partly Cloudy", 3.0, 14.0, true, WeatherThemeKind.Cloudy)]
    [InlineData("clearsky_day", "Clear", 2.0, 9.0, true, WeatherThemeKind.Morning)]
    [InlineData("clearsky_day", "Clear", 2.0, 17.0, true, WeatherThemeKind.Evening)]
    [InlineData("clearsky_day", "Clear", 2.0, 13.0, true, WeatherThemeKind.Sunshine)]
    [InlineData("clearsky_night", "Clear", 2.0, 23.0, false, WeatherThemeKind.Night)]
    public void ResolveKind_MapsConditionsAndTimeToExpectedTheme(
        string symbol,
        string condition,
        double windMps,
        double hour,
        bool isDaytime,
        WeatherThemeKind expected)
    {
        var kind = WeatherCardTheme.ResolveKind(
            symbol,
            condition,
            windMps,
            TimeSpan.FromHours(hour),
            Sunrise,
            Sunset,
            isDaytime);

        Assert.Equal(expected, kind);
    }

    [Fact]
    public void GetPalette_ReturnsDistinctNonEmptyPalettesForAllThemes()
    {
        foreach (WeatherThemeKind kind in Enum.GetValues<WeatherThemeKind>())
        {
            var palette = WeatherCardTheme.GetPalette(kind);
            Assert.Equal(kind, palette.Kind);
            Assert.NotEqual(0u, palette.TopColor);
            Assert.NotEqual(0u, palette.MiddleColor);
            Assert.NotEqual(0u, palette.BottomColor);
            Assert.NotEqual(0u, palette.IconColor);
        }
    }
}
