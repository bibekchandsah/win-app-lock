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
            // Non-blocking quick check
            return _brightnessService.GetBrightnessAsync("All").ConfigureAwait(false).GetAwaiter().GetResult();
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
                _targetBoostPercent = percent;
                _hasBoosted = true;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        int current = await _brightnessService.GetBrightnessAsync("All");
                        _originalBrightness["All"] = current;
                        await _brightnessService.SetBrightnessAsync(percent, "All", debounce: false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BrightnessController] Boost async error: {ex.Message}");
                    }
                });
            }
            else
            {
                _targetBoostPercent = percent;
                _ = _brightnessService.SetBrightnessAsync(percent, "All", debounce: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrightnessController] Set error: {ex.Message}");
        }
    }

    public void RestoreBrightness()
    {
        if (!_hasBoosted || _brightnessService == null) return;
        _hasBoosted = false;
        _targetBoostPercent = -1;

        _ = Task.Run(async () =>
        {
            try
            {
                if (_originalBrightness.TryGetValue("All", out int original))
                {
                    await _brightnessService.SetBrightnessAsync(original, "All", debounce: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrightnessController] Restore error: {ex.Message}");
            }
            finally
            {
                _originalBrightness.Clear();
            }
        });
    }
}
