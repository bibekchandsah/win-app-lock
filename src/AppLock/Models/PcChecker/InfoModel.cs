using System;
using System.Collections.Generic;

namespace AppLock.Models.PcChecker;

/// <summary>How much access a value needs: nothing, administrator rights, or a sensor driver.</summary>
public enum InfoTier
{
    Standard = 0,
    Admin = 1,
    Driver = 2
}

/// <summary>How much detail a value belongs to. Basic ⊂ Detailed ⊂ Everything.</summary>
public enum DetailLevel
{
    Basic = 0,
    Detailed = 1,
    Everything = 2
}

/// <summary>Identifiers are hidden by the "Hide identifiers" toggle; secrets are never collected.</summary>
public enum InfoSensitivity
{
    None = 0,
    Identifier = 1,
    Secret = 2
}

public enum InfoStatus
{
    None = 0,
    Ok = 1,
    Info = 2,
    Warning = 3,
    Problem = 4
}

/// <summary>Why a value could not be read, shown instead of a blank or a guess.</summary>
public enum MissingReason
{
    None = 0,
    NeedsAdmin,
    NeedsDriver,
    NotReported,
    Unsupported,
    Error
}

/// <summary>One label/value pair reported by a provider.</summary>
public sealed class InfoItem
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }

    /// <summary>Untransformed value (bytes, milliseconds, raw ids) kept for exports and tests.</summary>
    public string? RawValue { get; set; }

    public InfoSensitivity Sensitivity { get; set; } = InfoSensitivity.None;
    public InfoStatus Status { get; set; } = InfoStatus.None;
    public string? Explain { get; set; }
    public string? Source { get; set; }
    public DetailLevel Level { get; set; } = DetailLevel.Detailed;
    public MissingReason Missing { get; set; } = MissingReason.None;

    public static InfoItem Of(
        string key,
        string label,
        string? value,
        DetailLevel level = DetailLevel.Detailed,
        string? explain = null,
        string? source = null,
        InfoStatus status = InfoStatus.None,
        InfoSensitivity sensitivity = InfoSensitivity.None)
        => new()
        {
            Key = key,
            Label = label,
            Value = value,
            Level = level,
            Explain = explain,
            Source = source,
            Status = status,
            Sensitivity = sensitivity
        };

    /// <summary>A value the PC could not report, with the reason in plain language.</summary>
    public static InfoItem MissingValue(
        string key,
        string label,
        MissingReason reason,
        DetailLevel level = DetailLevel.Detailed,
        string? source = null)
        => new()
        {
            Key = key,
            Label = label,
            Value = null,
            Level = level,
            Missing = reason,
            Source = source,
            Status = InfoStatus.Info
        };

    /// <summary>Plain-language reason text for a missing value.</summary>
    public static string ReasonText(MissingReason reason) => reason switch
    {
        MissingReason.NeedsAdmin => "Needs administrator rights",
        MissingReason.NeedsDriver => "Needs the sensor driver",
        MissingReason.NotReported => "This PC doesn't report this",
        MissingReason.Unsupported => "Not supported on this Windows version",
        MissingReason.Error => "Could not be read",
        _ => "Not available"
    };
}

public sealed class InfoGroup
{
    public string Title { get; set; } = string.Empty;
    public List<InfoItem> Items { get; set; } = new();
}

/// <summary>A simple table (disks, apps, devices…). The renderer draws these as sortable lists.</summary>
public sealed class InfoTable
{
    public string Title { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public List<List<string?>> Rows { get; set; } = new();
}

/// <summary>One result of a health rule (filled in by the health check milestone).</summary>
public sealed class Finding
{
    public string Id { get; set; } = string.Empty;
    public InfoStatus Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
}

/// <summary>Everything one provider collected for one category.</summary>
public sealed class InfoSection
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<InfoGroup> Groups { get; set; } = new();
    public List<InfoTable> Tables { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    public DateTime CollectedAt { get; set; } = DateTime.Now;

    /// <summary>Set when the provider failed; the UI shows it as "Not available (reason)".</summary>
    public string? Error { get; set; }

    public InfoGroup Group(string title)
    {
        var existing = Groups.Find(g => g.Title == title);
        if (existing != null) return existing;

        var group = new InfoGroup { Title = title };
        Groups.Add(group);
        return group;
    }

    public IEnumerable<InfoItem> AllItems()
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                yield return item;
            }
        }
    }

    public InfoItem? Find(string key)
    {
        foreach (var item in AllItems())
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) return item;
        }
        return null;
    }
}
