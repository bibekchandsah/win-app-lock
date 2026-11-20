using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services.WebFilter;

public sealed class DoHServerEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Ips { get; set; } = new();
}

/// <summary>
/// Manages outbound firewall rules for anti-bypass protection in the "AppLock Web Filter" group.
/// Blocks port 53 / 853 to non-filter/non-upstream addresses and blocks known public DoH resolvers on port 443.
/// </summary>
public sealed class WebFilterFirewallService
{
    public const string GroupName = "AppLock Web Filter";
    public const string DescriptionPrefix = "Managed by AppLock Web Filter";

    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_PROFILE2_ALL = 0x7;
    private const int NET_FW_IP_PROTOCOL_TCP = 6;
    private const int NET_FW_IP_PROTOCOL_UDP = 17;

    public bool IsElevated()
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

    /// <summary>
    /// Applies active bypass protection firewall rules based on the user's settings.
    /// </summary>
    public async Task ApplyRulesAsync(WebFilterSettings settings, IEnumerable<string>? allowedUpstreamIps = null)
    {
        if (!IsElevated())
        {
            Debug.WriteLine("[WebFilterFirewall] Not elevated; cannot apply firewall rules.");
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return;

                dynamic rules = policy.Rules;

                // 1. Remove any previous AppLock Web Filter rules first
                RemoveRulesInternal(rules);
                settings.Applied.FirewallRuleNames.Clear();

                // 2. Block other DNS servers (ports 53 and 853) if enabled
                if (settings.Bypass.BlockOtherDns)
                {
                    var excludedIps = new List<IPAddress>();
                    if (allowedUpstreamIps != null)
                    {
                        foreach (var s in allowedUpstreamIps)
                        {
                            if (IPAddress.TryParse(s, out var ip))
                            {
                                excludedIps.Add(ip);
                            }
                        }
                    }

                    string complementRanges = ComputeIpv4ComplementRanges(excludedIps);

                    // Outbound UDP 53
                    string ruleNameUdp53 = $"{GroupName} - Block DNS (UDP 53)";
                    CreateBlockRule(rules, ruleNameUdp53, NET_FW_IP_PROTOCOL_UDP, "53", complementRanges);
                    settings.Applied.FirewallRuleNames.Add(ruleNameUdp53);

                    // Outbound TCP 53
                    string ruleNameTcp53 = $"{GroupName} - Block DNS (TCP 53)";
                    CreateBlockRule(rules, ruleNameTcp53, NET_FW_IP_PROTOCOL_TCP, "53", complementRanges);
                    settings.Applied.FirewallRuleNames.Add(ruleNameTcp53);

                    // Outbound TCP 853 (DNS over TLS)
                    string ruleNameTcp853 = $"{GroupName} - Block DoT (TCP 853)";
                    CreateBlockRule(rules, ruleNameTcp853, NET_FW_IP_PROTOCOL_TCP, "853", complementRanges);
                    settings.Applied.FirewallRuleNames.Add(ruleNameTcp853);
                }

                // 3. Block known public DoH servers (port 443 TCP/UDP) if enabled
                if (settings.Bypass.BlockKnownDoh)
                {
                    var dohIpsToBlock = GetDoHIpsToBlock(settings);
                    if (dohIpsToBlock.Count > 0)
                    {
                        string remoteAddrs = string.Join(",", dohIpsToBlock);

                        // Outbound TCP 443 to DoH resolvers
                        string ruleNameTcp443 = $"{GroupName} - Block Public DoH (TCP 443)";
                        CreateBlockRule(rules, ruleNameTcp443, NET_FW_IP_PROTOCOL_TCP, "443", remoteAddrs);
                        settings.Applied.FirewallRuleNames.Add(ruleNameTcp443);

                        // Outbound UDP 443 (QUIC / HTTP/3) to DoH resolvers
                        string ruleNameUdp443 = $"{GroupName} - Block Public DoH (UDP 443)";
                        CreateBlockRule(rules, ruleNameUdp443, NET_FW_IP_PROTOCOL_UDP, "443", remoteAddrs);
                        settings.Applied.FirewallRuleNames.Add(ruleNameUdp443);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebFilterFirewall] ApplyRulesAsync error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Removes all firewall rules in the "AppLock Web Filter" group.
    /// </summary>
    public async Task RemoveRulesAsync(WebFilterSettings? settings = null)
    {
        if (!IsElevated()) return;

        await Task.Run(() =>
        {
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return;

                dynamic? policy = Activator.CreateInstance(policyType);
                if (policy == null) return;

                dynamic rules = policy.Rules;
                RemoveRulesInternal(rules);

                if (settings != null)
                {
                    settings.Applied.FirewallRuleNames.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebFilterFirewall] RemoveRulesAsync error: {ex.Message}");
            }
        });
    }

    private static void RemoveRulesInternal(dynamic rules)
    {
        var rulesToRemove = new List<string>();
        foreach (dynamic r in rules)
        {
            try
            {
                string rGroup = r.Grouping ?? string.Empty;
                string rName = r.Name ?? string.Empty;
                if (rGroup == GroupName || rName.StartsWith(GroupName, StringComparison.OrdinalIgnoreCase))
                {
                    rulesToRemove.Add(rName);
                }
            }
            catch { }
        }

        foreach (var name in rulesToRemove)
        {
            try
            {
                rules.Remove(name);
                Debug.WriteLine($"[WebFilterFirewall] Removed rule: {name}");
            }
            catch { }
        }
    }

    private static void CreateBlockRule(
        dynamic rules,
        string ruleName,
        int protocol,
        string remotePort,
        string remoteAddresses)
    {
        Type? ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
        if (ruleType == null) return;

        dynamic? rule = Activator.CreateInstance(ruleType);
        if (rule == null) return;

        rule.Name = ruleName;
        rule.Description = $"{DescriptionPrefix}: {ruleName}";
        rule.Grouping = GroupName;
        rule.Direction = NET_FW_RULE_DIR_OUT;
        rule.Action = NET_FW_ACTION_BLOCK;
        rule.Enabled = true;
        rule.Profiles = NET_FW_PROFILE2_ALL;
        rule.Protocol = protocol;
        rule.RemotePorts = remotePort;
        if (!string.IsNullOrWhiteSpace(remoteAddresses))
        {
            rule.RemoteAddresses = remoteAddresses;
        }

        rules.Add(rule);
        Debug.WriteLine($"[WebFilterFirewall] Created rule: {ruleName}");
    }

    /// <summary>
    /// Computes IPv4 complement ranges excluding loopback (127.0.0.0/8) and any allowed upstream IPs.
    /// </summary>
    public static string ComputeIpv4ComplementRanges(IEnumerable<IPAddress>? excludedIps)
    {
        // Excluded intervals: [StartUint, EndUint]
        var intervals = new List<(uint Start, uint End)>
        {
            // Always exclude 127.0.0.0/8
            (IpToUint(IPAddress.Parse("127.0.0.0")), IpToUint(IPAddress.Parse("127.255.255.255")))
        };

        if (excludedIps != null)
        {
            foreach (var ip in excludedIps)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    uint val = IpToUint(ip);
                    intervals.Add((val, val));
                }
            }
        }

        // Sort intervals by start
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Merge overlapping/adjacent intervals
        var merged = new List<(uint Start, uint End)>();
        foreach (var iv in intervals)
        {
            if (merged.Count == 0)
            {
                merged.Add(iv);
            }
            else
            {
                var last = merged[^1];
                if (iv.Start <= last.End + 1)
                {
                    merged[^1] = (last.Start, Math.Max(last.End, iv.End));
                }
                else
                {
                    merged.Add(iv);
                }
            }
        }

        // Generate complement ranges covering [0.0.0.0, 255.255.255.255]
        var ranges = new List<string>();
        uint current = 0;

        foreach (var (start, end) in merged)
        {
            if (start > current)
            {
                ranges.Add($"{UintToIp(current)}-{UintToIp(start - 1)}");
            }

            if (end == uint.MaxValue)
            {
                current = uint.MaxValue;
                break;
            }
            current = end + 1;
        }

        if (current < uint.MaxValue)
        {
            ranges.Add($"{UintToIp(current)}-255.255.255.255");
        }
        else if (current == uint.MaxValue && (merged.Count == 0 || merged[^1].End < uint.MaxValue))
        {
            ranges.Add("255.255.255.255");
        }

        return string.Join(",", ranges);
    }

    /// <summary>
    /// Loads public DoH server IPs from doh-servers.json and excludes the currently chosen upstream resolver.
    /// </summary>
    public static List<string> GetDoHIpsToBlock(WebFilterSettings settings, string? jsonPath = null)
    {
        var result = new List<string>();
        string path = jsonPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "web-filter", "doh-servers.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "web-filter", "doh-servers.json");
        }
        if (!File.Exists(path)) return result;

        try
        {
            string json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<DoHServerEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (entries == null) return result;

            string excludedProvider = settings.Upstream switch
            {
                UpstreamKind.CloudflareDoh => "cloudflare",
                UpstreamKind.GoogleDoh => "google",
                UpstreamKind.Quad9Doh => "quad9",
                _ => string.Empty
            };

            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(excludedProvider) &&
                    string.Equals(entry.Id, excludedProvider, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Skip currently chosen upstream DoH resolver
                }

                result.AddRange(entry.Ips);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterFirewall] GetDoHIpsToBlock error: {ex.Message}");
        }

        return result;
    }

    private static uint IpToUint(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UintToIp(uint val)
    {
        return new IPAddress(new byte[]
        {
            (byte)((val >> 24) & 0xFF),
            (byte)((val >> 16) & 0xFF),
            (byte)((val >> 8) & 0xFF),
            (byte)(val & 0xFF)
        });
    }
}
