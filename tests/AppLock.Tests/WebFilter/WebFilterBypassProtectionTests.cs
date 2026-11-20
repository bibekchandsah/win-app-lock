using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using AppLock.Models;
using AppLock.Services.WebFilter;
using Xunit;

namespace AppLock.Tests.WebFilter;

public class WebFilterBypassProtectionTests
{
    [Fact]
    public void ComputeIpv4ComplementRanges_WithNoExcluded_ExcludesOnlyLoopback()
    {
        string ranges = WebFilterFirewallService.ComputeIpv4ComplementRanges(null);

        // Expected complement of 127.0.0.0/8:
        // 0.0.0.0-126.255.255.255,128.0.0.0-255.255.255.255
        Assert.Contains("0.0.0.0-126.255.255.255", ranges);
        Assert.Contains("128.0.0.0-255.255.255.255", ranges);
        Assert.DoesNotContain("127.", ranges);
    }

    [Fact]
    public void ComputeIpv4ComplementRanges_WithUpstream_ExcludesUpstreamAndLoopback()
    {
        var upstream = new[] { IPAddress.Parse("192.168.1.1") };
        string ranges = WebFilterFirewallService.ComputeIpv4ComplementRanges(upstream);

        // Should exclude 127.0.0.0/8 and 192.168.1.1
        Assert.Contains("0.0.0.0-126.255.255.255", ranges);
        Assert.Contains("128.0.0.0-192.168.1.0", ranges);
        Assert.Contains("192.168.1.2-255.255.255.255", ranges);
        Assert.DoesNotContain("192.168.1.1-", ranges);
        Assert.DoesNotContain("-192.168.1.1", ranges);
    }

    [Fact]
    public void GetDoHIpsToBlock_ExcludesChosenUpstream()
    {
        // Create temp doh-servers.json
        string tempJson = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempJson, @"[
              { ""id"": ""cloudflare"", ""name"": ""Cloudflare"", ""ips"": [""1.1.1.1"", ""1.0.0.1""] },
              { ""id"": ""google"", ""name"": ""Google"", ""ips"": [""8.8.8.8"", ""8.8.4.4""] },
              { ""id"": ""quad9"", ""name"": ""Quad9"", ""ips"": [""9.9.9.9""] }
            ]");

            var settings = new WebFilterSettings
            {
                Upstream = UpstreamKind.CloudflareDoh
            };

            var ips = WebFilterFirewallService.GetDoHIpsToBlock(settings, tempJson);

            // Cloudflare IPs (1.1.1.1, 1.0.0.1) must NOT be blocked when Cloudflare is upstream
            Assert.DoesNotContain("1.1.1.1", ips);
            Assert.DoesNotContain("1.0.0.1", ips);

            // Google and Quad9 must be blocked
            Assert.Contains("8.8.8.8", ips);
            Assert.Contains("8.8.4.4", ips);
            Assert.Contains("9.9.9.9", ips);
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }

    [Fact]
    public void GetDoHIpsToBlock_WhenOriginalDns_BlocksAllDoHResolvers()
    {
        string tempJson = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempJson, @"[
              { ""id"": ""cloudflare"", ""name"": ""Cloudflare"", ""ips"": [""1.1.1.1""] },
              { ""id"": ""google"", ""name"": ""Google"", ""ips"": [""8.8.8.8""] }
            ]");

            var settings = new WebFilterSettings
            {
                Upstream = UpstreamKind.OriginalDns
            };

            var ips = WebFilterFirewallService.GetDoHIpsToBlock(settings, tempJson);

            // With OriginalDns, ALL DoH resolvers should be blocked for maximum bypass protection
            Assert.Contains("1.1.1.1", ips);
            Assert.Contains("8.8.8.8", ips);
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }

    [Fact]
    public void VpnAndProxyDetector_InstantiatesAndChecksStatusWithoutThrowing()
    {
        using var detector = new VpnAndProxyDetector();
        detector.CheckStatus();
        // Should execute and report a boolean state without crashing
        _ = detector.IsVpnOrProxyActive;
        _ = detector.StatusSummary;
    }

    [Fact]
    public void BrowserPolicyService_IsDomainJoined_ReturnsBool()
    {
        // Verification that P/Invoke does not throw or corrupt memory
        bool isDomainJoined = BrowserPolicyService.IsDomainJoined();
        Assert.True(isDomainJoined || !isDomainJoined);
    }
}
