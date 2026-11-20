using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

public class DomainMatcherTests
{
    private static readonly DateTime MondayMorning = new(2026, 11, 16, 10, 0, 0);
    private static readonly DateTime MondayNight = new(2026, 11, 16, 23, 0, 0);
    private static readonly DateTime TuesdayEarly = new(2026, 11, 17, 1, 0, 0);
    private static readonly DateTime TuesdayLate = new(2026, 11, 17, 3, 0, 0);

    [Fact]
    public void NeverBlockOverridesEveryOtherList()
    {
        var matcher = Build(new DomainRuleSet
        {
            NeverBlock = new SuffixSet(new[] { "msftconnecttest.com" }),
            AlwaysBlock = new SuffixSet(new[] { "www.msftconnecttest.com" })
        });

        var match = matcher.Match("www.msftconnecttest.com");

        Assert.False(match.IsBlocked);
        Assert.Equal("Never block list", match.Reason);
    }

    [Fact]
    public void AMoreSpecificAllowOverridesAParentBlock()
    {
        var matcher = Build(new DomainRuleSet
        {
            AlwaysBlock = new SuffixSet(new[] { "example.com" }),
            AlwaysAllow = new SuffixSet(new[] { "www.example.com" })
        });

        Assert.False(matcher.Match("www.example.com").IsBlocked);
        Assert.True(matcher.Match("other.example.com").IsBlocked);
    }

    [Fact]
    public void AMoreSpecificBlockStillAppliesUnderAnAllowedParent()
    {
        var matcher = Build(new DomainRuleSet
        {
            AlwaysAllow = new SuffixSet(new[] { "example.com" }),
            AlwaysBlock = new SuffixSet(new[] { "ads.example.com" })
        });

        Assert.False(matcher.Match("www.example.com").IsBlocked);

        var ads = matcher.Match("tracker.ads.example.com");
        Assert.True(ads.IsBlocked);
        Assert.Equal("Always block list", ads.Reason);
    }

    [Fact]
    public void TemporaryAllowWinsOverAlwaysBlockUntilItExpires()
    {
        DateTime utcNow = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        var rules = new DomainRuleSet
        {
            AlwaysBlock = new SuffixSet(new[] { "example.com" }),
            TempAllows = new[] { new TempAllowEntry("example.com", utcNow.AddMinutes(15)) }
        };

        var before = new DomainMatcher(rules, () => utcNow.ToLocalTime(), () => utcNow);
        Assert.False(before.Match("example.com").IsBlocked);

        var after = new DomainMatcher(rules, () => utcNow.ToLocalTime(), () => utcNow.AddMinutes(16));
        Assert.True(after.Match("example.com").IsBlocked);
    }

    [Fact]
    public void CategoryListsOnlyApplyWhenTheCategoryIsPresent()
    {
        using var snapshot = ListSnapshot.FromDomains(new[] { "games.example" }, seed: 3);

        var withoutCategory = Build(new DomainRuleSet());
        Assert.False(withoutCategory.Match("games.example").IsBlocked);

        var withCategory = Build(new DomainRuleSet
        {
            Categories = new[]
            {
                new CategoryRuleSet
                {
                    Id = "games",
                    DisplayName = "Games",
                    Lists = new[] { new CategoryListSource("Curated games", snapshot) }
                }
            }
        });

        var match = withCategory.Match("www.games.example");
        Assert.True(match.IsBlocked);
        Assert.Equal("Games", match.Reason);
        Assert.Equal("Curated games", match.SourceName);
        Assert.Equal("games", match.CategoryId);
    }

    [Fact]
    public void CategoryScheduleOnlyBlocksInsideTheWindow()
    {
        using var snapshot = ListSnapshot.FromDomains(new[] { "games.example" }, seed: 3);

        var category = new CategoryRuleSet
        {
            Id = "games",
            DisplayName = "Games",
            Lists = new[] { new CategoryListSource("Curated games", snapshot) },
            ActiveWindows = new[]
            {
                new TimeWindow(new TimeOnly(9, 0), new TimeOnly(17, 0), Weekdays())
            }
        };

        var rules = new DomainRuleSet { Categories = new[] { category } };

        Assert.True(BuildAt(rules, MondayMorning).Match("games.example").IsBlocked);
        Assert.False(BuildAt(rules, MondayMorning.AddHours(-2)).Match("games.example").IsBlocked);
        Assert.False(BuildAt(rules, new DateTime(2026, 11, 21, 10, 0, 0)).Match("games.example").IsBlocked); // Saturday
    }

    [Fact]
    public void CategoryScheduleHandlesWindowsThatCrossMidnight()
    {
        using var snapshot = ListSnapshot.FromDomains(new[] { "games.example" }, seed: 3);

        var category = new CategoryRuleSet
        {
            Id = "games",
            DisplayName = "Games",
            Lists = new[] { new CategoryListSource("Curated games", snapshot) },
            ActiveWindows = new[]
            {
                new TimeWindow(new TimeOnly(22, 0), new TimeOnly(2, 0), new[] { true, false, false, false, false, false, false })
            }
        };

        var rules = new DomainRuleSet { Categories = new[] { category } };

        Assert.True(BuildAt(rules, MondayNight).Match("games.example").IsBlocked);
        Assert.True(BuildAt(rules, TuesdayEarly).Match("games.example").IsBlocked);
        Assert.False(BuildAt(rules, TuesdayLate).Match("games.example").IsBlocked);
        Assert.False(BuildAt(rules, new DateTime(2026, 11, 17, 23, 0, 0)).Match("games.example").IsBlocked); // Tuesday not selected
    }

    [Fact]
    public void ScheduleWindowsSurviveADaylightSavingChange()
    {
        using var snapshot = ListSnapshot.FromDomains(new[] { "games.example" }, seed: 3);

        var category = new CategoryRuleSet
        {
            Id = "games",
            DisplayName = "Games",
            Lists = new[] { new CategoryListSource("Curated games", snapshot) },
            ActiveWindows = new[]
            {
                new TimeWindow(new TimeOnly(1, 30), new TimeOnly(3, 30), AllDays())
            }
        };

        var rules = new DomainRuleSet { Categories = new[] { category } };

        // 29 March 2026 is a European DST switch day; local wall-clock comparisons stay correct.
        Assert.True(BuildAt(rules, new DateTime(2026, 3, 29, 3, 0, 0)).Match("games.example").IsBlocked);
        Assert.False(BuildAt(rules, new DateTime(2026, 3, 29, 1, 0, 0)).Match("games.example").IsBlocked);
    }

    [Fact]
    public void ServiceReasonBeatsCategoryAtTheSameSpecificity()
    {
        using var categorySnapshot = ListSnapshot.FromDomains(new[] { "tiktok.com" }, seed: 4);

        var rules = new DomainRuleSet
        {
            Categories = new[]
            {
                new CategoryRuleSet
                {
                    Id = "social",
                    DisplayName = "Social media",
                    Lists = new[] { new CategoryListSource("StevenBlack social", categorySnapshot) }
                }
            },
            Services = new[]
            {
                new ServiceRuleSet
                {
                    Id = "tiktok",
                    DisplayName = "TikTok",
                    CategoryDisplayName = "Social media",
                    Domains = new SuffixSet(new[] { "tiktok.com" })
                }
            }
        };

        var match = Build(rules).Match("tiktok.com");

        Assert.True(match.IsBlocked);
        Assert.Equal("Social media \u203a TikTok", match.Reason);
        Assert.Equal("tiktok", match.ServiceId);
    }

    [Fact]
    public void PausedAndDisabledLetEverythingThrough()
    {
        var rules = new DomainRuleSet
        {
            AlwaysBlock = new SuffixSet(new[] { "example.com" }),
            Paused = true
        };

        Assert.False(Build(rules).Match("example.com").IsBlocked);

        var disabled = new DomainRuleSet
        {
            AlwaysBlock = new SuffixSet(new[] { "example.com" }),
            Enabled = false
        };

        Assert.False(Build(disabled).Match("example.com").IsBlocked);
    }

    [Fact]
    public void InvalidAndUnlistedNamesAreAllowed()
    {
        var matcher = Build(new DomainRuleSet { AlwaysBlock = new SuffixSet(new[] { "example.com" }) });

        Assert.False(matcher.Match("127.0.0.1").IsBlocked);
        Assert.False(matcher.Match("localhost").IsBlocked);
        Assert.False(matcher.Match("unrelated.test").IsBlocked);
        Assert.False(matcher.Match("").IsBlocked);
    }

    [Fact]
    public void CheckReturnsAVerdictForTheDnsServer()
    {
        var matcher = Build(new DomainRuleSet { AlwaysBlock = new SuffixSet(new[] { "example.com" }) });

        var request = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var verdict = matcher.Check(query);

        Assert.True(verdict.IsBlocked);
        Assert.Equal("Always block list", verdict.Reason);
    }

    private static DomainMatcher Build(DomainRuleSet rules)
        => new(rules, () => MondayMorning, () => new DateTime(2026, 11, 16, 4, 15, 0, DateTimeKind.Utc));

    private static DomainMatcher BuildAt(DomainRuleSet rules, DateTime localNow)
        => new(rules, () => localNow, () => new DateTime(2026, 11, 16, 4, 15, 0, DateTimeKind.Utc));

    private static bool[] AllDays() => new[] { true, true, true, true, true, true, true };

    private static bool[] Weekdays() => new[] { true, true, true, true, true, false, false };
}
