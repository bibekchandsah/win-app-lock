using System;
using System.Collections.Generic;
using System.Linq;

namespace AppLock.Models;

/// <summary>
/// Screen Time settings. Stored inside the protected settings JSON, so the tamper-resistant settings file
/// remains the source of truth for limits; only the usage history lives in SQLite.
/// </summary>
public class ScreenTimeSettings
{
    public bool TrackingEnabled { get; set; } = true;
    public bool ConsentShown { get; set; }

    /// <summary>Shared idle threshold with Self-Discipline quota counting (30..900 seconds).</summary>
    public int IdlePauseSeconds { get; set; } = 60;

    public bool CountWhileMediaPlaying { get; set; } = true;

    /// <summary>30, 90, 365 or 0 for "keep forever".</summary>
    public int RetentionDays { get; set; } = 90;

    public bool RememberWindowTitles { get; set; }
    public bool LimitWarningsEnabled { get; set; } = true;
    public bool ShowHourlyDetail { get; set; } = true;

    private List<string> _excludedAppKeys = new();
    public List<string> ExcludedAppKeys
    {
        get => _excludedAppKeys ??= new();
        set => _excludedAppKeys = value ?? new();
    }

    private List<string> _hiddenAppKeys = new();
    public List<string> HiddenAppKeys
    {
        get => _hiddenAppKeys ??= new();
        set => _hiddenAppKeys = value ?? new();
    }

    public ScreenTimeSettings Clone() => new()
    {
        TrackingEnabled = TrackingEnabled,
        ConsentShown = ConsentShown,
        IdlePauseSeconds = IdlePauseSeconds,
        CountWhileMediaPlaying = CountWhileMediaPlaying,
        RetentionDays = RetentionDays,
        RememberWindowTitles = RememberWindowTitles,
        LimitWarningsEnabled = LimitWarningsEnabled,
        ShowHourlyDetail = ShowHourlyDetail,
        ExcludedAppKeys = ExcludedAppKeys.ToList(),
        HiddenAppKeys = HiddenAppKeys.ToList()
    };

    /// <summary>Clamps a value read from an older or hand-edited settings file.</summary>
    public int EffectiveIdlePauseSeconds => Math.Clamp(IdlePauseSeconds, 30, 900);
}
