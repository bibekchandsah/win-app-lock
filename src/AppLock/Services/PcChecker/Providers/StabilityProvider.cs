using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Stability: Windows reliability records plus the system event log entries that matter — blue screens
/// (BugCheck 1001), unexpected shutdowns (Kernel-Power 41) and hardware errors (WHEA-Logger).
/// </summary>
public sealed class StabilityProvider : IInfoProvider
{
    public string Id => "stability";
    public string Category => "stability";
    public InfoTier Tier => InfoTier.Admin;
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(5);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Stability & events", Category = Category };
        var since = DateTime.Now.AddDays(-30);

        var reliability = Wmi.Query("SELECT TimeGenerated, SourceName, ProductName, EventIdentifier, Message FROM Win32_ReliabilityRecords");
        var recentRecords = new List<(DateTime When, string Source, string Message)>();

        foreach (var row in reliability)
        {
            var when = row.Date("TimeGenerated");
            if (when is null || when < since) continue;
            recentRecords.Add((when.Value, row.Str("SourceName") ?? "Windows",
                Trim(row.Str("Message") ?? string.Empty, 160)));
        }

        recentRecords = recentRecords.OrderByDescending(r => r.When).ToList();

        var summary = section.Group("Last 30 days");
        summary.Items.Add(InfoItem.Of("stability.records", "Reliability records", recentRecords.Count.ToString(), DetailLevel.Basic,
            "Events Windows' own reliability monitor recorded in the last 30 days.",
            "Source: WMI Win32_ReliabilityRecords"));

        var bugChecks = ReadEventLog("BugCheck", "SELECT TimeGenerated, EventCode, InsertionStrings, Message FROM Win32_NTLogEvent " +
                                                 "WHERE Logfile='System' AND EventCode=1001", out var bugCheckError, ct);
        var powerLoss = ReadEventLog("Unexpected shutdown", "SELECT TimeGenerated, EventCode, Message FROM Win32_NTLogEvent " +
                                                             "WHERE Logfile='System' AND EventCode=41", out var powerError, ct);
        var hardware = ReadEventLog("Hardware error (WHEA)", "SELECT TimeGenerated, EventCode, Message FROM Win32_NTLogEvent " +
                                                             "WHERE Logfile='System' AND SourceName='Microsoft-Windows-WHEA-Logger'", out var wheaError, ct);

        AddEventSummary(summary, "stability.bugchecks", "Blue screens", bugChecks, bugCheckError, ct);
        AddEventSummary(summary, "stability.powerLoss", "Unexpected shutdowns", powerLoss, powerError, ct);
        AddEventSummary(summary, "stability.hardware", "Hardware errors (WHEA)", hardware, wheaError, ct);

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.5);

        var crashGroup = section.Group("Blue screens and unexpected shutdowns");
        var crashes = bugChecks.Concat(powerLoss).OrderByDescending(e => e.When).Take(15).ToList();

        if (crashes.Count == 0)
        {
            var noneItem = InfoItem.Of("stability.none", "Crashes", "None found", DetailLevel.Basic,
                "No blue screens or unexpected shutdowns are in the system log.", "Source: Windows event log",
                InfoStatus.Ok);
            crashGroup.Items.Add(noneItem);
        }

        foreach (var crash in crashes)
        {
            var label = crash.BugCheckCode != null
                ? $"Blue screen {BugCheckTable.Format(crash.BugCheckCode)}"
                : crash.Title;

            crashGroup.Items.Add(InfoItem.Of($"stability.crash.{crash.When.Ticks}", label,
                $"{PcFormat.DateTimeText(crash.When)} · {crash.Detail}", DetailLevel.Basic,
                crash.BugCheckCode != null
                    ? "A stop error (blue screen). The code names the subsystem that failed; the faulting driver is in the dump."
                    : "Windows did not shut down cleanly. Power loss, a hang or a hardware fault can cause this.",
                "Source: Windows event log (System)", InfoStatus.Problem));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.75);

        var hardwareGroup = section.Group("Hardware errors");
        if (hardware.Count == 0)
        {
            hardwareGroup.Items.Add(InfoItem.Of("stability.whea.none", "Hardware errors", "None found", DetailLevel.Basic,
                "No WHEA hardware errors are recorded. WHEA reports CPU, memory, PCIe and cache faults.",
                "Source: Windows event log (System)", InfoStatus.Ok));
        }

        foreach (var error in hardware.OrderByDescending(e => e.When).Take(10))
        {
            hardwareGroup.Items.Add(InfoItem.Of($"stability.whea.{error.When.Ticks}", "Hardware error",
                $"{PcFormat.DateTimeText(error.When)} · {error.Detail}", DetailLevel.Basic,
                "A hardware error Windows corrected or could not correct. Repeated entries point at failing hardware.",
                "Source: Windows event log (System)", InfoStatus.Warning));
        }

        var timelineGroup = section.Group("Reliability timeline");
        if (recentRecords.Count == 0)
        {
            timelineGroup.Items.Add(InfoItem.Of("stability.timeline.none", "Recent events", "Nothing recorded", DetailLevel.Detailed,
                "No reliability events in the last 30 days.", "Source: WMI Win32_ReliabilityRecords", InfoStatus.Ok));
        }

        foreach (var record in recentRecords.Take(20))
        {
            timelineGroup.Items.Add(InfoItem.Of($"stability.record.{record.When.Ticks}", PcFormat.DateTimeText(record.When),
                $"{record.Source} · {record.Message}", DetailLevel.Detailed,
                "One entry from Windows' reliability history.", "Source: WMI Win32_ReliabilityRecords"));
        }

        progress?.Report(1);
        return section;
    }

    private sealed record LogEvent(DateTime When, string Title, string Detail, string? BugCheckCode);

    private static void AddEventSummary(InfoGroup summary, string key, string label, List<LogEvent> events, string? error,
        CancellationToken ct)
    {
        var item = InfoItem.Of(key, label, events.Count.ToString(), DetailLevel.Basic,
            "Counted from the Windows system event log for the last 30 days.", "Source: Windows event log (System)");
        if (events.Count > 0) item.Status = label.Contains("Hardware", StringComparison.OrdinalIgnoreCase) ? InfoStatus.Warning : InfoStatus.Problem;
        if (error != null && events.Count == 0)
        {
            item.Value = "Needs administrator rights";
            item.Missing = ElevationHelper.IsElevated ? MissingReason.NotReported : MissingReason.NeedsAdmin;
            item.Status = InfoStatus.None;
        }
        summary.Items.Add(item);
        ct.ThrowIfCancellationRequested();
    }

    private static List<LogEvent> ReadEventLog(string title, string wql, out string? error, CancellationToken ct)
    {
        error = null;
        var events = new List<LogEvent>();
        var rows = Wmi.Query(wql);

        if (rows.Count == 0)
        {
            error = "no rows";
            return events;
        }

        var since = DateTime.Now.AddDays(-30);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var when = row.Date("TimeGenerated");
            if (when is null || when < since) continue;

            var detail = Trim(CleanMessage(row.Str("Message")), 200);
            string? bugCheck = null;

            if (title == "BugCheck")
            {
                var insertion = row.Str("InsertionStrings");
                bugCheck = ExtractBugCheckCode(insertion) ?? ExtractBugCheckCode(row.Str("Message"));
            }

            events.Add(new LogEvent(when.Value, title, string.IsNullOrWhiteSpace(detail) ? title : detail, bugCheck));
        }

        return events;
    }

    private static string? ExtractBugCheckCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // BugCheck 1001 often starts with "The computer has rebooted from a bugcheck. The bugcheck was: 0x0000009f ..."
        var marker = text.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        while (marker >= 0 && marker + 10 <= text.Length)
        {
            var candidate = text.Substring(marker + 2, 8);
            if (candidate.All(Uri.IsHexDigit)) return candidate;
            marker = text.IndexOf("0x", marker + 2, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static string CleanMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var single = message.Replace("\r", " ").Replace("\n", " ");
        while (single.Contains("  ", StringComparison.Ordinal)) single = single.Replace("  ", " ");
        return single.Trim();
    }

    private static string Trim(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
