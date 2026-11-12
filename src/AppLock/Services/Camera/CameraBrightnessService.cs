using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Microsoft.Win32;

namespace AppLock.Services.Camera;

/// <summary>
/// Service for controlling camera brightness settings.
/// Uses Windows Registry to sync with Windows Settings camera brightness.
/// </summary>
public class CameraBrightnessService : IDisposable
{
    private MediaCapture? _mediaCapture;
    private VideoDeviceController? _videoController;
    private string? _currentDeviceId;
    private bool _isInitialized;
    private bool _disposed;

    // Registry path for camera settings
    private const string CameraSettingsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged";

    public event EventHandler<double>? BrightnessChanged;

    /// <summary>
    /// Gets whether the service is initialized and has a camera available.
    /// </summary>
    public bool IsAvailable => _isInitialized && _videoController != null;

    /// <summary>
    /// Gets whether camera brightness control is available.
    /// </summary>
    public bool SupportsBrightness => _isInitialized;

    /// <summary>
    /// Gets the minimum brightness value (always 0 to match Windows Settings).
    /// </summary>
    public double MinBrightness => 0;

    /// <summary>
    /// Gets the maximum brightness value (always 100 to match Windows Settings).
    /// </summary>
    public double MaxBrightness => 100;

    /// <summary>
    /// Gets the brightness step value (1% increments).
    /// </summary>
    public double BrightnessStep => 1;

    /// <summary>
    /// Gets or sets the current brightness value.
    /// Returns null if brightness control is not supported.
    /// Reads from Windows Registry to match Windows Settings behavior.
    /// </summary>
    public double? CurrentBrightness
    {
        get
        {
            if (!_isInitialized)
                return null;

            // First try to get from registry (matches Windows Settings)
            var registryValue = GetRegistryBrightness();
            if (registryValue.HasValue)
            {
                return registryValue.Value;
            }

            // Fallback to MediaCapture API
            if (_videoController == null)
                return null;

            try
            {
                // Try Exposure first (more commonly supported)
                if (_videoController.Exposure?.Capabilities?.Supported == true)
                {
                    if (!_videoController.Exposure.TryGetValue(out double value))
                        return null;
                    return value;
                }
                // Fallback to Brightness if available
                else if (_videoController.Brightness?.Capabilities?.Supported == true)
                {
                    if (!_videoController.Brightness.TryGetValue(out double value))
                        return null;
                    return value;
                }

                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraBrightness] Error getting brightness: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Initialize the service by finding and opening the first available camera.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        try
        {
            // Find all video capture devices
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices.Count == 0)
            {
                Debug.WriteLine("[CameraBrightness] No camera devices found");
                return false;
            }

            // Use the first camera (usually the integrated webcam)
            var device = devices[0];
            _currentDeviceId = device.Id;

            Debug.WriteLine($"[CameraBrightness] Found camera: {device.Name} (ID: {device.Id})");

            // Store device info
            _currentDeviceId = device.Id;

            // Initialize MediaCapture for capability detection
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = _currentDeviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };

            Debug.WriteLine($"[CameraBrightness] Initializing MediaCapture...");
            await _mediaCapture.InitializeAsync(settings);
            _videoController = _mediaCapture.VideoDeviceController;

            _isInitialized = true;

            Debug.WriteLine($"[CameraBrightness] Initialized successfully");
            
            // Log what controls are available
            Debug.WriteLine($"[CameraBrightness] === Available Camera Controls ===");
            Debug.WriteLine($"[CameraBrightness] Brightness (MediaCapture): {_videoController.Brightness?.Capabilities?.Supported ?? false}");
            Debug.WriteLine($"[CameraBrightness] Exposure (MediaCapture): {_videoController.Exposure?.Capabilities?.Supported ?? false}");
            Debug.WriteLine($"[CameraBrightness] Registry brightness: {GetRegistryBrightness() != null}");
            Debug.WriteLine($"[CameraBrightness] ================================");
            
            // Try to read current brightness from registry
            var registryBrightness = GetRegistryBrightness();
            if (registryBrightness.HasValue)
            {
                Debug.WriteLine($"[CameraBrightness] Registry brightness value: {registryBrightness.Value}");
            }

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[CameraBrightness] Camera access denied: {ex.Message}");
            Debug.WriteLine("[CameraBrightness] Make sure camera permission is granted in Windows Settings > Privacy > Camera");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the camera brightness to the specified value.
    /// Syncs with Windows Settings by writing to registry.
    /// </summary>
    /// <param name="brightness">Brightness value (0-100 to match Windows Settings)</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool SetBrightness(double brightness)
    {
        if (!_isInitialized)
        {
            Debug.WriteLine("[CameraBrightness] Not initialized");
            return false;
        }

        try
        {
            // Clamp to Windows Settings range (0-100)
            int brightnessInt = (int)Math.Clamp(brightness, 0, 100);

            Debug.WriteLine($"[CameraBrightness] SetBrightness called with value: {brightnessInt}");

            // Set in registry (matches Windows Settings)
            bool success = SetRegistryBrightness(brightnessInt);
            
            if (success)
            {
                Debug.WriteLine($"[CameraBrightness] ✓ Brightness set to {brightnessInt}% (registry + device)");
                BrightnessChanged?.Invoke(this, brightnessInt);
                return true;
            }

            Debug.WriteLine($"[CameraBrightness] ✗ Failed to set brightness");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Error setting brightness: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enables or disables automatic brightness/exposure (if supported).
    /// </summary>
    public bool SetAutoBrightness(bool enable)
    {
        if (!SupportsBrightness || _videoController == null)
            return false;

        try
        {
            // Try Exposure first
            if (_videoController.Exposure?.Capabilities?.Supported == true)
            {
                if (_videoController.Exposure.TrySetAuto(enable))
                {
                    Debug.WriteLine($"[CameraBrightness] Auto exposure {(enable ? "enabled" : "disabled")}");
                    return true;
                }
            }
            // Fallback to Brightness
            else if (_videoController.Brightness?.Capabilities?.Supported == true)
            {
                if (_videoController.Brightness.TrySetAuto(enable))
                {
                    Debug.WriteLine($"[CameraBrightness] Auto brightness {(enable ? "enabled" : "disabled")}");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Error setting auto brightness: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets a list of all available camera devices.
    /// </summary>
    public async Task<List<DeviceInformation>> GetAvailableCamerasAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Error getting cameras: {ex.Message}");
            return new List<DeviceInformation>();
        }
    }

    /// <summary>
    /// Switches to a different camera device.
    /// </summary>
    public async Task<bool> SwitchCameraAsync(string deviceId)
    {
        if (_currentDeviceId == deviceId && _isInitialized)
            return true;

        // Dispose current camera
        _mediaCapture?.Dispose();
        _mediaCapture = null;
        _videoController = null;
        _isInitialized = false;
        _currentDeviceId = null;

        // Initialize with new device
        try
        {
            _currentDeviceId = deviceId;
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };

            await _mediaCapture.InitializeAsync(settings);
            _videoController = _mediaCapture.VideoDeviceController;
            _isInitialized = true;

            Debug.WriteLine($"[CameraBrightness] Switched to camera: {deviceId}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Failed to switch camera: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _mediaCapture?.Dispose();
        _mediaCapture = null;
        _videoController = null;
        _isInitialized = false;

        Debug.WriteLine("[CameraBrightness] Disposed");
    }

    // ── Registry-based brightness control (matches Windows Settings) ──────────

    /// <summary>
    /// Gets the brightness value from Windows Registry.
    /// This is where Windows Settings stores the camera brightness.
    /// Returns value in range 0-100.
    /// </summary>
    private double? GetRegistryBrightness()
    {
        try
        {
            // Windows 11 stores camera settings in:
            // HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Camera\Brightness
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Camera");
            if (key != null)
            {
                var value = key.GetValue("Brightness");
                if (value != null && int.TryParse(value.ToString(), out int brightness))
                {
                    Debug.WriteLine($"[CameraBrightness] Registry brightness: {brightness}");
                    return brightness;
                }
            }

            // Alternative location for some cameras
            using var key2 = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\MediaFoundation\Platform");
            if (key2 != null)
            {
                var value = key2.GetValue("Brightness");
                if (value != null && int.TryParse(value.ToString(), out int brightness))
                {
                    Debug.WriteLine($"[CameraBrightness] Platform brightness: {brightness}");
                    return brightness;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Error reading registry brightness: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets the brightness value in Windows Registry.
    /// This updates the same value that Windows Settings uses.
    /// </summary>
    private bool SetRegistryBrightness(int brightness)
    {
        try
        {
            // Clamp to 0-100 range (Windows Settings range)
            brightness = Math.Clamp(brightness, 0, 100);

            // Try primary location
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Camera");
            if (key != null)
            {
                key.SetValue("Brightness", brightness, RegistryValueKind.DWord);
                Debug.WriteLine($"[CameraBrightness] Set registry brightness to {brightness}");
                
                // Also apply via VideoDeviceController if available
                ApplyBrightnessToDevice(brightness);
                
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Error writing registry brightness: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Applies brightness to the actual camera device.
    /// Maps 0-100 range to device-specific range.
    /// </summary>
    private void ApplyBrightnessToDevice(int registryBrightness)
    {
        if (_videoController == null)
            return;

        try
        {
            // Map 0-100 to device range
            // Try Exposure first
            if (_videoController.Exposure?.Capabilities?.Supported == true)
            {
                var caps = _videoController.Exposure.Capabilities;
                double deviceValue = caps.Min + (registryBrightness / 100.0) * (caps.Max - caps.Min);
                
                _videoController.Exposure.TrySetAuto(false);
                if (_videoController.Exposure.TrySetValue(deviceValue))
                {
                    Debug.WriteLine($"[CameraBrightness] Applied {registryBrightness}% as Exposure {deviceValue}");
                }
            }
            // Fallback to Brightness
            else if (_videoController.Brightness?.Capabilities?.Supported == true)
            {
                var caps = _videoController.Brightness.Capabilities;
                double deviceValue = caps.Min + (registryBrightness / 100.0) * (caps.Max - caps.Min);
                
                _videoController.Brightness.TrySetAuto(false);
                if (_videoController.Brightness.TrySetValue(deviceValue))
                {
                    Debug.WriteLine($"[CameraBrightness] Applied {registryBrightness}% as Brightness {deviceValue}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraBrightness] Error applying brightness to device: {ex.Message}");
        }
    }
}
