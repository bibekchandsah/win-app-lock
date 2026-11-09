namespace AppLock.Services.RingLight;

/// <summary>
/// Interface for screen brightness control integration with Ring Light.
/// </summary>
public interface IBrightnessController
{
    bool IsSupported { get; }
    int? GetCurrentBrightness();
    void SetBrightness(int percent);
    void RestoreBrightness();
}

