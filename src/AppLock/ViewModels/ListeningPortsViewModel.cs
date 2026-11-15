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
using Microsoft.UI.Dispatching;

namespace AppLock.ViewModels;

public partial class ListeningPortsViewModel : ObservableObject, IDisposable
{
    private readonly IListeningPortService _portService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly List<ListeningPortItemViewModel> _allPorts = new();
    private System.Threading.Timer? _autoRefreshTimer;
    private bool _disposed;

    public ObservableCollection<ListeningPortItemViewModel> Ports { get; } = new();

    [ObservableProperty] private int _totalListeningCount;
    [ObservableProperty] private int _reachableCount;
    [ObservableProperty] private int _localhostCount;
    [ObservableProperty] private int _blockedCount;
    [ObservableProperty] private string _lastUpdatedText = "Never";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedFilter = "All"; // All, Reachable, Localhost, TCP, UDP

    public ListeningPortsViewModel(IListeningPortService portService, DispatcherQueue dispatcherQueue)
    {
        _portService = portService;
        _dispatcherQueue = dispatcherQueue;
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

            _dispatcherQueue.TryEnqueue(() =>
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
                _dispatcherQueue.TryEnqueue(() => IsLoading = false);
            }
        }
    }

    /// <summary>
    /// In-place diff update — same pattern as ActiveConnectionViewModel and NetworkScannerViewModel.
    /// Mutates existing rows in place without clearing the collection.
    /// Prevents UI flicker, maintains scroll position, and preserves user focus.
    /// </summary>
    private void ApplySnapshot(IReadOnlyList<ListeningPort> rawList)
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
            }
            else
            {
                _allPorts.RemoveAt(i);
            }
        }

        // 2. Append new items
        foreach (var p in rawList.OrderBy(p => p.Port).ThenBy(p => p.Protocol))
        {
            var key = $"{p.Protocol}:{p.LocalAddress}:{p.Port}:{p.Pid}";
            if (!seenKeys.Contains(key))
            {
                seenKeys.Add(key);
                _allPorts.Add(new ListeningPortItemViewModel(p));
            }
        }

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

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedFilterChanged(string value)
    {
        ApplyFilter();
    }

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
        else if (filter.Equals("TCP", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase));
        }
        else if (filter.Equals("UDP", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase));
        }

        // 2. Search Query
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(p =>
                p.Port.ToString().Contains(q) ||
                p.Protocol.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.AddressAndPort.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.AppName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(q) ||
                (p.AppPath != null && p.AppPath.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                p.ScopeText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var targetList = filtered
            .OrderBy(p => p.Port)
            .ThenBy(p => p.Protocol)
            .ToList();

        // In-place diff update for Ports ObservableCollection (same as ActiveConnectionViewModel)
        // 1. Remove items that no longer match the filter
        for (int i = Ports.Count - 1; i >= 0; i--)
        {
            var existing = Ports[i];
            if (!targetList.Any(t => t.Key == existing.Key))
            {
                Ports.RemoveAt(i);
            }
        }

        // 2. Add or reorder matching items
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
                {
                    Ports.Insert(i, targetItem);
                }
                else
                {
                    Ports.Add(targetItem);
                }
            }
        }
    }

    public void StartAutoRefresh(int intervalSeconds = 5)
    {
        StopAutoRefresh();
        _autoRefreshTimer = new System.Threading.Timer(_ =>
        {
            _dispatcherQueue.TryEnqueue(() =>
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
