using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Network: every adapter with its status, link speed, addresses, gateway, DNS and DHCP details,
/// plus the Wi-Fi connection profile. Saved Wi-Fi passwords are never read or shown.
/// </summary>
public sealed class NetworkProvider : IInfoProvider
{
    public string Id => "network";
    public string Category => "network";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public TimeSpan CacheLifetime => TimeSpan.Zero;

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Network", Category = Category };
        var group = section.Group("Adapters");
        const string source = "Source: System.Net.NetworkInformation";

        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            group.Items.Add(InfoItem.MissingValue("network.error", "Adapters", MissingReason.Error, DetailLevel.Basic, source));
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Network enumeration failed: {ex.Message}");
            adapters = Array.Empty<NetworkInterface>();
        }

        var active = adapters.Where(a => a.OperationalStatus == OperationalStatus.Up).ToList();
        group.Items.Add(InfoItem.Of("network.activeCount", "Active adapters", active.Count.ToString(), DetailLevel.Basic,
            "How many network connections are up right now.", source));

        var wifi = ReadWifiProfile();
        if (wifi.Ssid != null)
        {
            group.Items.Add(InfoItem.Of("network.ssid", "Wi-Fi network", wifi.Ssid, DetailLevel.Basic,
                "The wireless network this PC is connected to.", "Source: Windows.Networking.Connectivity"));
            if (wifi.Signal > 0)
            {
                group.Items.Add(InfoItem.Of("network.signal", "Wi-Fi signal", $"{wifi.Signal}%", DetailLevel.Detailed,
                    "Signal strength as Windows reports it.", "Source: Windows.Networking.Connectivity"));
            }
            group.Items.Add(InfoItem.Of("network.profile", "Connection profile", wifi.ProfileName, DetailLevel.Detailed,
                "The name Windows saved for this network. Passwords are never read.",
                "Source: Windows.Networking.Connectivity"));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.4);

        var detailGroup = section.Group("Adapter details");
        foreach (var adapter in adapters.OrderByDescending(a => a.OperationalStatus == OperationalStatus.Up))
        {
            var name = adapter.Name;
            var status = adapter.OperationalStatus == OperationalStatus.Up ? "Up" : adapter.OperationalStatus.ToString();
            var type = DescribeType(adapter.NetworkInterfaceType);
            var speed = adapter.Speed > 0 ? $"{adapter.Speed / 1_000_000d:0} Mbps" : "Unknown";

            var item = InfoItem.Of($"network.adapter.{name}", name, $"{type} · {status} · {speed}", DetailLevel.Basic,
                "One network interface, its link state and its current link speed.", source);
            if (adapter.OperationalStatus != OperationalStatus.Up) item.Status = InfoStatus.Info;
            detailGroup.Items.Add(item);

            try
            {
                var properties = adapter.GetIPProperties();

                var ipv4 = properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToList();
                if (ipv4.Count > 0)
                {
                    detailGroup.Items.Add(InfoItem.Of($"network.adapter.{name}.ipv4", $"{name} IPv4", string.Join(", ", ipv4),
                        DetailLevel.Detailed, "The IPv4 address of this adapter.", source, sensitivity: InfoSensitivity.Identifier));
                }

                var ipv6 = properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                    .Select(a => a.Address.ToString())
                    .ToList();
                if (ipv6.Count > 0)
                {
                    detailGroup.Items.Add(InfoItem.Of($"network.adapter.{name}.ipv6", $"{name} IPv6", string.Join(", ", ipv6),
                        DetailLevel.Everything, "The IPv6 address of this adapter.", source, sensitivity: InfoSensitivity.Identifier));
                }

                var gateways = properties.GatewayAddresses.Select(g => g.Address.ToString()).Where(g => g != "0.0.0.0").ToList();
                if (gateways.Count > 0)
                {
                    detailGroup.Items.Add(InfoItem.Of($"network.adapter.{name}.gateway", $"{name} gateway", string.Join(", ", gateways),
                        DetailLevel.Detailed, "The router this adapter sends traffic through.", source, sensitivity: InfoSensitivity.Identifier));
                }

                var dns = properties.DnsAddresses.Select(d => d.ToString()).ToList();
                if (dns.Count > 0)
                {
                    detailGroup.Items.Add(InfoItem.Of($"network.adapter.{name}.dns", $"{name} DNS servers", string.Join(", ", dns),
                        DetailLevel.Detailed, "The name servers Windows asks to turn names into addresses.", source,
                        sensitivity: InfoSensitivity.Identifier));
                }

                var dhcp = properties.DhcpServerAddresses.Select(d => d.ToString()).ToList();
                if (dhcp.Count > 0)
                {
                    detailGroup.Items.Add(InfoItem.Of($"network.adapter.{name}.dhcp", $"{name} DHCP server", string.Join(", ", dhcp),
                        DetailLevel.Everything, "The server that handed out this adapter's address.", source,
                        sensitivity: InfoSensitivity.Identifier));
                }

                var mtu = properties.GetIPv4Properties()?.Mtu ?? 0;
                if (mtu > 0)
                {
                    detailGroup.Items.Add(InfoItem.Of($"network.adapter.{name}.mtu", $"{name} MTU", mtu.ToString(),
                        DetailLevel.Everything, "The largest packet size this adapter sends without fragmenting.", source));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PcChecker] Adapter {name} details failed: {ex.Message}");
            }

            var mac = adapter.GetPhysicalAddress()?.ToString();
            if (!string.IsNullOrWhiteSpace(mac))
            {
                detailGroup.Items.Add(InfoItem.Of($"network.adapter.{name}.mac", $"{name} MAC address",
                    string.Join(":", Enumerable.Range(0, mac.Length / 2).Select(i => mac.Substring(i * 2, 2))),
                    DetailLevel.Detailed, "The hardware address of this adapter.", source, sensitivity: InfoSensitivity.Identifier));
            }

            ct.ThrowIfCancellationRequested();
        }

        progress?.Report(1);
        return section;
    }

    private sealed record WifiInfo(string? Ssid, string? ProfileName, int Signal);

    /// <summary>SSID and signal of the current Wi-Fi connection; never any key material.</summary>
    private static WifiInfo ReadWifiProfile()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            var wlan = profile?.WlanConnectionProfileDetails;
            if (wlan == null) return new WifiInfo(null, null, 0);

            var ssid = wlan.GetConnectedSsid();
            var signal = profile!.GetSignalBars() is var bars and > 0 ? (int)(bars / 5.0 * 100) : 0;
            return new WifiInfo(ssid, profile.ProfileName, signal);
        }
        catch
        {
            return new WifiInfo(null, null, 0);
        }
    }

    private static string DescribeType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Loopback => "Loopback",
        NetworkInterfaceType.Tunnel => "VPN / tunnel",
        NetworkInterfaceType.Ppp => "PPP",
        _ => type.ToString()
    };
}
