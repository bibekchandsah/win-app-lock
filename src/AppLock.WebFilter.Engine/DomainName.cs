using System.Globalization;
using System.Net;

namespace AppLock.WebFilter.Engine;

/// <summary>
/// Domain-name normalization shared by the list pipeline and the matcher. Names are lower-cased,
/// have their trailing dot removed and are converted to punycode, so a list entry and a DNS query
/// always compare as plain ASCII.
/// </summary>
public static class DomainName
{
    private static readonly IdnMapping Idn = new()
    {
        AllowUnassigned = false,
        UseStd3AsciiRules = true
    };

    /// <summary>Normalizes a bare domain. Rejects IP addresses, single labels and invalid syntax.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;

        var name = (input ?? string.Empty).Trim().TrimEnd('.');
        if (name.Length is 0 or > 253) return false;

        try
        {
            name = Idn.GetAscii(name).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!IsValidAsciiDomain(name)) return false;

        normalized = name;
        return true;
    }

    /// <summary>True for a syntactically valid, fully qualified ASCII domain name.</summary>
    public static bool IsValidAsciiDomain(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 253) return false;
        if (IPAddress.TryParse(name, out _)) return false;

        int labels = 0;
        foreach (var label in name.Split('.'))
        {
            if (!IsValidLabel(label)) return false;
            labels++;
        }

        // A single label is a local/NetBIOS name and never belongs on a public block list.
        return labels >= 2;
    }

    private static bool IsValidLabel(string label)
    {
        if (label.Length is 0 or > 63) return false;
        if (label[0] == '-' || label[^1] == '-') return false;

        foreach (char c in label)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts the host name from user input: a domain, <c>*.domain</c>, a URL with a scheme, or a
    /// host/path without one. IP addresses and anything that is not a valid domain are rejected.
    /// </summary>
    public static bool TryExtractHost(string? input, out string host)
    {
        host = string.Empty;

        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        if (text.StartsWith("*.", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        bool hasScheme = text.Contains("://", StringComparison.Ordinal);
        bool looksLikeUrl = text.Contains('/') || text.Contains('?') || text.Contains('#');

        if (!hasScheme && !looksLikeUrl)
        {
            return TryNormalize(text, out host);
        }

        if (!hasScheme)
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return false;

        return TryNormalize(uri.Host, out host);
    }

    /// <summary>
    /// Yields the name and then each parent domain: <c>a.b.example.com</c>, <c>b.example.com</c>,
    /// <c>example.com</c>, <c>com</c>. The matcher walks these to implement suffix matching.
    /// </summary>
    public static IEnumerable<string> EnumerateSuffixes(string name)
    {
        int start = 0;
        while (start < name.Length)
        {
            yield return start == 0 ? name : name[start..];

            int dot = name.IndexOf('.', start);
            if (dot < 0) yield break;

            start = dot + 1;
        }
    }

    public static int LabelCount(string name)
    {
        int count = 1;
        foreach (char c in name)
        {
            if (c == '.') count++;
        }
        return count;
    }
}
