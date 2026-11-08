namespace AppLock.Models;

/// <summary>Status information for the Windows Scheduled Task responsible for BitLocker auto-unlock.</summary>
public sealed class StartupTaskInfo
{
    public bool Exists { get; set; }
    public string State { get; set; } = "Not created";
    public string LastRunTime { get; set; } = "Never";
    public string LastResult { get; set; } = "N/A";
    public string LastResultDisplay => LastResult switch
    {
        "0" or "0x0" => "0 (Successful)",
        "1" or "0x1" => "1 (Failed / Error)",
        "2" or "0x2" => "2 (File not found)",
        "267009" or "0x41301" => "Running",
        "267011" or "0x41303" => "Never run",
        "267014" or "0x41306" => "Terminated by user",
        "-2147024891" or "2147942405" => "Access denied",
        "N/A" or "" or null => "Never run",
        _ => LastResult
    };
    public string RegisteredExePath { get; set; } = string.Empty;
    public bool ExePathMatches { get; set; } = true;
    public bool NeedsRepair => Exists && !ExePathMatches;
}

