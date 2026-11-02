namespace AppLock.Models;

/// <summary>Status information for the Windows Scheduled Task responsible for BitLocker auto-unlock.</summary>
public sealed class StartupTaskInfo
{
    public bool Exists { get; set; }
    public string State { get; set; } = "Not created";
    public string LastRunTime { get; set; } = "Never";
    public string LastResult { get; set; } = "N/A";
    public string RegisteredExePath { get; set; } = string.Empty;
    public bool ExePathMatches { get; set; } = true;
    public bool NeedsRepair => Exists && !ExePathMatches;
}

