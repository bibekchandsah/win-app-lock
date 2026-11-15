using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models;

namespace AppLock.ViewModels;

public partial class ActiveConnectionGroupViewModel : ObservableObject
{
    public string Key { get; }

    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private string _processPath = string.Empty;
    [ObservableProperty] private int _processId;
    [ObservableProperty] private string _pidText = string.Empty;
    [ObservableProperty] private bool _isSystemProcess;
    [ObservableProperty] private bool _canBlock = true;
    [ObservableProperty] private string _connectionCountText = string.Empty;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string _defaultProcessGlyph = "\uE74C";

    public ObservableCollection<ActiveConnection> Connections { get; } = new();

    public ActiveConnection? RepresentativeConnection => Connections.FirstOrDefault();

    public ActiveConnectionGroupViewModel(string key)
    {
        Key = key;
    }

    public void UpdateGroup(IEnumerable<ActiveConnection> items)
    {
        var itemList = items.OrderBy(c => c.Protocol)
                            .ThenBy(c => c.LocalPort)
                            .ThenBy(c => c.RemoteAddress)
                            .ThenBy(c => c.RemotePort)
                            .ToList();

        var first = itemList.FirstOrDefault();
        if (first == null) return;

        ProcessName = first.ProcessName;
        ProcessPath = first.ProcessPath;
        ProcessId = first.ProcessId;
        IsSystemProcess = first.IsSystemProcess;
        CanBlock = first.CanBlock;

        var pids = itemList.Select(c => c.ProcessId).Distinct().OrderBy(p => p).ToList();
        PidText = pids.Count == 1 ? $"PID {pids[0]}" : $"PIDs: {string.Join(", ", pids)}";

        int count = itemList.Count;
        ConnectionCountText = $"{count} connection{(count == 1 ? "" : "s")}";

        // In-place diff update for child connections
        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            var existing = Connections[i];
            var existingKey = GetItemKey(existing);
            if (!itemList.Any(t => GetItemKey(t) == existingKey))
            {
                Connections.RemoveAt(i);
            }
        }

        for (int i = 0; i < itemList.Count; i++)
        {
            var target = itemList[i];
            var targetKey = GetItemKey(target);

            int existingIndex = -1;
            for (int j = 0; j < Connections.Count; j++)
            {
                if (GetItemKey(Connections[j]) == targetKey)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existingItem = Connections[existingIndex];
                existingItem.State = target.State;
                existingItem.LocalAddress = target.LocalAddress;
                existingItem.LocalPort = target.LocalPort;
                existingItem.RemoteAddress = target.RemoteAddress;
                existingItem.RemotePort = target.RemotePort;
                existingItem.CanBlock = target.CanBlock;
                existingItem.IsSystemProcess = target.IsSystemProcess;

                if (existingIndex != i)
                {
                    Connections.Move(existingIndex, i);
                }
            }
            else
            {
                if (i < Connections.Count)
                    Connections.Insert(i, target);
                else
                    Connections.Add(target);
            }
        }

        OnPropertyChanged(nameof(RepresentativeConnection));
    }

    private static string GetItemKey(ActiveConnection c)
    {
        return $"{c.ProcessId}|{c.Protocol}|{c.LocalAddress}:{c.LocalPort}|{c.RemoteAddress}:{c.RemotePort}";
    }
}
