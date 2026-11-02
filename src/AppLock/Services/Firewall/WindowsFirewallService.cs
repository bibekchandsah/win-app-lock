using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AppLock.Services.Firewall;

/// <summary>
/// Windows Firewall service using COM interop (HNetCfg.FwPolicy2)
/// Only manages rules in the "AppLock Connection Firewall" group
/// </summary>
public class WindowsFirewallService : IFirewallService
{
    private const string APPLOCK_GROUP = "AppLock Connection Firewall";
    private const string DESCRIPTION_PREFIX = "AppLock-managed|id=";
    
    // COM constants
    private const int NET_FW_RULE_DIR_IN = 1;
    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF;
    private const int NET_FW_IP_PROTOCOL_ANY = 256;

    public async Task<bool> CreateRuleAsync(
        string ruleName,
        string exePath,
        bool blockInbound,
        bool blockOutbound,
        int profiles,
        Guid id,
        string? description = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return false;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return false;

                dynamic rules = policy.Rules;

                // Create outbound rule if requested
                if (blockOutbound)
                {
                    string outboundName = $"{ruleName} (Outbound)";
                    CreateSingleRule(rules, outboundName, exePath, NET_FW_RULE_DIR_OUT, profiles, id, description);
                }

                // Create inbound rule if requested
                if (blockInbound)
                {
                    string inboundName = $"{ruleName} (Inbound)";
                    CreateSingleRule(rules, inboundName, exePath, NET_FW_RULE_DIR_IN, profiles, id, description);
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Access denied: Administrator rights required");
                return false;
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"COM error creating firewall rule: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating firewall rule: {ex.Message}");
                return false;
            }
        });
    }

    private void CreateSingleRule(dynamic rules, string ruleName, string exePath, int direction, int profiles, Guid id, string? description)
    {
        try
        {
            // Remove existing rule with same name if it exists and belongs to AppLock
            try
            {
                dynamic? existingRule = FindRuleByName(rules, ruleName);
                if (existingRule != null)
                {
                    string grouping = existingRule.Grouping ?? string.Empty;
                    if (grouping == APPLOCK_GROUP)
                    {
                        rules.Remove(ruleName);
                    }
                }
            }
            catch { }

            Type? ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (ruleType == null) return;

            dynamic? rule = Activator.CreateInstance(ruleType);
            if (rule == null) return;
            
            rule.Name = ruleName;
            rule.Description = description ?? $"{DESCRIPTION_PREFIX}{id}";
            rule.Grouping = APPLOCK_GROUP;
            rule.ApplicationName = exePath;
            rule.Direction = direction;
            rule.Action = NET_FW_ACTION_BLOCK;
            rule.Enabled = true;
            rule.Profiles = profiles;
            rule.Protocol = NET_FW_IP_PROTOCOL_ANY;

            rules.Add(rule);
            System.Diagnostics.Debug.WriteLine($"Created firewall rule: {ruleName}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error creating single rule {ruleName}: {ex.Message}");
            throw;
        }
    }

    private dynamic? FindRuleByName(dynamic rules, string name)
    {
        try
        {
            foreach (dynamic rule in rules)
            {
                if (rule.Name == name)
                {
                    return rule;
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<bool> RemoveRuleAsync(string ruleName)
    {
        return await Task.Run(() =>
        {
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return false;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return false;

                dynamic rules = policy.Rules;

                // Only remove if it's an AppLock rule
                dynamic? rule = FindRuleByName(rules, ruleName);
                if (rule != null)
                {
                    string grouping = rule.Grouping ?? string.Empty;
                    if (grouping == APPLOCK_GROUP)
                    {
                        rules.Remove(ruleName);
                        System.Diagnostics.Debug.WriteLine($"Removed firewall rule: {ruleName}");
                        return true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Rule {ruleName} does not belong to AppLock group, skipping removal");
                        return false;
                    }
                }

                return false;
            }
            catch (UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Access denied: Administrator rights required");
                return false;
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"COM error removing firewall rule: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error removing firewall rule: {ex.Message}");
                return false;
            }
        });
    }

    public async Task<List<FirewallRuleInfo>> GetAppLockRulesAsync()
    {
        return await Task.Run(() =>
        {
            var result = new List<FirewallRuleInfo>();

            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return result;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return result;

                dynamic rules = policy.Rules;

                foreach (dynamic rule in rules)
                {
                    try
                    {
                        string grouping = rule.Grouping ?? string.Empty;
                        if (grouping == APPLOCK_GROUP)
                        {
                            var ruleInfo = new FirewallRuleInfo
                            {
                                Name = rule.Name ?? string.Empty,
                                Description = rule.Description ?? string.Empty,
                                ApplicationName = rule.ApplicationName ?? string.Empty,
                                Grouping = grouping,
                                Enabled = rule.Enabled,
                                BlocksInbound = rule.Direction == NET_FW_RULE_DIR_IN,
                                BlocksOutbound = rule.Direction == NET_FW_RULE_DIR_OUT,
                                Profiles = rule.Profiles
                            };

                            // Extract ID from description if present
                            if (ruleInfo.Description.StartsWith(DESCRIPTION_PREFIX))
                            {
                                string idStr = ruleInfo.Description.Substring(DESCRIPTION_PREFIX.Length);
                                if (Guid.TryParse(idStr, out Guid id))
                                {
                                    ruleInfo.RuleId = id;
                                }
                            }

                            result.Add(ruleInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error reading rule: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting AppLock rules: {ex.Message}");
            }

            return result;
        });
    }

    public async Task<bool> SetRuleEnabledAsync(string ruleName, bool enabled)
    {
        return await Task.Run(() =>
        {
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return false;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return false;

                dynamic rules = policy.Rules;
                dynamic? rule = FindRuleByName(rules, ruleName);

                if (rule != null)
                {
                    string grouping = rule.Grouping ?? string.Empty;
                    if (grouping == APPLOCK_GROUP)
                    {
                        rule.Enabled = enabled;
                        System.Diagnostics.Debug.WriteLine($"Set rule {ruleName} enabled: {enabled}");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting rule enabled state: {ex.Message}");
                return false;
            }
        });
    }

    public async Task<bool> RuleExistsAsync(string ruleName)
    {
        return await Task.Run(() =>
        {
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return false;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return false;

                dynamic rules = policy.Rules;
                dynamic? rule = FindRuleByName(rules, ruleName);

                if (rule != null)
                {
                    string grouping = rule.Grouping ?? string.Empty;
                    return grouping == APPLOCK_GROUP;
                }

                return false;
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<FirewallStatus> GetFirewallStatusAsync()
    {
        return await Task.Run(() =>
        {
            var status = new FirewallStatus
            {
                IsEnabled = false,
                IsServiceRunning = false,
                IsGroupPolicyManaged = false,
                CurrentProfile = NET_FW_PROFILE2_ALL
            };

            try
            {
                // Check if Windows Firewall service is running
                try
                {
                    // Use P/Invoke or WMI as ServiceController requires separate package
                    // For now, assume running if we can query firewall status
                    status.IsServiceRunning = true;
                }
                catch { }

                // Check firewall status via COM
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return status;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return status;

                // Get current profile types (bitmap)
                status.CurrentProfile = policy.CurrentProfileTypes;

                // Check if firewall is enabled for the current profile
                // Profile values: Domain=1, Private=2, Public=4
                bool domainEnabled = false, privateEnabled = false, publicEnabled = false;

                try
                {
                    domainEnabled = policy.FirewallEnabled(1);  // NET_FW_PROFILE2_DOMAIN
                }
                catch { }

                try
                {
                    privateEnabled = policy.FirewallEnabled(2); // NET_FW_PROFILE2_PRIVATE
                }
                catch { }

                try
                {
                    publicEnabled = policy.FirewallEnabled(4);  // NET_FW_PROFILE2_PUBLIC
                }
                catch { }

                // If any active profile has firewall enabled, consider it enabled
                if ((status.CurrentProfile & 1) != 0 && domainEnabled) status.IsEnabled = true;
                if ((status.CurrentProfile & 2) != 0 && privateEnabled) status.IsEnabled = true;
                if ((status.CurrentProfile & 4) != 0 && publicEnabled) status.IsEnabled = true;

                // Check for group policy management (simplified - actual check is more complex)
                // If we can't modify rules, likely group-policy managed
                status.IsGroupPolicyManaged = false; // TODO: Implement proper check

            }
            catch (UnauthorizedAccessException)
            {
                status.IsGroupPolicyManaged = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting firewall status: {ex.Message}");
            }

            return status;
        });
    }
}
