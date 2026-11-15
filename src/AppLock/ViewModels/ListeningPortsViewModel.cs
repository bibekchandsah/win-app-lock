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

    public async Task LoadPortsAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var rawList = await _portService.GetListeningPortsAsync(ct);
            var viewModels = rawList
                .OrderBy(p => p.Port)
                .ThenBy(p => p.Protocol)
                .Select(p => new ListeningPortItemViewModel(p))
                .ToList();

            _dispatcherQueue.TryEnqueue(() =>
            {
                _allPorts.Clear();
                _allPorts.AddRange(viewModels);

                TotalListeningCount = _allPorts.Count;
                ReachableCount = _allPorts.Count(p => p.IsReachable);
                LocalhostCount = _allPorts.Count(p => p.Scope == PortScope.LocalhostOnly);
                BlockedCount = _allPorts.Count(p => p.Exposure == FirewallExposure.BlockedByAppLock);
                LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");

                ApplyFilter();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ListeningPortsViewModel] LoadPortsAsync failed: {ex.Message}");
        }
        finally
        {
            _dispatcherQueue.TryEnqueue(() => IsLoading = false);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await LoadPortsAsync();
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

        Ports.Clear();
        foreach (var item in filtered)
        {
            Ports.Add(item);
        }
    }

    public void StartAutoRefresh(int intervalSeconds = 5)
    {
        StopAutoRefresh();
        _autoRefreshTimer = new System.Threading.Timer(_ =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                _ = LoadPortsAsync();
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
