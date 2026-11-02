using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppLock.Models;
using System.Diagnostics;

namespace AppLock.Services.Firewall;

/// <summary>
/// Detects drift between AppLock settings and actual Windows Firewall rules
/// </summary>
public class RuleSyncService
{
    private readonly IFirewallService _firewallService;

    public RuleSyncService(IFirewallService firewallService)
    {
        _firewallService = firewallService;
    }

    /// <summary>
    /// Checks all entries and detects drift
    /// </summary>
    public async Task<List<DriftInfo>> DetectDriftAsync(List<NetworkRuleEntry> entries)
    {
        var driftList = new List<DriftInfo>();

        try
        {
            var actualRules = await _firewallService.GetAppLockRulesAsync();

            foreach (var entry in entries)
            {
                var drift = await CheckEntryDriftAsync(entry, actualRules);
                if (drift != null)
                {
                    driftList.Add(drift);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error detecting drift: {ex.Message}");
        }

        return driftList;
    }

    /// <summary>
    /// Checks a single entry for drift
    /// </summary>
    public async Task<DriftInfo?> CheckEntryDriftAsync(NetworkRuleEntry entry, List<FirewallRuleInfo>? actualRules = null)
    {
        try
        {
            if (actualRules == null)
            {
                actualRules = await _firewallService.GetAppLockRulesAsync();
            }

            string ruleName = entry.CustomRuleName ?? $"AppLock – {entry.DisplayName}";
            
            bool outboundExpected = entry.BlockOutbound && entry.Blocked;
            bool inboundExpected = entry.BlockInbound && entry.Blocked;

            var outboundRule = actualRules.FirstOrDefault(r => r.Name == $"{ruleName} (Outbound)");
            var inboundRule = actualRules.FirstOrDefault(r => r.Name == $"{ruleName} (Inbound)");

            bool outboundExists = outboundRule != null && outboundRule.Enabled;
            bool inboundExists = inboundRule != null && inboundRule.Enabled;

            // Check for drift
            if (outboundExpected != outboundExists || inboundExpected != inboundExists)
            {
                var driftType = DriftType.None;

                if (entry.Blocked)
                {
                    // Expected blocked, but rule missing or disabled
                    if (!outboundExists && outboundExpected)
                        driftType |= DriftType.OutboundMissing;
                    if (!inboundExists && inboundExpected)
                        driftType |= DriftType.InboundMissing;
                }
                else
                {
                    // Expected allowed, but rule exists
                    if (outboundExists)
                        driftType |= DriftType.OutboundExtra;
                    if (inboundExists)
                        driftType |= DriftType.InboundExtra;
                }

                if (driftType != DriftType.None)
                {
                    return new DriftInfo
                    {
                        Entry = entry,
                        DriftType = driftType,
                        ExpectedBlocked = entry.Blocked,
                        ActualOutboundExists = outboundExists,
                        ActualInboundExists = inboundExists
                    };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking drift for {entry.DisplayName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Repairs drift by recreating or removing rules
    /// </summary>
    public async Task<bool> RepairDriftAsync(DriftInfo drift)
    {
        try
        {
            var entry = drift.Entry;
            string ruleName = entry.CustomRuleName ?? $"AppLock – {entry.DisplayName}";

            if (entry.Blocked)
            {
                // Should be blocked - recreate missing rules
                if ((drift.DriftType & DriftType.OutboundMissing) != 0 || 
                    (drift.DriftType & DriftType.InboundMissing) != 0)
                {
                    return await _firewallService.CreateRuleAsync(
                        ruleName,
                        entry.ExePath,
                        entry.BlockInbound,
                        entry.BlockOutbound,
                        entry.Profiles,
                        entry.Id);
                }
            }
            else
            {
                // Should be allowed - remove extra rules
                bool success = true;
                if ((drift.DriftType & DriftType.OutboundExtra) != 0)
                {
                    success &= await _firewallService.RemoveRuleAsync($"{ruleName} (Outbound)");
                }
                if ((drift.DriftType & DriftType.InboundExtra) != 0)
                {
                    success &= await _firewallService.RemoveRuleAsync($"{ruleName} (Inbound)");
                }
                return success;
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error repairing drift: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Repairs all detected drift
    /// </summary>
    public async Task<RepairResult> RepairAllDriftAsync(List<DriftInfo> driftList)
    {
        int repaired = 0;
        int failed = 0;

        foreach (var drift in driftList)
        {
            bool success = await RepairDriftAsync(drift);
            if (success)
                repaired++;
            else
                failed++;
        }

        return new RepairResult
        {
            Repaired = repaired,
            Failed = failed
        };
    }
}

[Flags]
public enum DriftType
{
    None = 0,
    OutboundMissing = 1,
    InboundMissing = 2,
    OutboundExtra = 4,
    InboundExtra = 8
}

public class DriftInfo
{
    public NetworkRuleEntry Entry { get; set; } = null!;
    public DriftType DriftType { get; set; }
    public bool ExpectedBlocked { get; set; }
    public bool ActualOutboundExists { get; set; }
    public bool ActualInboundExists { get; set; }

    public string GetDescription()
    {
        if ((DriftType & (DriftType.OutboundMissing | DriftType.InboundMissing)) != 0)
            return "Rule missing";
        if ((DriftType & (DriftType.OutboundExtra | DriftType.InboundExtra)) != 0)
            return "Modified outside AppLock";
        return "Unknown drift";
    }
}

public class RepairResult
{
    public int Repaired { get; set; }
    public int Failed { get; set; }
    public int Total => Repaired + Failed;
}
