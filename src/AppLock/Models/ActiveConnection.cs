using System;
using System.Net;
using System.Net.Sockets;

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

    public bool IsLocal => IsLocalAddress(RemoteAddress, LocalAddress);
    public bool IsPrivateNetwork => IsPrivateNetworkAddress(RemoteAddress);
    public bool IsInternet => !IsLocal && !IsPrivateNetwork && !string.IsNullOrWhiteSpace(RemoteAddress) && RemoteAddress != "0.0.0.0" && RemoteAddress != "::";

    public string ScopeText
    {
        get
        {
            if (IsLocal) return "Localhost";
            if (IsPrivateNetwork) return "Local network";
            if (IsInternet) return "Internet";
            return "Other";
        }
    }

    public static bool IsLocalAddress(string? remote, string? local = null)
    {
        if (string.IsNullOrWhiteSpace(remote) || remote == "-" || remote == "0.0.0.0" || remote == "::")
            return true;

        if (remote.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (remote.StartsWith("127."))
            return true;

        if (remote == "::1" || remote == "[::1]")
            return true;

        if (!string.IsNullOrEmpty(local) && remote.Equals(local, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(remote, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                return true;
        }

        return false;
    }

    public static bool IsPrivateNetworkAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (Link Local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 (Link local)
            if (ip.IsIPv6LinkLocal) return true;
            // fc00::/7 (Unique local)
            byte[] bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }
}
