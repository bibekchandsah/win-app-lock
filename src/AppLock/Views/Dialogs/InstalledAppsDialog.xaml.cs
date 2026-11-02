using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.ViewModels;
using AppLock.Services.Firewall;

namespace AppLock.Views.Dialogs;

public sealed partial class InstalledAppsDialog : ContentDialog
{
    public InstalledAppsDialogViewModel ViewModel { get; }

    public InstalledAppsDialog(XamlRoot xamlRoot, InstalledAppsService installedAppsService, HashSet<string> existingPaths)
    {
        this.InitializeComponent();
        XamlRoot = xamlRoot;
        ViewModel = new InstalledAppsDialogViewModel(installedAppsService, existingPaths);
        _ = ViewModel.LoadAppsAsync();
    }

    public IReadOnlyList<InstalledApp> SelectedApps => ViewModel.SelectedApps;

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var listView = (ListView)sender;
        ViewModel.UpdateSelection(listView.SelectedItems.Cast<InstalledAppViewModel>().ToList());
    }
}
