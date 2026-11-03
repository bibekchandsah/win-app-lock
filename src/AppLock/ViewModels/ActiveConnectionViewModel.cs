using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            
            // Group by process to show unique processes
            var grouped = allConnections
                .GroupBy(c => c.ProcessId)
                .Select(g => g.First())
                .OrderBy(c => c.ProcessName)
                .ToList();

            Connections.Clear();
            foreach (var conn in allConnections)
            {
                Connections.Add(conn);
            }

            FilterConnections();
            
            StatusText = $"{grouped.Count} processes with connections ({allConnections.Count} total)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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

        FilteredConnections = new ObservableCollection<ActiveConnection>(filtered);
    }
}
