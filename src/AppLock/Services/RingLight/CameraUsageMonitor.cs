using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AppLock.Models;
using Microsoft.Win32;

namespace AppLock.Services.RingLight;

/// <summary>
/// Monitors Windows privacy consent store for webcam usage without accessing the camera.
/// Implements 1-second continuous use debounce for 'On' and 5-second silence debounce for 'Off'.
/// </summary>
public sealed class CameraUsageMonitor : ICameraUsageSource
{
    private const string WebcamConsentSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    public event EventHandler<(bool InUse, IReadOnlyList<string> ActiveApps)>? CameraUsageChanged;

    private readonly object _lock = new();
    private Timer? _pollTimer;
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
                using var key = Registry.CurrentUser.OpenSubKey(WebcamConsentSubKey);
                return key != null;
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
            using var baseKey = Registry.CurrentUser.OpenSubKey(WebcamConsentSubKey);
            if (baseKey == null) return result;

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
                    if (!IsAppLock(name))
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
                        if (!IsAppLock(name))
                            result.Add(name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Check error: {ex.Message}");
        }

        return result;
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

    private void OnPollTick(object? state)
    {
        if (_disposed) return;

        IReadOnlyList<string> activeApps;
        lock (_lock)
        {
            if (!_isMonitoring) return;
            activeApps = GetActiveCameraApps();

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
            var stopVal = key.GetValue("LastUsedTimeStop");

            if (startVal == null || stopVal == null) return false;

            long start = Convert.ToInt64(startVal);
            long stop = Convert.ToInt64(stopVal);

            // Using camera if start time is greater than stop time (often stop == 0 during active use)
            return start > stop;
        }
        catch
        {
            return false;
        }
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
        // For packaged apps, simplify e.g. "Microsoft.WindowsCamera_8wekyb3d8bbwe" to friendly name or keep as family name
        return keyName;
    }

    private static bool IsAppLock(string name)
    {
        return name.Contains("AppLock", StringComparison.OrdinalIgnoreCase);
    }
}
