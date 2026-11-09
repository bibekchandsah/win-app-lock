using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AppLock.Services.Brightness;

namespace AppLock.Services.RingLight;

/// <summary>
/// Manages temporary brightness boosting while the Ring Light is active.
/// Restores original brightness upon turn off or app exit, unless user changed brightness manually.
/// </summary>
public sealed class BrightnessController : IBrightnessController
{
    private readonly BrightnessService? _brightnessService;
    private readonly Dictionary<string, int> _originalBrightness = new();
    private int _targetBoostPercent = -1;
    private bool _hasBoosted;

    public BrightnessController(BrightnessService? brightnessService)
    {
        _brightnessService = brightnessService;
    }

    public bool IsSupported
    {
        get
        {
            if (_brightnessService == null) return false;
            try
            {
                var displays = _brightnessService.GetDisplays();
                return displays != null && displays.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public int? GetCurrentBrightness()
    {
        if (_brightnessService == null) return null;
        try
        {
            return _brightnessService.GetBrightnessAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    public void SetBrightness(int percent)
    {
        if (_brightnessService == null) return;
        try
        {
            if (!_hasBoosted)
            {
                int current = GetCurrentBrightness() ?? 50;
                _originalBrightness["All"] = current;
                _targetBoostPercent = percent;
                _hasBoosted = true;
            }

            _ = _brightnessService.SetBrightnessAsync(percent, "All", debounce: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrightnessController] Set error: {ex.Message}");
        }
    }

    public void RestoreBrightness()
    {
        if (!_hasBoosted || _brightnessService == null) return;
        try
        {
            // Only restore if user hasn't manually adjusted brightness away from our target
            int? current = GetCurrentBrightness();
            if (current.HasValue && Math.Abs(current.Value - _targetBoostPercent) <= 2)
            {
                if (_originalBrightness.TryGetValue("All", out int original))
                {
                    _ = _brightnessService.SetBrightnessAsync(original, "All", debounce: false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrightnessController] Restore error: {ex.Message}");
        }
        finally
        {
            _hasBoosted = false;
            _targetBoostPercent = -1;
            _originalBrightness.Clear();
        }
    }
}
