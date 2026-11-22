using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.WindowIsland.Modules.Weather;

public interface IWeatherProvider
{
    Task<WeatherForecastData?> GetForecastAsync(double lat, double lon, CancellationToken ct = default);
    Task<IReadOnlyList<WeatherLocationResult>> SearchLocationsAsync(string query, CancellationToken ct = default);
}
