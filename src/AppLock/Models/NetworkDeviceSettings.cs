namespace AppLock.Models;

public class NetworkDeviceSettings
{
    /// <summary>Whether the scanner runs in the background and watches for new devices.</summary>
    public bool ScannerEnabled { get; set; } = false;

    /// <summary>Interval in seconds between successive ARP-table scans.</summary>
    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>Show an in-app alert when an unknown device appears on the network.</summary>
    public bool AlertOnNewDevice { get; set; } = true;
}
