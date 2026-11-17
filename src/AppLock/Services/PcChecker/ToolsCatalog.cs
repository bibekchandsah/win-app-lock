using System.Collections.Generic;

namespace AppLock.Services.PcChecker;

/// <summary>
/// The Tools category is a short list of the Windows tools people actually reach for after reading a report.
/// Each entry is opened through the shell, so nothing is installed and nothing runs elevated.
/// </summary>
public static class ToolsCatalog
{
    public sealed record ToolEntry(string Id, string Label, string Description, string Target, string? Arguments = null);

    public static IReadOnlyList<ToolEntry> All { get; } = new List<ToolEntry>
    {
        new("tool.systeminfo", "System Information", "Microsoft's own full hardware and environment report.", "msinfo32.exe"),
        new("tool.devicemanager", "Device Manager", "Devices, drivers and the problem codes behind them.", "devmgmt.msc"),
        new("tool.reliability", "Reliability Monitor", "The timeline this page's Stability category summarises.", "perfmon.exe", "/rel"),
        new("tool.diskcleanup", "Disk Cleanup", "Remove temporary files and old Windows installations.", "cleanmgr.exe"),
        new("tool.storage", "Storage settings", "See what is using space and turn on Storage Sense.", "ms-settings:storagesense"),
        new("tool.update", "Windows Update", "Check for updates and the latest drivers.", "ms-settings:windowsupdate"),
        new("tool.directx", "DirectX diagnostics", "Full display and audio diagnostics, including driver versions.", "dxdiag.exe"),
        new("tool.network", "Network connections", "Adapters, addresses and adapter properties.", "ncpa.cpl"),
        new("tool.power", "Power options", "Power plans, sleep and fast startup.", "powercfg.cpl"),
        new("tool.taskmanager", "Task Manager", "Startup impact, running processes and performance graphs.", "taskmgr.exe")
    };
}
