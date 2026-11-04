using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace AppLock.Services.Brightness;

/// <summary>
/// Brightness provider for built-in laptop panels using WMI
/// </summary>
public class WmiBrightnessProvider : IBrightnessProvider, IDisposable
{
    private const int TimeoutSeconds = 3;
    private byte[]? _supportedLevels;

    public string DisplayId => "WMI_BuiltIn";
    public string ControlMethod => "Built-in";

    public bool IsAvailable()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness");
            using var collection = searcher.Get();
            return collection.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> GetBrightnessAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness");
                using var collection = searcher.Get();
                
                foreach (ManagementObject obj in collection)
                {
                    var currentBrightness = obj["CurrentBrightness"];
                    if (currentBrightness != null)
                    {
                        // Cache supported levels
                        if (_supportedLevels == null)
                        {
                            var levels = obj["Level"] as byte[];
                            if (levels != null && levels.Length > 0)
                            {
                                _supportedLevels = levels;
                            }
                        }

                        return Convert.ToInt32(currentBrightness);
                    }
                }
                
                return 100;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI GetBrightness error: {ex.Message}");
                return 100;
            }
        });
    }

    public async Task<bool> SetBrightnessAsync(int percent)
    {
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        return await Task.Run(() =>
        {
            try
            {
                // Map to nearest supported level
                int targetLevel = percent;
                if (_supportedLevels != null && _supportedLevels.Length > 0)
                {
                    targetLevel = MapToNearestSupportedLevel(percent);
                }

                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                using var collection = searcher.Get();
                
                foreach (ManagementObject obj in collection)
                {
                    var inParams = obj.GetMethodParameters("WmiSetBrightness");
                    inParams["Timeout"] = 1; // 1 second
                    inParams["Brightness"] = (byte)targetLevel;
                    
                    obj.InvokeMethod("WmiSetBrightness", inParams, null);
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI SetBrightness error: {ex.Message}");
                return false;
            }
        });
    }

    private int MapToNearestSupportedLevel(int percent)
    {
        if (_supportedLevels == null || _supportedLevels.Length == 0)
            return percent;

        // Find the closest supported level
        int closest = _supportedLevels[0];
        int minDiff = Math.Abs(percent - closest);

        foreach (var level in _supportedLevels)
        {
            int diff = Math.Abs(percent - level);
            if (diff < minDiff)
            {
                minDiff = diff;
                closest = level;
            }
        }

        return closest;
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
