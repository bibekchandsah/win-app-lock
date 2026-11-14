using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace AppLock.Services;

/// <summary>
/// Service for monitoring user idle time
/// </summary>
public class IdleService : IDisposable
{
    private Timer? _idleCheckTimer;
    private int _idleThresholdMinutes;
    private bool _isMonitoring;
    private DateTime _lastActiveTime;

    public event EventHandler? IdleTimeReached;

    /// <summary>True while the idle timer is running.</summary>
    public bool IsMonitoring => _isMonitoring;

    /// <summary>The threshold the monitor is currently running with, in minutes.</summary>
    public int ThresholdMinutes => _idleThresholdMinutes;

    public IdleService()
    {
        _lastActiveTime = DateTime.Now;
    }

    /// <summary>
    /// Start monitoring for idle time
    /// </summary>
    /// <param name="idleThresholdMinutes">Minutes of inactivity before triggering</param>
    public void StartMonitoring(int idleThresholdMinutes)
    {
        if (idleThresholdMinutes <= 0)
        {
            StopMonitoring();
            return;
        }

        _idleThresholdMinutes = idleThresholdMinutes;
        _isMonitoring = true;
        _lastActiveTime = DateTime.Now;

        // Check every 10 seconds
        _idleCheckTimer?.Dispose();
        _idleCheckTimer = new Timer(CheckIdleTime, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        Log($"Started idle monitoring: {idleThresholdMinutes} minutes threshold");
    }

    /// <summary>
    /// Stop monitoring for idle time
    /// </summary>
    public void StopMonitoring()
    {
        _isMonitoring = false;
        _idleCheckTimer?.Dispose();
        _idleCheckTimer = null;

        Log("Stopped idle monitoring");
    }

    /// <summary>
    /// Reset the idle timer (called when user activity is detected)
    /// </summary>
    public void ResetIdleTimer()
    {
        _lastActiveTime = DateTime.Now;
    }

    private void CheckIdleTime(object? state)
    {
        if (!_isMonitoring)
            return;

        try
        {
            double idleMinutes = GetIdleMinutes();

            if (idleMinutes >= _idleThresholdMinutes)
            {
                Log($"Idle time reached: {idleMinutes:F1} minutes (threshold {_idleThresholdMinutes})");

                // Stop monitoring to prevent multiple triggers; MainWindow restarts it after unlock
                // (and its watchdog restarts it if the lock screen could not be shown).
                StopMonitoring();

                try
                {
                    IdleTimeReached?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    // Never let a handler kill the timer callback.
                    Log($"IdleTimeReached handler failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error checking idle time: {ex.Message}");
        }
    }

    /// <summary>
    /// Idle minutes used for the auto-lock check. Prefers AppLock's own input clock, because
    /// Windows' GetLastInputInfo can be reset continuously by a device on some machines and then
    /// never reaches the configured threshold. Falls back to Windows when the hooks are missing.
    /// </summary>
    private static double GetIdleMinutes()
    {
        if (InputActivityMonitor.IsRunning)
        {
            return InputActivityMonitor.IdleMinutes;
        }

        return GetIdleTimeMs() / 1000.0 / 60.0;
    }

    /// <summary>
    /// Get the current system idle time in milliseconds
    /// </summary>
    private static uint GetIdleTimeMs()
    {
        LASTINPUTINFO lastInputInfo = new()
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (PInvoke.GetLastInputInfo(ref lastInputInfo))
        {
            uint currentTickCount = (uint)Environment.TickCount;
            uint lastInputTick = lastInputInfo.dwTime;

            return currentTickCount - lastInputTick;
        }

        Log("GetLastInputInfo failed; reporting zero idle time");
        return 0;
    }

    /// <summary>
    /// Get the current idle time in minutes
    /// </summary>
    public static double GetIdleTimeMinutes()
    {
        return GetIdleTimeMs() / 1000.0 / 60.0;
    }

    /// <summary>Idle minutes from the same clock the auto-lock check uses.</summary>
    public static double CurrentIdleMinutes => GetIdleMinutes();

    /// <summary>Small, size-capped diagnostic log so idle-lock problems can be traced.</summary>
    internal static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[Idle] {message}");

        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "Logs");
            System.IO.Directory.CreateDirectory(dir);

            var path = System.IO.Path.Combine(dir, "idle.log");
            var info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > 200 * 1024)
            {
                System.IO.File.WriteAllText(path, string.Empty);
            }

            System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch { }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
