using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Services.Firewall;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.ViewModels;

public partial class InstalledAppViewModel : ObservableObject
{
    private readonly InstalledApp _app;
    private bool _iconLoadStarted = false;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    [ObservableProperty]
    private BitmapImage? _icon;

    public InstalledAppViewModel(InstalledApp app)
    {
        _app = app;
        _icon = app.Icon;
        
        // Capture dispatcher queue
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    }

    public async Task LoadIconAsync()
    {
        if (_iconLoadStarted) return; // Prevent duplicate loading
        _iconLoadStarted = true;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[IconLoad] Starting for: {_app.DisplayName}");
            
            if (_app.IsUwp)
            {
                System.Diagnostics.Debug.WriteLine($"[IconLoad] Skipping UWP app: {_app.DisplayName}");
                return; // Skip UWP apps for now
            }

            if (string.IsNullOrEmpty(_app.ExecutablePath))
            {
                System.Diagnostics.Debug.WriteLine($"[IconLoad] No executable path for: {_app.DisplayName}");
                return;
            }

            if (!System.IO.File.Exists(_app.ExecutablePath))
            {
                System.Diagnostics.Debug.WriteLine($"[IconLoad] File not found: {_app.ExecutablePath}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[IconLoad] Extracting icon from: {_app.ExecutablePath}");
            
            // ExtractIcon needs to run on UI thread for BitmapImage operations
            if (_dispatcherQueue != null)
            {
                var tcs = new TaskCompletionSource<bool>();
                
                _dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var icon = ServiceContainer.WindowEnumerator.ExtractIcon(_app.ExecutablePath);
                        if (icon != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[IconLoad] Icon extracted successfully for: {_app.DisplayName}");
                            Icon = icon;
                            _app.Icon = icon;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[IconLoad] Icon extraction returned null for: {_app.DisplayName}");
                        }
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[IconLoad] ExtractIcon failed: {ex.Message}");
                        tcs.SetException(ex);
                    }
                });
                
                await tcs.Task;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IconLoad] Exception for {_app.DisplayName}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[IconLoad] Stack trace: {ex.StackTrace}");
        }
    }

    public string DisplayName => _app.DisplayName;
    public string PathOrPackage => _app.IsUwp ? $"Package: {_app.PackageFamilyName}" : _app.ExecutablePath ?? "";
    public string Publisher => _app.Publisher ?? "";
    public Visibility HasPublisher => !string.IsNullOrEmpty(_app.Publisher) ? Visibility.Visible : Visibility.Collapsed;
    public bool IsUwp => _app.IsUwp;
    public Visibility IsUwpVisible => _app.IsUwp ? Visibility.Visible : Visibility.Collapsed;
    public InstalledApp App => _app;
}

public partial class InstalledAppsDialogViewModel : ObservableObject
{
    private readonly InstalledAppsService _installedAppsService;
    private readonly HashSet<string> _existingPaths;
    private List<InstalledAppViewModel> _allApps = new();
    private HashSet<InstalledAppViewModel> _iconsLoaded = new();

    [ObservableProperty]
    private ObservableCollection<InstalledAppViewModel> _filteredApps = new();

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasSelection;

    private List<InstalledAppViewModel> _selectedItems = new();

    public IReadOnlyList<InstalledApp> SelectedApps => _selectedItems.Select(vm => vm.App).ToList();

    public InstalledAppsDialogViewModel(InstalledAppsService installedAppsService, HashSet<string> existingPaths)
    {
        _installedAppsService = installedAppsService;
        _existingPaths = existingPaths;
    }

    partial void OnSearchQueryChanged(string value)
    {
        FilterApps();
    }

    public async Task LoadAppsAsync()
    {
        IsLoading = true;
        StatusText = "Loading installed applications...";

        try
        {
            var apps = await _installedAppsService.GetInstalledAppsAsync();
            
            // Filter out already-added apps
            var newApps = apps.Where(a =>
            {
                var key = a.IsUwp ? a.PackageFamilyName : a.ExecutablePath;
                return !string.IsNullOrEmpty(key) && !_existingPaths.Contains(key);
            }).ToList();

            _allApps = newApps.Select(a => new InstalledAppViewModel(a)).ToList();
            FilterApps();

            StatusText = $"{_allApps.Count} apps available";

            // Important: Await this to ensure icons start loading
            await Task.Delay(100); // Small delay to let UI render first
            await LoadInitialIconsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading apps: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"LoadAppsAsync error: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadInitialIconsAsync()
    {
        try
        {
            // Load icons for first 15 visible apps
            var initialApps = FilteredApps.Take(15).Where(a => !a.IsUwp).ToList();
            
            System.Diagnostics.Debug.WriteLine($"Loading icons for {initialApps.Count} initial apps...");
            
            foreach (var app in initialApps)
            {
                if (!_iconsLoaded.Contains(app))
                {
                    _iconsLoaded.Add(app);
                    System.Diagnostics.Debug.WriteLine($"Loading icon for: {app.DisplayName}");
                    await app.LoadIconAsync();
                }
            }
            
            System.Diagnostics.Debug.WriteLine("Initial icon loading complete");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadInitialIconsAsync error: {ex}");
        }
    }

    private void FilterApps()
    {
        IEnumerable<InstalledAppViewModel> filtered = _allApps;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery.ToLowerInvariant();
            filtered = filtered.Where(a =>
                a.DisplayName.ToLowerInvariant().Contains(query) ||
                a.PathOrPackage.ToLowerInvariant().Contains(query) ||
                (a.Publisher?.ToLowerInvariant().Contains(query) ?? false));
        }

        FilteredApps = new ObservableCollection<InstalledAppViewModel>(filtered);
        
        // Load icons for newly visible apps after filtering
        _ = LoadInitialIconsAsync();
    }

    /// <summary>
    /// Called when user scrolls - loads icons for visible items
    /// </summary>
    public async Task LoadIconsForVisibleItemsAsync(IEnumerable<InstalledAppViewModel> visibleItems)
    {
        var itemsToLoad = visibleItems
            .Where(item => !_iconsLoaded.Contains(item) && !item.IsUwp)
            .ToList();

        if (itemsToLoad.Count == 0)
            return;

        System.Diagnostics.Debug.WriteLine($"[LoadVisible] Loading {itemsToLoad.Count} icons...");

        // Mark as loading to prevent duplicate requests
        foreach (var item in itemsToLoad)
        {
            _iconsLoaded.Add(item);
        }

        // Load icons in parallel batches of 5 for better performance
        const int batchSize = 5;
        for (int i = 0; i < itemsToLoad.Count; i += batchSize)
        {
            var batch = itemsToLoad.Skip(i).Take(batchSize);
            var tasks = batch.Select(item => item.LoadIconAsync()).ToList();
            
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadVisible] Batch error: {ex.Message}");
            }
            
            // Small delay between batches to keep UI responsive
            if (i + batchSize < itemsToLoad.Count)
            {
                await Task.Delay(100);
            }
        }

        System.Diagnostics.Debug.WriteLine($"[LoadVisible] Completed loading {itemsToLoad.Count} icons");
    }

    public void UpdateSelection(List<InstalledAppViewModel> selectedItems)
    {
        _selectedItems = selectedItems;
        HasSelection = selectedItems.Count > 0;
    }
}
