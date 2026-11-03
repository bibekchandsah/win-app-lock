using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models;
using AppLock.Services.Firewall;

namespace AppLock.ViewModels;

public partial class ActiveConnectionViewModel : ObservableObject
{
    private readonly ActiveConnectionService _connectionService;
    private CancellationTokenSource? _refreshCts;
    private bool _isRefreshing;

    [ObservableProperty]
    private ObservableCollection<ActiveConnection> _connections = new();

    [ObservableProperty]
    private ObservableCollection<ActiveConnection> _filteredConnections = new();

    [ObservableProperty]
    private string _filterQuery = "";

    [ObservableProperty]
    private bool _showOnlyWithConnections = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    public string FilterButtonText => ShowOnlyWithConnections ? "Connected only" : "Show all";

    public ActiveConnectionViewModel()
    {
        _connectionService = new ActiveConnectionService();
    }

    partial void OnFilterQueryChanged(string value)
    {
        FilterConnections();
    }

    partial void OnShowOnlyWithConnectionsChanged(bool value)
    {
        OnPropertyChanged(nameof(FilterButtonText));
        FilterConnections();
    }

    public async Task LoadConnectionsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        StatusText = "Loading connections...";

        try
        {
            var allConnections = await _connectionService.GetActiveConnectionsAsync(ct);
            
            // Create a lookup using GroupBy to handle duplicate keys
            var currentByKey = allConnections
                .GroupBy(c => GetConnectionKey(c))
                .ToDictionary(g => g.Key, g => g.First());
            
            var seenKeys = new HashSet<string>();

            // 1. Update existing connections or remove if they're gone
            for (int i = Connections.Count - 1; i >= 0; i--)
            {
                var existing = Connections[i];
                var existingKey = GetConnectionKey(existing);
                
                if (currentByKey.TryGetValue(existingKey, out var current))
                {
                    // Connection still exists - update its properties in-place
                    seenKeys.Add(existingKey);
                    
                    // Update only mutable properties (Connection is computed, don't update it)
                    existing.State = current.State;
                    existing.LocalAddress = current.LocalAddress;
                    existing.LocalPort = current.LocalPort;
                    existing.RemoteAddress = current.RemoteAddress;
                    existing.RemotePort = current.RemotePort;
                }
                else
                {
                    // Connection no longer exists - remove it
                    Connections.RemoveAt(i);
                }
            }

            // 2. Add new connections that we haven't seen
            foreach (var group in allConnections.GroupBy(c => GetConnectionKey(c)))
            {
                var key = group.Key;
                if (!seenKeys.Contains(key))
                {
                    // Add only the first connection with this key
                    Connections.Add(group.First());
                }
            }

            FilterConnections();
            
            // Group by process to show unique processes
            var grouped = allConnections
                .GroupBy(c => c.ProcessId)
                .Select(g => g.First())
                .ToList();
            
            StatusText = $"{grouped.Count} processes with connections ({allConnections.Count} total)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"LoadConnectionsAsync error: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Creates a unique key for a connection to track it across refreshes.
    /// Uses all connection properties to ensure uniqueness.
    /// </summary>
    private string GetConnectionKey(ActiveConnection conn)
    {
        // Include State to differentiate connections in different states
        return $"{conn.ProcessId}|{conn.Protocol}|{conn.LocalAddress}|{conn.LocalPort}|{conn.RemoteAddress}|{conn.RemotePort}|{conn.State}";
    }

    public void StartAutoRefresh(int intervalSeconds = 3)
    {
        StopAutoRefresh();
        _refreshCts = new CancellationTokenSource();
        _ = AutoRefreshLoop(intervalSeconds, _refreshCts.Token);
    }

    public void StopAutoRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    private async Task AutoRefreshLoop(int intervalSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isRefreshing)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
                if (!ct.IsCancellationRequested)
                {
                    _isRefreshing = true;
                    await LoadConnectionsAsync(ct);
                    _isRefreshing = false;
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Auto-refresh error: {ex.Message}");
                _isRefreshing = false;
            }
        }
    }

    private void FilterConnections()
    {
        var filtered = Connections.AsEnumerable();

        // Filter by search query
        if (!string.IsNullOrWhiteSpace(FilterQuery))
        {
            var query = FilterQuery.ToLowerInvariant();
            filtered = filtered.Where(c =>
                c.ProcessName.ToLowerInvariant().Contains(query) ||
                c.ProcessPath.ToLowerInvariant().Contains(query) ||
                c.LocalAddress.Contains(query) ||
                c.RemoteAddress.Contains(query) ||
                c.Protocol.ToLowerInvariant().Contains(query));
        }

        // Filter: show only with remote connections
        if (ShowOnlyWithConnections)
        {
            filtered = filtered.Where(c => 
                !string.IsNullOrEmpty(c.RemoteAddress) && 
                c.RemoteAddress != "0.0.0.0" &&
                c.State == "ESTABLISHED");
        }

        var newItems = filtered.OrderBy(c => c.ProcessName).ToList();

        // Update FilteredConnections in-place to minimize UI flickering
        // Remove items that are no longer in the new list
        for (int i = FilteredConnections.Count - 1; i >= 0; i--)
        {
            var existing = FilteredConnections[i];
            var existingKey = GetConnectionKey(existing);
            if (!newItems.Any(n => GetConnectionKey(n) == existingKey))
            {
                FilteredConnections.RemoveAt(i);
            }
        }

        // Add new items or move existing ones to maintain order
        for (int i = 0; i < newItems.Count; i++)
        {
            var newItem = newItems[i];
            var newKey = GetConnectionKey(newItem);
            
            // Find existing item in FilteredConnections
            int existingIndex = -1;
            for (int j = 0; j < FilteredConnections.Count; j++)
            {
                if (GetConnectionKey(FilteredConnections[j]) == newKey)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                // Item exists - move to correct position if needed
                if (existingIndex != i)
                {
                    FilteredConnections.Move(existingIndex, i);
                }
                // Properties already updated in-place in the Connections list
            }
            else
            {
                // New item - insert at correct position
                if (i < FilteredConnections.Count)
                {
                    FilteredConnections.Insert(i, newItem);
                }
                else
                {
                    FilteredConnections.Add(newItem);
                }
            }
        }
    }
}
