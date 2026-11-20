namespace AppLock.WebFilter.Engine;

/// <summary>Decision layers, lowest number wins when two rules are equally specific.</summary>
public enum MatchLayer
{
    NeverBlock = 0,
    TempAllow = 1,
    AlwaysAllow = 2,
    AlwaysBlock = 3,
    Service = 4,
    Category = 5
}

public sealed record TempAllowEntry(string Domain, DateTime ExpiresUtc);

public sealed record CategoryListSource(string Name, ListSnapshot Snapshot);

public sealed class CategoryRuleSet
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The compiled lists that make up this category. May be empty when nothing downloaded.</summary>
    public IReadOnlyList<CategoryListSource> Lists { get; init; } = Array.Empty<CategoryListSource>();

    /// <summary>Empty means "active all day"; otherwise the category only filters inside these windows.</summary>
    public IReadOnlyList<TimeWindow> ActiveWindows { get; init; } = Array.Empty<TimeWindow>();
}

public sealed class ServiceRuleSet
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? CategoryDisplayName { get; init; }

    public required SuffixSet Domains { get; init; }

    public IReadOnlyList<TimeWindow> ActiveWindows { get; init; } = Array.Empty<TimeWindow>();
}

/// <summary>Everything the matcher needs, built from the saved settings and the compiled lists.</summary>
public sealed class DomainRuleSet
{
    public bool Enabled { get; init; } = true;

    public bool Paused { get; init; }

    public SuffixSet NeverBlock { get; init; } = new(Array.Empty<string>());

    public IReadOnlyList<TempAllowEntry> TempAllows { get; init; } = Array.Empty<TempAllowEntry>();

    public SuffixSet AlwaysAllow { get; init; } = new(Array.Empty<string>());

    public SuffixSet AlwaysBlock { get; init; } = new(Array.Empty<string>());

    public IReadOnlyList<CategoryRuleSet> Categories { get; init; } = Array.Empty<CategoryRuleSet>();

    public IReadOnlyList<ServiceRuleSet> Services { get; init; } = Array.Empty<ServiceRuleSet>();
}

public readonly record struct DomainMatch(
    bool IsBlocked,
    string? Reason,
    string? CategoryId,
    string? ServiceId,
    string? MatchedDomain,
    string? SourceName,
    MatchLayer Layer)
{
    public static DomainMatch Allow(string? reason = null, MatchLayer layer = MatchLayer.NeverBlock)
        => new(false, reason, null, null, null, null, layer);
}

/// <summary>
/// Implements the precedence from the spec: never-block, temporary allow, always allow, always
/// block, services and categories. The most specific matching entry wins, with the layer order
/// breaking ties, so <c>www.example.com</c> may be allowed while <c>ads.example.com</c> stays
/// blocked underneath an allowed <c>example.com</c>.
/// </summary>
public sealed class DomainMatcher : IDnsFilterPolicy
{
    private readonly DomainRuleSet _rules;
    private readonly Func<DateTime> _localClock;
    private readonly Func<DateTime> _utcClock;

    public DomainMatcher(DomainRuleSet rules, Func<DateTime>? localClock = null, Func<DateTime>? utcClock = null)
    {
        _rules = rules;
        _localClock = localClock ?? (() => DateTime.Now);
        _utcClock = utcClock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The best decision for a DNS query name.</summary>
    public DomainMatch Match(string? domain)
    {
        if (!_rules.Enabled)
        {
            return DomainMatch.Allow("Web Filter is off");
        }

        if (_rules.Paused)
        {
            return DomainMatch.Allow("Web Filter is paused");
        }

        if (!DomainName.TryNormalize(domain, out var name))
        {
            return DomainMatch.Allow();
        }

        // 1. The never-block list overrides everything, including user rules. A filtering mistake
        //    must never break Windows connectivity or lock the user out of the internet.
        if (_rules.NeverBlock.TryMatch(name, out _))
        {
            return DomainMatch.Allow("Never block list");
        }

        DomainMatch? best = null;
        int bestLabels = -1;
        int bestLength = -1;

        // 2. Temporary allows that have not expired.
        var utcNow = _utcClock();
        foreach (var allow in _rules.TempAllows)
        {
            if (allow.ExpiresUtc <= utcNow) continue;
            if (!DomainName.TryNormalize(allow.Domain, out var allowDomain)) continue;

            if (Matches(name, allowDomain))
            {
                Consider(
                    new DomainMatch(false, "Temporary allow", null, null, allowDomain, null, MatchLayer.TempAllow),
                    allowDomain,
                    ref best,
                    ref bestLabels,
                    ref bestLength);
            }
        }

        // 3. Always allow.
        if (_rules.AlwaysAllow.TryMatch(name, out var allowed))
        {
            Consider(
                new DomainMatch(false, "Always allow list", null, null, allowed, null, MatchLayer.AlwaysAllow),
                allowed,
                ref best,
                ref bestLabels,
                ref bestLength);
        }

        // 4. Always block.
        if (_rules.AlwaysBlock.TryMatch(name, out var blocked))
        {
            Consider(
                new DomainMatch(true, "Always block list", null, null, blocked, null, MatchLayer.AlwaysBlock),
                blocked,
                ref best,
                ref bestLabels,
                ref bestLength);
        }

        var localNow = _localClock();

        // 5a. Service blocks, only while enabled and inside the service's schedule.
        foreach (var service in _rules.Services)
        {
            if (!ScheduleWindows.IsActive(service.ActiveWindows, localNow)) continue;

            if (service.Domains.TryMatch(name, out var matched))
            {
                string reason = string.IsNullOrEmpty(service.CategoryDisplayName)
                    ? service.DisplayName
                    : $"{service.CategoryDisplayName} \u203a {service.DisplayName}";

                Consider(
                    new DomainMatch(true, reason, null, service.Id, matched, service.DisplayName, MatchLayer.Service),
                    matched,
                    ref best,
                    ref bestLabels,
                    ref bestLength);
            }
        }

        // 5b. Category lists, only while enabled and inside the category's schedule.
        foreach (var category in _rules.Categories)
        {
            if (!ScheduleWindows.IsActive(category.ActiveWindows, localNow)) continue;

            for (int i = 0; i < category.Lists.Count; i++)
            {
                var list = category.Lists[i];

                if (list.Snapshot.TryMatch(name, out var matched))
                {
                    Consider(
                        new DomainMatch(true, category.DisplayName, category.Id, null, matched, list.Name, MatchLayer.Category),
                        matched,
                        ref best,
                        ref bestLabels,
                        ref bestLength);
                }
            }
        }

        // 6. Nothing matched.
        return best ?? DomainMatch.Allow();
    }

    public FilterVerdict Check(in DnsQuery query)
    {
        var match = Match(query.Name);
        return match.IsBlocked
            ? FilterVerdict.Block(match.Reason ?? "Blocked")
            : FilterVerdict.Allow;
    }

    private static bool Matches(string name, string entry)
        => name.Equals(entry, StringComparison.Ordinal) ||
           (name.Length > entry.Length && name.EndsWith("." + entry, StringComparison.Ordinal));

    private static void Consider(
        DomainMatch candidate,
        string matched,
        ref DomainMatch? best,
        ref int bestLabels,
        ref int bestLength)
    {
        int labels = DomainName.LabelCount(matched);
        int length = matched.Length;

        bool moreSpecific = labels > bestLabels || (labels == bestLabels && length > bestLength);
        bool sameSpecificityButHigherPriority = labels == bestLabels && length == bestLength &&
                                                (best is null || candidate.Layer < best.Value.Layer);

        if (moreSpecific || sameSpecificityButHigherPriority)
        {
            best = candidate;
            bestLabels = labels;
            bestLength = length;
        }
    }
}
