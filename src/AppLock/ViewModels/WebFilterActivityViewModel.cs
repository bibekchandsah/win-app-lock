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
    public DateTime LatestTimestampUtc { get; }
    public int Count { get; }
    public string TimeFormatted { get; }
    public string CountBadge { get; }
    public bool HasCountBadge { get; }

    public LogRowViewModel(WebFilterLogGroup group)
    {
        Domain = group.Domain;
        Reason = group.Reason;
        LatestTimestampUtc = group.LatestTimestampUtc;
        Count = group.Count;
        var localTime = group.LatestTimestampUtc.ToLocalTime();
        TimeFormatted = localTime.Date == DateTime.Today
            ? localTime.ToString("HH:mm")
            : localTime.ToString("MMM dd HH:mm");
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
    private readonly List<LogRowViewModel> _allFilteredRows = new();

    [ObservableProperty] private int _blockedToday;
    [ObservableProperty] private int _blockedThisWeek;
    [ObservableProperty] private string _topBlockedCategory = "None";
    [ObservableProperty] private bool _logBlocked;
    [ObservableProperty] private bool _logAllowed;
    [ObservableProperty] private int _logRetentionDays;
    [ObservableProperty] private bool _notifyOnBlock;
    [ObservableProperty] private bool _isLoading;

    // Filter, Sort & Pagination properties
    [ObservableProperty] private string _dateFilterMode = "today"; // "all", "today", "custom"
    [ObservableProperty] private DateTimeOffset? _selectedDate = DateTimeOffset.Now;
    [ObservableProperty] private int _sortOption = 0; // 0=Newest, 1=Oldest, 2=Most blocked, 3=Domain A-Z, 4=Domain Z-A
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _pageSize = 10;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private int _totalCount = 0;
    [ObservableProperty] private bool _canGoPrevious = false;
    [ObservableProperty] private bool _canGoNext = false;
    [ObservableProperty] private bool _hasPagination = false;
    [ObservableProperty] private string _pageSummary = "Page 1 of 1";

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

            DateTime? startUtc = null;
            DateTime? endUtc = null;

            if (DateFilterMode == "today")
            {
                var today = DateTime.Today;
                startUtc = today.ToUniversalTime();
                endUtc = today.AddDays(1).ToUniversalTime();
            }
            else if (DateFilterMode == "custom" && SelectedDate.HasValue)
            {
                var day = SelectedDate.Value.Date;
                startUtc = day.ToUniversalTime();
                endUtc = day.AddDays(1).ToUniversalTime();
            }

            var groups = await _logService.GetBlockedGroupsAsync(startUtc, endUtc, 2000).ConfigureAwait(true);
            _allFilteredRows.Clear();
            foreach (var g in groups)
            {
                _allFilteredRows.Add(new LogRowViewModel(g));
            }

            ApplySortAndPaging();

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

    public void SetDateFilterMode(string mode, DateTimeOffset? customDate = null)
    {
        DateFilterMode = mode.ToLowerInvariant();
        if (customDate.HasValue)
        {
            SelectedDate = customDate.Value;
        }
        CurrentPage = 1;
        _ = RefreshAsync();
    }

    public void SetSortOption(int sortOption)
    {
        SortOption = sortOption;
        CurrentPage = 1;
        ApplySortAndPaging();
    }

    public void SetPageSize(int size)
    {
        if (size < 1) size = 1;
        PageSize = size;
        CurrentPage = 1;
        ApplySortAndPaging();
    }

    public void NextPage()
    {
        if (CanGoNext)
        {
            CurrentPage++;
            UpdatePagingView();
        }
    }

    public void PreviousPage()
    {
        if (CanGoPrevious)
        {
            CurrentPage--;
            UpdatePagingView();
        }
    }

    private void ApplySortAndPaging()
    {
        IEnumerable<LogRowViewModel> sorted = SortOption switch
        {
            1 => _allFilteredRows.OrderBy(r => r.LatestTimestampUtc),
            2 => _allFilteredRows.OrderByDescending(r => r.Count).ThenByDescending(r => r.LatestTimestampUtc),
            3 => _allFilteredRows.OrderBy(r => r.Domain, StringComparer.OrdinalIgnoreCase),
            4 => _allFilteredRows.OrderByDescending(r => r.Domain, StringComparer.OrdinalIgnoreCase),
            _ => _allFilteredRows.OrderByDescending(r => r.LatestTimestampUtc) // 0: Newest
        };

        var sortedList = sorted.ToList();
        _allFilteredRows.Clear();
        _allFilteredRows.AddRange(sortedList);

        TotalCount = _allFilteredRows.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        UpdatePagingView();
    }

    private void UpdatePagingView()
    {
        CanGoPrevious = CurrentPage > 1;
        CanGoNext = CurrentPage < TotalPages;
        HasPagination = TotalPages > 1;

        if (TotalCount == 0)
        {
            PageSummary = "0 items";
        }
        else
        {
            int startItem = (CurrentPage - 1) * PageSize + 1;
            int endItem = Math.Min(CurrentPage * PageSize, TotalCount);
            PageSummary = $"Showing {startItem}–{endItem} of {TotalCount} (Page {CurrentPage} of {TotalPages})";
        }

        RecentBlocked.Clear();
        var pageItems = _allFilteredRows
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize);

        foreach (var item in pageItems)
        {
            RecentBlocked.Add(item);
        }

        OnPropertyChanged(nameof(HasRecentBlocked));
        OnPropertyChanged(nameof(NoRecentBlocked));
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
