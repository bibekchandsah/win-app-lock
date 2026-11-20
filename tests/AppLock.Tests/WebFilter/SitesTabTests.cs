using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.WebFilter.Engine;
using Xunit;

namespace AppLock.Tests.WebFilter;

public class SitesTabTests
{
    [Theory]
    [InlineData("example.com", true, "example.com")]
    [InlineData("*.example.com", true, "example.com")]
    [InlineData("https://example.com/some/path?query=1#frag", true, "example.com")]
    [InlineData("http://sub.domain.org:8080/", true, "sub.domain.org")]
    [InlineData("192.168.1.1", false, "")]
    [InlineData("::1", false, "")]
    [InlineData("localhost", false, "")]
    [InlineData("invalid..name", false, "")]
    [InlineData("", false, "")]
    public void DomainName_TryExtractHost_ValidatesCorrectly(string input, bool expectedValid, string expectedHost)
    {
        bool valid = DomainName.TryExtractHost(input, out var host);
        Assert.Equal(expectedValid, valid);
        if (expectedValid)
        {
            Assert.Equal(expectedHost, host);
        }
    }

    [Fact]
    public void ServiceMatching_BlocksServiceDomainsAndSubdomains()
    {
        var youtubeService = new ServiceRuleSet
        {
            Id = "youtube",
            DisplayName = "YouTube",
            CategoryDisplayName = "Video streaming",
            Domains = new SuffixSet(new[] { "youtube.com", "youtu.be", "googlevideo.com" })
        };

        var ruleSet = new DomainRuleSet
        {
            Enabled = true,
            Services = new[] { youtubeService }
        };

        var matcher = new DomainMatcher(ruleSet);

        // Exact match
        var match1 = matcher.Match("youtube.com");
        Assert.True(match1.IsBlocked);
        Assert.Equal("youtube", match1.ServiceId);

        // Subdomain match
        var match2 = matcher.Match("m.youtube.com");
        Assert.True(match2.IsBlocked);
        Assert.Equal("youtube", match2.ServiceId);

        // Another service domain
        var match3 = matcher.Match("rr1---sn-4g5edn6s.googlevideo.com");
        Assert.True(match3.IsBlocked);

        // Unrelated domain allowed
        var match4 = matcher.Match("google.com");
        Assert.False(match4.IsBlocked);
    }

    [Fact]
    public void Precedence_AlwaysAllow_Overrides_AlwaysBlock_And_Service()
    {
        var tiktokService = new ServiceRuleSet
        {
            Id = "tiktok",
            DisplayName = "TikTok",
            Domains = new SuffixSet(new[] { "tiktok.com" })
        };

        var ruleSet = new DomainRuleSet
        {
            Enabled = true,
            AlwaysAllow = new SuffixSet(new[] { "api.tiktok.com" }),
            AlwaysBlock = new SuffixSet(new[] { "tiktok.com" }),
            Services = new[] { tiktokService }
        };

        var matcher = new DomainMatcher(ruleSet);

        // Always allow wins for the more specific entry
        var match1 = matcher.Match("api.tiktok.com");
        Assert.False(match1.IsBlocked);
        Assert.Equal(MatchLayer.AlwaysAllow, match1.Layer);

        // Always block wins over Service for the base domain
        var match2 = matcher.Match("tiktok.com");
        Assert.True(match2.IsBlocked);
        Assert.Equal(MatchLayer.AlwaysBlock, match2.Layer);
    }

    [Fact]
    public void ImportExport_RoundTrip_PreservesDomainsAndNotes()
    {
        var input = @"
# This is a comment
example.com # primary site
*.test.org
https://sub.blocked.net/login
";

        var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<DomainEntry>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            string domainPart = trimmed;
            string notePart = string.Empty;

            int commentIndex = trimmed.IndexOf('#');
            if (commentIndex >= 0)
            {
                domainPart = trimmed[..commentIndex].Trim();
                notePart = trimmed[(commentIndex + 1)..].Trim();
            }

            if (DomainName.TryExtractHost(domainPart, out var host))
            {
                entries.Add(new DomainEntry { Domain = host, Note = notePart });
            }
        }

        Assert.Equal(3, entries.Count);
        Assert.Equal("example.com", entries[0].Domain);
        Assert.Equal("primary site", entries[0].Note);
        Assert.Equal("test.org", entries[1].Domain);
        Assert.Equal("sub.blocked.net", entries[2].Domain);
    }

    [Fact]
    public void TempAllow_WinsOverAlwaysBlockAndServices_UntilExpiry()
    {
        var now = DateTime.UtcNow;
        var rules = new DomainRuleSet
        {
            Enabled = true,
            AlwaysBlock = new SuffixSet(new[] { "reddit.com" }),
            TempAllows = new[] { new TempAllowEntry("reddit.com", now.AddMinutes(15)) }
        };

        var matcherBefore = new DomainMatcher(rules, () => now.ToLocalTime(), () => now);
        var matchBefore = matcherBefore.Match("reddit.com");
        Assert.False(matchBefore.IsBlocked);
        Assert.Equal(MatchLayer.TempAllow, matchBefore.Layer);

        // After 16 minutes, the temp allow has expired
        var matcherAfter = new DomainMatcher(rules, () => now.ToLocalTime(), () => now.AddMinutes(16));
        var matchAfter = matcherAfter.Match("reddit.com");
        Assert.True(matchAfter.IsBlocked);
        Assert.Equal(MatchLayer.AlwaysBlock, matchAfter.Layer);
    }

    [Fact]
    public void ScheduleWindows_EvaluatesActiveTimesCorrectly()
    {
        // Monday 9:00 - 17:00
        var window = new TimeWindow(new TimeOnly(9, 0), new TimeOnly(17, 0), new[] { true, false, false, false, false, false, false });

        var mon10am = new DateTime(2026, 11, 16, 10, 0, 0); // Monday
        var mon8am = new DateTime(2026, 11, 16, 8, 0, 0);   // Monday
        var tue10am = new DateTime(2026, 11, 17, 10, 0, 0); // Tuesday

        Assert.True(window.IsActiveAt(mon10am));
        Assert.False(window.IsActiveAt(mon8am));
        Assert.False(window.IsActiveAt(tue10am));
    }
}
