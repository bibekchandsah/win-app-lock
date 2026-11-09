using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;

namespace AppLock.Services.RingLight;

/// <summary>
/// Status result returned when checking camera devices and active streaming state.
/// </summary>
public sealed class CameraCheckResult
{
    public bool HasHardware => InstalledCameras.Count > 0;
    public IReadOnlyList<string> InstalledCameras { get; init; } = Array.Empty<string>();
    public bool IsStreaming { get; init; }
    public IReadOnlyList<string> ActiveApps { get; init; } = Array.Empty<string>();
    public string? Reason { get; init; }
}

/// <summary>
/// Monitors Windows privacy consent store and MediaCapture hardware streams for webcam usage.
/// Implements 1-second continuous use debounce for 'On' and 5-second silence debounce for 'Off'.
/// </summary>
public sealed class CameraUsageMonitor : ICameraUsageSource
{
    private const string WebcamConsentSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private static readonly (string ProcessName, string DisplayName)[] KnownCameraProcesses = new[]
    {
        ("WindowsCamera", "Windows Camera"),
        ("Teams", "Microsoft Teams"),
        ("ms-teams", "Microsoft Teams"),
        ("Zoom", "Zoom"),
        ("Discord", "Discord"),
        ("obs64", "OBS Studio"),
        ("obs32", "OBS Studio"),
        ("obs", "OBS Studio"),
        ("Skype", "Skype"),
        ("SkypeApp", "Skype"),
        ("WhatsApp", "WhatsApp"),
        ("Telegram", "Telegram"),
        ("slack", "Slack"),
        ("CamoStudio", "Camo Studio"),
        ("viber", "Viber"),
    };

    public event EventHandler<(bool InUse, IReadOnlyList<string> ActiveApps)>? CameraUsageChanged;

    private readonly object _lock = new();
    private Timer? _pollTimer;
    private int _isPolling;
    private bool _isMonitoring;
    private bool _isDebouncedInUse;
    private int _consecutiveActiveSeconds;
    private int _consecutiveInactiveSeconds;
    private bool _disposed;

    public bool IsMonitoring
    {
        get { lock (_lock) return _isMonitoring; }
    }

    public bool IsSupported
    {
        get
        {
            try
            {
                using var cuKey = Registry.CurrentUser.OpenSubKey(WebcamConsentSubKey);
                if (cuKey != null) return true;
                using var lmKey = Registry.LocalMachine.OpenSubKey(WebcamConsentSubKey);
                return lmKey != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _isMonitoring) return;
            _isMonitoring = true;
            _consecutiveActiveSeconds = 0;
            _consecutiveInactiveSeconds = 0;
            _isDebouncedInUse = false;

            // Poll every 1 second (1000ms) on a background thread
            _pollTimer = new Timer(OnPollTick, null, 1000, 1000);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isMonitoring) return;
            _isMonitoring = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            _consecutiveActiveSeconds = 0;
            _consecutiveInactiveSeconds = 0;
            _isDebouncedInUse = false;
        }
    }

    /// <summary>
    /// Checks the registry consent store directly and returns all currently active camera apps.
    /// </summary>
    public IReadOnlyList<string> GetActiveCameraApps()
    {
        var result = new List<string>();
        try
        {
            ScanRegistryRoot(Registry.CurrentUser, result);
            if (result.Count == 0)
            {
                ScanRegistryRoot(Registry.LocalMachine, result);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Check error: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Enumerates physical camera devices recognized by Windows PnP (e.g. Integrated Camera, USB Webcams).
    /// </summary>
    public IReadOnlyList<string> GetInstalledCameras()
    {
        var list = new List<string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name))
                {
                    list.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Hardware enumeration error: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Enumerates video capture devices asynchronously via Windows.Devices.Enumeration,
    /// falling back to WMI if empty.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetInstalledCamerasAsync()
    {
        var list = new List<string>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            foreach (var d in devices)
            {
                if (!string.IsNullOrWhiteSpace(d.Name) && !list.Contains(d.Name))
                {
                    list.Add(d.Name);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] DeviceInformation enumeration error: {ex.Message}");
        }

        if (list.Count == 0)
        {
            foreach (var c in GetInstalledCameras())
            {
                if (!list.Contains(c)) list.Add(c);
            }
        }

        return list;
    }

    /// <summary>
    /// Probes each video capture device by attempting MediaCapture initialization.
    /// If an exclusive stream is actively held by another app, initialization fails with
    /// UnauthorizedAccessException or HRESULT 0xC00D3704 / ERROR_BUSY.
    /// If the camera is free/idle, initialization succeeds and the capture object is immediately disposed.
    /// </summary>
    public async Task<(bool IsStreaming, string? DeviceName, string? Reason)> ProbeCameraStreamingAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices.Count == 0)
                return (false, null, "No camera devices found");

            foreach (var dev in devices)
            {
                if (!dev.IsEnabled) continue;

                try
                {
                    using var capture = new MediaCapture();
                    var initSettings = new MediaCaptureInitializationSettings
                    {
                        VideoDeviceId = dev.Id,
                        StreamingCaptureMode = StreamingCaptureMode.Video
                    };
                    await capture.InitializeAsync(initSettings);
                    // Successfully opened -> this camera is idle and available
                }
                catch (UnauthorizedAccessException)
                {
                    // Camera stream is exclusively locked by another application
                    return (true, dev.Name, "Access denied: stream locked by another app");
                }
                catch (System.Runtime.InteropServices.COMException comEx)
                {
                    return (true, dev.Name, $"Device busy (HRESULT 0x{comEx.HResult:X8})");
                }
                catch (Exception ex)
                {
                    return (true, dev.Name, ex.Message);
                }
            }

            return (false, devices[0].Name, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Probe error: {ex.Message}");
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Checks running processes for known video conferencing or camera apps.
    /// </summary>
    public static IReadOnlyList<string> GetRunningCameraAppNames()
    {
        var found = new List<string>();
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    var pName = p.ProcessName;
                    foreach (var (proc, display) in KnownCameraProcesses)
                    {
                        if (pName.Equals(proc, StringComparison.OrdinalIgnoreCase) && !found.Contains(display))
                        {
                            found.Add(display);
                        }
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Process scan error: {ex.Message}");
        }
        return found;
    }

    /// <summary>
    /// Performs an all-in-one check of camera hardware devices, MediaCapture streaming lock,
    /// and active apps (both from ConsentStore and process detection).
    /// </summary>
    public async Task<CameraCheckResult> CheckCameraStatusAsync()
    {
        var installed = await GetInstalledCamerasAsync();
        if (installed.Count == 0)
        {
            return new CameraCheckResult
            {
                InstalledCameras = Array.Empty<string>(),
                IsStreaming = false,
                ActiveApps = Array.Empty<string>(),
                Reason = "No camera hardware detected"
            };
        }

        // 1. Probe hardware via MediaCapture initialization
        var (isStreaming, devName, reason) = await ProbeCameraStreamingAsync();

        // 2. Collect any active apps from Windows privacy consent store
        var activeApps = new List<string>(GetActiveCameraApps());

        // 3. Collect any running dedicated camera apps (e.g. Windows Camera, Zoom, Teams)
        var runningMeetingApps = GetRunningCameraAppNames();

        if (isStreaming)
        {
            // Hardware lock detected: stream is in use!
            foreach (var app in runningMeetingApps)
            {
                if (!activeApps.Contains(app)) activeApps.Add(app);
            }

            if (activeApps.Count == 0)
            {
                activeApps.Add("Active camera stream");
            }

            return new CameraCheckResult
            {
                InstalledCameras = installed,
                IsStreaming = true,
                ActiveApps = activeApps,
                Reason = reason
            };
        }

        // If probe succeeded (idle) but consent store had an active app, trust consent store
        if (activeApps.Count > 0)
        {
            return new CameraCheckResult
            {
                InstalledCameras = installed,
                IsStreaming = true,
                ActiveApps = activeApps,
                Reason = "Detected via Windows privacy consent store"
            };
        }

        return new CameraCheckResult
        {
            InstalledCameras = installed,
            IsStreaming = false,
            ActiveApps = Array.Empty<string>(),
            Reason = "Camera is idle"
        };
    }

    private static void ScanRegistryRoot(RegistryKey root, List<string> result)
    {
        using var baseKey = root.OpenSubKey(WebcamConsentSubKey);
        if (baseKey == null) return;

        // 1. Packaged apps (directly under webcam subkey, excluding 'NonPackaged')
        foreach (var subKeyName in baseKey.GetSubKeyNames())
        {
            if (subKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                continue;

            using var appKey = baseKey.OpenSubKey(subKeyName);
            if (appKey == null) continue;

            if (IsAppUsingCamera(appKey))
            {
                var name = CleanPackagedName(subKeyName);
                if (!IsAppLock(name) && !result.Contains(name))
                    result.Add(name);
            }
        }

        // 2. Desktop apps (under NonPackaged)
        using var nonPackagedKey = baseKey.OpenSubKey("NonPackaged");
        if (nonPackagedKey != null)
        {
            foreach (var subKeyName in nonPackagedKey.GetSubKeyNames())
            {
                using var appKey = nonPackagedKey.OpenSubKey(subKeyName);
                if (appKey == null) continue;

                if (IsAppUsingCamera(appKey))
                {
                    var name = CleanDesktopName(subKeyName);
                    if (!IsAppLock(name) && !result.Contains(name))
                        result.Add(name);
                }
            }
        }
    }

    /// <summary>
    /// Checks whether active camera apps match the specified <see cref="AutoRingSettings"/> filter.
    /// </summary>
    public static bool MatchesFilter(IReadOnlyList<string> activeApps, AutoRingSettings auto)
    {
        if (activeApps.Count == 0) return false;
        if (!auto.OnlySelectedApps || auto.AppFilter.Count == 0) return true;

        foreach (var app in activeApps)
        {
            foreach (var filter in auto.AppFilter)
            {
                if (string.IsNullOrWhiteSpace(filter)) continue;
                var trimmed = filter.Trim();
                if (app.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains(app, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _isMonitoring = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async void OnPollTick(object? state)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;

        try
        {
            if (!_isMonitoring) return;

            // 1. Check registry consent store
            var activeApps = new List<string>(GetActiveCameraApps());

            // 2. If consent store returned no apps, check if potential camera streaming app is active
            var runningApps = GetRunningCameraAppNames();
            bool hasPotentialStreamingApp = runningApps.Count > 0 || _isDebouncedInUse;

            if (activeApps.Count == 0 && hasPotentialStreamingApp)
            {
                var probe = await ProbeCameraStreamingAsync();
                if (probe.IsStreaming)
                {
                    foreach (var app in runningApps)
                    {
                        if (!activeApps.Contains(app)) activeApps.Add(app);
                    }
                    if (activeApps.Count == 0)
                    {
                        activeApps.Add("Camera");
                    }
                }
            }

            lock (_lock)
            {
                if (!_isMonitoring) return;

                if (activeApps.Count > 0)
                {
                    _consecutiveActiveSeconds++;
                    _consecutiveInactiveSeconds = 0;

                    // 1 second of continuous use required to turn on
                    if (!_isDebouncedInUse && _consecutiveActiveSeconds >= 1)
                    {
                        _isDebouncedInUse = true;
                        NotifyUsageChanged(true, activeApps);
                    }
                }
                else
                {
                    _consecutiveInactiveSeconds++;
                    _consecutiveActiveSeconds = 0;

                    // 5 seconds of silence required to turn off
                    if (_isDebouncedInUse && _consecutiveInactiveSeconds >= 5)
                    {
                        _isDebouncedInUse = false;
                        NotifyUsageChanged(false, Array.Empty<string>());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraUsageMonitor] Poll tick error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    private void NotifyUsageChanged(bool inUse, IReadOnlyList<string> activeApps)
    {
        try
        {
            CameraUsageChanged?.Invoke(this, (inUse, activeApps));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Event error: {ex.Message}");
        }
    }

    private static bool IsAppUsingCamera(RegistryKey key)
    {
        try
        {
            var startVal = key.GetValue("LastUsedTimeStart");
            if (startVal == null) return false;

            var stopVal = key.GetValue("LastUsedTimeStop");

            long start = ToInt64Safe(startVal);
            long stop  = stopVal != null ? ToInt64Safe(stopVal) : 0;

            // Using camera if start time is greater than stop time (stop == 0 during initial/active use)
            return start > stop;
        }
        catch
        {
            return false;
        }
    }

    private static long ToInt64Safe(object value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            ulong ul => (long)ul,
            uint ui => ui,
            byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => Convert.ToInt64(value)
        };
    }

    private static string CleanDesktopName(string keyName)
    {
        // Desktop keys replace '\' with '#'
        var unescaped = keyName.Replace('#', '\\');
        try
        {
            var fileName = Path.GetFileName(unescaped);
            return string.IsNullOrEmpty(fileName) ? unescaped : fileName;
        }
        catch
        {
            return unescaped;
        }
    }

    private static string CleanPackagedName(string keyName)
    {
        // For packaged apps, e.g. "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Windows Camera" or prefix
        if (keyName.Contains('_'))
        {
            var prefix = keyName.Split('_')[0];
            if (prefix.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                return prefix["Microsoft.".Length..];
            return prefix;
        }
        return keyName;
    }

    private static bool IsAppLock(string name)
    {
        return name.Contains("AppLock", StringComparison.OrdinalIgnoreCase);
    }
}
