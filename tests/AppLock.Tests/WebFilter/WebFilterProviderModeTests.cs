using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AppLock.Models;
using AppLock.Services.WebFilter;
using Xunit;

namespace AppLock.Tests.WebFilter;

public class WebFilterProviderModeTests
{
    private static NetworkAdapterInfo MakeAdapter(
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

    [Fact]
    public void Catalog_HasExpectedDefaultProviders()
    {
        var providers = WebFilterCatalog.DefaultProviders;

        Assert.NotNull(providers);
        Assert.True(providers.Count >= 5);

        var cloudflare = providers.FirstOrDefault(p => p.Id == "cloudflare-family");
        Assert.NotNull(cloudflare);
        Assert.Contains("1.1.1.3", cloudflare.Ipv4);
        Assert.Contains("1.0.0.3", cloudflare.Ipv4);
        Assert.Contains("2606:4700:4700::1113", cloudflare.Ipv6);

        var cleanbrowsing = providers.FirstOrDefault(p => p.Id == "cleanbrowsing-family");
        Assert.NotNull(cleanbrowsing);
        Assert.Contains("185.228.168.168", cleanbrowsing.Ipv4);

        var opendns = providers.FirstOrDefault(p => p.Id == "opendns-familyshield");
        Assert.NotNull(opendns);
        Assert.Contains("208.67.222.123", opendns.Ipv4);

        var adguard = providers.FirstOrDefault(p => p.Id == "adguard-family");
        Assert.NotNull(adguard);
        Assert.Contains("94.140.14.15", adguard.Ipv4);
    }

    [Fact]
    public void Catalog_FindProvider_ReturnsMatchingOrFallback()
    {
        var catalog = WebFilterCatalog.Empty;

        var cloudflare = catalog.FindProvider("cloudflare-family");
        Assert.NotNull(cloudflare);
        Assert.Equal("Cloudflare Family", cloudflare.Name);

        // Unknown provider falls back to first provider safely
        var fallback = catalog.FindProvider("non-existent-provider");
        Assert.NotNull(fallback);
    }

    [Fact]
    public void DnsConfigurator_ApplyProviderDns_SetsAdapterIps()
    {
        var adapter = MakeAdapter("{WIFI}", "Wi-Fi",
            ipv4Dhcp: true, ipv4: new[] { "192.168.1.1" },
            ipv6Dhcp: true);
        var fakeService = new FakeNetworkAdapterService(new[] { adapter });
        var configurator = new DnsConfigurator(fakeService);

        var settings = new WebFilterSettings
        {
            Mode = WebFilterMode.Provider,
            ProviderId = "cloudflare-family"
        };

        var provider = WebFilterCatalog.DefaultProviders.First(p => p.Id == "cloudflare-family");

        bool applied = configurator.ApplyProviderDns(settings, provider.Ipv4, provider.Ipv6);

        Assert.True(applied);
        Assert.Single(fakeService.SetCalls);

        var call = fakeService.SetCalls[0];
        Assert.Equal("{WIFI}", call.AdapterId);
        Assert.Equal(provider.Ipv4, call.Ipv4Servers);
        Assert.Equal(provider.Ipv6, call.Ipv6Servers);
        Assert.False(call.Ipv4Dhcp);

        // Verify original DNS was preserved
        Assert.NotNull(settings.OriginalDns);
        Assert.Single(settings.OriginalDns.Adapters);
        Assert.Contains("192.168.1.1", settings.OriginalDns.Adapters[0].Ipv4Servers);
    }

    [Fact]
    public void DnsVerifier_DetectsProviderDrift_AndReapplies()
    {
        var provider = WebFilterCatalog.DefaultProviders.First(p => p.Id == "cloudflare-family");

        // Adapter drifted to 8.8.8.8
        var adapter = MakeAdapter("{WIFI}", "Wi-Fi",
            ipv4Dhcp: false, ipv4: new[] { "8.8.8.8" });
        var fakeService = new FakeNetworkAdapterService(new[] { adapter });
        var configurator = new DnsConfigurator(fakeService);

        var settings = new WebFilterSettings
        {
            Enabled = true,
            Mode = WebFilterMode.Provider,
            ProviderId = "cloudflare-family"
        };

        bool driftNotified = false;
        var verifier = new DnsVerifier(
            configurator,
            () => settings,
            () => provider.Ipv4[0],
            () => provider.Ipv4.Concat(provider.Ipv6).ToList());

        verifier.DnsDriftDetected += (s, drifted) =>
        {
            driftNotified = true;
            Assert.Contains("Wi-Fi", drifted);
        };

        verifier.Start();
        verifier.VerifyNow();

        Assert.True(driftNotified);
        Assert.Contains(fakeService.SetCalls, c => c.Ipv4Servers.SequenceEqual(provider.Ipv4));
    }

    [Fact]
    public void Settings_Serialization_PreservesModeAndProvider()
    {
        var settings = new WebFilterSettings
        {
            Enabled = true,
            Mode = WebFilterMode.Provider,
            ProviderId = "adguard-family",
            Upstream = UpstreamKind.GoogleDoh,
            BlockResponse = BlockResponse.NullAddress,
            CacheEnabled = false
        };

        string json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<WebFilterSettings>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Enabled);
        Assert.Equal(WebFilterMode.Provider, deserialized.Mode);
        Assert.Equal("adguard-family", deserialized.ProviderId);
        Assert.Equal(UpstreamKind.GoogleDoh, deserialized.Upstream);
        Assert.Equal(BlockResponse.NullAddress, deserialized.BlockResponse);
        Assert.False(deserialized.CacheEnabled);
    }
}
