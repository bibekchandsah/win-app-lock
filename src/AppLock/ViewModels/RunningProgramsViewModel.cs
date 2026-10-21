using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AppLock.Models;
using AppLock.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppLock.ViewModels;

public partial class RunningProgramsViewModel : ObservableObject
{
    private readonly WindowEnumerator _windowEnumerator;
    private readonly LockManager _lockManager;

    [ObservableProperty]
    private ObservableCollection<RunningWindow> _windows = new();

    [ObservableProperty]
    private ObservableCollection<RunningWindow> _filteredWindows = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _runningCount;

    public RunningProgramsViewModel(LockManager lockManager)
    {
        _windowEnumerator = new WindowEnumerator();
        _lockManager = lockManager;
        
        _lockManager.RuleAdded += (s, e) => UpdateLockStates();
        _lockManager.RuleRemoved += (s, e) => UpdateLockStates();
        _lockManager.ProtectionStateChanged += (s, e) => UpdateLockStates();
        
        FilteredWindows.CollectionChanged += (s, e) => RunningCount = FilteredWindows.Count;
        
        RefreshWindows();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterWindows();
    }

    [RelayCommand]
    private void RefreshWindows()
    {
        IsLoading = true;

        try
        {
            var windows = _windowEnumerator.EnumerateWindows();
            
            Windows.Clear();
            foreach (var window in windows)
            {
                window.IsLocked = _lockManager.IsWindowLocked(window);
                Windows.Add(window);
                
                // Load icon asynchronously
                _ = LoadIconAsync(window);
            }

            FilterWindows();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadIconAsync(RunningWindow window)
    {
        await Task.Run(() =>
        {
            // Icon loading is handled in WindowEnumerator
            // This is a placeholder for future async icon loading
        });
    }

    [RelayCommand]
    private void PickWindow()
    {
        // TODO: Implement window picker in Milestone 6
    }

    [RelayCommand]
    private void LockWindow(RunningWindow window)
    {
        var rule = new LockRule
        {
            Type = LockRuleType.ProcessName,
            ProcessName = window.ProcessName,
            DisplayName = $"{window.ProcessName} - {window.WindowTitle}"
        };

        _lockManager.AddRule(rule);
        window.IsLocked = true;
    }

    private void FilterWindows()
    {
        FilteredWindows.Clear();

        var query = SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        var filtered = string.IsNullOrEmpty(query)
            ? Windows
            : Windows.Where(w =>
                w.ProcessName.ToLowerInvariant().Contains(query) ||
                w.WindowTitle.ToLowerInvariant().Contains(query));

        foreach (var window in filtered)
        {
            FilteredWindows.Add(window);
        }
    }

    private void UpdateLockStates()
    {
        foreach (var window in Windows)
        {
            window.IsLocked = _lockManager.IsWindowLocked(window);
        }
    }
}
