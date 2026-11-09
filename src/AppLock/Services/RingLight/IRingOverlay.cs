using System;
using System.Threading.Tasks;
using AppLock.Models;
using Windows.Graphics;

namespace AppLock.Services.RingLight;

/// <summary>
/// Represents an overlay window displaying the ring light on a monitor.
/// </summary>
public interface IRingOverlay : IDisposable
{
    void Show(RingLightSettings settings);
    Task HideAsync(int fadeMs);
    void ApplySettings(RingLightSettings settings, RectInt32 bounds);
    void ApplyCaptureExclusion(bool showInScreenCapture);
}

