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
    /// Detects active video capture utility processes spawned by Chromium browsers
    /// (Google Chrome, Microsoft Edge, Brave, Opera, Vivaldi) and Electron apps when a web page
    /// or web call (such as Instagram, Google Meet, Teams Web, Zoom Web) starts streaming the webcam.
    ///
    /// Uses WMI to find processes whose CommandLine contains the Chromium video capture service
    /// argument. This subprocess is spawned by the browser ONLY while the camera is actively streaming
    /// and disappears the moment the camera is released — making it a precise, real-time indicator.
    /// </summary>
    public static IReadOnlyList<string> GetActiveVideoCaptureProcesses()
    {
        var found = new List<string>();
        try
        {
            // ManagementScope and ObjectQuery do not implement IDisposable — do not wrap in using.
            var scope = new System.Management.ManagementScope(@"\\.\root\cimv2");
            scope.Options.Timeout = TimeSpan.FromSeconds(5);

            // WHERE CommandLine IS NOT NULL skips system/protected processes where WMI cannot
            // read CommandLine (avoids silent filter failures on null rows).
            var query = new System.Management.ObjectQuery(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process " +
                "WHERE CommandLine IS NOT NULL AND CommandLine LIKE '%video_capture.mojom.VideoCaptureService%'");

            var options = new System.Management.EnumerationOptions { ReturnImmediately = false };
            using var searcher = new System.Management.ManagementObjectSearcher(scope, query, options);

            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                string display = name.ToLowerInvariant() switch
                {
                    "chrome.exe"  => "Google Chrome (Web video call)",
                    "msedge.exe"  => "Microsoft Edge (Web video call)",
                    "brave.exe"   => "Brave (Web video call)",
                    "opera.exe"   => "Opera (Web video call)",
                    "vivaldi.exe" => "Vivaldi (Web video call)",
                    _ => string.IsNullOrEmpty(name)
                            ? "Web video call"
                            : $"{Path.GetFileNameWithoutExtension(name)} (Web video call)"
                };

                if (!found.Contains(display))
                    found.Add(display);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] VideoCaptureService WMI query error: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Fast check to verify whether any browser or camera process is running before querying WMI.
    /// </summary>
    private static bool HasRunningBrowserOrCameraProcess()
    {
        try
        {
            return Process.GetProcessesByName("chrome").Length > 0 ||
                   Process.GetProcessesByName("msedge").Length > 0 ||
                   Process.GetProcessesByName("brave").Length > 0 ||
                   Process.GetProcessesByName("opera").Length > 0 ||
                   Process.GetProcessesByName("vivaldi").Length > 0 ||
                   Process.GetProcessesByName("WindowsCamera").Length > 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Performs an all-in-one check of camera hardware devices, MediaCapture streaming lock,
    /// and active apps (both from ConsentStore, web VideoCaptureService, and process detection).
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

        var activeApps = new List<string>();

        // 1. Detect browser/Electron web video calls via WMI (Chrome, Edge, Brave, etc.)
        // Accurate: the video_capture.mojom.VideoCaptureService subprocess exists only
        // when the browser is actively holding the camera pipeline open.
        var webCaptureApps = GetActiveVideoCaptureProcesses();
        foreach (var app in webCaptureApps)
        {
            if (!activeApps.Contains(app)) activeApps.Add(app);
        }

        // Note: GetRunningCameraAppNames() (process-name scan for Teams, Discord, OBS, etc.)
        // is intentionally NOT called here — it reports apps just for being open, not for
        // actually using the camera, causing false positives.

        // 2. Collect from Windows privacy consent store (catches UWP/packaged apps)
        foreach (var app in GetActiveCameraApps())
        {
            if (!activeApps.Contains(app)) activeApps.Add(app);
        }

        // 3. Probe hardware via MediaCapture initialization (catches exclusive driver locks)
        var (isExclusiveLocked, devName, reason) = await ProbeCameraStreamingAsync();
        if (isExclusiveLocked && activeApps.Count == 0)
        {
            activeApps.Add("Active camera stream");
        }

        bool isStreaming = activeApps.Count > 0 || isExclusiveLocked;

        return new CameraCheckResult
        {
            InstalledCameras = installed,
            IsStreaming = isStreaming,
            ActiveApps = activeApps,
            Reason = isStreaming
                ? (activeApps.Count > 0 ? string.Join(", ", activeApps) : reason ?? "Camera streaming active")
                : "Camera is idle"
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

            var activeApps = new List<string>();

            // 1. Check browser video capture utility processes (Chrome, Edge, Brave, etc.)
            // Pre-check with GetProcessesByName before the slower WMI query.
            if (HasRunningBrowserOrCameraProcess())
            {
                foreach (var app in GetActiveVideoCaptureProcesses())
                {
                    if (!activeApps.Contains(app)) activeApps.Add(app);
                }
            }

            // Note: GetRunningCameraAppNames() is intentionally NOT called here —
            // it reports apps just for being open (not for actually using the camera),
            // causing false positives for Teams, Discord, OBS, etc. that are open but idle.

            // 2. Check registry consent store (catches UWP/packaged apps, Windows Camera, etc.)
            foreach (var app in GetActiveCameraApps())
            {
                if (!activeApps.Contains(app)) activeApps.Add(app);
            }

            // 4. Probe hardware if in-use or potential lock
            if (activeApps.Count == 0 && _isDebouncedInUse)
            {
                var probe = await ProbeCameraStreamingAsync();
                if (probe.IsStreaming)
                {
                    activeApps.Add("Camera");
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
