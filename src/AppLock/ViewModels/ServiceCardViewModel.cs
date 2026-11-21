using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.WebFilter;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Backs one service row / toggle (e.g. YouTube, Discord, TikTok).
/// Backed directly by <see cref="WebFilterSettings.Services"/>.
/// </summary>
public sealed partial class ServiceCardViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly WebServiceSource _source;

    public ServiceCardViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        WebServiceSource source,
        string categoryDisplayName)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _source = source;
        CategoryDisplayName = categoryDisplayName;

        if (!_settings.WebFilter.Services.TryGetValue(source.Id, out _))
        {
            _settings.WebFilter.Services[source.Id] = new ServiceRule { Enabled = false };
        }
    }

    public string Id => _source.Id;

    public string Name => _source.Name;

    public string Category => _source.Category;

    public string CategoryDisplayName { get; }

    public string IconGlyph => string.IsNullOrEmpty(_source.Icon) ? "\uE774" : _source.Icon;

    public IReadOnlyList<string> Domains => _source.Domains;

    public string DomainsSummary => _source.Domains.Count == 1
        ? _source.Domains[0]
        : $"{_source.Domains.Count} domains ({string.Join(", ", _source.Domains)})";

    public string DomainsCountLabel => $"{_source.Domains.Count} domains";

    private ServiceRule Rule =>
        _settings.WebFilter.Services.TryGetValue(Id, out var r) ? r : new ServiceRule();

    public bool IsEnabled
    {
        get => Rule.Enabled;
        set
        {
            var rule = _settings.WebFilter.Services.TryGetValue(Id, out var r)
                ? r
                : (_settings.WebFilter.Services[Id] = new ServiceRule());

            if (rule.Enabled == value) return;
            rule.Enabled = value;
            _settingsStore.Save(_settings);
            ServiceContainer.WebFilterService?.UpdatePolicy();

            OnPropertyChanged();
            OnPropertyChanged(nameof(ScheduleLabel));
        }
    }

    public string ScheduleLabel
    {
        get
        {
            var blocks = _settings.WebFilter.Services.TryGetValue(Id, out var r)
                ? r.ActiveBlocks
                : null;

            return (blocks == null || blocks.Count == 0) ? "Always" : FormatBlocks(blocks);
        }
    }

    public List<TimeBlock> GetSchedule()
    {
        return _settings.WebFilter.Services.TryGetValue(Id, out var r)
            ? r.ActiveBlocks
            : new List<TimeBlock>();
    }

    public void SetSchedule(List<TimeBlock> blocks)
    {
        var rule = _settings.WebFilter.Services.TryGetValue(Id, out var r)
            ? r
            : (_settings.WebFilter.Services[Id] = new ServiceRule());

        rule.ActiveBlocks = blocks;
        _settingsStore.Save(_settings);
        ServiceContainer.WebFilterService?.UpdatePolicy();
        OnPropertyChanged(nameof(ScheduleLabel));
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
        bool allWeekdays = days[1] && days[2] && days[3] && days[4] && days[5] && !days[0] && !days[6];
        bool allDays = System.Linq.Enumerable.All(days, d => d);
        if (allDays) return "Every day";
        if (allWeekdays) return "Mon–Fri";
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var on = new List<string>();
        for (int i = 0; i < 7; i++) if (days[i]) on.Add(names[i]);
        return string.Join(", ", on);
    }
}
