using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.WebFilter.Engine;

public enum YouTubeRestrictionMode
{
    Off = 0,
    Moderate = 1,
    Strict = 2
}

/// <summary>
/// Rewrites search engine and YouTube queries to enforced SafeSearch VIPs
/// by answering with the target VIP's IP addresses under the original question name.
/// </summary>
public sealed class SafeSearchRewriter
{
    // Well-known fallback IPs for SafeSearch VIP endpoints (RFC / provider documented)
    private static readonly IReadOnlyList<IPAddress> GoogleSafeIpsV4 = new[] { IPAddress.Parse("216.239.38.120") };
    private static readonly IReadOnlyList<IPAddress> GoogleSafeIpsV6 = new[] { IPAddress.Parse("2001:4860:4806::78") };

    private static readonly IReadOnlyList<IPAddress> BingStrictIpsV4 = new[] { IPAddress.Parse("204.79.197.220") };
    private static readonly IReadOnlyList<IPAddress> BingStrictIpsV6 = new[] { IPAddress.Parse("2620:1ec:c11::200") };

    private static readonly IReadOnlyList<IPAddress> DuckDuckGoSafeIpsV4 = new[] { IPAddress.Parse("52.142.124.215") };

    private static readonly IReadOnlyList<IPAddress> YouTubeStrictIpsV4 = new[] { IPAddress.Parse("216.239.38.119") };
    private static readonly IReadOnlyList<IPAddress> YouTubeStrictIpsV6 = new[] { IPAddress.Parse("2001:4860:4806::77") };

    private static readonly IReadOnlyList<IPAddress> YouTubeModerateIpsV4 = new[] { IPAddress.Parse("216.239.38.120") };
    private static readonly IReadOnlyList<IPAddress> YouTubeModerateIpsV6 = new[] { IPAddress.Parse("2001:4860:4806::78") };

    private static readonly HashSet<string> YouTubeDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "youtubei.googleapis.com",
        "youtube.googleapis.com",
        "www.youtube-nocookie.com"
    };

    private static readonly HashSet<string> BingDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "bing.com",
        "www.bing.com"
    };

    private static readonly HashSet<string> DuckDuckGoDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "duckduckgo.com",
        "www.duckduckgo.com"
    };

    private readonly HashSet<string> _googleDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<IPAddress> Ips, DateTime ExpiresUtc)> _vipCache = new(StringComparer.OrdinalIgnoreCase);

    public bool EnforceSafeSearch { get; set; }
    public YouTubeRestrictionMode YouTubeMode { get; set; } = YouTubeRestrictionMode.Off;

    public SafeSearchRewriter(bool enforceSafeSearch = false, YouTubeRestrictionMode youTubeMode = YouTubeRestrictionMode.Off, IEnumerable<string>? googleDomains = null)
    {
        EnforceSafeSearch = enforceSafeSearch;
        YouTubeMode = youTubeMode;

        if (googleDomains != null)
        {
            foreach (var d in googleDomains)
            {
                if (!string.IsNullOrWhiteSpace(d))
                {
                    _googleDomains.Add(d.Trim().ToLowerInvariant());
                }
            }
        }

        // Always include default core Google domains
        _googleDomains.Add("google.com");
        _googleDomains.Add("google.co.uk");
        _googleDomains.Add("google.ca");
        _googleDomains.Add("google.de");
        _googleDomains.Add("google.fr");
        _googleDomains.Add("google.it");
        _googleDomains.Add("google.es");
        _googleDomains.Add("google.co.jp");
        _googleDomains.Add("google.com.au");
        _googleDomains.Add("google.co.in");
    }

    /// <summary>
    /// Checks whether the given hostname should be rewritten, and returns the target VIP hostname.
    /// </summary>
    public bool TryGetRewriteTarget(string host, out string targetHost)
    {
        targetHost = string.Empty;
        if (string.IsNullOrWhiteSpace(host)) return false;

        string normalized = host.Trim().ToLowerInvariant().TrimEnd('.');

        // 1. YouTube Restricted Mode
        if (YouTubeMode != YouTubeRestrictionMode.Off && YouTubeDomains.Contains(normalized))
        {
            targetHost = YouTubeMode == YouTubeRestrictionMode.Strict
                ? "restrict.youtube.com"
                : "restrictmoderate.youtube.com";
            return true;
        }

        // 2. SafeSearch (Google, Bing, DuckDuckGo)
        if (!EnforceSafeSearch) return false;

        if (BingDomains.Contains(normalized))
        {
            targetHost = "strict.bing.com";
            return true;
        }

        if (DuckDuckGoDomains.Contains(normalized))
        {
            targetHost = "safe.duckduckgo.com";
            return true;
        }

        if (IsGoogleDomain(normalized))
        {
            targetHost = "forcesafesearch.google.com";
            return true;
        }

        return false;
    }

    private bool IsGoogleDomain(string host)
    {
        if (host.Equals("forcesafesearch.google.com", StringComparison.OrdinalIgnoreCase)) return false;

        if (_googleDomains.Contains(host)) return true;

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            string root = host.Substring(4);
            if (_googleDomains.Contains(root)) return true;
        }

        // Pattern matching for google.<ccTLD> or google.com.<ccTLD>
        if (host.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] parts = host.Split('.');
        if (parts.Length >= 2)
        {
            if (parts[0].Equals("google", StringComparison.OrdinalIgnoreCase) ||
                (parts[0].Equals("www", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3 && parts[1].Equals("google", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites a query by resolving the target VIP and answering with the VIP's addresses under the query name.
    /// </summary>
    public async Task<byte[]?> RewriteQueryAsync(
        IUpstreamResolver upstream,
        byte[] originalRequest,
        DnsQuery query,
        string targetHost,
        CancellationToken cancellationToken)
    {
        bool isV6 = query.Type == DnsPacket.TypeAaaa;
        string cacheKey = $"{targetHost}:{(isV6 ? "AAAA" : "A")}";

        // 1. Check in-memory VIP cache
        if (_vipCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return DnsPacket.BuildResponseWithIps(originalRequest, query, cached.Ips, 300);
        }

        // 2. Try resolving target VIP upstream
        try
        {
            byte[] targetQueryBytes = DnsPacket.BuildQuery(targetHost, query.Type, (ushort)Random.Shared.Next(1, 65535));
            if (DnsPacket.TryParseQuery(targetQueryBytes, out var targetQuery))
            {
                var responseBytes = await upstream.ResolveAsync(targetQuery, targetQueryBytes, cancellationToken).ConfigureAwait(false);
                if (responseBytes != null && DnsPacket.ExtractIpAnswers(responseBytes, query.Type, out var resolvedIps, out uint ttl))
                {
                    _vipCache[cacheKey] = (resolvedIps, DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 60, 3600)));
                    return DnsPacket.BuildResponseWithIps(originalRequest, query, resolvedIps, ttl);
                }
            }
        }
        catch { }

        // 3. Fall back to well-known documented VIP IP addresses
        var fallbackIps = GetFallbackIps(targetHost, isV6);
        if (fallbackIps.Count > 0)
        {
            return DnsPacket.BuildResponseWithIps(originalRequest, query, fallbackIps, 300);
        }

        return null;
    }

    private static IReadOnlyList<IPAddress> GetFallbackIps(string targetHost, bool isV6)
    {
        if (targetHost.StartsWith("forcesafesearch", StringComparison.OrdinalIgnoreCase))
        {
            return isV6 ? GoogleSafeIpsV6 : GoogleSafeIpsV4;
        }

        if (targetHost.StartsWith("strict.bing", StringComparison.OrdinalIgnoreCase))
        {
            return isV6 ? BingStrictIpsV6 : BingStrictIpsV4;
        }

        if (targetHost.StartsWith("safe.duckduckgo", StringComparison.OrdinalIgnoreCase))
        {
            return isV6 ? Array.Empty<IPAddress>() : DuckDuckGoSafeIpsV4;
        }

        if (targetHost.StartsWith("restrictmoderate", StringComparison.OrdinalIgnoreCase))
        {
            return isV6 ? YouTubeModerateIpsV6 : YouTubeModerateIpsV4;
        }

        if (targetHost.StartsWith("restrict", StringComparison.OrdinalIgnoreCase))
        {
            return isV6 ? YouTubeStrictIpsV6 : YouTubeStrictIpsV4;
        }

        return Array.Empty<IPAddress>();
    }
}
