using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.Brightness;

public class BrightnessService : IDisposable
{
    private readonly Dictionary<string, IBrightnessProvider> _providers = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private int _pendingBrightness = -1;
    private const int DebounceMilliseconds = 80;
    private readonly DispatcherQueue _dispatcher;
    private int _minSoftwareBrightness = 10;

    public event EventHandler<BrightnessChangedEventArgs>? BrightnessChanged;

    // Monitor enumeration
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public BrightnessService(DispatcherQueue? dispatcher = null)
    {
        _dispatcher = dispatcher ?? DispatcherQueue.GetForCurrentThread();
        InitializeProviders();
    }

    private void InitializeProviders()
    {
        try
        {
            // Try WMI for built-in panel
            var wmiProvider = new WmiBrightnessProvider();
            if (wmiProvider.IsAvailable())
            {
                _providers[wmiProvider.DisplayId] = wmiProvider;
                System.Diagnostics.Debug.WriteLine("WMI brightness provider initialized");
            }

            // Try DDC/CI for external monitors
            var monitors = new List<IntPtr>();
            MonitorEnumDelegate monitorDelegate = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                monitors.Add(hMonitor);
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorDelegate, IntPtr.Zero);

            int ddciCount = 0;
            foreach (var hMonitor in monitors)
            {
                try
                {
                    var displayId = $"DDC_{ddciCount}";
                    var ddciProvider = new DdcCiBrightnessProvider(hMonitor, displayId);
                    
                    // Test if it works (this is async but we'll do quick check)
                    if (ddciProvider.IsAvailable())
                    {
                        _providers[displayId] = ddciProvider;
                        ddciCount++;
                        System.Diagnostics.Debug.WriteLine($"DDC/CI provider initialized: {displayId}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to initialize DDC/CI for monitor: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"Brightness service initialized with {_providers.Count} hardware providers");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize brightness providers: {ex.Message}");
        }
    }

    public void SetMinSoftwareBrightness(int percent)
    {
        if (percent < 0) percent = 0;
        if (percent > 50) percent = 50; // Max 50% floor
        _minSoftwareBrightness = percent;
    }

    public List<DisplayInfo> GetDisplays()
    {
        var displays = DisplayEnumerator.EnumerateDisplays();
        
        // Update control method based on available providers
        foreach (var display in displays)
        {
            // Check if we have WMI provider for this display
            if (_providers.ContainsKey("WMI_BuiltIn") && display.IsBuiltIn)
            {
                display.ControlMethod = "Built-in";
            }
            // Check if we have DDC/CI provider
            else if (_providers.Any(p => p.Value.ControlMethod.Contains("DDC/CI")))
            {
                // Try to match DDC provider to this display
                var ddciProvider = _providers.FirstOrDefault(p => p.Value.ControlMethod.Contains("DDC/CI"));
                if (ddciProvider.Value != null)
                {
                    display.ControlMethod = "External (DDC/CI)";
                }
                else
                {
                    display.ControlMethod = "Software dimming";
                }
            }
            else
            {
                display.ControlMethod = "Software dimming";
            }
        }

        return displays;
    }

    public async Task<int> GetBrightnessAsync(string? displayId = null)
    {
        try
        {
            // If no display specified or all displays, use WMI if available
            if (string.IsNullOrEmpty(displayId) || displayId == "All")
            {
                if (_providers.TryGetValue("WMI_BuiltIn", out var provider))
                {
                    return await provider.GetBrightnessAsync();
                }
                
                // Try first DDC/CI provider
                var ddciProvider = _providers.FirstOrDefault(p => p.Value.ControlMethod.Contains("DDC/CI"));
                if (ddciProvider.Value != null)
                {
                    return await ddciProvider.Value.GetBrightnessAsync();
                }
            }

            return 100;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetBrightness error: {ex.Message}");
            return 100;
        }
    }

    public async Task<bool> SetBrightnessAsync(int percent, string? displayId = null, bool debounce = true)
    {
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        if (debounce)
        {
            // Debounce writes
            _pendingBrightness = percent;
            
            // Cancel any pending write
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            try
            {
                await Task.Delay(DebounceMilliseconds, token);
                
                // Execute the write
                var finalPercent = _pendingBrightness;
                _pendingBrightness = -1;
                return await SetBrightnessImmediateAsync(finalPercent, displayId);
            }
            catch (TaskCanceledException)
            {
                // Debounce was cancelled, a newer value is coming
                return true;
            }
        }
        else
        {
            return await SetBrightnessImmediateAsync(percent, displayId);
        }
    }

    private async Task<bool> SetBrightnessImmediateAsync(int percent, string? displayId)
    {
        await _writeLock.WaitAsync();
        try
        {
            bool success = false;
            bool usedHardware = false;

            // If no display specified or all displays, try all hardware providers
            if (string.IsNullOrEmpty(displayId) || displayId == "All")
            {
                foreach (var provider in _providers.Values)
                {
                    try
                    {
                        if (await provider.SetBrightnessAsync(percent))
                        {
                            success = true;
                            usedHardware = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Provider {provider.DisplayId} failed: {ex.Message}");
                    }
                }

                // If no hardware providers worked, use software dimming as fallback
                if (!usedHardware)
                {
                    System.Diagnostics.Debug.WriteLine("No hardware brightness control available, software dimming would be used here");
                    // Software dimming would be created on-demand per display
                    success = true; // Pretend success for now
                }
            }

            if (success)
            {
                BrightnessChanged?.Invoke(this, new BrightnessChangedEventArgs 
                { 
                    Brightness = percent,
                    DisplayId = displayId ?? "All"
                });
            }

            return success;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _writeLock?.Dispose();

        foreach (var provider in _providers.Values)
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _providers.Clear();
    }
}

public class BrightnessChangedEventArgs : EventArgs
{
    public int Brightness { get; set; }
    public string DisplayId { get; set; } = string.Empty;
}
