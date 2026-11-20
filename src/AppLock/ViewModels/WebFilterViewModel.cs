using System;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.WebFilter;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Header, status and "Check a website" state for the Web Filter page. The settings are the single
/// source of truth; this only formats them for the page and saves when the master switch changes.
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
