using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;

namespace AppLock.Services.Firewall;

/// <summary>
/// Windows Firewall service using COM interop (HNetCfg.FwPolicy2)
/// Only manages rules in the "AppLock Connection Firewall" group
/// </summary>
public class WindowsFirewallService : IFirewallService
{
    private const string APPLOCK_GROUP = "AppLock Connection Firewall";
    private const string DESCRIPTION_PREFIX = "Managed by AppLock ";
    
    // COM constants
    private const int NET_FW_RULE_DIR_IN = 1;
    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_PROFILE2_ALL = 0x7; // Domain (1) + Private (2) + Public (4)
    private const int NET_FW_IP_PROTOCOL_ANY = 256;

    private bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

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
                LogService.LogAction("firewall", $"CreateRuleAsync: Starting for {ruleName}, IsElevated={IsElevated()}");
                
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null)
                {
                    LogService.LogAction("firewall", "CreateRuleAsync: Failed to get FwPolicy2 type");
                    return false;
                }
                LogService.LogAction("firewall", "CreateRuleAsync: Got FwPolicy2 type");

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null)
                {
                    LogService.LogAction("firewall", "CreateRuleAsync: Failed to create FwPolicy2 instance");
                    return false;
                }
                LogService.LogAction("firewall", "CreateRuleAsync: Created FwPolicy2 instance");

                dynamic rules = policy.Rules;
                int ruleCount = 0;
                try { ruleCount = rules.Count; } catch { }
                LogService.LogAction("firewall", $"CreateRuleAsync: Got Rules collection, count={ruleCount}");

                // Create outbound rule if requested
                if (blockOutbound)
                {
                    string outboundName = $"{ruleName} (Outbound)";
                    LogService.LogAction("firewall", $"CreateRuleAsync: Creating outbound rule: {outboundName}");
                    CreateSingleRule(rules, outboundName, exePath, NET_FW_RULE_DIR_OUT, profiles, id, description);
                }

                // Create inbound rule if requested
                if (blockInbound)
                {
                    string inboundName = $"{ruleName} (Inbound)";
                    LogService.LogAction("firewall", $"CreateRuleAsync: Creating inbound rule: {inboundName}");
                    CreateSingleRule(rules, inboundName, exePath, NET_FW_RULE_DIR_IN, profiles, id, description);
                }

                LogService.LogAction("firewall", "CreateRuleAsync: Success");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                LogService.LogAction("firewall", $"CreateRuleAsync: Access denied - {ex.Message}");
                return false;
            }
            catch (COMException ex)
            {
                LogService.LogAction("firewall", $"CreateRuleAsync: COM error - {ex.Message}, HResult=0x{ex.HResult:X8}");
                return false;
            }
            catch (Exception ex)
            {
                LogService.LogAction("firewall", $"CreateRuleAsync: Error - {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        });
    }

    private void CreateSingleRule(dynamic rules, string ruleName, string exePath, int direction, int profiles, Guid id, string? description)
    {
        try
        {
            LogService.LogAction("firewall", $"CreateSingleRule: Starting for {ruleName}");
            
            // Remove existing rule with same name if it exists and belongs to AppLock
            try
            {
                dynamic? existingRule = FindRuleByName(rules, ruleName);
                if (existingRule != null)
                {
                    string grouping = existingRule.Grouping ?? string.Empty;
                    if (grouping == APPLOCK_GROUP)
                    {
                        LogService.LogAction("firewall", $"CreateSingleRule: Removing existing rule {ruleName}");
                        rules.Remove(ruleName);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.LogAction("firewall", $"CreateSingleRule: Error checking existing rule - {ex.Message}");
            }

            Type? ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (ruleType == null)
            {
                LogService.LogAction("firewall", "CreateSingleRule: Failed to get FWRule type");
                return;
            }
            LogService.LogAction("firewall", "CreateSingleRule: Got FWRule type");

            dynamic? rule = Activator.CreateInstance(ruleType);
            if (rule == null)
            {
                LogService.LogAction("firewall", "CreateSingleRule: Failed to create FWRule instance");
                return;
            }
            LogService.LogAction("firewall", "CreateSingleRule: Created FWRule instance");
            
            LogService.LogAction("firewall", $"CreateSingleRule: Setting properties - Name={ruleName}, ExePath={exePath}, Direction={direction}");
            rule.Name = ruleName;
            rule.Description = description ?? $"{DESCRIPTION_PREFIX}{id:N}"; // Use :N format (no hyphens)
            rule.Grouping = APPLOCK_GROUP;
            rule.ApplicationName = exePath;
            rule.Direction = direction;
            rule.Action = NET_FW_ACTION_BLOCK;
            rule.Enabled = true;
            rule.Profiles = profiles;
            rule.Protocol = NET_FW_IP_PROTOCOL_ANY;

            LogService.LogAction("firewall", $"CreateSingleRule: Adding rule to collection");
            rules.Add(rule);
            LogService.LogAction("firewall", $"CreateSingleRule: Successfully added rule {ruleName}");
        }
        catch (Exception ex)
        {
            LogService.LogAction("firewall", $"CreateSingleRule: Error - {ex.GetType().Name}: {ex.Message}");
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
                LogService.LogAction("firewall", $"RemoveRuleAsync: Attempting to remove {ruleName}");
                
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null)
                {
                    LogService.LogAction("firewall", "RemoveRuleAsync: Failed to get FwPolicy2 type");
                    return false;
                }

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null)
                {
                    LogService.LogAction("firewall", "RemoveRuleAsync: Failed to create FwPolicy2 instance");
                    return false;
                }

                dynamic rules = policy.Rules;

                // Only remove if it's an AppLock rule
                dynamic? rule = FindRuleByName(rules, ruleName);
                if (rule != null)
                {
                    string grouping = rule.Grouping ?? string.Empty;
                    if (grouping == APPLOCK_GROUP)
                    {
                        rules.Remove(ruleName);
                        LogService.LogAction("firewall", $"RemoveRuleAsync: Successfully removed {ruleName}");
                        return true;
                    }
                    else
                    {
                        LogService.LogAction("firewall", $"RemoveRuleAsync: Rule {ruleName} does not belong to AppLock group, skipping");
                        return false;
                    }
                }

                // Rule doesn't exist - consider this success (already removed)
                LogService.LogAction("firewall", $"RemoveRuleAsync: Rule {ruleName} not found (already removed)");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                LogService.LogAction("firewall", $"RemoveRuleAsync: Access denied - {ex.Message}");
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
