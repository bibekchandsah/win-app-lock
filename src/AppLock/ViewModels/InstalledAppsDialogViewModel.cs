using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Services.Firewall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.ViewModels;

public partial class InstalledAppViewModel : ObservableObject
{
    private readonly InstalledApp _app;

    public InstalledAppViewModel(InstalledApp app)
    {
        _app = app;
    }

    public string DisplayName => _app.DisplayName;
    public string PathOrPackage => _app.IsUwp ? $"Package: {_app.PackageFamilyName}" : _app.ExecutablePath ?? "";
    public string Publisher => _app.Publisher ?? "";
    public Visibility HasPublisher => !string.IsNullOrEmpty(_app.Publisher) ? Visibility.Visible : Visibility.Collapsed;
    public BitmapImage? Icon => _app.Icon;
    public bool IsUwp => _app.IsUwp;
    public Visibility IsUwpVisible => _app.IsUwp ? Visibility.Visible : Visibility.Collapsed;
    public InstalledApp App => _app;
}

public partial class InstalledAppsDialogViewModel : ObservableObject
{
    private readonly InstalledAppsService _installedAppsService;
    private readonly HashSet<string> _existingPaths;
    private List<InstalledAppViewModel> _allApps = new();

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
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading apps: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
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
    }

    public void UpdateSelection(List<InstalledAppViewModel> selectedItems)
    {
        _selectedItems = selectedItems;
        HasSelection = selectedItems.Count > 0;
    }
}
