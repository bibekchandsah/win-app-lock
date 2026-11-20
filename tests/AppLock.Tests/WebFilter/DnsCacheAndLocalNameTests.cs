using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

public class DnsCacheTests
{
    [Fact]
    public void CachedAnswerGetsTheCurrentTransactionId()
    {
        var cache = new DnsCache();
        var request = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA, 0x0001);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var answer = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress);
        cache.Set(query.Name, query.Type, answer);

        Assert.True(cache.TryGet(query.Name, query.Type, 0x9999, out var cached));
        Assert.Equal(0x9999, DnsPacket.ReadU16(cached, 0));
        Assert.Equal(1, DnsPacket.ReadU16(cached, 6));
    }

    [Fact]
    public void MissesUnknownNamesAndOtherTypes()
    {
        var cache = new DnsCache();
        var request = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        cache.Set(query.Name, query.Type, DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NxDomain));

        Assert.False(cache.TryGet("other.example", DnsPacket.TypeA, 1, out _));
        Assert.False(cache.TryGet("example.com", DnsPacket.TypeAaaa, 1, out _));
    }

    [Fact]
    public void EvictsWhenFull()
    {
        var cache = new DnsCache(capacity: 16);

        for (int i = 0; i < 40; i++)
        {
            var request = DnsPacket.BuildQuery($"host{i}.example", DnsPacket.TypeA);
            Assert.True(DnsPacket.TryParseQuery(request, out var query));
            cache.Set(query.Name, query.Type, DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress));
        }

        Assert.Equal(16, cache.Count);
    }

    [Fact]
    public void ClearEmptiesTheCache()
    {
        var cache = new DnsCache();
        var request = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));
        cache.Set(query.Name, query.Type, DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NxDomain));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(query.Name, query.Type, 1, out _));
    }
}

public class LocalNameRulesTests
{
    [Theory]
    [InlineData("nas")]
    [InlineData("nas.local")]
    [InlineData("printer.lan")]
    [InlineData("router.home.arpa")]
    [InlineData("fileserver.corp")]
    [InlineData("intranet.internal")]
    [InlineData("1.0.0.10.in-addr.arpa")]
    [InlineData("5.1.168.192.in-addr.arpa")]
    [InlineData("5.10.20.172.in-addr.arpa")]
    [InlineData("x.ip6.arpa")]
    public void LocalNamesGoToTheOriginalDns(string name)
    {
        Assert.True(LocalNameRules.IsLocalName(name, null));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("www.google.com")]
    [InlineData("1.1.1.1.in-addr.arpa")]
    [InlineData("14.8.172.in-addr.arpa")]
    public void PublicNamesGoToDoh(string name)
    {
        Assert.False(LocalNameRules.IsLocalName(name, null));
    }

    [Fact]
    public void MachineDnsSuffixCountsAsLocal()
    {
        Assert.True(LocalNameRules.IsLocalName("server.contoso.com", "contoso.com"));
        Assert.True(LocalNameRules.IsLocalName("contoso.com", "contoso.com."));
        Assert.False(LocalNameRules.IsLocalName("contoso.com.evil.example", "contoso.com"));
    }
}
