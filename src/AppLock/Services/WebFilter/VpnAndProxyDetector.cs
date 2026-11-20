using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Win32;

namespace AppLock.Services.WebFilter;

/// <summary>
/// Monitors network interfaces and system proxy settings on NetworkChange and periodic polling
/// to detect active VPN connections and HTTP/HTTPS proxies that could bypass local DNS filtering.
/// </summary>
public sealed class VpnAndProxyDetector : IDisposable
{
    private static readonly string[] VpnKeywords = new[]
    {
        "vpn", "wireguard", "tap", "wintun", "tailscale", "openvpn",
        "cisco", "anyconnect", "nord", "proton", "warp", "zerotier",
        "mullvad", "expressvpn", "surfshark", "fortinet", "globalprotect",
        "sstp", "l2tp", "ikev2"
    };

    private readonly Timer? _pollTimer;
    private bool _isVpnOrProxyActive;
    private string _statusSummary = string.Empty;
    private bool _disposed;

    public bool IsVpnOrProxyActive => _isVpnOrProxyActive;
    public string StatusSummary => _statusSummary;

    public event EventHandler<bool>? StatusChanged;

    public VpnAndProxyDetector()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _pollTimer = new Timer(_ => CheckStatus(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VpnAndProxyDetector] Init error: {ex.Message}");
        }

        CheckStatus();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        CheckStatus();
    }

    public void CheckStatus()
    {
        if (_disposed) return;

        bool active = false;
        string summary = string.Empty;

        try
        {
            // 1. Check network interfaces for active VPNs
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                bool isVpnType = nic.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                                 nic.NetworkInterfaceType.ToString().Equals("Tunnel", StringComparison.OrdinalIgnoreCase);

                string desc = (nic.Description ?? "").ToLowerInvariant();
                string name = (nic.Name ?? "").ToLowerInvariant();

                bool hasVpnKeyword = VpnKeywords.Any(k => desc.Contains(k) || name.Contains(k));

                if (isVpnType || hasVpnKeyword)
                {
                    active = true;
                    summary = $"Active VPN connection detected: {nic.Name}";
                    break;
                }
            }

            // 2. Check system proxy if no VPN found
            if (!active)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                    if (key != null)
                    {
                        object? proxyEnable = key.GetValue("ProxyEnable");
                        object? autoConfigUrl = key.GetValue("AutoConfigURL");

                        if (proxyEnable is int enableInt && enableInt == 1)
                        {
                            active = true;
                            summary = "System proxy is currently enabled in Windows Settings.";
                        }
                        else if (autoConfigUrl is string pac && !string.IsNullOrWhiteSpace(pac))
                        {
                            active = true;
                            summary = "Automatic proxy configuration script (PAC) is currently active.";
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VpnAndProxyDetector] CheckStatus error: {ex.Message}");
        }

        if (active != _isVpnOrProxyActive || summary != _statusSummary)
        {
            _isVpnOrProxyActive = active;
            _statusSummary = summary;
            Debug.WriteLine($"[VpnAndProxyDetector] Status changed: active={active}, summary={summary}");
            StatusChanged?.Invoke(this, active);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _pollTimer?.Dispose();
        }
        catch { }
    }
}
