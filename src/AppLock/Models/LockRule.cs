using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.Models;

public enum LockRuleType
{
    ProcessName,
    WindowTitle,
    PickedWindow
}

public class LockRule : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LockRuleType Type { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public string WindowTitlePattern { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string DisplayName { get; set; } = string.Empty;

    // Per-app custom password (optional — falls back to admin password when not set)
    public byte[] CustomPasswordHash { get; set; } = Array.Empty<byte>();
    public byte[] CustomPasswordSalt { get; set; } = Array.Empty<byte>();

    // Self-Discipline settings (quota, schedules, idle relock)
    private SelfDisciplineSettings _selfDiscipline = new();
    public SelfDisciplineSettings SelfDiscipline
    {
        get => _selfDiscipline ??= new();
        set => _selfDiscipline = value ?? new();
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasActiveSelfDiscipline =>
        SelfDiscipline != null && (
            SelfDiscipline.QuotaEnabled ||
            (SelfDiscipline.TimeBlocks != null && SelfDiscipline.TimeBlocks.Count > 0) ||
            SelfDiscipline.IdleRelockEnabled
        );

    [System.Text.Json.Serialization.JsonIgnore]
    public string SelfDisciplineSummary
    {
        get
        {
            if (SelfDiscipline == null || !HasActiveSelfDiscipline)
                return string.Empty;

            var parts = new List<string>();
            if (SelfDiscipline.QuotaEnabled)
            {
                parts.Add($"{SelfDiscipline.DailyLimitMinutes} min/day");
            }
            if (SelfDiscipline.TimeBlocks != null && SelfDiscipline.TimeBlocks.Count > 0)
            {
                int count = SelfDiscipline.TimeBlocks.Count;
                parts.Add($"{count} time block{(count > 1 ? "s" : "")}");
            }
            if (SelfDiscipline.IdleRelockEnabled)
            {
                parts.Add($"idle {SelfDiscipline.IdleRelockMinutes} min");
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Call this after modifying SelfDiscipline settings to update the UI.
    /// </summary>
    public void NotifySelfDisciplineChanged()
    {
        OnPropertyChanged(nameof(SelfDiscipline));
        OnPropertyChanged(nameof(HasActiveSelfDiscipline));
        OnPropertyChanged(nameof(SelfDisciplineSummary));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCustomPassword =>
        CustomPasswordHash != null && CustomPasswordHash.Length > 0 &&
        CustomPasswordSalt != null && CustomPasswordSalt.Length > 0;

    /// <summary>
    /// Call this after modifying CustomPasswordHash/CustomPasswordSalt to update the UI.
    /// </summary>
    public void NotifyCustomPasswordChanged()
    {
        OnPropertyChanged(nameof(HasCustomPassword));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon { get; set; }

    /// <summary>
    /// Transient flag set by SelfDisciplineService when this rule is idle-relocked.
    /// Cleared when the app is unlocked again. Not persisted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IdleRelockPending { get; set; }
}
