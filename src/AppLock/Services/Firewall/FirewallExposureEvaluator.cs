using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using AppLock.Models;

namespace AppLock.Services.Firewall;

public record ExposureEvaluationResult(
    FirewallExposure Exposure,
    string? MatchingRuleName,
    string? MatchingRuleDetails,
    string ActiveProfileName,
    bool IsThirdPartyFirewall);

public class FirewallExposureEvaluator
{
    private class CachedRule
    {
        public string Name = string.Empty;
        public string Grouping = string.Empty;
        public bool Enabled;
        public int Direction; // 1 = Inbound, 2 = Outbound
        public int Action;    // 1 = Allow, 0 = Block
        public int Protocol;  // 6 = TCP, 17 = UDP, 256 = Any
        public int Profiles;  // Bitmask: Domain=1, Private=2, Public=4
        public string? LocalPorts;
        public string? ApplicationName;
        public string? ServiceName;
    }

    private class CachedPolicy
    {
        public bool IsThirdPartyFirewall;
        public bool IsFirewallEnabled = true;
        public int ActiveProfiles = 7; // Default all
        public int DefaultInboundAction = 0; // 0 = Block, 1 = Allow
        public List<CachedRule> InboundRules = new();
    }

    private static CachedPolicy? _cachedPolicy;
    private static DateTime _lastCacheTime = DateTime.MinValue;
    private static readonly object _syncLock = new();

    public static void InvalidateCache()
    {
        lock (_syncLock)
        {
            _cachedPolicy = null;
            _lastCacheTime = DateTime.MinValue;
        }
    }

    public static ExposureEvaluationResult Evaluate(
        PortScope scope,
        string protocol,
        int port,
        string? appPath,
        IReadOnlyList<string>? services,
        AppSettings? settings)
    {
        // 1. Loopback addresses are not filtered by Windows Firewall
        if (scope == PortScope.LocalhostOnly)
        {
            return new ExposureEvaluationResult(
                FirewallExposure.LocalhostUnfiltered,
                MatchingRuleName: null,
                MatchingRuleDetails: "Loopback traffic (127.0.0.1 / ::1) is not filtered by Windows Firewall. Only apps running on this PC can access it.",
                ActiveProfileName: "Loopback",
                IsThirdPartyFirewall: false);
        }

        // 2. Check AppLock's explicit rules
        if (settings != null)
        {
            // AppLock port rules
            if (settings.ListeningPorts?.PortRules != null)
            {
                var matchedPortRule = settings.ListeningPorts.PortRules.FirstOrDefault(r =>
                    string.Equals(r.Protocol, protocol, StringComparison.OrdinalIgnoreCase) &&
                    IsPortMatch(r.LocalPorts, port));

                if (matchedPortRule != null)
                {
                    return new ExposureEvaluationResult(
                        FirewallExposure.BlockedByAppLock,
                        MatchingRuleName: $"AppLock – Block port {matchedPortRule.LocalPorts}/{matchedPortRule.Protocol}",
                        MatchingRuleDetails: "Explicitly blocked by an AppLock Port Rule.",
                        ActiveProfileName: "All Profiles",
                        IsThirdPartyFirewall: false);
                }
            }

            // AppLock app inbound rules
            if (!string.IsNullOrEmpty(appPath) && settings.NetworkRules != null)
            {
                var matchedAppRule = settings.NetworkRules.FirstOrDefault(r =>
                    string.Equals(r.ExePath, appPath, StringComparison.OrdinalIgnoreCase));

                if (matchedAppRule != null && matchedAppRule.BlockInbound)
                {
                    return new ExposureEvaluationResult(
                        FirewallExposure.BlockedByAppLock,
                        MatchingRuleName: $"AppLock – {Path.GetFileName(appPath)} (Inbound)",
                        MatchingRuleDetails: "Explicitly blocked by an AppLock Application Rule.",
                        ActiveProfileName: "All Profiles",
                        IsThirdPartyFirewall: false);
                }
            }
        }

        // 3. Query or use cached Windows Firewall policy
        var policy = GetCachedPolicy();

        if (policy.IsThirdPartyFirewall)
        {
            return new ExposureEvaluationResult(
                FirewallExposure.Unknown,
                MatchingRuleName: null,
                MatchingRuleDetails: "Third-party firewall detected. AppLock cannot reliably determine rule matching.",
                ActiveProfileName: GetProfileName(policy.ActiveProfiles),
                IsThirdPartyFirewall: true);
        }

        if (!policy.IsFirewallEnabled)
        {
            return new ExposureEvaluationResult(
                FirewallExposure.FirewallOff,
                MatchingRuleName: null,
                MatchingRuleDetails: "Windows Firewall is turned off for this network profile. Anyone on your network can connect.",
                ActiveProfileName: GetProfileName(policy.ActiveProfiles),
                IsThirdPartyFirewall: false);
        }

        // 4. Find matching enabled inbound rules
        int targetProto = protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? 6 :
                          protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? 17 : 256;

        CachedRule? matchingBlockRule = null;
        CachedRule? matchingAllowRule = null;

        lock (_syncLock)
        {
            foreach (var rule in policy.InboundRules)
            {
                // Must match current profile
                if ((rule.Profiles & policy.ActiveProfiles) == 0 && rule.Profiles != int.MaxValue && rule.Profiles != 0x7FFFFFFF)
                    continue;

                // Protocol check
                if (rule.Protocol != 256 && rule.Protocol != targetProto)
                    continue;

                // Port check (null/empty or "*" means all ports)
                if (!string.IsNullOrEmpty(rule.LocalPorts) && rule.LocalPorts != "*")
                {
                    if (!IsPortMatch(rule.LocalPorts, port))
                        continue;
                }

                // Application check
                if (!string.IsNullOrEmpty(rule.ApplicationName))
                {
                    string expandedRuleApp = Environment.ExpandEnvironmentVariables(rule.ApplicationName);
                    if (string.IsNullOrEmpty(appPath) || !string.Equals(expandedRuleApp, appPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                // Service check
                if (!string.IsNullOrEmpty(rule.ServiceName))
                {
                    if (services == null || !services.Contains(rule.ServiceName, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                // Rule matches! Block wins over Allow
                if (rule.Action == 0) // Block
                {
                    matchingBlockRule = rule;
                    break; // Block wins immediately
                }
                else if (rule.Action == 1) // Allow
                {
                    matchingAllowRule ??= rule;
                }
            }
        }

        string activeProfName = GetProfileName(policy.ActiveProfiles);

        if (matchingBlockRule != null)
        {
            bool isAppLock = matchingBlockRule.Grouping == "AppLock Connection Firewall";
            return new ExposureEvaluationResult(
                isAppLock ? FirewallExposure.BlockedByAppLock : FirewallExposure.BlockedByDefault,
                MatchingRuleName: matchingBlockRule.Name,
                MatchingRuleDetails: $"Blocked by Windows rule \"{matchingBlockRule.Name}\"",
                ActiveProfileName: activeProfName,
                IsThirdPartyFirewall: false);
        }

        if (matchingAllowRule != null)
        {
            return new ExposureEvaluationResult(
                FirewallExposure.AllowedByRule,
                MatchingRuleName: matchingAllowRule.Name,
                MatchingRuleDetails: $"Allowed by Windows rule \"{matchingAllowRule.Name}\"",
                ActiveProfileName: activeProfName,
                IsThirdPartyFirewall: false);
        }

        // 5. Default inbound policy check
        if (policy.DefaultInboundAction == 0) // Default is Block
        {
            return new ExposureEvaluationResult(
                FirewallExposure.BlockedByDefault,
                MatchingRuleName: "Default inbound action (Block)",
                MatchingRuleDetails: "Blocked by default: No inbound allow rule matched this port or application.",
                ActiveProfileName: activeProfName,
                IsThirdPartyFirewall: false);
        }
        else
        {
            return new ExposureEvaluationResult(
                FirewallExposure.AllowedByRule,
                MatchingRuleName: "Default inbound action (Allow)",
                MatchingRuleDetails: "Allowed by default inbound action of the active network profile.",
                ActiveProfileName: activeProfName,
                IsThirdPartyFirewall: false);
        }
    }

    private static string GetProfileName(int profiles)
    {
        var names = new List<string>();
        if ((profiles & 1) != 0) names.Add("Domain");
        if ((profiles & 2) != 0) names.Add("Private");
        if ((profiles & 4) != 0) names.Add("Public");
        return names.Count > 0 ? string.Join(", ", names) : "Standard";
    }

    private static bool IsPortMatch(string? rulePorts, int port)
    {
        if (string.IsNullOrWhiteSpace(rulePorts) || rulePorts == "*")
            return true;

        var tokens = rulePorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.Contains('-'))
            {
                var parts = token.Split('-', 2);
                if (int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end))
                {
                    if (port >= start && port <= end) return true;
                }
            }
            else if (int.TryParse(token, out int p))
            {
                if (p == port) return true;
            }
        }

        return false;
    }

    private static CachedPolicy GetCachedPolicy()
    {
        lock (_syncLock)
        {
            if (_cachedPolicy != null && (DateTime.UtcNow - _lastCacheTime).TotalSeconds < 30)
                return _cachedPolicy;

            var policy = new CachedPolicy();
            _lastCacheTime = DateTime.UtcNow;

            // Check for third-party firewalls
            try
            {
                policy.IsThirdPartyFirewall = DetectThirdPartyFirewall();
            }
            catch { }

            // Query Windows Firewall via COM
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType != null)
                {
                    dynamic? fw = Activator.CreateInstance(policyType);
                    if (fw != null)
                    {
                        int activeProfiles = 7;
                        try { activeProfiles = fw.CurrentProfileTypes; } catch { }
                        policy.ActiveProfiles = activeProfiles;

                        bool enabled = false;
                        try
                        {
                            if ((activeProfiles & 1) != 0 && fw.FirewallEnabled(1)) enabled = true;
                            if ((activeProfiles & 2) != 0 && fw.FirewallEnabled(2)) enabled = true;
                            if ((activeProfiles & 4) != 0 && fw.FirewallEnabled(4)) enabled = true;
                        }
                        catch
                        {
                            enabled = true;
                        }

                        policy.IsFirewallEnabled = enabled;

                        // Default Inbound Action: 0 = Block, 1 = Allow
                        try
                        {
                            if ((activeProfiles & 4) != 0)
                                policy.DefaultInboundAction = fw.DefaultInboundAction(4);
                            else if ((activeProfiles & 2) != 0)
                                policy.DefaultInboundAction = fw.DefaultInboundAction(2);
                            else if ((activeProfiles & 1) != 0)
                                policy.DefaultInboundAction = fw.DefaultInboundAction(1);
                        }
                        catch
                        {
                            policy.DefaultInboundAction = 0; // standard default is Block
                        }

                        // Read inbound rules
                        dynamic rules = fw.Rules;
                        foreach (dynamic rule in rules)
                        {
                            try
                            {
                                if (!rule.Enabled) continue;
                                if (rule.Direction != 1) continue; // 1 = Inbound

                                policy.InboundRules.Add(new CachedRule
                                {
                                    Name = rule.Name ?? string.Empty,
                                    Grouping = rule.Grouping ?? string.Empty,
                                    Enabled = true,
                                    Direction = 1,
                                    Action = rule.Action,
                                    Protocol = rule.Protocol,
                                    Profiles = rule.Profiles,
                                    LocalPorts = rule.LocalPorts,
                                    ApplicationName = rule.ApplicationName,
                                    ServiceName = rule.ServiceName
                                });
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FirewallExposureEvaluator] Query failed: {ex.Message}");
            }

            return _cachedPolicy = policy;
        }
    }

    private static bool DetectThirdPartyFirewall()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT * FROM FirewallProduct");
            foreach (var item in searcher.Get())
            {
                string? displayName = item["displayName"]?.ToString();
                if (!string.IsNullOrEmpty(displayName) &&
                    !displayName.Contains("Windows Firewall", StringComparison.OrdinalIgnoreCase) &&
                    !displayName.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }
}
