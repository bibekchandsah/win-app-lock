using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

public partial class ListeningPortGroupViewModel : ObservableObject
{
    public string Key { get; }

    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _displayAppName = string.Empty;
    [ObservableProperty] private string? _appPath;
    [ObservableProperty] private string? _publisher;
    [ObservableProperty] private bool _hasPublisher;
    [ObservableProperty] private string _pidText = string.Empty;
    [ObservableProperty] private string _portCountText = string.Empty;
    [ObservableProperty] private string _defaultProcessGlyph = "\uE74C";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _hasMultiplePorts;

    public ObservableCollection<ListeningPortItemViewModel> Ports { get; } = new();

    public ListeningPortGroupViewModel(string key)
    {
        Key = key;
    }

    public void UpdateGroup(IEnumerable<ListeningPortItemViewModel> items)
    {
        var itemList = items.OrderBy(p => p.Port).ThenBy(p => p.Protocol).ToList();
        var first = itemList.FirstOrDefault();
        if (first == null) return;

        AppName = first.AppName;
        DisplayAppName = first.DisplayAppName;
        AppPath = first.AppPath;
        Publisher = first.Publisher;
        HasPublisher = !string.IsNullOrEmpty(first.Publisher);
        DefaultProcessGlyph = first.DefaultProcessGlyph;

        var pids = itemList.Select(p => p.Pid).Distinct().OrderBy(p => p).ToList();
        PidText = pids.Count == 1 ? $"PID {pids[0]}" : $"PIDs: {string.Join(", ", pids)}";

        int count = itemList.Count;
        PortCountText = $"{count} {(count == 1 ? "port" : "ports")}";
        HasMultiplePorts = count > 1;

        // In-place diff update for Ports ObservableCollection
        for (int i = Ports.Count - 1; i >= 0; i--)
        {
            var existing = Ports[i];
            if (!itemList.Any(t => t.Key == existing.Key))
            {
                Ports.RemoveAt(i);
            }
        }

        for (int i = 0; i < itemList.Count; i++)
        {
            var target = itemList[i];
            int existingIndex = -1;
            for (int j = 0; j < Ports.Count; j++)
            {
                if (Ports[j].Key == target.Key)
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
                    Ports.Insert(i, target);
                else
                    Ports.Add(target);
            }
        }
    }
}
