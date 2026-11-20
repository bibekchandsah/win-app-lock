using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AppLock.Services.WebFilter;

public sealed class RealNetworkAdapterService : INetworkAdapterService
{
    private const uint DNS_INTERFACE_SETTINGS_VERSION1 = 1;
    private const ulong DNS_SETTING_NAMESERVER = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DNS_INTERFACE_SETTINGS
    {
        public uint Version;
        public ulong Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Domain;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? NameServer;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLLMNR;
        public uint QueryAdapterName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProfileNameServer;
    }

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetInterfaceDnsSettings(
        [In] ref Guid Interface,
        [In] ref DNS_INTERFACE_SETTINGS Settings);

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern int DnsFlushResolverCache();

    public IReadOnlyList<NetworkAdapterInfo> GetEligibleAdapters(bool includeVpn = false)
    {
        var result = new List<NetworkAdapterInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in interfaces)
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                var desc = (nic.Description ?? string.Empty).ToLowerInvariant();
                var name = (nic.Name ?? string.Empty).ToLowerInvariant();

                if (!includeVpn)
                {
                    if (desc.Contains("tap-windows") || desc.Contains("wireguard") || desc.Contains("openvpn") ||
                        desc.Contains("nordlynx") || desc.Contains("tailscale") || desc.Contains("zerotier") ||
                        desc.Contains("vpn") || name.Contains("vpn"))
                    {
                        continue;
                    }
                }

                IPInterfaceProperties? props;
                try
                {
                    props = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                if (props == null) continue;

                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g?.Address != null &&
                    !IPAddress.Any.Equals(g.Address) &&
                    !IPAddress.IPv6Any.Equals(g.Address) &&
                    !IPAddress.None.Equals(g.Address) &&
                    !IPAddress.IsLoopback(g.Address));

                if (!hasGateway) continue;

                var info = ReadAdapterInfo(nic, props, hasGateway);
                if (info != null)
                {
                    result.Add(info);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Error enumerating network adapters: {ex.Message}");
        }

        return result;
    }

    public NetworkAdapterInfo? GetAdapter(string adapterId)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase));

            if (nic == null) return null;

            var props = nic.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g =>
                g?.Address != null &&
                !IPAddress.Any.Equals(g.Address) &&
                !IPAddress.IPv6Any.Equals(g.Address) &&
                !IPAddress.None.Equals(g.Address) &&
                !IPAddress.IsLoopback(g.Address));

            return ReadAdapterInfo(nic, props, hasGateway);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Error getting adapter {adapterId}: {ex.Message}");
            return null;
        }
    }

    private static NetworkAdapterInfo? ReadAdapterInfo(NetworkInterface nic, IPInterfaceProperties props, bool hasGateway)
    {
        string id = nic.Id;
        string name = nic.Name;
        string desc = nic.Description;

        bool ipv4Dhcp = true;
        var ipv4Servers = new List<string>();
        bool ipv6Dhcp = true;
        var ipv6Servers = new List<string>();

        // Query Registry for exact static vs DHCP configuration
        ReadIpv4Registry(id, out ipv4Dhcp, ipv4Servers);
        ReadIpv6Registry(id, out ipv6Dhcp, ipv6Servers);

        // Fallback to NetworkInterface DNS addresses if registry didn't list servers
        if (ipv4Servers.Count == 0 && props.DnsAddresses != null)
        {
            foreach (var addr in props.DnsAddresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipv4Servers.Add(addr.ToString());
                }
            }
        }

        if (ipv6Servers.Count == 0 && props.DnsAddresses != null)
        {
            foreach (var addr in props.DnsAddresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetworkV6 && !addr.IsIPv6LinkLocal && !addr.IsIPv6SiteLocal)
                {
                    ipv6Servers.Add(addr.ToString());
                }
            }
        }

        return new NetworkAdapterInfo
        {
            Id = id,
            Name = name,
            Description = desc,
            InterfaceType = nic.NetworkInterfaceType,
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            HasGateway = hasGateway,
            Ipv4FromDhcp = ipv4Dhcp,
            Ipv4Servers = ipv4Servers,
            Ipv6FromDhcp = ipv6Dhcp,
            Ipv6Servers = ipv6Servers
        };
    }

    private static void ReadIpv4Registry(string adapterId, out bool isDhcp, List<string> servers)
    {
        isDhcp = true;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");
            if (key != null)
            {
                var staticDns = key.GetValue("NameServer") as string;
                if (!string.IsNullOrWhiteSpace(staticDns))
                {
                    isDhcp = false;
                    servers.AddRange(staticDns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                }
                else
                {
                    var dhcpDns = key.GetValue("DhcpNameServer") as string;
                    if (!string.IsNullOrWhiteSpace(dhcpDns))
                    {
                        servers.AddRange(dhcpDns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                    }
                }

                if (key.GetValue("EnableDHCP") is int enableDhcp)
                {
                    if (enableDhcp == 0 && servers.Count > 0)
                    {
                        isDhcp = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Registry read IPv4 failed for {adapterId}: {ex.Message}");
        }
    }

    private static void ReadIpv6Registry(string adapterId, out bool isDhcp, List<string> servers)
    {
        isDhcp = true;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\{adapterId}");
            if (key != null)
            {
                var staticDns = key.GetValue("NameServer") as string;
                if (!string.IsNullOrWhiteSpace(staticDns))
                {
                    isDhcp = false;
                    servers.AddRange(staticDns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Registry read IPv6 failed for {adapterId}: {ex.Message}");
        }
    }

    public bool SetAdapterDns(
        string adapterId,
        string adapterName,
        IReadOnlyList<string> ipv4Servers,
        IReadOnlyList<string> ipv6Servers,
        bool ipv4Dhcp,
        bool ipv6Dhcp)
    {
        bool pInvokeSucceeded = false;

        // 1. Primary path: SetInterfaceDnsSettings from iphlpapi.dll (Windows 10 2004+)
        if (Guid.TryParse(adapterId, out var guid))
        {
            try
            {
                var allServers = new List<string>();
                if (!ipv4Dhcp && ipv4Servers != null) allServers.AddRange(ipv4Servers);
                if (!ipv6Dhcp && ipv6Servers != null) allServers.AddRange(ipv6Servers);

                var settings = new DNS_INTERFACE_SETTINGS
                {
                    Version = DNS_INTERFACE_SETTINGS_VERSION1,
                    Flags = DNS_SETTING_NAMESERVER,
                    NameServer = allServers.Count > 0 ? string.Join(",", allServers) : null
                };

                uint ret = SetInterfaceDnsSettings(ref guid, ref settings);
                if (ret == 0)
                {
                    pInvokeSucceeded = true;
                }
                else
                {
                    Debug.WriteLine($"[WebFilter] SetInterfaceDnsSettings returned {ret} for {adapterId}");
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Older OS: will fall back to netsh
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebFilter] SetInterfaceDnsSettings threw for {adapterId}: {ex.Message}");
            }
        }

        if (pInvokeSucceeded)
        {
            return true;
        }

        // 2. Fallback path: netsh
        bool netshSucceeded = true;
        try
        {
            // IPv4
            if (ipv4Dhcp)
            {
                netshSucceeded &= RunNetsh(new[] { "interface", "ipv4", "set", "dnsservers", $"name={adapterName}", "source=dhcp" });
            }
            else if (ipv4Servers != null && ipv4Servers.Count > 0)
            {
                netshSucceeded &= RunNetsh(new[] { "interface", "ipv4", "set", "dnsservers", $"name={adapterName}", "source=static", $"address={ipv4Servers[0]}", "register=none", "validate=no" });
                for (int i = 1; i < ipv4Servers.Count; i++)
                {
                    RunNetsh(new[] { "interface", "ipv4", "add", "dnsservers", $"name={adapterName}", $"address={ipv4Servers[i]}", $"index={i + 1}", "validate=no" });
                }
            }

            // IPv6
            if (ipv6Dhcp)
            {
                RunNetsh(new[] { "interface", "ipv6", "set", "dnsservers", $"name={adapterName}", "source=dhcp" });
            }
            else if (ipv6Servers != null && ipv6Servers.Count > 0)
            {
                RunNetsh(new[] { "interface", "ipv6", "set", "dnsservers", $"name={adapterName}", "source=static", $"address={ipv6Servers[0]}", "register=none", "validate=no" });
                for (int i = 1; i < ipv6Servers.Count; i++)
                {
                    RunNetsh(new[] { "interface", "ipv6", "add", "dnsservers", $"name={adapterName}", $"address={ipv6Servers[i]}", $"index={i + 1}", "validate=no" });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Netsh fallback failed: {ex.Message}");
            netshSucceeded = false;
        }

        return netshSucceeded;
    }

    private static bool RunNetsh(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process == null) return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] RunNetsh failed: {ex.Message}");
            return false;
        }
    }

    public void FlushDnsCache()
    {
        try
        {
            DnsFlushResolverCache();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] DnsFlushResolverCache failed: {ex.Message}");
        }

        try
        {
            var psi = new ProcessStartInfo("ipconfig")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("/flushdns");
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
        }
        catch { }
    }
}
