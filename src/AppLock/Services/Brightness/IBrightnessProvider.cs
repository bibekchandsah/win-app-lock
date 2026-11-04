using System.Threading.Tasks;

namespace AppLock.Services.Brightness;

public interface IBrightnessProvider
{
    string DisplayId { get; }
    string ControlMethod { get; }
    Task<int> GetBrightnessAsync();
    Task<bool> SetBrightnessAsync(int percent);
    bool IsAvailable();
}
