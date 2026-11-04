using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.Services.Brightness;

/// <summary>
/// Brightness provider for external monitors using DDC/CI
/// </summary>
public class DdcCiBrightnessProvider : IBrightnessProvider, IDisposable
{
    private readonly IntPtr _hMonitor;
    private readonly string _displayId;
    private IntPtr _hPhysicalMonitor = IntPtr.Zero;
    private uint _minBrightness;
    private uint _maxBrightness;
    private uint _currentBrightness;
    private bool _isInitialized;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const int TimeoutMilliseconds = 2000;

    // Dxva2.dll imports
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorCapabilities(IntPtr hMonitor, out uint pdwMonitorCapabilities, out uint pdwSupportedColorTemperatures);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    public string DisplayId => _displayId;
    public string ControlMethod => "External (DDC/CI)";

    public DdcCiBrightnessProvider(IntPtr hMonitor, string displayId)
    {
        _hMonitor = hMonitor;
        _displayId = displayId;
    }

    private async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        await _lock.WaitAsync();
        try
        {
            if (_isInitialized)
                return true;

            // Get physical monitor handle
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(_hMonitor, out uint numMonitors) || numMonitors == 0)
            {
                System.Diagnostics.Debug.WriteLine($"DDC/CI: No physical monitors for {_displayId}");
                return false;
            }

            var physicalMonitors = new PHYSICAL_MONITOR[numMonitors];
            if (!GetPhysicalMonitorsFromHMONITOR(_hMonitor, numMonitors, physicalMonitors))
            {
                System.Diagnostics.Debug.WriteLine($"DDC/CI: Failed to get physical monitors for {_displayId}");
                return false;
            }

            _hPhysicalMonitor = physicalMonitors[0].hPhysicalMonitor;

            // Check capabilities
            if (!GetMonitorCapabilities(_hPhysicalMonitor, out uint capabilities, out _))
            {
                System.Diagnostics.Debug.WriteLine($"DDC/CI: Failed to get capabilities for {_displayId}");
                DestroyPhysicalMonitors(numMonitors, physicalMonitors);
                _hPhysicalMonitor = IntPtr.Zero;
                return false;
            }

            // MC_CAPS_BRIGHTNESS = 0x00000002
            const uint MC_CAPS_BRIGHTNESS = 0x00000002;
            if ((capabilities & MC_CAPS_BRIGHTNESS) == 0)
            {
                System.Diagnostics.Debug.WriteLine($"DDC/CI: Monitor {_displayId} doesn't support brightness control");
                DestroyPhysicalMonitors(numMonitors, physicalMonitors);
                _hPhysicalMonitor = IntPtr.Zero;
                return false;
            }

            // Get brightness range
            if (!GetMonitorBrightness(_hPhysicalMonitor, out _minBrightness, out _currentBrightness, out _maxBrightness))
            {
                System.Diagnostics.Debug.WriteLine($"DDC/CI: Failed to get brightness range for {_displayId}");
                DestroyPhysicalMonitors(numMonitors, physicalMonitors);
                _hPhysicalMonitor = IntPtr.Zero;
                return false;
            }

            _isInitialized = true;
            System.Diagnostics.Debug.WriteLine($"DDC/CI initialized: {_displayId}, range {_minBrightness}-{_maxBrightness}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DDC/CI initialization error for {_displayId}: {ex.Message}");
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool IsAvailable()
    {
        return Task.Run(() => InitializeAsync()).GetAwaiter().GetResult();
    }

    public async Task<int> GetBrightnessAsync()
    {
        if (!await InitializeAsync())
            return 100;

        await _lock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeoutMilliseconds);
            var task = Task.Run(() =>
            {
                if (GetMonitorBrightness(_hPhysicalMonitor, out _, out uint current, out _))
                {
                    _currentBrightness = current;
                    return MapToPercent(current);
                }
                return 100;
            }, cts.Token);

            return await task;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DDC/CI GetBrightness error for {_displayId}: {ex.Message}");
            return 100;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetBrightnessAsync(int percent)
    {
        if (!await InitializeAsync())
            return false;

        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        await _lock.WaitAsync();
        try
        {
            uint value = MapFromPercent(percent);
            
            using var cts = new CancellationTokenSource(TimeoutMilliseconds);
            var task = Task.Run(() =>
            {
                // Retry up to 3 times
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (SetMonitorBrightness(_hPhysicalMonitor, value))
                    {
                        _currentBrightness = value;
                        return true;
                    }
                    Thread.Sleep(100);
                }
                return false;
            }, cts.Token);

            bool success = await task;
            if (!success)
            {
                System.Diagnostics.Debug.WriteLine($"DDC/CI SetBrightness failed for {_displayId} after 3 attempts");
            }
            return success;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DDC/CI SetBrightness error for {_displayId}: {ex.Message}");
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private int MapToPercent(uint value)
    {
        if (_maxBrightness == _minBrightness)
            return 100;

        return (int)((value - _minBrightness) * 100 / (_maxBrightness - _minBrightness));
    }

    private uint MapFromPercent(int percent)
    {
        return _minBrightness + (uint)(percent * (_maxBrightness - _minBrightness) / 100);
    }

    public void Dispose()
    {
        if (_hPhysicalMonitor != IntPtr.Zero)
        {
            try
            {
                var physicalMonitors = new[] { new PHYSICAL_MONITOR { hPhysicalMonitor = _hPhysicalMonitor } };
                DestroyPhysicalMonitors(1, physicalMonitors);
            }
            catch { }
            _hPhysicalMonitor = IntPtr.Zero;
        }
        _lock?.Dispose();
    }
}
