namespace AppLock.WebFilter.Engine;

/// <summary>
/// A small list of domains matched by suffix (a rule for <c>example.com</c> also matches
/// <c>a.example.com</c>). Used for the always block/allow lists, the never-block list and the
/// curated service lists, where the matched entry itself matters for "most specific wins".
/// </summary>
public sealed class SuffixSet
{
    private readonly HashSet<string> _domains;

    public SuffixSet(IEnumerable<string>? domains)
    {
        _domains = new HashSet<string>(StringComparer.Ordinal);

        if (domains == null) return;

        foreach (var domain in domains)
        {
            if (DomainName.TryNormalize(domain, out var normalized))
            {
                _domains.Add(normalized);
            }
        }
    }

    public int Count => _domains.Count;

    public bool IsEmpty => _domains.Count == 0;

    public bool Contains(string normalizedDomain) => _domains.Contains(normalizedDomain);

    /// <summary>Finds the most specific entry that matches <paramref name="normalizedName"/>.</summary>
    public bool TryMatch(string normalizedName, out string matched)
    {
        matched = string.Empty;

        foreach (var suffix in DomainName.EnumerateSuffixes(normalizedName))
        {
            if (_domains.Contains(suffix))
            {
                matched = suffix;
                return true;
            }
        }

        return false;
    }

    public IReadOnlyCollection<string> Domains => _domains;
}
