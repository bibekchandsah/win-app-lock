using System;

namespace AppLock.WindowIsland.Modules.Weather;

/// <summary>
/// Computes sunrise and sunset times locally using NOAA solar calculations.
/// Runs completely offline with 0 external API calls.
/// </summary>
public static class NoaaSolarCalculator
{
    private const double Zenith = 90.833; // Official zenith for sunrise/sunset in degrees

    /// <summary>
    /// Calculates local sunrise and sunset for a given geographic coordinate and date.
    /// Returns (Sunrise, Sunset) in local time.
    /// </summary>
    public static (DateTime? Sunrise, DateTime? Sunset) CalculateSunriseSunset(double lat, double lon, DateTime date, TimeSpan? tzOffset = null)
    {
        try
        {
            // Normalize longitude
            int dayOfYear = date.DayOfYear;

            // Approximate time calculations in UTC
            DateTime? sunriseUtc = CalculateEventUtc(lat, lon, dayOfYear, isSunrise: true, date.Year);
            DateTime? sunsetUtc  = CalculateEventUtc(lat, lon, dayOfYear, isSunrise: false, date.Year);

            // Convert to location's local clock time using provided timezone offset, or fallback to solar longitude offset
            TimeSpan offset = tzOffset ?? TimeSpan.FromHours(lon / 15.0);
            DateTime? localSunrise = sunriseUtc.HasValue ? sunriseUtc.Value.Add(offset) : null;
            DateTime? localSunset  = sunsetUtc.HasValue  ? sunsetUtc.Value.Add(offset)  : null;

            return (localSunrise, localSunset);
        }
        catch
        {
            return (null, null);
        }
    }

    private static DateTime? CalculateEventUtc(double lat, double lon, int dayOfYear, bool isSunrise, int year)
    {
        // 1. Convert longitude to hour value and calculate an approximate time
        double lngHour = lon / 15.0;
        double t = isSunrise
            ? dayOfYear + ((6.0 - lngHour) / 24.0)
            : dayOfYear + ((18.0 - lngHour) / 24.0);

        // 2. Calculate the Sun's mean anomaly
        double M = (0.9856 * t) - 3.289;

        // 3. Calculate the Sun's true longitude
        double L = M + (1.916 * Math.Sin(ToRadians(M))) + (0.020 * Math.Sin(ToRadians(2 * M))) + 282.634;
        L = NormalizeDegrees(L);

        // 4. Calculate the Sun's right ascension
        double RA = ToDegrees(Math.Atan(0.91764 * Math.Tan(ToRadians(L))));
        RA = NormalizeDegrees(RA);

        // Right ascension value needs to be in the same quadrant as L
        double Lquadrant  = Math.Floor(L / 90.0) * 90.0;
        double RAquadrant = Math.Floor(RA / 90.0) * 90.0;
        RA += (Lquadrant - RAquadrant);
        RA /= 15.0; // convert to hours

        // 5. Calculate the Sun's declination
        double sinDec = 0.39782 * Math.Sin(ToRadians(L));
        double cosDec = Math.Cos(Math.Asin(sinDec));

        // 6. Calculate the Sun's local hour angle
        double cosH = (Math.Cos(ToRadians(Zenith)) - (sinDec * Math.Sin(ToRadians(lat)))) / (cosDec * Math.Cos(ToRadians(lat)));

        if (cosH > 1.0)
        {
            // The sun never rises on this location on the specified date
            return null;
        }
        if (cosH < -1.0)
        {
            // The sun never sets on this location on the specified date
            return null;
        }

        // 7. Calculate H and convert into hours
        double H = isSunrise ? 360.0 - ToDegrees(Math.Acos(cosH)) : ToDegrees(Math.Acos(cosH));
        H /= 15.0;

        // 8. Calculate local mean time of rising/setting
        double T = H + RA - (0.06571 * t) - 6.622;

        // 9. Adjust back to UTC
        double UT = T - lngHour;
        while (UT < 0) UT += 24.0;
        while (UT >= 24.0) UT -= 24.0;

        int hours = (int)UT;
        double minuteFraction = (UT - hours) * 60.0;
        int minutes = (int)minuteFraction;
        int seconds = (int)((minuteFraction - minutes) * 60.0);

        return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddDays(dayOfYear - 1)
            .AddHours(hours)
            .AddMinutes(minutes)
            .AddSeconds(seconds);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static double NormalizeDegrees(double deg)
    {
        while (deg < 0) deg += 360.0;
        while (deg >= 360.0) deg -= 360.0;
        return deg;
    }
}
