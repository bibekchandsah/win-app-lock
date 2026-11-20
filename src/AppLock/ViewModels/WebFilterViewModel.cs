using System;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Header and status state for the Web Filter page. The settings are the single source of truth;
/// this only formats them for the page and saves when the master switch changes.
/// </summary>
public class WebFilterViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    public WebFilterViewModel(AppSettings settings, SettingsStore settingsStore)
    {
        _settings = settings;
        _settingsStore = settingsStore;
    }

    public WebFilterSettings Filter => _settings.WebFilter;

    public bool IsEnabled => Filter.Enabled;

    public int EnabledCategoryCount => Filter.Categories.Values.Count(c => c.Enabled);

    public string StatusTitle => Filter.Enabled
        ? (Filter.IsPaused ? "Web Filter is paused" : "Web Filter is On")
        : "Web Filter is Off";

    public string StatusDetail
    {
        get
        {
            if (Filter.IsPaused && Filter.PausedUntilUtc is { } until)
            {
                return $"Paused until {until.ToLocalTime():HH:mm}.";
            }

            if (!Filter.Enabled)
            {
                return "No websites are blocked. The filter isn't set up yet.";
            }

            var mode = Filter.Mode == WebFilterMode.Provider ? "Filtered DNS provider" : "Local filter";
            var categories = EnabledCategoryCount == 1 ? "1 category" : $"{EnabledCategoryCount} categories";
            return $"{mode} \u00b7 {categories}";
        }
    }

    /// <summary>Turns the master switch on or off and persists it.</summary>
    public void SetEnabled(bool enabled)
    {
        if (Filter.Enabled == enabled) return;

        Filter.Enabled = enabled;
        _settingsStore.Save(_settings);

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
    }

    /// <summary>Re-reads the status text after settings changed elsewhere on the page.</summary>
    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(EnabledCategoryCount));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
    }
}
