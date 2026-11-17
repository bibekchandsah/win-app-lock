using System.Collections.Generic;

namespace AppLock.Models.PcChecker;

/// <summary>
/// Persisted PC Checker preferences. Stored inside AppLock's own settings JSON, so it inherits the
/// same storage and integrity handling as every other section.
/// </summary>
public class PcCheckerSettings
{
    /// <summary>"Basic" | "Detailed" | "Everything".</summary>
    public string DetailLevel { get; set; } = "Detailed";

    public bool HideIdentifiers { get; set; }

    /// <summary>"Windows" (how Explorer shows sizes) | "GB" | "GiB".</summary>
    public string SizeUnits { get; set; } = "Windows";

    /// <summary>"C" or "F".</summary>
    public string TempUnit { get; set; } = "C";

    public double LiveIntervalSeconds { get; set; } = 1.0;

    public int ChartHistorySeconds { get; set; } = 60;

    /// <summary>"Off" | "Weekly" | "Monthly".</summary>
    public string AutoSnapshot { get; set; } = "Off";

    public int SnapshotRetention { get; set; } = 12;

    public bool NotifyOnChanges { get; set; } = true;

    public bool SensorsEnabled { get; set; }

    public int CpuTempWarn { get; set; } = 90;

    public int GpuTempWarn { get; set; } = 90;

    public int DiskTempWarn { get; set; } = 70;

    public bool ExportRedact { get; set; } = true;

    public bool ExportIncludeSoftware { get; set; } = true;

    public bool ExportIncludeEvents { get; set; }

    public List<string> DismissedFindings { get; set; } = new();
}
