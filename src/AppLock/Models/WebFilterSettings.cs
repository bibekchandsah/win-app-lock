using System;
using System.Collections.Generic;
using System.Linq;

namespace AppLock.Models;

public enum WebFilterMode
{
    /// <summary>A small DNS filter runs on this PC and Windows points at it.</summary>
    LocalFilter = 0,

    /// <summary>Windows DNS is pointed at a family-filter provider.</summary>
    Provider = 1
}

public enum UpstreamKind
{
    CloudflareDoh = 0,
    GoogleDoh = 1,
    Quad9Doh = 2,
    OriginalDns = 3,
    Custom = 4
}

public enum BlockResponse
{
    /// <summary>Answer a blocked name with NXDOMAIN (the default).</summary>
    NxDomain = 0,

    /// <summary>Answer A/AAAA with 0.0.0.0 / :: and other types with an empty answer.</summary>
    NullAddress = 1
}

public enum YouTubeRestriction
{
    Off = 0,
    Moderate = 1,
    Strict = 2
}

/// <summary>
/// Everything the Web Filter add-on persists, stored inside the protected settings JSON
/// (<see cref="AppSettings.WebFilter"/>). Absent values fall back to the defaults here, so a
/// settings file written before this feature existed loads without any special handling.
/// </summary>
public class WebFilterSettings
{
    public bool Enabled { get; set; }

    public WebFilterMode Mode { get; set; } = WebFilterMode.LocalFilter;

    public string ProviderId { get; set; } = "cloudflare-family";

    public UpstreamKind Upstream { get; set; } = UpstreamKind.CloudflareDoh;

    public string CustomUpstream { get; set; } = string.Empty;

    public BlockResponse BlockResponse { get; set; } = BlockResponse.NxDomain;

    /// <summary>Per-category switch and schedule, keyed by category id (adult, gambling, ...).</summary>
    public Dictionary<string, CategoryRule> Categories { get; set; } = new();

    /// <summary>Per-service switch and schedule, keyed by service id (youtube, facebook, ...).</summary>
    public Dictionary<string, ServiceRule> Services { get; set; } = new();

    public List<DomainEntry> AlwaysBlock { get; set; } = new();

    public List<DomainEntry> AlwaysAllow { get; set; } = new();

    public List<TempAllow> TempAllows { get; set; } = new();

    public bool EnforceSafeSearch { get; set; }

    public YouTubeRestriction YouTubeMode { get; set; } = YouTubeRestriction.Off;

    public BypassProtection Bypass { get; set; } = new();

    public bool LogBlocked { get; set; } = true;

    public bool LogAllowed { get; set; }

    public int LogRetentionDays { get; set; } = 30;

    public bool NotifyOnBlock { get; set; }

    public bool AutoUpdateLists { get; set; } = true;

    public DateTime? PausedUntilUtc { get; set; }

    public bool CacheEnabled { get; set; } = true;

    public bool StartWithAppLock { get; set; } = true;

    /// <summary>The per-adapter DNS settings captured before AppLock first changed them.</summary>
    public SavedDnsConfig OriginalDns { get; set; } = new();

    /// <summary>Exactly what was applied (firewall rules, registry values), so it can be reverted.</summary>
    public AppliedChanges Applied { get; set; } = new();

    public bool IsPaused => PausedUntilUtc is { } until && until > DateTime.UtcNow;

    public int EnabledCategoryCount => Categories.Values.Count(c => c.Enabled);

    /// <summary>
    /// Makes a deserialized instance safe to use: fills missing collections, clamps the retention
    /// choice and normalizes saved domains. Returns true when something had to change.
    /// </summary>
    public bool Normalize()
    {
        bool changed = false;

        if (Categories == null) { Categories = new Dictionary<string, CategoryRule>(); changed = true; }
        if (Services == null) { Services = new Dictionary<string, ServiceRule>(); changed = true; }
        if (AlwaysBlock == null) { AlwaysBlock = new List<DomainEntry>(); changed = true; }
        if (AlwaysAllow == null) { AlwaysAllow = new List<DomainEntry>(); changed = true; }
        if (TempAllows == null) { TempAllows = new List<TempAllow>(); changed = true; }
        if (Bypass == null) { Bypass = new BypassProtection(); changed = true; }
        if (OriginalDns == null) { OriginalDns = new SavedDnsConfig(); changed = true; }
        if (Applied == null) { Applied = new AppliedChanges(); changed = true; }
        if (ProviderId == null) { ProviderId = "cloudflare-family"; changed = true; }
        if (CustomUpstream == null) { CustomUpstream = string.Empty; changed = true; }

        foreach (var rule in Categories.Values)
        {
            if (rule == null) continue;
            if (rule.ActiveBlocks == null) { rule.ActiveBlocks = new List<TimeBlock>(); changed = true; }
            changed |= NormalizeBlocks(rule.ActiveBlocks);
        }

        foreach (var rule in Services.Values)
        {
            if (rule == null) continue;
            if (rule.ActiveBlocks == null) { rule.ActiveBlocks = new List<TimeBlock>(); changed = true; }
            changed |= NormalizeBlocks(rule.ActiveBlocks);
        }

        changed |= NormalizeDomains(AlwaysBlock);
        changed |= NormalizeDomains(AlwaysAllow);

        TempAllows.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Domain));
        foreach (var allow in TempAllows)
        {
            var normalized = NormalizeDomain(allow.Domain);
            if (!string.Equals(normalized, allow.Domain, StringComparison.Ordinal))
            {
                allow.Domain = normalized;
                changed = true;
            }
        }

        int retention = LogRetentionDays switch
        {
            7 => 7,
            90 => 90,
            _ => 30
        };
        if (retention != LogRetentionDays)
        {
            LogRetentionDays = retention;
            changed = true;
        }

        changed |= OriginalDns.Normalize();
        changed |= Applied.Normalize();

        return changed;
    }

    private static bool NormalizeBlocks(List<TimeBlock> blocks)
    {
        bool changed = false;
        foreach (var block in blocks)
        {
            if (block.Days == null || block.Days.Length != 7)
            {
                block.Days = new[] { true, true, true, true, true, true, true };
                changed = true;
            }
        }
        return changed;
    }

    private static bool NormalizeDomains(List<DomainEntry> entries)
    {
        bool changed = false;
        int before = entries.Count;
        entries.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Domain));
        if (entries.Count != before) changed = true;

        foreach (var entry in entries)
        {
            var normalized = NormalizeDomain(entry.Domain);
            if (!string.Equals(normalized, entry.Domain, StringComparison.Ordinal))
            {
                entry.Domain = normalized;
                changed = true;
            }
            if (entry.Note == null) { entry.Note = string.Empty; changed = true; }
        }

        return changed;
    }

    /// <summary>Lower case, no surrounding whitespace and no trailing dot.</summary>
    public static string NormalizeDomain(string? domain)
        => (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
}

public class CategoryRule
{
    public bool Enabled { get; set; }

    /// <summary>Empty means the category is active all day; otherwise these are the "active" windows.</summary>
    public List<TimeBlock> ActiveBlocks { get; set; } = new();
}

public class ServiceRule
{
    public bool Enabled { get; set; }

    public List<TimeBlock> ActiveBlocks { get; set; } = new();
}

public class DomainEntry
{
    public string Domain { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public class TempAllow
{
    public string Domain { get; set; } = string.Empty;

    public DateTime ExpiresUtc { get; set; }
}

public class BypassProtection
{
    public bool BlockOtherDns { get; set; } = true;

    public bool BlockKnownDoh { get; set; } = true;

    public bool BrowserPolicies { get; set; } = true;
}

public class SavedDnsConfig
{
    public List<SavedAdapterDns> Adapters { get; set; } = new();

    public DateTime? SavedUtc { get; set; }

    public bool HasEntries => Adapters.Count > 0;

    public bool Normalize()
    {
        if (Adapters == null) { Adapters = new List<SavedAdapterDns>(); return true; }
        bool changed = false;
        foreach (var adapter in Adapters)
        {
            if (adapter.Ipv4Servers == null) { adapter.Ipv4Servers = new List<string>(); changed = true; }
            if (adapter.Ipv6Servers == null) { adapter.Ipv6Servers = new List<string>(); changed = true; }
        }
        return changed;
    }
}

/// <summary>One adapter's DNS configuration as it was before the filter was applied.</summary>
public class SavedAdapterDns
{
    /// <summary>The adapter GUID (or its NetConnectionID for the netsh fallback).</summary>
    public string AdapterId { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Ipv4FromDhcp { get; set; } = true;

    public List<string> Ipv4Servers { get; set; } = new();

    public bool Ipv6FromDhcp { get; set; } = true;

    public List<string> Ipv6Servers { get; set; } = new();
}

/// <summary>What AppLock changed on this PC, recorded so turning the filter off reverts exactly it.</summary>
public class AppliedChanges
{
    public List<string> FirewallRuleNames { get; set; } = new();

    public List<PolicyValue> PolicyValues { get; set; } = new();

    public string LoopbackAddress { get; set; } = string.Empty;

    public DateTime? AppliedUtc { get; set; }

    public bool Normalize()
    {
        bool changed = false;
        if (FirewallRuleNames == null) { FirewallRuleNames = new List<string>(); changed = true; }
        if (PolicyValues == null) { PolicyValues = new List<PolicyValue>(); changed = true; }
        if (LoopbackAddress == null) { LoopbackAddress = string.Empty; changed = true; }
        return changed;
    }
}

/// <summary>A registry value written by the browser Secure DNS policies, with its previous value.</summary>
public class PolicyValue
{
    public string KeyPath { get; set; } = string.Empty;

    public string ValueName { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>True when the value did not exist before AppLock wrote it, so removing it is the revert.</summary>
    public bool WasMissing { get; set; } = true;
}
