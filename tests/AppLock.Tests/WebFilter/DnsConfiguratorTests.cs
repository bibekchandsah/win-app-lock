using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using AppLock.Models;
using AppLock.Services.WebFilter;
using Xunit;

namespace AppLock.Tests.WebFilter;

/// <summary>
/// Tests for DnsConfigurator: save/restore round-trips with a fake adapter service,
/// loopback guard (never save 127.x/::1 as original), and restore safety net.
/// </summary>
public class DnsConfiguratorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static FakeNetworkAdapterService MakeService(params NetworkAdapterInfo[] adapters)
        => new(adapters);

    private static NetworkAdapterInfo Adapter(
        string id, string name, bool hasGateway = true,
        bool ipv4Dhcp = true, string[]? ipv4 = null,
        bool ipv6Dhcp = true, string[]? ipv6 = null)
        => new()
        {
            Id = id,
            Name = name,
            Description = name,
            InterfaceType = System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211,
            IsUp = true,
            HasGateway = hasGateway,
            Ipv4FromDhcp = ipv4Dhcp,
            Ipv4Servers = ipv4?.ToList() ?? new List<string>(),
            Ipv6FromDhcp = ipv6Dhcp,
            Ipv6Servers = ipv6?.ToList() ?? new List<string>()
        };

    // ── IsLoopbackOrNull ────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.53", true)]
    [InlineData("127.0.53.53", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("1.1.1.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.127.255.73", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsLoopbackOrNull_DetectsLoopback(string? address, bool expected)
    {
        Assert.Equal(expected, DnsConfigurator.IsLoopbackOrNull(address));
    }

    // ── SaveOriginalDns ─────────────────────────────────────────────────────

    [Fact]
    public void SaveOriginalDns_CapturesDhcpAdapter()
    {
        var svc = MakeService(Adapter("{A}", "Wi-Fi", ipv4: new[] { "100.127.255.73" }));
        var conf = new DnsConfigurator(svc);
        var settings = new WebFilterSettings();

        bool ok = conf.SaveOriginalDns(settings);

        Assert.True(ok);
        Assert.Single(settings.OriginalDns.Adapters);
        var saved = settings.OriginalDns.Adapters[0];
        Assert.Equal("{A}", saved.AdapterId);
        Assert.True(saved.Ipv4FromDhcp);           // default for Adapter()
        Assert.Contains("100.127.255.73", saved.Ipv4Servers);
    }

    [Fact]
    public void SaveOriginalDns_NeverSavesLoopbackAsOriginal()
    {
        // Adapter already has a filter address (crash-restart scenario)
        var svc = MakeService(Adapter("{A}", "Wi-Fi",
            ipv4Dhcp: false, ipv4: new[] { "127.0.0.1" }));
        var conf = new DnsConfigurator(svc);
        var settings = new WebFilterSettings();

        conf.SaveOriginalDns(settings);

        // Loopback must not be persisted as the original
        var saved = settings.OriginalDns.Adapters[0];
        Assert.DoesNotContain("127.0.0.1", saved.Ipv4Servers);
        // And it must fall back to DHCP since there are no non-loopback servers
        Assert.True(saved.Ipv4FromDhcp);
    }

    [Fact]
    public void SaveOriginalDns_PreservesExistingEntry()
    {
        var svc = MakeService(Adapter("{A}", "Wi-Fi", ipv4: new[] { "1.1.1.1" }));
        var conf = new DnsConfigurator(svc);
        var settings = new WebFilterSettings();
        settings.OriginalDns.Adapters.Add(new SavedAdapterDns
        {
            AdapterId = "{A}",
            Description = "Wi-Fi",
            Ipv4FromDhcp = false,
            Ipv4Servers = new List<string> { "192.168.1.1" }
        });

        conf.SaveOriginalDns(settings);

        // Should still only have one entry (not duplicated)
        Assert.Single(settings.OriginalDns.Adapters);
        // The pre-existing entry is kept
        Assert.Contains("192.168.1.1", settings.OriginalDns.Adapters[0].Ipv4Servers);
    }

    [Fact]
    public void SaveOriginalDns_ScrubsLoopbackFromExistingEntry()
    {
        var svc = MakeService(Adapter("{A}", "Wi-Fi"));
        var conf = new DnsConfigurator(svc);
        var settings = new WebFilterSettings();
        // Simulate a poisoned saved entry (previous bug / crash during apply)
        settings.OriginalDns.Adapters.Add(new SavedAdapterDns
        {
            AdapterId = "{A}",
            Description = "Wi-Fi",
            Ipv4FromDhcp = false,
            Ipv4Servers = new List<string> { "127.0.0.1" }  // loopback - must be purged
        });

        conf.SaveOriginalDns(settings);

        var saved = settings.OriginalDns.Adapters[0];
        Assert.DoesNotContain("127.0.0.1", saved.Ipv4Servers);
        Assert.True(saved.Ipv4FromDhcp);
    }

    // ── ApplyFilterDns / RestoreOriginalDns round-trip ────────────────────

    [Fact]
    public void ApplyThenRestore_RestoresStaticDns()
    {
        var svc = MakeService(Adapter("{A}", "Wi-Fi",
            ipv4Dhcp: false, ipv4: new[] { "1.1.1.1", "8.8.8.8" }));
        var conf = new DnsConfigurator(svc);
        var settings = new WebFilterSettings();

        bool applied = conf.ApplyFilterDns(settings, "127.0.0.1", "::1");
        Assert.True(applied);

        // Adapter now reports loopback
        svc.OverrideAdapter("{A}", Adapter("{A}", "Wi-Fi",
            ipv4Dhcp: false, ipv4: new[] { "127.0.0.1" },
            ipv6Dhcp: false, ipv6: new[] { "::1" }));

        bool restored = conf.RestoreOriginalDns(settings);
        Assert.True(restored);

        var setCall = svc.SetCalls.Last();
        Assert.Equal("{A}", setCall.AdapterId);
        Assert.Contains("1.1.1.1", setCall.Ipv4Servers);
        Assert.Contains("8.8.8.8", setCall.Ipv4Servers);
        Assert.False(setCall.Ipv4Dhcp);
    }

    [Fact]
    public void ApplyThenRestore_RestoresDhcp()
    {
        var svc = MakeService(Adapter("{A}", "Wi-Fi",
            ipv4Dhcp: true, ipv4: new[] { "192.168.1.1" }));
        var conf = new DnsConfigurator(svc);
        var settings = new WebFilterSettings();

        conf.ApplyFilterDns(settings, "127.0.0.1");

        svc.OverrideAdapter("{A}", Adapter("{A}", "Wi-Fi",
            ipv4Dhcp: false, ipv4: new[] { "127.0.0.1" }));

        conf.RestoreOriginalDns(settings);

        var setCall = svc.SetCalls.Last();
        Assert.True(setCall.Ipv4Dhcp);
    }

    [Fact]
    public void Restore_SafetyNet_ResetsDhcpForUnmanagedLoopbackAdapter()
    {
        // A new adapter appeared since we saved — currently pointing to loopback
        var svc = MakeService(
            Adapter("{B}", "Ethernet",
                ipv4Dhcp: false, ipv4: new[] { "127.0.0.53" }));
        var conf = new DnsConfigurator(svc);
        var settings = new WebFilterSettings();
        // No saved entry for {B} — restore should still fix it
        settings.OriginalDns.Adapters.Clear();

        conf.RestoreOriginalDns(settings);

        // Safety net should have reset {B} to DHCP
        Assert.Contains(svc.SetCalls, c => c.AdapterId == "{B}" && c.Ipv4Dhcp);
    }

    [Fact]
    public void IsDnsPointingToFilter_DetectsLoopback()
    {
        var svc = MakeService(Adapter("{A}", "Wi-Fi",
            ipv4Dhcp: false, ipv4: new[] { "127.0.0.1" }));
        var conf = new DnsConfigurator(svc);

        Assert.True(conf.IsDnsPointingToFilter());
    }

    [Fact]
    public void IsDnsPointingToFilter_FalseForExternalDns()
    {
        var svc = MakeService(Adapter("{A}", "Wi-Fi",
            ipv4Dhcp: false, ipv4: new[] { "1.1.1.1" }));
        var conf = new DnsConfigurator(svc);

        Assert.False(conf.IsDnsPointingToFilter());
    }
}

// ── Fake adapter service ───────────────────────────────────────────────────

public sealed class FakeNetworkAdapterService : INetworkAdapterService
{
    public sealed record SetCall(
        string AdapterId, string AdapterName,
        IReadOnlyList<string> Ipv4Servers, IReadOnlyList<string> Ipv6Servers,
        bool Ipv4Dhcp, bool Ipv6Dhcp);

    private readonly List<NetworkAdapterInfo> _adapters;

    public FakeNetworkAdapterService(IEnumerable<NetworkAdapterInfo> adapters)
    {
        _adapters = adapters.ToList();
    }

    public List<SetCall> SetCalls { get; } = new();
    public int FlushCount { get; private set; }

    public void OverrideAdapter(string id, NetworkAdapterInfo updated)
    {
        int idx = _adapters.FindIndex(a => a.Id == id);
        if (idx >= 0) _adapters[idx] = updated;
        else _adapters.Add(updated);
    }

    public IReadOnlyList<NetworkAdapterInfo> GetEligibleAdapters(bool includeVpn = false)
        => _adapters.Where(a => a.IsUp && a.HasGateway).ToList();

    public NetworkAdapterInfo? GetAdapter(string adapterId)
        => _adapters.FirstOrDefault(a => a.Id == adapterId);

    public bool SetAdapterDns(
        string adapterId, string adapterName,
        IReadOnlyList<string> ipv4Servers, IReadOnlyList<string> ipv6Servers,
        bool ipv4Dhcp, bool ipv6Dhcp)
    {
        SetCalls.Add(new SetCall(adapterId, adapterName, ipv4Servers, ipv6Servers, ipv4Dhcp, ipv6Dhcp));
        return true;
    }

    public void FlushDnsCache() => FlushCount++;
}
