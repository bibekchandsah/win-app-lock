using System;

namespace AppLock.Models;

/// <summary>
/// Records a network device event: joined, left, or changed IP.
/// </summary>
public class NetworkDeviceHistoryEntry
{
    public string MacAddress { get; set; } = string.Empty;
    public NetworkDeviceEventType EventType { get; set; }
    public DateTime Timestamp { get; set; }
    
    /// <summary>For IP changes: old IP. For other events: optional context.</summary>
    public string OldValue { get; set; } = string.Empty;
    
    /// <summary>For IP changes: new IP. For other events: optional context.</summary>
    public string NewValue { get; set; } = string.Empty;
}

public enum NetworkDeviceEventType
{
    Joined,
    Left,
    ChangedIP
}
