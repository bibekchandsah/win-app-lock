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
        _windowEnumerator = ServiceContainer.WindowEnumerator;
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
    private void LockWindow(RunningWindow window)
    {
        var rule = new LockRule
        {
            Type = LockRuleType.ProcessName,
            ProcessName = window.ProcessName,
            ProcessPath = window.ProcessPath,
            DisplayName = $"{window.ProcessName} - {window.WindowTitle}",
            Icon = window.Icon
        };

        _lockManager.AddRule(rule);
        window.IsLocked = true;
    }

    private void FilterWindows()
    {
        FilteredWindows.Clear();

        var query = SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        // Filter first
        var filtered = string.IsNullOrEmpty(query)
            ? Windows.AsEnumerable()
            : Windows.Where(w =>
                w.ProcessName.ToLowerInvariant().Contains(query) ||
                w.WindowTitle.ToLowerInvariant().Contains(query));

        // Group strategy:
        //   - UWP apps (ApplicationFrameHost host): each window is a DISTINCT app
        //     → group key = window HWND (unique per window)
        //   - Regular desktop apps: group by ProcessName (collapse multiple windows)
        //     → group key = ProcessName
        var groups = filtered
            .GroupBy(w =>
            {
                // If the raw process is ApplicationFrameHost OR the processName was derived
                // from an AUMID (contains no path separators and doesn't end in known exe names),
                // treat each window as its own group using its title as the key.
                bool isUwp = w.ProcessPath != null &&
                    (w.ProcessPath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ||
                     // AUMID stored in ProcessPath looks like "Microsoft.WindowsCalculator_xxx!App"
                     (w.ProcessPath.Contains('_') && w.ProcessPath.Contains('!')));

                if (isUwp)
                {
                    // Use window title as unique key so each UWP app shows separately
                    return string.IsNullOrWhiteSpace(w.WindowTitle)
                        ? w.Handle.ToString()
                        : $"UWP:{w.WindowTitle}";
                }

                return w.ProcessName ?? w.WindowTitle ?? w.Handle.ToString();
            }, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.StartsWith("UWP:", StringComparison.Ordinal)
                ? g.Key.Substring(4)   // Sort UWP apps by title
                : g.Key,
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            // Pick the best representative: prefer windows with a meaningful title
            var representative = group
                .OrderByDescending(w => !string.IsNullOrWhiteSpace(w.WindowTitle))
                .ThenByDescending(w => w.WindowTitle?.Length ?? 0)
                .First();

            int count = group.Count();
            representative.WindowCount = count;

            // Mark locked if ANY window in this process group is locked
            representative.IsLocked = group.Any(w => w.IsLocked);

            FilteredWindows.Add(representative);
        }
    }

    private void UpdateLockStates()
    {
        foreach (var window in Windows)
        {
            window.IsLocked = _lockManager.IsWindowLocked(window);
        }
        // Re-run filter so representative lock states are recalculated
        FilterWindows();
    }
}
