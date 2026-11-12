using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Devices;

namespace AppLock.Services.Camera;

/// <summary>
/// Service for controlling camera brightness/exposure settings across all installed webcams.
/// Uses Windows.Media.Devices APIs to adjust camera hardware brightness.
/// </summary>
public class CameraBrightnessService : IDisposable
{
    private MediaCapture? _mediaCapture;
    private VideoDeviceController? _videoController;
    private string? _currentDeviceId;
    private bool _isInitialized;
    private bool _disposed;

    public event EventHandler<double>? BrightnessChanged;

    /// <summary>
    /// Gets whether the service is initialized and has a camera available.
    /// </summary>
    public bool IsAvailable => _isInitialized && _videoController != null;

    /// <summary>
    /// Gets whether the current camera supports brightness/exposure control.
    /// </summary>
    public bool SupportsBrightness =>
        (_videoController?.Exposure?.Capabilities?.Supported ?? false) ||
        (_videoController?.Brightness?.Capabilities?.Supported ?? false);

    /// <summary>
    /// Gets the minimum brightness value supported by the camera.
    /// </summary>
    public double MinBrightness
    {
        get
        {
            // Try Exposure first (more commonly supported), then Brightness
            if (_videoController?.Exposure?.Capabilities?.Supported == true)
                return _videoController.Exposure.Capabilities.Min;
            if (_videoController?.Brightness?.Capabilities?.Supported == true)
                return _videoController.Brightness.Capabilities.Min;
            return 0;
        }
    }

    /// <summary>
    /// Gets the maximum brightness value supported by the camera.
    /// </summary>
    public double MaxBrightness
    {
        get
        {
            // Try Exposure first (more commonly supported), then Brightness
            if (_videoController?.Exposure?.Capabilities?.Supported == true)
                return _videoController.Exposure.Capabilities.Max;
            if (_videoController?.Brightness?.Capabilities?.Supported == true)
                return _videoController.Brightness.Capabilities.Max;
            return 100;
        }
    }

    /// <summary>
    /// Gets the brightness step value supported by the camera.
    /// </summary>
    public double BrightnessStep
    {
        get
        {
            // Try Exposure first (more commonly supported), then Brightness
            if (_videoController?.Exposure?.Capabilities?.Supported == true)
                return _videoController.Exposure.Capabilities.Step;
            if (_videoController?.Brightness?.Capabilities?.Supported == true)
                return _videoController.Brightness.Capabilities.Step;
            return 1;
        }
    }

    /// <summary>
    /// Gets or sets the current brightness value.
    /// Returns null if brightness control is not supported.
    /// Uses Exposure control (which affects perceived brightness) as primary method.
    /// </summary>
    public double? CurrentBrightness
    {
        get
        {
            if (!SupportsBrightness || _videoController == null)
                return null;

            try
            {
                // Try Exposure first (more commonly supported and matches Windows Settings behavior)
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

            // Initialize MediaCapture
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = _currentDeviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                // Use SharedMode = true to avoid conflicts with apps already using the camera
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };

            await _mediaCapture.InitializeAsync(settings);
            _videoController = _mediaCapture.VideoDeviceController;

            _isInitialized = true;

            Debug.WriteLine($"[CameraBrightness] Initialized successfully");
            Debug.WriteLine($"[CameraBrightness] Brightness supported: {SupportsBrightness}");
            if (SupportsBrightness)
            {
                Debug.WriteLine($"[CameraBrightness] Brightness range: {MinBrightness} - {MaxBrightness} (step: {BrightnessStep})");
                Debug.WriteLine($"[CameraBrightness] Current brightness: {CurrentBrightness}");
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
    /// Uses Exposure control for better compatibility (matches Windows Camera Settings).
    /// </summary>
    /// <param name="brightness">Brightness value (within MinBrightness to MaxBrightness range)</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool SetBrightness(double brightness)
    {
        if (!SupportsBrightness || _videoController == null)
        {
            Debug.WriteLine("[CameraBrightness] Brightness control not supported");
            return false;
        }

        try
        {
            // Clamp to valid range
            brightness = Math.Clamp(brightness, MinBrightness, MaxBrightness);

            // Try Exposure first (more commonly supported and matches Windows Settings)
            if (_videoController.Exposure?.Capabilities?.Supported == true)
            {
                if (_videoController.Exposure.TrySetValue(brightness))
                {
                    Debug.WriteLine($"[CameraBrightness] Exposure set to {brightness}");
                    BrightnessChanged?.Invoke(this, brightness);
                    return true;
                }
            }
            // Fallback to Brightness if available
            else if (_videoController.Brightness?.Capabilities?.Supported == true)
            {
                if (_videoController.Brightness.TrySetValue(brightness))
                {
                    Debug.WriteLine($"[CameraBrightness] Brightness set to {brightness}");
                    BrightnessChanged?.Invoke(this, brightness);
                    return true;
                }
            }

            Debug.WriteLine($"[CameraBrightness] Failed to set brightness to {brightness}");
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
}
