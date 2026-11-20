namespace AppLock.WebFilter.Engine;

/// <summary>
/// Decides whether a name is local (router, NAS, company network) and must therefore be answered by
/// the network's own DNS servers instead of a public DoH resolver.
/// </summary>
public static class LocalNameRules
{
    private static readonly string[] LocalSuffixes =
    {
        "local", "lan", "home", "home.arpa", "internal", "corp", "localdomain", "intranet", "private"
    };

    public static bool IsLocalName(string name, string? machineDnsSuffix)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;

        var trimmed = name.Trim().TrimEnd('.').ToLowerInvariant();

        // A single label is resolved by the local resolver (search suffix / NetBIOS), never DoH.
        if (!trimmed.Contains('.')) return true;

        foreach (var suffix in LocalSuffixes)
        {
            if (trimmed == suffix || trimmed.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
        }

        var machineSuffix = (machineDnsSuffix ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (machineSuffix.Length > 0 &&
            (trimmed == machineSuffix || trimmed.EndsWith("." + machineSuffix, StringComparison.Ordinal)))
        {
            return true;
        }

        return IsPrivateReverseLookup(trimmed);
    }

    private static bool IsPrivateReverseLookup(string name)
    {
        if (name.EndsWith(".ip6.arpa", StringComparison.Ordinal)) return true;
        if (!name.EndsWith(".in-addr.arpa", StringComparison.Ordinal)) return false;

        if (IsInZone(name, "10.in-addr.arpa")) return true;
        if (IsInZone(name, "168.192.in-addr.arpa")) return true;
        if (IsInZone(name, "254.169.in-addr.arpa")) return true;
        if (IsInZone(name, "127.in-addr.arpa")) return true;

        for (int second = 16; second <= 31; second++)
        {
            if (IsInZone(name, $"{second}.172.in-addr.arpa")) return true;
        }

        return false;
    }

    /// <summary>Reverse zones are suffixes: 5.10.20.172.in-addr.arpa belongs to 20.172.in-addr.arpa.</summary>
    private static bool IsInZone(string name, string zone)
        => name == zone || name.EndsWith("." + zone, StringComparison.Ordinal);
}
