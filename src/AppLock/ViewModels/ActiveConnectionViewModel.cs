using System;
using System.Collections.Generic;
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
    private ObservableCollection<ActiveConnectionGroupViewModel> _groupedConnections = new();

    [ObservableProperty]
    private string _filterQuery = "";

    [ObservableProperty]
    private bool _showOnlyEstablished = true;

    [ObservableProperty]
    private bool _hideLocalConnections = true;

    [ObservableProperty]
    private int _localConnectionsHiddenCount = 0;

    [ObservableProperty]
    private bool _showInternet = true;

    [ObservableProperty]
    private bool _showLocalNetwork = true;

    [ObservableProperty]
    private bool _groupByApp = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    // Backwards-compatible alias for existing code
    public bool ShowOnlyWithConnections
    {
        get => ShowOnlyEstablished;
        set => ShowOnlyEstablished = value;
    }

    public string FilterButtonText => ShowOnlyEstablished ? "Established only" : "All states";

    public bool IsScopeAll => ShowInternet && ShowLocalNetwork;
    public bool IsScopeInternetOnly => ShowInternet && !ShowLocalNetwork;
    public bool IsScopeLocalNetworkOnly => !ShowInternet && ShowLocalNetwork;

    public ActiveConnectionViewModel()
    {
        _connectionService = new ActiveConnectionService();
    }

    partial void OnFilterQueryChanged(string value) => FilterConnections();

    partial void OnShowOnlyEstablishedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOnlyWithConnections));
        OnPropertyChanged(nameof(FilterButtonText));
        FilterConnections();
    }

    partial void OnHideLocalConnectionsChanged(bool value) => FilterConnections();

    partial void OnShowInternetChanged(bool value)
    {
        NotifyScopeProperties();
        FilterConnections();
    }

    partial void OnShowLocalNetworkChanged(bool value)
    {
        NotifyScopeProperties();
        FilterConnections();
    }

    partial void OnGroupByAppChanged(bool value) => FilterConnections();

    private void NotifyScopeProperties()
    {
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeInternetOnly));
        OnPropertyChanged(nameof(IsScopeLocalNetworkOnly));
    }

    public void SetScopeFilter(string scope)
    {
        switch (scope.ToLowerInvariant())
        {
            case "internet":
                ShowInternet = true;
                ShowLocalNetwork = false;
                break;
            case "localnetwork":
            case "lan":
                ShowInternet = false;
                ShowLocalNetwork = true;
                break;
            case "all":
            default:
                ShowInternet = true;
                ShowLocalNetwork = true;
                break;
        }
    }

    public async Task LoadConnectionsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        if (string.IsNullOrEmpty(StatusText))
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
                    seenKeys.Add(existingKey);
                    
                    existing.State = current.State;
                    existing.LocalAddress = current.LocalAddress;
                    existing.LocalPort = current.LocalPort;
                    existing.RemoteAddress = current.RemoteAddress;
                    existing.RemotePort = current.RemotePort;
                    existing.IsSystemProcess = current.IsSystemProcess;
                    existing.CanBlock = current.CanBlock;
                }
                else
                {
                    Connections.RemoveAt(i);
                }
            }

            // 2. Add new connections
            foreach (var group in allConnections.GroupBy(c => GetConnectionKey(c)))
            {
                var key = group.Key;
                if (!seenKeys.Contains(key))
                {
                    Connections.Add(group.First());
                }
            }

            FilterConnections();
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

    private static string GetConnectionKey(ActiveConnection conn)
    {
        return $"{conn.ProcessId}|{conn.Protocol}|{conn.LocalAddress}|{conn.LocalPort}|{conn.RemoteAddress}|{conn.RemotePort}";
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

    public void FilterConnections()
    {
        var rawList = Connections.ToList();
        LocalConnectionsHiddenCount = rawList.Count(c => c.IsLocal);

        var filtered = rawList.AsEnumerable();

        // 1. Established only filter
        if (ShowOnlyEstablished)
        {
            filtered = filtered.Where(c => c.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase));
        }

        // 2. Hide local (loopback) filter
        if (HideLocalConnections)
        {
            filtered = filtered.Where(c => !c.IsLocal);
        }

        // 3. Scope filter chips: Internet only · Local network · All
        if (ShowInternet && !ShowLocalNetwork)
        {
            filtered = filtered.Where(c => c.IsInternet);
        }
        else if (!ShowInternet && ShowLocalNetwork)
        {
            filtered = filtered.Where(c => c.IsPrivateNetwork);
        }
        else if (ShowInternet && ShowLocalNetwork)
        {
            filtered = filtered.Where(c => c.IsInternet || c.IsPrivateNetwork || (!HideLocalConnections && c.IsLocal));
        }

        // 4. Search query filter
        if (!string.IsNullOrWhiteSpace(FilterQuery))
        {
            var query = FilterQuery.Trim().ToLowerInvariant();
            filtered = filtered.Where(c =>
                c.ProcessName.ToLowerInvariant().Contains(query) ||
                c.ProcessPath.ToLowerInvariant().Contains(query) ||
                c.LocalAddress.Contains(query) ||
                c.LocalPort.ToString().Contains(query) ||
                c.RemoteAddress.Contains(query) ||
                c.RemotePort.ToString().Contains(query) ||
                c.Protocol.ToLowerInvariant().Contains(query) ||
                c.State.ToLowerInvariant().Contains(query));
        }

        // Stable sort for flat list
        var newFlatItems = filtered
            .OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ProcessId)
            .ThenBy(c => c.Protocol)
            .ThenBy(c => c.LocalPort)
            .ThenBy(c => c.RemoteAddress)
            .ThenBy(c => c.RemotePort)
            .ToList();

        // In-place diff update for FilteredConnections
        for (int i = FilteredConnections.Count - 1; i >= 0; i--)
        {
            var existing = FilteredConnections[i];
            var existingKey = GetConnectionKey(existing);
            if (!newFlatItems.Any(n => GetConnectionKey(n) == existingKey))
            {
                FilteredConnections.RemoveAt(i);
            }
        }

        for (int i = 0; i < newFlatItems.Count; i++)
        {
            var newItem = newFlatItems[i];
            var newKey = GetConnectionKey(newItem);

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
                if (existingIndex != i)
                {
                    FilteredConnections.Move(existingIndex, i);
                }
            }
            else
            {
                if (i < FilteredConnections.Count)
                    FilteredConnections.Insert(i, newItem);
                else
                    FilteredConnections.Add(newItem);
            }
        }

        // In-place diff update for GroupedConnections
        var groupedGroups = newFlatItems
            .GroupBy(c => $"{c.ProcessName.ToLowerInvariant()}|{c.ProcessPath.ToLowerInvariant()}")
            .Select(g => new
            {
                Key = g.Key,
                Items = g.ToList()
            })
            .OrderBy(g => g.Items.First().ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(g => g.Items.Count)
            .ToList();

        for (int i = GroupedConnections.Count - 1; i >= 0; i--)
        {
            var existingGroup = GroupedConnections[i];
            if (!groupedGroups.Any(g => g.Key == existingGroup.Key))
            {
                GroupedConnections.RemoveAt(i);
            }
        }

        for (int i = 0; i < groupedGroups.Count; i++)
        {
            var targetGroup = groupedGroups[i];
            var existingGroup = GroupedConnections.FirstOrDefault(g => g.Key == targetGroup.Key);

            if (existingGroup != null)
            {
                existingGroup.UpdateGroup(targetGroup.Items);
                int currentIndex = GroupedConnections.IndexOf(existingGroup);
                if (currentIndex != i)
                {
                    GroupedConnections.Move(currentIndex, i);
                }
            }
            else
            {
                var newGroup = new ActiveConnectionGroupViewModel(targetGroup.Key);
                newGroup.UpdateGroup(targetGroup.Items);
                if (i < GroupedConnections.Count)
                    GroupedConnections.Insert(i, newGroup);
                else
                    GroupedConnections.Add(newGroup);
            }
        }

        // Accurate summary line: "33 apps with connections · 162 total · 86 local hidden"
        int visibleApps = GroupedConnections.Count;
        int total = rawList.Count;
        int localHidden = HideLocalConnections ? LocalConnectionsHiddenCount : 0;

        if (localHidden > 0 && HideLocalConnections)
        {
            StatusText = $"{visibleApps} apps with connections · {total} total · {localHidden} local hidden";
        }
        else
        {
            StatusText = $"{visibleApps} apps with connections · {total} total";
        }
    }
}
