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

        System.Diagnostics.Debug.WriteLine($"Started idle monitoring: {idleThresholdMinutes} minutes threshold");
    }

    /// <summary>
    /// Stop monitoring for idle time
    /// </summary>
    public void StopMonitoring()
    {
        _isMonitoring = false;
        _idleCheckTimer?.Dispose();
        _idleCheckTimer = null;

        System.Diagnostics.Debug.WriteLine("Stopped idle monitoring");
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
            uint idleTimeMs = GetIdleTimeMs();
            double idleMinutes = idleTimeMs / 1000.0 / 60.0;

            if (idleMinutes >= _idleThresholdMinutes)
            {
                System.Diagnostics.Debug.WriteLine($"Idle time reached: {idleMinutes:F1} minutes");
                
                // Stop monitoring to prevent multiple triggers
                StopMonitoring();
                
                // Trigger event
                IdleTimeReached?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking idle time: {ex.Message}");
        }
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

        return 0;
    }

    /// <summary>
    /// Get the current idle time in minutes
    /// </summary>
    public static double GetIdleTimeMinutes()
    {
        return GetIdleTimeMs() / 1000.0 / 60.0;
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
