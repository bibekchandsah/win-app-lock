using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AppLock.Services.Firewall;

/// <summary>
/// In-memory fake firewall service for testing
/// </summary>
public class FakeFirewallService : IFirewallService
{
    private readonly List<FirewallRuleInfo> _rules = new();
    private bool _isElevated = true;
    private bool _isFirewallEnabled = true;
    private bool _isGroupPolicyManaged = false;

    public void SetElevated(bool elevated) => _isElevated = elevated;
    public void SetFirewallEnabled(bool enabled) => _isFirewallEnabled = enabled;
    public void SetGroupPolicyManaged(bool managed) => _isGroupPolicyManaged = managed;
    public List<FirewallRuleInfo> GetRules() => _rules;

    public Task<bool> CreateRuleAsync(
        string ruleName,
        string exePath,
        bool blockInbound,
        bool blockOutbound,
        int profiles,
        Guid id,
        string? description = null)
    {
        if (!_isElevated)
            return Task.FromResult(false);

        if (_isGroupPolicyManaged)
            return Task.FromResult(false);

        // Create outbound rule
        if (blockOutbound)
        {
            var outboundName = $"{ruleName} (Outbound)";
            _rules.RemoveAll(r => r.Name == outboundName);
            _rules.Add(new FirewallRuleInfo
            {
                Name = outboundName,
                Description = description ?? $"AppLock-managed|id={id}",
                ApplicationName = exePath,
                Grouping = "AppLock Connection Firewall",
                Enabled = true,
                BlocksOutbound = true,
                BlocksInbound = false,
                Profiles = profiles,
                RuleId = id
            });
        }

        // Create inbound rule
        if (blockInbound)
        {
            var inboundName = $"{ruleName} (Inbound)";
            _rules.RemoveAll(r => r.Name == inboundName);
            _rules.Add(new FirewallRuleInfo
            {
                Name = inboundName,
                Description = description ?? $"AppLock-managed|id={id}",
                ApplicationName = exePath,
                Grouping = "AppLock Connection Firewall",
                Enabled = true,
                BlocksOutbound = false,
                BlocksInbound = true,
                Profiles = profiles,
                RuleId = id
            });
        }

        return Task.FromResult(true);
    }

    public Task<bool> RemoveRuleAsync(string ruleName)
    {
        if (!_isElevated)
            return Task.FromResult(false);

        var rule = _rules.FirstOrDefault(r => r.Name == ruleName);
        if (rule != null && rule.Grouping == "AppLock Connection Firewall")
        {
            _rules.Remove(rule);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<List<FirewallRuleInfo>> GetAppLockRulesAsync()
    {
        var appLockRules = _rules
            .Where(r => r.Grouping == "AppLock Connection Firewall")
            .ToList();
        return Task.FromResult(appLockRules);
    }

    public Task<bool> SetRuleEnabledAsync(string ruleName, bool enabled)
    {
        if (!_isElevated)
            return Task.FromResult(false);

        var rule = _rules.FirstOrDefault(r => r.Name == ruleName);
        if (rule != null && rule.Grouping == "AppLock Connection Firewall")
        {
            rule.Enabled = enabled;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> RuleExistsAsync(string ruleName)
    {
        return Task.FromResult(_rules.Any(r => 
            r.Name == ruleName && 
            r.Grouping == "AppLock Connection Firewall"));
    }

    public Task<FirewallStatus> GetFirewallStatusAsync()
    {
        return Task.FromResult(new FirewallStatus
        {
            IsEnabled = _isFirewallEnabled,
            IsServiceRunning = _isFirewallEnabled,
            IsGroupPolicyManaged = _isGroupPolicyManaged,
            CurrentProfile = 0x7FFFFFFF
        });
    }
}
