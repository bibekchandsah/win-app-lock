using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using AppLock.Models;

namespace AppLock.Services.WebFilter;

public sealed class DnsConfigurator
{
    private readonly INetworkAdapterService _adapterService;

    public DnsConfigurator(INetworkAdapterService? adapterService = null)
    {
        _adapterService = adapterService ?? new RealNetworkAdapterService();
    }

    public INetworkAdapterService AdapterService => _adapterService;

    /// <summary>
    /// Checks if an IP address string is a loopback, unspecified, or null address.
    /// Used to prevent saving loopback addresses as the PC's original DNS.
    /// </summary>
    public static bool IsLoopbackOrNull(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return true;

        var trimmed = address.Trim();
        if (trimmed.StartsWith("127.", StringComparison.Ordinal) ||
            trimmed.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("0.0.0.0", StringComparison.Ordinal) ||
            trimmed.Equals("::", StringComparison.Ordinal))
        {
            return true;
        }

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            return IPAddress.IsLoopback(ip) ||
                   ip.Equals(IPAddress.Any) ||
                   ip.Equals(IPAddress.IPv6Any) ||
                   ip.Equals(IPAddress.None);
        }

        return false;
    }

    /// <summary>
    /// Captures the per-adapter DNS settings (DHCP vs static, server lists) before the filter
    /// makes its first change. Will NEVER record a loopback address as original DNS.
    /// </summary>
    public bool SaveOriginalDns(WebFilterSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        var adapters = _adapterService.GetEligibleAdapters();
        if (adapters.Count == 0)
        {
            Debug.WriteLine("[WebFilter] No eligible adapters found when saving original DNS.");
            return false;
        }

        bool updated = false;
        settings.OriginalDns ??= new SavedDnsConfig();

        foreach (var adapter in adapters)
        {
            // If we already saved this adapter and it contains non-loopback configuration, preserve it.
            var existing = settings.OriginalDns.Adapters.FirstOrDefault(a =>
                string.Equals(a.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Description, adapter.Description, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Verify existing doesn't contain poisoned loopback addresses
                existing.Ipv4Servers.RemoveAll(IsLoopbackOrNull);
                existing.Ipv6Servers.RemoveAll(IsLoopbackOrNull);
                if (existing.Ipv4Servers.Count == 0) existing.Ipv4FromDhcp = true;
                if (existing.Ipv6Servers.Count == 0) existing.Ipv6FromDhcp = true;
                continue;
            }

            var cleanIpv4 = adapter.Ipv4Servers.Where(s => !IsLoopbackOrNull(s)).ToList();
            var cleanIpv6 = adapter.Ipv6Servers.Where(s => !IsLoopbackOrNull(s)).ToList();

            var saved = new SavedAdapterDns
            {
                AdapterId = adapter.Id,
                Description = adapter.Description,
                Ipv4FromDhcp = adapter.Ipv4FromDhcp || cleanIpv4.Count == 0,
                Ipv4Servers = cleanIpv4,
                Ipv6FromDhcp = adapter.Ipv6FromDhcp || cleanIpv6.Count == 0,
                Ipv6Servers = cleanIpv6
            };

            settings.OriginalDns.Adapters.Add(saved);
            updated = true;
        }

        if (updated || settings.OriginalDns.SavedUtc == null)
        {
            settings.OriginalDns.SavedUtc = DateTime.UtcNow;
        }

        return true;
    }

    /// <summary>
    /// Points Windows DNS on all eligible adapters to the local filter's loopback addresses.
    /// Always saves original DNS first.
    /// </summary>
    public bool ApplyFilterDns(WebFilterSettings settings, string ipv4Loopback, string ipv6Loopback = "::1")
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        // Always save original DNS first
        SaveOriginalDns(settings);

        var adapters = _adapterService.GetEligibleAdapters();
        if (adapters.Count == 0) return false;

        bool anySucceeded = false;
        foreach (var adapter in adapters)
        {
            bool ok = _adapterService.SetAdapterDns(
                adapter.Id,
                adapter.Name,
                new[] { ipv4Loopback },
                new[] { ipv6Loopback },
                ipv4Dhcp: false,
                ipv6Dhcp: false);

            if (ok) anySucceeded = true;
        }

        settings.Applied ??= new AppliedChanges();
        settings.Applied.LoopbackAddress = ipv4Loopback;
        settings.Applied.AppliedUtc = DateTime.UtcNow;

        _adapterService.FlushDnsCache();
        return anySucceeded;
    }

    /// <summary>
    /// Points Windows DNS on all eligible adapters to a filtered DNS provider's IP addresses.
    /// Always saves original DNS first.
    /// </summary>
    public bool ApplyProviderDns(WebFilterSettings settings, IReadOnlyList<string> ipv4Servers, IReadOnlyList<string> ipv6Servers)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        SaveOriginalDns(settings);

        var adapters = _adapterService.GetEligibleAdapters();
        if (adapters.Count == 0) return false;

        bool anySucceeded = false;
        foreach (var adapter in adapters)
        {
            bool ok = _adapterService.SetAdapterDns(
                adapter.Id,
                adapter.Name,
                ipv4Servers,
                ipv6Servers,
                ipv4Dhcp: ipv4Servers.Count == 0,
                ipv6Dhcp: ipv6Servers.Count == 0);

            if (ok) anySucceeded = true;
        }

        _adapterService.FlushDnsCache();
        return anySucceeded;
    }

    /// <summary>
    /// Restores the saved original per-adapter DNS settings. If an adapter was not in the saved list
    /// but is pointing to loopback, safely resets it to DHCP so internet connectivity is restored.
    /// </summary>
    public bool RestoreOriginalDns(WebFilterSettings settings)
    {
        if (settings == null) return false;

        var activeAdapters = _adapterService.GetEligibleAdapters();
        var savedList = settings.OriginalDns?.Adapters ?? new List<SavedAdapterDns>();
        var handledAdapterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool allOk = true;

        // 1. Restore adapters that have saved configurations
        foreach (var saved in savedList)
        {
            var match = activeAdapters.FirstOrDefault(a =>
                string.Equals(a.Id, saved.AdapterId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Description, saved.Description, StringComparison.OrdinalIgnoreCase));

            string adapterName = match?.Name ?? saved.Description;
            string adapterId = match?.Id ?? saved.AdapterId;

            handledAdapterIds.Add(adapterId);

            var cleanIpv4 = (saved.Ipv4Servers ?? new List<string>()).Where(s => !IsLoopbackOrNull(s)).ToList();
            var cleanIpv6 = (saved.Ipv6Servers ?? new List<string>()).Where(s => !IsLoopbackOrNull(s)).ToList();

            bool ok = _adapterService.SetAdapterDns(
                adapterId,
                adapterName,
                cleanIpv4,
                cleanIpv6,
                saved.Ipv4FromDhcp || cleanIpv4.Count == 0,
                saved.Ipv6FromDhcp || cleanIpv6.Count == 0);

            if (!ok) allOk = false;
        }

        // 2. Safety net: any other active adapter currently pointing to loopback is reset to DHCP
        foreach (var adapter in activeAdapters)
        {
            if (handledAdapterIds.Contains(adapter.Id)) continue;

            bool pointsToLoopback = adapter.Ipv4Servers.Any(IsLoopbackOrNull) || adapter.Ipv6Servers.Any(IsLoopbackOrNull);
            if (pointsToLoopback)
            {
                Debug.WriteLine($"[WebFilter] Safety net: restoring unmanaged loopback adapter {adapter.Name} to DHCP.");
                _adapterService.SetAdapterDns(adapter.Id, adapter.Name, Array.Empty<string>(), Array.Empty<string>(), true, true);
            }
        }

        if (settings.Applied != null)
        {
            settings.Applied.LoopbackAddress = string.Empty;
        }

        _adapterService.FlushDnsCache();
        return allOk;
    }

    /// <summary>
    /// Checks whether any eligible adapter currently points to the loopback filter or a specified address.
    /// </summary>
    public bool IsDnsPointingToFilter(string? filterAddress = null)
    {
        var adapters = _adapterService.GetEligibleAdapters();
        foreach (var adapter in adapters)
        {
            if (!string.IsNullOrEmpty(filterAddress))
            {
                if (adapter.Ipv4Servers.Any(s => string.Equals(s.Trim(), filterAddress.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                    adapter.Ipv6Servers.Any(s => string.Equals(s.Trim(), filterAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            if (adapter.Ipv4Servers.Any(IsLoopbackOrNull) || adapter.Ipv6Servers.Any(IsLoopbackOrNull))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Headless restore helper used by --restore-dns.</summary>
    public static void RestoreDnsHeadless(WebFilterSettings? settings = null)
    {
        var cfg = settings ?? new WebFilterSettings();
        var configurator = new DnsConfigurator();
        configurator.RestoreOriginalDns(cfg);

        try
        {
            var fw = new WebFilterFirewallService();
            fw.RemoveRulesAsync(cfg).GetAwaiter().GetResult();
            var bp = new BrowserPolicyService();
            bp.RemoveBrowserPolicies(cfg);
        }
        catch { }
    }
}
