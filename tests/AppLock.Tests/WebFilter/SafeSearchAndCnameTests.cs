using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AppLock.WebFilter.Engine;
using Xunit;

namespace AppLock.Tests.WebFilter;

public class SafeSearchAndCnameTests
{
    [Fact]
    public void SafeSearchRewriter_TargetsGoogleDomains_WhenEnabled()
    {
        var rewriter = new SafeSearchRewriter(
            enforceSafeSearch: true,
            youTubeMode: YouTubeRestrictionMode.Off,
            googleDomains: new[] { "google.com", "google.co.uk", "google.ca", "google.com.au" });

        Assert.True(rewriter.TryGetRewriteTarget("google.com", out var target1));
        Assert.Equal("forcesafesearch.google.com", target1);

        Assert.True(rewriter.TryGetRewriteTarget("www.google.com", out var target2));
        Assert.Equal("forcesafesearch.google.com", target2);

        Assert.True(rewriter.TryGetRewriteTarget("www.google.co.uk", out var target3));
        Assert.Equal("forcesafesearch.google.com", target3);

        Assert.True(rewriter.TryGetRewriteTarget("google.com.au", out var target4));
        Assert.Equal("forcesafesearch.google.com", target4);

        // Forcesafesearch itself should not be rewritten (avoid loop)
        Assert.False(rewriter.TryGetRewriteTarget("forcesafesearch.google.com", out _));

        // When SafeSearch is off, no rewrite
        rewriter.EnforceSafeSearch = false;
        Assert.False(rewriter.TryGetRewriteTarget("google.com", out _));
    }

    [Fact]
    public void SafeSearchRewriter_TargetsBingAndDuckDuckGo_WhenEnabled()
    {
        var rewriter = new SafeSearchRewriter(enforceSafeSearch: true);

        Assert.True(rewriter.TryGetRewriteTarget("bing.com", out var targetBing1));
        Assert.Equal("strict.bing.com", targetBing1);

        Assert.True(rewriter.TryGetRewriteTarget("www.bing.com", out var targetBing2));
        Assert.Equal("strict.bing.com", targetBing2);

        Assert.True(rewriter.TryGetRewriteTarget("duckduckgo.com", out var targetDdg1));
        Assert.Equal("safe.duckduckgo.com", targetDdg1);

        Assert.True(rewriter.TryGetRewriteTarget("www.duckduckgo.com", out var targetDdg2));
        Assert.Equal("safe.duckduckgo.com", targetDdg2);
    }

    [Fact]
    public void SafeSearchRewriter_YouTubeRestrictionModes()
    {
        var rewriter = new SafeSearchRewriter(enforceSafeSearch: false, youTubeMode: YouTubeRestrictionMode.Strict);

        Assert.True(rewriter.TryGetRewriteTarget("youtube.com", out var target1));
        Assert.Equal("restrict.youtube.com", target1);

        Assert.True(rewriter.TryGetRewriteTarget("www.youtube.com", out var target2));
        Assert.Equal("restrict.youtube.com", target2);

        Assert.True(rewriter.TryGetRewriteTarget("m.youtube.com", out var target3));
        Assert.Equal("restrict.youtube.com", target3);

        // Moderate mode
        rewriter.YouTubeMode = YouTubeRestrictionMode.Moderate;
        Assert.True(rewriter.TryGetRewriteTarget("youtube.com", out var targetMod));
        Assert.Equal("restrictmoderate.youtube.com", targetMod);

        // Off mode
        rewriter.YouTubeMode = YouTubeRestrictionMode.Off;
        Assert.False(rewriter.TryGetRewriteTarget("youtube.com", out _));
    }

    [Fact]
    public void DnsPacket_BuildResponseWithIps_CreatesValidWireFormat()
    {
        byte[] request = DnsPacket.BuildQuery("www.google.com", DnsPacket.TypeA, 0x1234);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var ips = new[] { IPAddress.Parse("216.239.38.120") };
        byte[] response = DnsPacket.BuildResponseWithIps(request, query, ips, 300);

        Assert.NotNull(response);
        Assert.True(response.Length > DnsPacket.HeaderLength);

        // Check header flags
        Assert.Equal(0x12, response[0]);
        Assert.Equal(0x34, response[1]);
        Assert.True((response[2] & 0x80) != 0); // QR = 1 (response)
        Assert.Equal(0, response[3] & 0x0F);   // RCODE = 0 (NoError)

        Assert.Equal(1, DnsPacket.ReadU16(response, 4)); // Questions = 1
        Assert.Equal(1, DnsPacket.ReadU16(response, 6)); // Answers = 1

        // Extract IP answers to verify wire parsing
        Assert.True(DnsPacket.ExtractIpAnswers(response, DnsPacket.TypeA, out var extractedIps, out uint ttl));
        Assert.Single(extractedIps);
        Assert.Equal(IPAddress.Parse("216.239.38.120"), extractedIps[0]);
        Assert.Equal(300u, ttl);
    }

    [Fact]
    public void DnsPacket_ExtractCnameTargets_ExtractsNamesFromWire()
    {
        // Build a mock response with a CNAME record:
        // Question: direct.example.com
        // Answer: CNAME cloaked.tracking.net
        byte[] request = DnsPacket.BuildQuery("direct.example.com", DnsPacket.TypeA, 0x5678);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        // Construct mock response bytes with CNAME answer
        var responseList = new List<byte>(request);
        // Set QR=1
        responseList[2] = 0x81;
        responseList[3] = 0x80;
        // Answer count = 1
        responseList[6] = 0x00;
        responseList[7] = 0x01;

        // Answer record:
        // Name pointer 0xC00C
        responseList.Add(0xC0);
        responseList.Add(0x0C);
        // Type = 5 (CNAME)
        responseList.Add(0x00);
        responseList.Add(0x05);
        // Class = 1 (IN)
        responseList.Add(0x00);
        responseList.Add(0x01);
        // TTL = 60
        responseList.Add(0x00);
        responseList.Add(0x00);
        responseList.Add(0x00);
        responseList.Add(0x3C);

        // Target: cloaked.tracking.net
        var targetBytes = new List<byte>();
        // 7 cloaked 8 tracking 3 net 0
        targetBytes.Add(7);
        targetBytes.AddRange(System.Text.Encoding.ASCII.GetBytes("cloaked"));
        targetBytes.Add(8);
        targetBytes.AddRange(System.Text.Encoding.ASCII.GetBytes("tracking"));
        targetBytes.Add(3);
        targetBytes.AddRange(System.Text.Encoding.ASCII.GetBytes("net"));
        targetBytes.Add(0);

        // RDLength
        responseList.Add((byte)(targetBytes.Count >> 8));
        responseList.Add((byte)(targetBytes.Count & 0xFF));
        responseList.AddRange(targetBytes);

        var cnameTargets = DnsPacket.ExtractCnameTargets(responseList.ToArray());
        Assert.Single(cnameTargets);
        Assert.Equal("cloaked.tracking.net", cnameTargets[0]);
    }

    [Fact]
    public async Task SafeSearchRewriter_RewriteQueryAsync_UsesFallbackWhenUpstreamFails()
    {
        var rewriter = new SafeSearchRewriter(enforceSafeSearch: true);
        var mockFailingUpstream = new FailingUpstreamResolver();

        byte[] request = DnsPacket.BuildQuery("duckduckgo.com", DnsPacket.TypeA, 0x1111);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = await rewriter.RewriteQueryAsync(
            mockFailingUpstream, request, query, "safe.duckduckgo.com", CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(DnsPacket.ExtractIpAnswers(response, DnsPacket.TypeA, out var ips, out _));
        Assert.Single(ips);
        Assert.Equal(IPAddress.Parse("52.142.124.215"), ips[0]);
    }

    [Fact]
    public async Task DnsFilterServer_CnameCloaking_DetectsAndBlocksTarget()
    {
        // Policy blocks "cloaked-ad.bad.com"
        var policy = new MockCnamePolicy(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cloaked-ad.bad.com"
        });

        // Upstream returns a CNAME answer pointing to "cloaked-ad.bad.com"
        var upstream = new CnameMockUpstreamResolver("innocent.example.com", "cloaked-ad.bad.com");

        var options = new DnsServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0, // not bound for direct call
            BlockedAnswer = BlockedAnswer.NxDomain,
            CacheEnabled = false
        };

        await using var server = new DnsFilterServer(options, upstream, policy);

        // Verify that queries to innocent.example.com return NXDOMAIN due to CNAME cloaking
        byte[] request = DnsPacket.BuildQuery("innocent.example.com", DnsPacket.TypeA, 0x4321);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        string? blockedReason = null;
        server.QueryBlocked += (s, e) => { blockedReason = e.Reason; };

        // We can test via server ResolveAsync or simulate the flow
        // Since ResolveAsync is private, we can test StartAsync on a free port or test policy + CNAME logic
        // But let's verify via StartAsync on an ephemeral UDP port:
        var epOptions = new DnsServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 53549,
            BlockedAnswer = BlockedAnswer.NxDomain,
            CacheEnabled = false
        };

        await using var liveServer = new DnsFilterServer(epOptions, upstream, policy);
        bool started = await liveServer.StartAsync();
        if (started)
        {
            using var client = new System.Net.Sockets.UdpClient();
            var ep = new IPEndPoint(IPAddress.Loopback, epOptions.Port);
            await client.SendAsync(request, request.Length, ep);

            var resp = await client.ReceiveAsync();
            int rcode = resp.Buffer[3] & 0x0F;
            Assert.Equal(3, rcode); // NXDOMAIN
            Assert.True(liveServer.Stats.BlockedQueries > 0);
        }
    }

    private sealed class FailingUpstreamResolver : IUpstreamResolver
    {
        public Task<byte[]?> ResolveAsync(DnsQuery query, byte[] queryPacket, CancellationToken cancellationToken)
        {
            return Task.FromResult<byte[]?>(null);
        }

        public void Dispose() { }
    }

    private sealed class CnameMockUpstreamResolver : IUpstreamResolver
    {
        private readonly string _queryName;
        private readonly string _cnameTarget;

        public CnameMockUpstreamResolver(string queryName, string cnameTarget)
        {
            _queryName = queryName;
            _cnameTarget = cnameTarget;
        }

        public Task<byte[]?> ResolveAsync(DnsQuery query, byte[] queryPacket, CancellationToken cancellationToken)
        {
            // Build response with CNAME
            var resp = new List<byte>(queryPacket);
            resp[2] = 0x81;
            resp[3] = 0x80;
            resp[6] = 0x00;
            resp[7] = 0x01; // 1 answer

            // Name: 0xC00C
            resp.Add(0xC0);
            resp.Add(0x0C);
            // Type CNAME = 5
            resp.Add(0x00);
            resp.Add(0x05);
            // Class IN = 1
            resp.Add(0x00);
            resp.Add(0x01);
            // TTL = 60
            resp.Add(0x00);
            resp.Add(0x00);
            resp.Add(0x00);
            resp.Add(0x3C);

            // Target
            var targetParts = _cnameTarget.Split('.');
            var targetBytes = new List<byte>();
            foreach (var part in targetParts)
            {
                targetBytes.Add((byte)part.Length);
                targetBytes.AddRange(System.Text.Encoding.ASCII.GetBytes(part));
            }
            targetBytes.Add(0);

            resp.Add((byte)(targetBytes.Count >> 8));
            resp.Add((byte)(targetBytes.Count & 0xFF));
            resp.AddRange(targetBytes);

            return Task.FromResult<byte[]?>(resp.ToArray());
        }

        public void Dispose() { }
    }

    private sealed class MockCnamePolicy : IDnsFilterPolicy
    {
        private readonly HashSet<string> _blocked;

        public MockCnamePolicy(HashSet<string> blocked)
        {
            _blocked = blocked;
        }

        public FilterVerdict Check(in DnsQuery query)
        {
            if (_blocked.Contains(query.Name))
            {
                return FilterVerdict.Block("Category: Ads & Trackers");
            }

            return FilterVerdict.Allow;
        }
    }
}
