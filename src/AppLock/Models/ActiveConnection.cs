using System;
using Microsoft.UI.Xaml;

namespace AppLock.Models;

public class ActiveConnection
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public string Protocol { get; set; } = ""; // TCP or UDP
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public string State { get; set; } = ""; // TCP states: ESTABLISHED, LISTEN, etc.
    public bool IsSystemProcess { get; set; }
    public bool CanBlock { get; set; } = true;
    
    public string LocalEndpoint => $"{LocalAddress}:{LocalPort}";
    public string RemoteEndpoint => string.IsNullOrEmpty(RemoteAddress) ? "-" : $"{RemoteAddress}:{RemotePort}";
    public string Connection => $"{LocalEndpoint} → {RemoteEndpoint}";
    public Visibility SystemBadgeVisibility => IsSystemProcess ? Visibility.Visible : Visibility.Collapsed;
}
