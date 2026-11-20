using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.WebFilter;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Header, status, "Check a website" and Categories tab state for the Web Filter page.
/// </summary>
public partial class WebFilterViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly WebFilterListService _lists;

    public WebFilterViewModel(AppSettings settings, SettingsStore settingsStore, WebFilterListService lists)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _lists = lists;
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
            return $"{mode} \u00b7 {categories} \u00b7 {TotalDomains:N0} domains";
        }
    }

    // ── Pause ─────────────────────────────────────────────────────────────────

    public bool IsPaused => Filter.IsPaused;

    public string PauseLabel => Filter.IsPaused
        ? (Filter.PausedUntilUtc is { } u ? $"Resume (paused until {u.ToLocalTime():HH:mm})" : "Resume")
        : "Pause";

    /// <summary>Pauses filtering for the given number of minutes (or resumes if paused).</summary>
    public void TogglePause(int minutes = 30)
    {
        if (Filter.IsPaused || minutes <= 0)
        {
            Filter.PausedUntilUtc = null;
        }
        else
        {
            Filter.PausedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
        }
        _settingsStore.Save(_settings);
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
    }

    // ── Categories ────────────────────────────────────────────────────────────

    private ObservableCollection<CategoryCardViewModel> _categoryCards = new();
    public ObservableCollection<CategoryCardViewModel> CategoryCards => _categoryCards;

    /// <summary>
    /// Seeds the WebFilterSettings.Categories dictionary from the catalog defaults
    /// (run once on first enable; idempotent thereafter).
    /// </summary>
    public void SeedCategoryDefaults()
    {
        var catalog = _lists.Catalog;
        bool changed = false;

        foreach (var cat in catalog.Categories)
        {
            if (!Filter.Categories.ContainsKey(cat.Id))
            {
                Filter.Categories[cat.Id] = new CategoryRule { Enabled = cat.DefaultEnabled };
                changed = true;
            }
        }

        if (!Filter.DefaultsSeeded)
        {
            Filter.DefaultsSeeded = true;
            changed = true;
        }

        if (changed)
        {
            _settingsStore.Save(_settings);
        }
    }

    /// <summary>Builds the category card view-models from the loaded catalog.</summary>
    public void BuildCategoryCards()
    {
        var catalog = _lists.Catalog;
        _categoryCards.Clear();

        foreach (var cat in catalog.Categories)
        {
            int count = _lists.GetCategoryDomainCount(cat.Id);
            var card = new CategoryCardViewModel(_settings, _settingsStore, cat, count);

            // Mark unavailable if no list loaded and no bundled file
            if (count == 0 && cat.Sources.TrueForAll(s => string.IsNullOrEmpty(s.Bundled)))
            {
                card.MarkUnavailable("List not available");
            }

            _categoryCards.Add(card);
        }
    }

    /// <summary>Updates domain counts after list initialization completes.</summary>
    public void RefreshCategoryDomainCounts()
    {
        foreach (var card in _categoryCards)
        {
            card.SetDomainCount(_lists.GetCategoryDomainCount(card.Id));
        }
    }

    // ── Master switch ─────────────────────────────────────────────────────────

    /// <summary>Turns the master switch on or off and persists it.</summary>
    public void SetEnabled(bool enabled)
    {
        if (Filter.Enabled == enabled) return;

        Filter.Enabled = enabled;
        _settingsStore.Save(_settings);

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
    }

    /// <summary>Re-reads the status text after settings changed elsewhere on the page.</summary>
    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(EnabledCategoryCount));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
    }

    // ── "Check a website" tool ────────────────────────────────────────────────

    [ObservableProperty]
    private string _checkInput = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isListReady;

    [ObservableProperty]
    private string _listStatus = "Block lists are loading\u2026";

    [ObservableProperty]
    private int _availableCategories;

    [ObservableProperty]
    private int _totalDomains;

    public bool CanCheck => IsListReady && !IsChecking && !string.IsNullOrWhiteSpace(CheckInput);

    partial void OnCheckInputChanged(string value) => OnPropertyChanged(nameof(CanCheck));

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    partial void OnIsListReadyChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    partial void OnTotalDomainsChanged(int value) => OnPropertyChanged(nameof(StatusDetail));

    /// <summary>Loads and compiles the bundled lists the first time the page opens.</summary>
    public async Task InitializeListsAsync(CancellationToken cancellationToken = default)
    {
        if (IsListReady) return;

        try
        {
            await _lists.InitializeAsync(cancellationToken).ConfigureAwait(true);

            AvailableCategories = _lists.AvailableCategoryCount;
            TotalDomains = _lists.TotalDomainCount;

            if (_lists.IsReady)
            {
                IsListReady = true;
                ListStatus = $"{AvailableCategories} of {_lists.CategoryCount} categories ready \u00b7 {TotalDomains:N0} domains";
            }
            else
            {
                ListStatus = _lists.LastError ?? "Block lists could not be prepared.";
            }

            if (!string.IsNullOrWhiteSpace(_lists.LastError) && IsListReady)
            {
                ListStatus += " \u00b7 " + _lists.LastError;
            }

            // Build/refresh category cards now that counts are available
            BuildCategoryCards();
            RefreshCategoryDomainCounts();
        }
        catch (OperationCanceledException)
        {
            ListStatus = "Loading block lists was cancelled.";
        }
        catch (Exception ex)
        {
            ListStatus = "Block lists could not be loaded: " + ex.Message;
        }

        OnPropertyChanged(nameof(StatusDetail));
    }

    /// <summary>Runs the check tool. Returns null while another check is in progress.</summary>
    public async Task<WebFilterCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsChecking) return null;

        IsChecking = true;
        try
        {
            return await _lists.CheckAsync(CheckInput, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
    }
}
