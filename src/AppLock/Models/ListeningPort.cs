using System;
using System.Collections.Generic;
using System.Net;

namespace AppLock.Models;

public enum PortScope
{
    LocalhostOnly,
    LocalNetwork,
    AllInterfaces
}

public enum FirewallExposure
{
    FirewallOff,
    BlockedByAppLock,
    AllowedByRule,
    BlockedByDefault,
    LocalhostUnfiltered,
    Unknown
}

public enum HintSeverity
{
    None,
    Info,
    Review,
    Important
}

public record PortHint(string Text, HintSeverity Severity);

public record ListeningPort(
    string Protocol,
    IPAddress LocalAddress,
    int Port,
    int Pid,
    string? AppPath,
    string? AppName,
    string? Publisher,
    IReadOnlyList<string> Services,
    PortScope Scope,
    FirewallExposure Exposure,
    string? ServiceLabel,
    PortHint? Hint,
    string? ServiceName = null,
    string? MatchingRuleName = null,
    string? ProcessUser = null,
    DateTime? ProcessStartTime = null,
    string? ActiveProfileName = null
);

public class KnownPort
{
    public string AppPath { get; set; } = string.Empty;
    public string Protocol { get; set; } = "TCP";
    public int Port { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public string AddedDisplay => Added.ToString("yyyy-MM-dd");
}

public class PortRuleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Protocol { get; set; } = "TCP";        // TCP|UDP
    public string LocalPorts { get; set; } = string.Empty; // "8080" or "8000-8100"
    public int Profiles { get; set; } = int.MaxValue;     // NET_FW_PROFILE2_ALL
    public string? Note { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

public class ListeningPortsSettings
{
    public int RefreshSeconds { get; set; } = 5;
    public bool GroupByApp { get; set; } = true;
    public bool ShowSystemServices { get; set; } = true;
    public bool HideUdp { get; set; }
    public bool WatchNewPorts { get; set; }
    public bool WatchIgnoreSystemDynamic { get; set; } = true;
    public string ToastAnchor { get; set; } = "BottomRight";
    public string ToastTheme { get; set; } = "FollowSystem";
    public List<KnownPort> KnownPorts { get; set; } = new();
    public List<PortRuleEntry> PortRules { get; set; } = new();
}
