using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services.WebFilter;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

public sealed partial class LogRowViewModel : ObservableObject
{
    public string Domain { get; }
    public string Reason { get; }
    public string TimeFormatted { get; }
    public string CountBadge { get; }
    public bool HasCountBadge { get; }

    public LogRowViewModel(WebFilterLogGroup group)
    {
        Domain = group.Domain;
        Reason = group.Reason;
        TimeFormatted = group.LatestTimestampUtc.ToLocalTime().ToString("HH:mm");
        HasCountBadge = group.Count > 1;
        CountBadge = $"×{group.Count}";
    }
}

public sealed partial class TopDomainRowViewModel : ObservableObject
{
    public string Domain { get; }
    public string CountLabel { get; }

    public TopDomainRowViewModel(TopBlockedDomain entry)
    {
        Domain = entry.Domain;
        CountLabel = $"{entry.Count:N0}×";
    }
}

public sealed partial class WebFilterActivityViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly WebFilterLogService _logService;

    [ObservableProperty] private int _blockedToday;
    [ObservableProperty] private int _blockedThisWeek;
    [ObservableProperty] private string _topBlockedCategory = "None";
    [ObservableProperty] private bool _logBlocked;
    [ObservableProperty] private bool _logAllowed;
    [ObservableProperty] private int _logRetentionDays;
    [ObservableProperty] private bool _notifyOnBlock;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<LogRowViewModel> RecentBlocked { get; } = new();
    public ObservableCollection<TopDomainRowViewModel> TopDomains { get; } = new();

    public WebFilterActivityViewModel(AppSettings settings, WebFilterLogService logService)
    {
        _settings = settings;
        _logService = logService;

        // Initialize from settings
        _logBlocked = settings.WebFilter.LogBlocked;
        _logAllowed = settings.WebFilter.LogAllowed;
        _logRetentionDays = settings.WebFilter.LogRetentionDays;
        _notifyOnBlock = settings.WebFilter.NotifyOnBlock;

        logService.LogChanged += (s, e) => _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var stats = await _logService.GetStatsAsync().ConfigureAwait(true);
            BlockedToday = stats.BlockedToday;
            BlockedThisWeek = stats.BlockedThisWeek;
            TopBlockedCategory = stats.TopBlockedCategory;

            var groups = await _logService.GetRecentBlockedGroupsAsync(100).ConfigureAwait(true);
            RecentBlocked.Clear();
            foreach (var g in groups)
            {
                RecentBlocked.Add(new LogRowViewModel(g));
            }

            var top = await _logService.GetTopBlockedDomainsAsync(7, 10).ConfigureAwait(true);
            TopDomains.Clear();
            foreach (var t in top)
            {
                TopDomains.Add(new TopDomainRowViewModel(t));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterActivity] RefreshAsync failed: {ex.Message}");
        }
    }

    partial void OnLogBlockedChanged(bool value)
    {
        _settings.WebFilter.LogBlocked = value;
        SaveSettings();
    }

    partial void OnLogAllowedChanged(bool value)
    {
        _settings.WebFilter.LogAllowed = value;
        SaveSettings();
    }

    partial void OnLogRetentionDaysChanged(int value)
    {
        int clamped = value switch { 7 => 7, 90 => 90, _ => 30 };
        _settings.WebFilter.LogRetentionDays = clamped;
        SaveSettings();
        _ = _logService.PruneOldEntriesAsync();
    }

    partial void OnNotifyOnBlockChanged(bool value)
    {
        _settings.WebFilter.NotifyOnBlock = value;
        SaveSettings();
    }

    public async Task ClearLogAsync()
    {
        await _logService.ClearLogAsync().ConfigureAwait(true);
    }

    private void SaveSettings()
    {
        try { Services.ServiceContainer.SettingsStore.Save(_settings); }
        catch (Exception ex) { Debug.WriteLine($"[WebFilterActivity] SaveSettings failed: {ex.Message}"); }
    }

    public string BlockedTodayLabel => $"{BlockedToday:N0}";
    public string BlockedThisWeekLabel => $"{BlockedThisWeek:N0}";
    public bool HasRecentBlocked => RecentBlocked.Count > 0;
    public bool NoRecentBlocked => RecentBlocked.Count == 0;
    public bool HasTopDomains => TopDomains.Count > 0;
}
