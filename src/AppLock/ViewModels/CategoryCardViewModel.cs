using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.WebFilter;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Backs one category card in the Categories tab.
/// The toggle state is the single source of truth (CategoryRule inside WebFilterSettings).
/// Domain count is provided by the list service after initialization.
/// </summary>
public sealed partial class CategoryCardViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly WebFilterCategorySource _source;

    public CategoryCardViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        WebFilterCategorySource source,
        int domainCount)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _source = source;
        DomainCount = domainCount;

        // Ensure there is a rule entry for this category.
        if (!_settings.WebFilter.Categories.TryGetValue(source.Id, out _))
        {
            _settings.WebFilter.Categories[source.Id] = new CategoryRule { Enabled = source.DefaultEnabled };
        }
    }

    // ── Catalog data (read-only) ──────────────────────────────────────────────

    public string Id => _source.Id;

    public string Name => _source.Name;

    public string Description => _source.Description;

    /// <summary>Short plain statement of what this category does not catch.</summary>
    public string Limits => _source.Limits;

    /// <summary>Domain count loaded from the list service; 0 until lists are ready.</summary>
    public int DomainCount { get; private set; }

    public string DomainCountLabel => DomainCount > 0
        ? $"{DomainCount:N0} domains"
        : string.Empty;

    // ── Category icon (FontIcon glyph from Segoe MDL2) ───────────────────────

    public string IconGlyph => Id switch
    {
        "adult"     => "\uE77B", // People
        "gambling"  => "\uE7BF", // Money
        "social"    => "\uE716", // Chat
        "malware"   => "\uE72E", // Lock
        "bypass"    => "\uE774", // Network
        "streaming" => "\uE786", // Video
        "dating"    => "\uEB51", // Heart
        "games"     => "\uE7FC", // GameController
        "piracy"    => "\uE7A5", // Blocked
        "drugs"     => "\uEBE8", // Warning / flag
        "ads"       => "\uECA7", // ClearSelection / filter
        _           => "\uE9CE"  // Filter generic
    };

    // ── Availability (list loaded?) ───────────────────────────────────────────

    [ObservableProperty]
    private bool _isAvailable = true;

    [ObservableProperty]
    private string _unavailableReason = string.Empty;

    public void MarkUnavailable(string reason)
    {
        IsAvailable = false;
        UnavailableReason = reason;
        OnPropertyChanged(nameof(UnavailableVisible));
        OnPropertyChanged(nameof(AvailableVisible));
        // Force toggle off when list is gone
        if (Rule.Enabled)
        {
            Rule.Enabled = false;
            _settingsStore.Save(_settings);
        }
        OnPropertyChanged(nameof(IsEnabled));
    }

    public void SetDomainCount(int count)
    {
        DomainCount = count;
        OnPropertyChanged(nameof(DomainCount));
        OnPropertyChanged(nameof(DomainCountLabel));
        IsAvailable = count > 0 || _source.Sources.TrueForAll(s => string.IsNullOrEmpty(s.Url));
        OnPropertyChanged(nameof(UnavailableVisible));
        OnPropertyChanged(nameof(AvailableVisible));
    }

    /// <summary>Visibility for the domain count label (shown when available).</summary>
    public Microsoft.UI.Xaml.Visibility AvailableVisible =>
        IsAvailable ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Visibility for the unavailable reason label.</summary>
    public Microsoft.UI.Xaml.Visibility UnavailableVisible =>
        IsAvailable ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    // ── Toggle (backed by settings) ───────────────────────────────────────────

    private CategoryRule Rule =>
        _settings.WebFilter.Categories.TryGetValue(Id, out var r) ? r : new CategoryRule();

    public bool IsEnabled
    {
        get => Rule.Enabled;
        set
        {
            var rule = _settings.WebFilter.Categories.TryGetValue(Id, out var r)
                ? r
                : (_settings.WebFilter.Categories[Id] = new CategoryRule());

            if (rule.Enabled == value) return;
            rule.Enabled = value;
            _settingsStore.Save(_settings);

            OnPropertyChanged();
            OnPropertyChanged(nameof(ScheduleLabel));
            FirstEnableHintVisible = value && !_hintSeen;
            if (value) _hintSeen = true;
        }
    }

    // ── First-enable hint ─────────────────────────────────────────────────────

    private bool _hintSeen;

    [ObservableProperty]
    private bool _firstEnableHintVisible;

    public void DismissHint() => FirstEnableHintVisible = false;

    // ── Schedule label ────────────────────────────────────────────────────────

    public string ScheduleLabel
    {
        get
        {
            var blocks = _settings.WebFilter.Categories.TryGetValue(Id, out var r)
                ? r.ActiveBlocks
                : null;

            return (blocks == null || blocks.Count == 0) ? "Always" : FormatBlocks(blocks);
        }
    }

    private static string FormatBlocks(IReadOnlyList<TimeBlock> blocks)
    {
        if (blocks.Count == 0) return "Always";
        var first = blocks[0];
        string days = FormatDays(first.Days);
        return $"{days} {first.Start:HH:mm}–{first.End:HH:mm}";
    }

    private static string FormatDays(bool[]? days)
    {
        if (days == null || days.Length < 7) return "Every day";
        string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        bool allWeekdays = days[1] && days[2] && days[3] && days[4] && days[5] && !days[0] && !days[6];
        bool allDays = System.Linq.Enumerable.All(days, d => d);
        if (allDays) return "Every day";
        if (allWeekdays) return "Mon–Fri";
        var on = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 7; i++) if (days[i]) on.Add(names[i]);
        return string.Join(", ", on);
    }
}
