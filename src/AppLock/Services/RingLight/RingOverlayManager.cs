using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AppLock.Models;
using Windows.Graphics;

namespace AppLock.Services.RingLight;

/// <summary>
/// Creates, positions, and destroys one <see cref="RingOverlayWindow"/> per target display.
/// For milestone 2: primary display only. Multi-monitor support arrives in milestone 5.
/// Must be used on the UI (STA) thread.
/// </summary>
public sealed class RingOverlayManager : IDisposable
{
    private readonly List<RingOverlayWindow> _overlays = new();
    private bool _disposed;

    /// <summary>Show overlays on the target displays using current settings.</summary>
    public void Show(RingLightSettings settings)
    {
        EnsureOverlays(settings);
        foreach (var overlay in _overlays)
        {
            overlay.ApplyCaptureExclusion(settings.ShowInScreenCapture);
            overlay.Show(settings);
        }
    }

    /// <summary>Hide and tear down all overlay windows.</summary>
    public async Task HideAllAsync(int fadeMs)
    {
        var tasks = new List<Task>();
        foreach (var overlay in _overlays)
            tasks.Add(overlay.HideAsync(fadeMs));
        await Task.WhenAll(tasks);

        foreach (var overlay in _overlays)
            overlay.Dispose();
        _overlays.Clear();
    }

    /// <summary>Re-apply settings live while overlays are visible.</summary>
    public void UpdateSettings(RingLightSettings settings)
    {
        var bounds = GetTargetBounds(settings);
        foreach (var overlay in _overlays)
            overlay.ApplySettings(settings, bounds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var o in _overlays)
            o.Dispose();
        _overlays.Clear();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void EnsureOverlays(RingLightSettings settings)
    {
        if (_overlays.Count > 0) return; // already created

        var bounds = GetTargetBounds(settings);
        var overlay = new RingOverlayWindow();
        overlay.ApplySettings(settings, bounds);
        _overlays.Add(overlay);
    }

    private static RectInt32 GetTargetBounds(RingLightSettings settings)
    {
        // Milestone 2: always primary display.
        // Milestone 5 will honour settings.DisplayTarget and handle multi-monitor.
        try
        {
            var monitors = Microsoft.UI.Windowing.DisplayArea.FindAll();
            Microsoft.UI.Windowing.DisplayArea? target = null;

            foreach (var m in monitors)
            {
                if (m.IsPrimary)
                {
                    target = m;
                    break;
                }
            }
            target ??= Microsoft.UI.Windowing.DisplayArea.Primary;

            if (settings.KeepTaskbarClear)
                return target.WorkArea;
            else
                return target.OuterBounds;
        }
        catch
        {
            // Fallback: 1920×1080 at origin
            return new RectInt32(0, 0, 1920, 1080);
        }
    }
}

