using System;

namespace AppLock.Models;

public class NetworkRuleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public string? PackageFamilyName { get; set; }
    public string? ServiceName { get; set; }
    public bool BlockOutbound { get; set; } = true;
    public bool BlockInbound { get; set; } = true;
    public int Profiles { get; set; } = int.MaxValue;
    public bool Blocked { get; set; } = true;
    public DateTime? BlockedUntil { get; set; }
    public string? Notes { get; set; }
    public string? CustomRuleName { get; set; }
    
    // Import/Export UI property (not persisted)
    public string? ImportStatus { get; set; }
}
