using System;

namespace AppLock.Models;

/// <summary>
/// A device that has been seen on the local network, identified by its MAC address.
/// </summary>
public class NetworkDeviceEntry
{
    /// <summary>MAC address in colon-separated upper-case hex, e.g. "AA:BB:CC:DD:EE:FF".</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Last known IP address (may change between scans).</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>User-assigned friendly name. Empty until the user edits it.</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>When was this device first detected.</summary>
    public DateTime FirstSeen { get; set; } = DateTime.Now;

    /// <summary>When was this device last seen.</summary>
    public DateTime LastSeen { get; set; } = DateTime.Now;

    /// <summary>
    /// Whether the user has explicitly marked this device as trusted.
    /// Devices are trusted the moment the user clicks "Trust" or on first scan
    /// while the alert feature is not yet enabled.
    /// </summary>
    public bool IsTrusted { get; set; } = true;
}
