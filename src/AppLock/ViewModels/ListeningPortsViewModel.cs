using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services.Firewall;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
#if !APPLOCK_TESTS
using Microsoft.UI.Dispatching;
#endif

namespace AppLock.ViewModels;

public enum ListeningPortSortOrder
{
    Port,
    App,
    Scope,
    Pid
}

#if APPLOCK_TESTS
public class DispatcherQueue
{
    public bool TryEnqueue(Action a) { a(); return true; }
}
#endif

public partial class ListeningPortsViewModel : ObservableObject, IDisposable
{
    private readonly IListeningPortService _portService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly List<ListeningPortItemViewModel> _allPorts = new();
    private System.Threading.Timer? _autoRefreshTimer;
    private bool _isFirstSnapshot = true;
    private bool _disposed;

    public Func<AppSettings?>? SettingsProvider { get; set; }
    public Action<AppSettings>? SettingsSaver { get; set; }

    public ObservableCollection<ListeningPortItemViewModel> Ports { get; } = new();
    public ObservableCollection<ListeningPortGroupViewModel> GroupedPorts { get; } = new();

    [ObservableProperty] private int _totalListeningCount;
    [ObservableProperty] private int _reachableCount;
    [ObservableProperty] private int _localhostCount;
    [ObservableProperty] private int _blockedCount;
    [ObservableProperty] private string _lastUpdatedText = "Never";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedFilter = "All"; // All, Reachable, Localhost, Blocked, TCP, UDP, IPv4, IPv6
    [ObservableProperty] private bool _groupByApp = true;
    [ObservableProperty] private bool _showSystemServices = true;
    [ObservableProperty] private bool _hideUdp = false;
    [ObservableProperty] private ListeningPortSortOrder _sortBy = ListeningPortSortOrder.Port;
    [ObservableProperty] private bool _hasItems;

    public event Action? VisibilityOrFilterUpdated;

    public ListeningPortsViewModel(IListeningPortService portService, DispatcherQueue? dispatcherQueue = null)
    {
        _portService = portService;
        _dispatcherQueue = dispatcherQueue;
    }

    public void LoadSettings()
    {
        var s = SettingsProvider?.Invoke()?.ListeningPorts;
        if (s != null)
        {
            GroupByApp = s.GroupByApp;
            ShowSystemServices = s.ShowSystemServices;
            HideUdp = s.HideUdp;
        }
    }

    private void SaveSettings()
    {
        var s = SettingsProvider?.Invoke();
        if (s != null)
        {
            s.ListeningPorts.GroupByApp = GroupByApp;
            s.ListeningPorts.ShowSystemServices = ShowSystemServices;
            s.ListeningPorts.HideUdp = HideUdp;
            SettingsSaver?.Invoke(s);
        }
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
        else
        {
            action();
        }
    }

    public async Task LoadPortsAsync(bool isManualOrInitial = false, CancellationToken ct = default)
    {
        if (isManualOrInitial || _allPorts.Count == 0)
        {
            IsLoading = true;
        }

        try
        {
            var rawList = await _portService.GetListeningPortsAsync(ct);

            RunOnDispatcher(() =>
            {
                ApplySnapshot(rawList);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ListeningPortsViewModel] LoadPortsAsync failed: {ex.Message}");
        }
        finally
        {
            if (isManualOrInitial || IsLoading)
            {
                RunOnDispatcher(() => IsLoading = false);
            }
        }
    }

    /// <summary>
    /// In-place diff update — preserves scroll position, expand/collapse state, and eliminates UI flicker.
    /// Distinguishes brand new ports with a "New" badge for 60 seconds.
    /// </summary>
    public void ApplySnapshot(IReadOnlyList<ListeningPort> rawList)
    {
        var freshByKey = rawList
            .GroupBy(p => $"{p.Protocol}:{p.LocalAddress}:{p.Port}:{p.Pid}")
            .ToDictionary(g => g.Key, g => g.First());

        var seenKeys = new HashSet<string>();

        // 1. In-place update existing items or remove disappeared ones
        for (int i = _allPorts.Count - 1; i >= 0; i--)
        {
            var existing = _allPorts[i];
            if (freshByKey.TryGetValue(existing.Key, out var fresh))
            {
                seenKeys.Add(existing.Key);
                existing.UpdateFrom(fresh);
                existing.RefreshIsNew();
            }
            else
            {
                _allPorts.RemoveAt(i);
            }
        }

        // 2. Append new items
        bool markAsNew = !_isFirstSnapshot;
        foreach (var p in rawList.OrderBy(p => p.Port).ThenBy(p => p.Protocol))
        {
            var key = $"{p.Protocol}:{p.LocalAddress}:{p.Port}:{p.Pid}";
            if (!seenKeys.Contains(key))
            {
                seenKeys.Add(key);
                _allPorts.Add(new ListeningPortItemViewModel(p, isNewlyDiscovered: markAsNew));
            }
        }

        _isFirstSnapshot = false;

        TotalListeningCount = _allPorts.Count;
        ReachableCount = _allPorts.Count(p => p.IsReachable);
        LocalhostCount = _allPorts.Count(p => p.Scope == PortScope.LocalhostOnly);
        BlockedCount = _allPorts.Count(p => p.Exposure == FirewallExposure.BlockedByAppLock);
        LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");

        ApplyFilter();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await LoadPortsAsync(isManualOrInitial: true);
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    partial void OnGroupByAppChanged(bool value)
    {
        SaveSettings();
        VisibilityOrFilterUpdated?.Invoke();
    }

    partial void OnShowSystemServicesChanged(bool value)
    {
        SaveSettings();
        ApplyFilter();
    }

    partial void OnHideUdpChanged(bool value)
    {
        SaveSettings();
        ApplyFilter();
    }

    partial void OnSortByChanged(ListeningPortSortOrder value) => ApplyFilter();

    public void ApplyFilter()
    {
        var q = SearchQuery?.Trim() ?? string.Empty;
        var filter = SelectedFilter;

        var filtered = _allPorts.AsEnumerable();

        // 1. Filter Chip
        if (filter.Equals("Reachable", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.IsReachable);
        }
        else if (filter.Equals("Localhost", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.Scope == PortScope.LocalhostOnly);
        }
        else if (filter.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.IsBlocked);
        }
        else if (filter.Equals("TCP", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase));
        }
        else if (filter.Equals("UDP", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase));
        }
        else if (filter.Equals("IPv4", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.IsIPv4);
        }
        else if (filter.Equals("IPv6", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.IsIPv6);
        }

        // 2. Options / Toggles
        if (HideUdp)
        {
            filtered = filtered.Where(p => !p.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase));
        }

        if (!ShowSystemServices)
        {
            filtered = filtered.Where(p => !p.IsSystemOrService);
        }

        // 3. Search Query
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(p =>
                p.Port.ToString().Contains(q) ||
                p.Protocol.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.AddressAndPort.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.AppName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(q) ||
                (p.ServiceName != null && p.ServiceName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (p.ServiceLabel != null && p.ServiceLabel.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (p.Publisher != null && p.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (p.HintText != null && p.HintText.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (p.AppPath != null && p.AppPath.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                p.ScopeText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        // 4. Stable Sorting
        IOrderedEnumerable<ListeningPortItemViewModel> sorted = SortBy switch
        {
            ListeningPortSortOrder.App => filtered.OrderBy(p => p.DisplayAppName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Port),
            ListeningPortSortOrder.Scope => filtered.OrderByDescending(p => (int)p.Scope).ThenBy(p => p.Port),
            ListeningPortSortOrder.Pid => filtered.OrderBy(p => p.Pid).ThenBy(p => p.Port),
            _ => filtered.OrderBy(p => p.Port).ThenBy(p => p.Protocol)
        };

        var targetList = sorted.ToList();
        HasItems = targetList.Count > 0;

        // 5. In-place diff update for flat Ports list
        for (int i = Ports.Count - 1; i >= 0; i--)
        {
            var existing = Ports[i];
            if (!targetList.Any(t => t.Key == existing.Key))
            {
                Ports.RemoveAt(i);
            }
        }

        for (int i = 0; i < targetList.Count; i++)
        {
            var targetItem = targetList[i];
            int existingIndex = -1;
            for (int j = 0; j < Ports.Count; j++)
            {
                if (Ports[j].Key == targetItem.Key)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                if (existingIndex != i)
                {
                    Ports.Move(existingIndex, i);
                }
            }
            else
            {
                if (i < Ports.Count)
                    Ports.Insert(i, targetItem);
                else
                    Ports.Add(targetItem);
            }
        }

        // 6. In-place diff update for GroupedPorts
        string GetGroupKey(ListeningPortItemViewModel p)
        {
            if (!string.IsNullOrEmpty(p.AppPath))
                return p.AppPath.ToLowerInvariant();
            if (!string.IsNullOrEmpty(p.AppName))
                return p.AppName.ToLowerInvariant();
            return $"pid:{p.Pid}";
        }

        var groups = targetList.GroupBy(GetGroupKey).ToList();

        IEnumerable<IGrouping<string, ListeningPortItemViewModel>> orderedGroups = SortBy switch
        {
            ListeningPortSortOrder.App => groups.OrderBy(g => g.First().DisplayAppName, StringComparer.OrdinalIgnoreCase),
            ListeningPortSortOrder.Scope => groups.OrderByDescending(g => g.Max(p => (int)p.Scope)).ThenBy(g => g.Min(p => p.Port)),
            ListeningPortSortOrder.Pid => groups.OrderBy(g => g.First().Pid),
            _ => groups.OrderBy(g => g.Min(p => p.Port)).ThenBy(g => g.First().AppName)
        };
        var orderedGroupList = orderedGroups.ToList();

        for (int i = GroupedPorts.Count - 1; i >= 0; i--)
        {
            var existing = GroupedPorts[i];
            if (!orderedGroupList.Any(g => g.Key == existing.Key))
            {
                GroupedPorts.RemoveAt(i);
            }
        }

        for (int i = 0; i < orderedGroupList.Count; i++)
        {
            var groupData = orderedGroupList[i];
            var existingGroup = GroupedPorts.FirstOrDefault(g => g.Key == groupData.Key);
            if (existingGroup != null)
            {
                existingGroup.UpdateGroup(groupData);
                int curIdx = GroupedPorts.IndexOf(existingGroup);
                if (curIdx != i)
                {
                    GroupedPorts.Move(curIdx, i);
                }
            }
            else
            {
                var newGroup = new ListeningPortGroupViewModel(groupData.Key);
                newGroup.UpdateGroup(groupData);
                if (i < GroupedPorts.Count)
                    GroupedPorts.Insert(i, newGroup);
                else
                    GroupedPorts.Add(newGroup);
            }
        }

        VisibilityOrFilterUpdated?.Invoke();
    }

    public void StartAutoRefresh(int intervalSeconds = 5)
    {
        StopAutoRefresh();
        _autoRefreshTimer = new System.Threading.Timer(_ =>
        {
            RunOnDispatcher(() =>
            {
                _ = LoadPortsAsync(isManualOrInitial: false);
            });
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
    }

    public void StopAutoRefresh()
    {
        _autoRefreshTimer?.Dispose();
        _autoRefreshTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAutoRefresh();
    }
}
