using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AppLock.Models;
using AppLock.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AppLock.ViewModels;

public partial class RunningProgramsViewModel : ObservableObject
{
    private readonly WindowEnumerator _windowEnumerator;
    private readonly LockManager _lockManager;
    private readonly WindowEventMonitor _windowEventMonitor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

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

    [ObservableProperty]
    private bool _autoRefreshEnabled = true;

    public RunningProgramsViewModel(LockManager lockManager)
    {
        _windowEnumerator = ServiceContainer.WindowEnumerator;
        _lockManager = lockManager;
        _windowEventMonitor = ServiceContainer.WindowEventMonitor;
        
        // Capture dispatcher queue at construction time (on UI thread)
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        
        _lockManager.RuleAdded += (s, e) => UpdateLockStates();
        _lockManager.RuleRemoved += (s, e) => UpdateLockStates();
        _lockManager.ProtectionStateChanged += (s, e) => UpdateLockStates();
        
        // Fast-path: title changes update only the affected window in-place (no re-enum)
        _windowEventMonitor.TitleChanged += OnTitleChanged;
        
        // Structural changes (new/closed windows) trigger a full re-enumeration
        _windowEventMonitor.WindowsChanged += OnWindowsChanged;
        
        FilteredWindows.CollectionChanged += (s, e) =>
        {
            // Dispatch out of the CollectionChanged call to avoid re-entrancy in x:Bind generated code
            _dispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                () => RunningCount = FilteredWindows.Count);
        };
        
        RefreshWindows();
        
        // Start monitoring if auto-refresh is enabled
        if (_autoRefreshEnabled)
        {
            _windowEventMonitor.Start();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterWindows();
    }

    partial void OnAutoRefreshEnabledChanged(bool value)
    {
        if (value)
        {
            _windowEventMonitor.Start();
            System.Diagnostics.Debug.WriteLine("Auto-refresh enabled");
        }
        else
        {
            _windowEventMonitor.Stop();
            System.Diagnostics.Debug.WriteLine("Auto-refresh disabled");
        }
    }

    private void OnWindowsChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            // Always refresh on structural changes — don't gate on IsLoading
            // (a concurrent manual refresh will just update the same data twice, which is harmless)
            if (AutoRefreshEnabled)
            {
                System.Diagnostics.Debug.WriteLine("[Auto-refresh] Structural change — refreshing window list");
                RefreshWindows();
            }
        });
    }

    /// <summary>
    /// Handles a targeted title-change notification. Reads the new title directly
    /// from Win32 and updates the matching window in-place — no full re-enumeration.
    /// </summary>
    private void OnTitleChanged(object? sender, WindowTitleChangedEventArgs e)
    {
        if (_dispatcherQueue == null || !AutoRefreshEnabled) return;

        // Read the new title on a background thread (avoids blocking the WinEvent callback)
        string newTitle = GetWindowTitle(e.WindowHandle);
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            // Try exact handle match first
            var match = Windows.FirstOrDefault(w => w.Handle == e.WindowHandle);
            if (match != null)
            {
                if (!string.Equals(match.WindowTitle, newTitle, StringComparison.Ordinal))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TitleChanged] 0x{e.WindowHandle:X}: '{match.WindowTitle}' -> '{newTitle}'");
                    match.WindowTitle = newTitle;
                    // Re-run filter so the displayed title and grouping stay correct
                    FilterWindows();
                }
                return;
            }

            // Handle not in list yet — could be a newly visible window; trigger full refresh
            System.Diagnostics.Debug.WriteLine(
                $"[TitleChanged] 0x{e.WindowHandle:X} not in list — scheduling full refresh");
            RefreshWindows();
        });
    }

    /// <summary>Reads a window title directly from Win32 without enumerating all windows.</summary>
    private static string GetWindowTitle(nint hwnd)
    {
        try
        {
            unsafe
            {
                var h = new HWND(hwnd);
                int len = PInvoke.GetWindowTextLength(h);
                if (len <= 0) return string.Empty;
                var buf = new char[len + 1];
                fixed (char* p = buf)
                {
                    PInvoke.GetWindowText(h, new PWSTR(p), buf.Length);
                }
                return new string(buf).TrimEnd('\0');
            }
        }
        catch { return string.Empty; }
    }

    [RelayCommand]
    private void RefreshWindows()
    {
        IsLoading = true;

        try
        {
            var currentWindows = _windowEnumerator.EnumerateWindows();
            
            // Create a lookup of current windows by handle
            var currentByHandle = currentWindows.ToDictionary(w => w.Handle);
            
            // Track which existing windows we've seen
            var seenHandles = new HashSet<nint>();

            // 1. Update existing windows or remove if they're gone
            for (int i = Windows.Count - 1; i >= 0; i--)
            {
                var existingWindow = Windows[i];
                
                if (currentByHandle.TryGetValue(existingWindow.Handle, out var currentWindow))
                {
                    // Window still exists - update its properties
                    seenHandles.Add(existingWindow.Handle);
                    
                    // ALWAYS update properties - don't skip if they appear the same
                    // (comparison might miss subtle changes or timing issues)
                    existingWindow.WindowTitle = currentWindow.WindowTitle;
                    existingWindow.ProcessName = currentWindow.ProcessName;
                    
                    // Update path and process ID too
                    existingWindow.ProcessPath = currentWindow.ProcessPath;
                    existingWindow.ProcessId = currentWindow.ProcessId;
                    
                    // Update icon if it changed
                    if (currentWindow.Icon != null && currentWindow.Icon != existingWindow.Icon)
                    {
                        existingWindow.Icon = currentWindow.Icon;
                    }
                    
                    existingWindow.IsLocked = _lockManager.IsWindowLocked(existingWindow);
                    existingWindow.HasCustomPassword =
                        _lockManager.GetMatchingRule(existingWindow)?.HasCustomPassword ?? false;
                }
                else
                {
                    // Window no longer exists - remove it
                    Windows.RemoveAt(i);
                }
            }

            // 2. Add new windows that we haven't seen
            foreach (var currentWindow in currentWindows)
            {
                if (!seenHandles.Contains(currentWindow.Handle))
                {
                    currentWindow.IsLocked = _lockManager.IsWindowLocked(currentWindow);
                    currentWindow.HasCustomPassword =
                        _lockManager.GetMatchingRule(currentWindow)?.HasCustomPassword ?? false;
                    Windows.Add(currentWindow);
                    
                    // Load icon asynchronously
                    _ = LoadIconAsync(currentWindow);
                }
            }

            FilterWindows();
            
            System.Diagnostics.Debug.WriteLine($"[RefreshWindows] Updated {seenHandles.Count} windows, added {currentWindows.Count - seenHandles.Count} new windows, total: {Windows.Count}");
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
        window.HasCustomPassword = false; // new rule — no custom password yet
    }

    private void FilterWindows()
    {
        var query = SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        // Filter first
        var filtered = string.IsNullOrEmpty(query)
            ? Windows.AsEnumerable()
            : Windows.Where(w =>
                w.ProcessName.ToLowerInvariant().Contains(query) ||
                w.WindowTitle.ToLowerInvariant().Contains(query));

        // Group strategy
        var groups = filtered
            .GroupBy(w =>
            {
                bool isUwp = w.ProcessPath != null &&
                    (w.ProcessPath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ||
                     (w.ProcessPath.Contains('_') && w.ProcessPath.Contains('!')));

                if (isUwp)
                {
                    return string.IsNullOrWhiteSpace(w.WindowTitle)
                        ? w.Handle.ToString()
                        : $"UWP:{w.WindowTitle}";
                }

                return w.ProcessName ?? w.WindowTitle ?? w.Handle.ToString();
            }, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.StartsWith("UWP:", StringComparison.Ordinal)
                ? g.Key.Substring(4)
                : g.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build list of representatives
        var newItems = new List<RunningWindow>();
        foreach (var group in groups)
        {
            var representative = group
                .OrderByDescending(w => !string.IsNullOrWhiteSpace(w.WindowTitle))
                .ThenByDescending(w => w.WindowTitle?.Length ?? 0)
                .First();

            representative.WindowCount = group.Count();
            representative.IsLocked = group.Any(w => w.IsLocked);
            representative.HasCustomPassword = group.Any(w => w.HasCustomPassword);
            newItems.Add(representative);
        }

        // Update FilteredWindows in-place to minimize UI changes
        // Remove items that are no longer in the new list
        for (int i = FilteredWindows.Count - 1; i >= 0; i--)
        {
            var existing = FilteredWindows[i];
            if (!newItems.Any(n => n.Handle == existing.Handle))
            {
                FilteredWindows.RemoveAt(i);
            }
        }

        // Add new items or move existing ones
        for (int i = 0; i < newItems.Count; i++)
        {
            var newItem = newItems[i];
            
            // Find existing item in FilteredWindows
            int existingIndex = -1;
            for (int j = 0; j < FilteredWindows.Count; j++)
            {
                if (FilteredWindows[j].Handle == newItem.Handle)
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
                    FilteredWindows.Move(existingIndex, i);
                }
                // The object reference is the same, properties already updated
            }
            else
            {
                // New item - insert at correct position
                if (i < FilteredWindows.Count)
                {
                    FilteredWindows.Insert(i, newItem);
                }
                else
                {
                    FilteredWindows.Add(newItem);
                }
            }
        }
    }

    [RelayCommand]
    private void UpdateLockStates()
    {
        foreach (var window in Windows)
        {
            window.IsLocked = _lockManager.IsWindowLocked(window);
            window.HasCustomPassword =
                _lockManager.GetMatchingRule(window)?.HasCustomPassword ?? false;
        }
        // Re-run filter so representative lock states are recalculated
        FilterWindows();
    }
}
