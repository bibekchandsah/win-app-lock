using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace AppLock.Services.WebFilter;

public sealed class NetworkAdapterInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required NetworkInterfaceType InterfaceType { get; init; }

    public required bool IsUp { get; init; }

    public required bool HasGateway { get; init; }

    public bool Ipv4FromDhcp { get; set; } = true;

    public List<string> Ipv4Servers { get; set; } = new();

    public bool Ipv6FromDhcp { get; set; } = true;

    public List<string> Ipv6Servers { get; set; } = new();
}

/// <summary>
/// Abstraction over Windows network adapters and DNS settings so DNS configuration
/// and restoration can be unit-tested without mutating the host machine.
/// </summary>
public interface INetworkAdapterService
{
    /// <summary>Returns active connected adapters that have an IPv4 or IPv6 default gateway.</summary>
    IReadOnlyList<NetworkAdapterInfo> GetEligibleAdapters(bool includeVpn = false);

    /// <summary>Reads current DNS settings for a specific adapter.</summary>
    NetworkAdapterInfo? GetAdapter(string adapterId);

    /// <summary>Sets DNS servers (or DHCP) for an adapter.</summary>
    bool SetAdapterDns(
        string adapterId,
        string adapterName,
        IReadOnlyList<string> ipv4Servers,
        IReadOnlyList<string> ipv6Servers,
        bool ipv4Dhcp,
        bool ipv6Dhcp);

    /// <summary>Flushes the Windows DNS resolver cache.</summary>
    void FlushDnsCache();
}
