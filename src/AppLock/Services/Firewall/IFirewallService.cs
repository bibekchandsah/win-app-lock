using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AppLock.Services.Firewall;

public interface IFirewallService
{
    /// <summary>
    /// Creates a firewall rule in the AppLock Connection Firewall group
    /// </summary>
    Task<bool> CreateRuleAsync(
        string ruleName,
        string exePath,
        bool blockInbound,
        bool blockOutbound,
        int profiles,
        Guid id,
        string? description = null);

    /// <summary>
    /// Removes a firewall rule by name (only if it belongs to AppLock group)
    /// </summary>
    Task<bool> RemoveRuleAsync(string ruleName);

    /// <summary>
    /// Reads all AppLock firewall rules
    /// </summary>
    Task<List<FirewallRuleInfo>> GetAppLockRulesAsync();

    /// <summary>
    /// Enables or disables a rule
    /// </summary>
    Task<bool> SetRuleEnabledAsync(string ruleName, bool enabled);

    /// <summary>
    /// Checks if a specific rule exists
    /// </summary>
    Task<bool> RuleExistsAsync(string ruleName);

    /// <summary>
    /// Gets firewall status for current profile
    /// </summary>
    Task<FirewallStatus> GetFirewallStatusAsync();
}

public class FirewallRuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public string Grouping { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool BlocksInbound { get; set; }
    public bool BlocksOutbound { get; set; }
    public int Profiles { get; set; }
    public Guid? RuleId { get; set; }
}

public class FirewallStatus
{
    public bool IsEnabled { get; set; }
    public bool IsServiceRunning { get; set; }
    public bool IsGroupPolicyManaged { get; set; }
    public int CurrentProfile { get; set; }
}
