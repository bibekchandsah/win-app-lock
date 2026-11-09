using System;
using System.Collections.Generic;

namespace AppLock.Services.RingLight;

/// <summary>
/// Source interface for monitoring webcam usage from the Windows privacy consent store.
/// Abstracts registry and system dependencies for unit testing.
/// </summary>
public interface ICameraUsageSource : IDisposable
{
    /// <summary>
    /// Raised when the debounced camera usage state changes.
    /// Passes whether camera is in active use and the list of currently active app identifiers.
    /// </summary>
    event EventHandler<(bool InUse, IReadOnlyList<string> ActiveApps)>? CameraUsageChanged;

    /// <summary>
    /// Gets the list of currently active camera apps without debouncing.
    /// </summary>
    IReadOnlyList<string> GetActiveCameraApps();

    /// <summary>
    /// Whether camera usage monitoring is supported on this system (e.g. registry key accessible).
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether background monitoring is actively running.
    /// </summary>
    bool IsMonitoring { get; }

    /// <summary>Starts monitoring camera usage.</summary>
    void Start();

    /// <summary>Stops monitoring camera usage.</summary>
    void Stop();
}
