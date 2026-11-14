using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;

namespace AppLock.Services.SpeedMeter;

/// <summary>Adapter, address and Wi-Fi information shown in the details flyout.</summary>
public sealed class NetworkDetails
{
    public string AdapterName { get; init; } = string.Empty;
    public string AdapterDescription { get; init; } = string.Empty;
    public string LocalIp { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;
    public string Dns { get; init; } = string.Empty;
    public long LinkSpeedBitsPerSec { get; init; }
    public bool IsWireless { get; init; }
    public string Ssid { get; init; } = string.Empty;
    public int SignalBars { get; init; }
    public bool IsVpn { get; init; }

    public bool HasAdapter => !string.IsNullOrEmpty(AdapterName);
}

/// <summary>
/// Reads the details of the adapter Windows routes through. Everything here is local API state,
/// so showing it makes no network requests.
/// </summary>
public static class NetworkDetailsProvider
{
    private static readonly string[] VpnKeywords =
    {
        "vpn", "wireguard", "openvpn", "tap-", "tun", "tailscale", "zerotier", "nordlynx"
    };

    public static NetworkDetails Read()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up)
                .Where(a => a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();

            var adapter = adapters
                .OrderByDescending(a => HasGateway(a))
                .ThenByDescending(a => a.Speed)
                .FirstOrDefault();

            if (adapter is null) return new NetworkDetails();

            var properties = adapter.GetIPProperties();

            var localIp = properties.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault(ip => !ip.StartsWith("169.254", StringComparison.Ordinal)) ?? string.Empty;

            var gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString() ?? string.Empty;

            var dns = string.Join(", ", properties.DnsAddresses
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString()));

            var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();

            return new NetworkDetails
            {
                AdapterName = adapter.Name,
                AdapterDescription = adapter.Description,
                LocalIp = localIp,
                Gateway = gateway,
                Dns = dns,
                LinkSpeedBitsPerSec = adapter.Speed,
                IsWireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                Ssid = ReadSsid(),
                SignalBars = ReadSignalBars(),
                IsVpn = VpnKeywords.Any(text.Contains)
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Details read error: {ex.Message}");
            return new NetworkDetails();
        }
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                          && !g.Address.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static string ReadSsid()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile?.IsWlanConnectionProfile != true) return string.Empty;

            return profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static int ReadSignalBars()
    {
        try
        {
            return NetworkInformation.GetInternetConnectionProfile()?.GetSignalBars() ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
