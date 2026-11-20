using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

public class DomainNameTests
{
    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData(" example.com. ", "example.com")]
    [InlineData("sub.example.co.uk", "sub.example.co.uk")]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]
    public void NormalizesDomains(string input, string expected)
    {
        Assert.True(DomainName.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ConvertsIdnToPunycode()
    {
        Assert.True(DomainName.TryNormalize("b\u00fccher.example", out var normalized));
        Assert.Equal("xn--bcher-kva.example", normalized);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    [InlineData("example")]
    [InlineData("example..com")]
    [InlineData("-bad.example")]
    [InlineData("bad-.example")]
    [InlineData("exa mple.com")]
    [InlineData("example.com/path")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsInvalidDomains(string input)
    {
        Assert.False(DomainName.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData("https://www.example.com/page?q=1", "www.example.com")]
    [InlineData("http://user:pass@example.com:8080/x", "example.com")]
    [InlineData("example.com/path", "example.com")]
    [InlineData("  EXAMPLE.com.  ", "example.com")]
    public void ExtractsTheHost(string input, string expected)
    {
        Assert.True(DomainName.TryExtractHost(input, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("https://1.2.3.4/x")]
    [InlineData("https://[::1]/")]
    [InlineData("localhost")]
    [InlineData("https://")]
    [InlineData("not a url")]
    public void DoesNotExtractHostsFromIpsOrJunk(string input)
    {
        Assert.False(DomainName.TryExtractHost(input, out _));
    }

    [Fact]
    public void EnumeratesSuffixesMostSpecificFirst()
    {
        Assert.Equal(
            new[] { "a.b.example.com", "b.example.com", "example.com", "com" },
            DomainName.EnumerateSuffixes("a.b.example.com").ToArray());
    }

    [Theory]
    [InlineData("example.com", 2)]
    [InlineData("a.b.example.com", 4)]
    [InlineData("com", 1)]
    public void CountsLabels(string name, int expected)
    {
        Assert.Equal(expected, DomainName.LabelCount(name));
    }
}
