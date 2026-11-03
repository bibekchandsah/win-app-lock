using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.ViewModels;
using AppLock.Services.Firewall;
using System;

namespace AppLock.Views.Dialogs;

public sealed partial class InstalledAppsDialog : ContentDialog
{
    public InstalledAppsDialogViewModel ViewModel { get; }
    private ScrollViewer? _scrollViewer;
    private bool _isLoadingIcons = false;

    public InstalledAppsDialog(XamlRoot xamlRoot, InstalledAppsService installedAppsService, HashSet<string> existingPaths)
    {
        this.InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ((FrameworkElement)xamlRoot.Content).ActualTheme;
        ViewModel = new InstalledAppsDialogViewModel(installedAppsService, existingPaths);
        _ = ViewModel.LoadAppsAsync();
    }

    public IReadOnlyList<InstalledApp> SelectedApps => ViewModel.SelectedApps;

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var listView = (ListView)sender;
        ViewModel.UpdateSelection(listView.SelectedItems.Cast<InstalledAppViewModel>().ToList());
    }

    private void AppsList_Loaded(object sender, RoutedEventArgs e)
    {
        // Find the ScrollViewer inside the ListView
        _scrollViewer = FindScrollViewer(AppsList);
        if (_scrollViewer != null)
        {
            _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
        }
    }

    private async void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isLoadingIcons || _scrollViewer == null)
            return;

        _isLoadingIcons = true;
        try
        {
            // Get currently visible items based on scroll position
            var itemHeight = 60.0; // Approximate height of each item
            var verticalOffset = _scrollViewer.VerticalOffset;
            var viewportHeight = _scrollViewer.ViewportHeight;
            
            var visibleStartIndex = Math.Max(0, (int)(verticalOffset / itemHeight) - 5); // Load 5 before visible
            var visibleCount = (int)(viewportHeight / itemHeight) + 15; // Load extra items
            
            var visibleItems = ViewModel.FilteredApps
                .Skip(visibleStartIndex)
                .Take(visibleCount)
                .ToList();

            System.Diagnostics.Debug.WriteLine($"[Scroll] Loading icons for {visibleItems.Count} items (start: {visibleStartIndex})");
            
            await ViewModel.LoadIconsForVisibleItemsAsync(visibleItems);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Scroll] Error: {ex.Message}");
        }
        finally
        {
            _isLoadingIcons = false;
        }
    }

    private ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer scrollViewer)
            return scrollViewer;

        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i);
            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }

        return null;
    }

    private async void LoadAllIcons_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender;
            button.IsEnabled = false;
            button.Content = "Loading...";

            System.Diagnostics.Debug.WriteLine("[LoadAll] Starting to load all icons...");
            
            // Load all remaining icons
            await ViewModel.LoadIconsForVisibleItemsAsync(ViewModel.FilteredApps);
            
            button.Content = "All loaded!";
            await Task.Delay(2000);
            button.Content = "Load all icons";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadAll] Error: {ex.Message}");
        }
        finally
        {
            if (sender is Button btn)
                btn.IsEnabled = true;
        }
    }
}
