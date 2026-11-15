using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using AppLock.Services.Firewall;
using AppLock.Services;
using AppLock.ViewModels;
using AppLock.Models;
using AppLock.Views.Dialogs;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Windows.UI;

namespace AppLock.Views;

public sealed partial class InternetAccessPage : Page
{
    private readonly IFirewallService _firewallService;
    private readonly InternetAccessViewModel _viewModel;
    private readonly ActiveConnectionViewModel _activeConnectionsViewModel;
    private readonly NetworkScannerViewModel _networkScannerViewModel;
    private readonly ListeningPortsViewModel _listeningPortsViewModel;
    private readonly InstalledAppsService _installedAppsService;
    private readonly SettingsStore _settingsStore;
    private bool _isElevated;
    private ScrollViewer? _appsScrollViewer;
    private bool _isLoadingAppIcons = false;

    public InternetAccessViewModel ViewModel => _viewModel;
    public ActiveConnectionViewModel ActiveConnectionsViewModel => _activeConnectionsViewModel;
    public NetworkScannerViewModel NetworkScannerViewModel => _networkScannerViewModel;
    public ListeningPortsViewModel ListeningPortsViewModel => _listeningPortsViewModel;

    public InternetAccessPage()
    {
        _firewallService = new WindowsFirewallService();
        _installedAppsService = new InstalledAppsService();
        _settingsStore = ServiceContainer.SettingsStore;
        _viewModel = new InternetAccessViewModel(_firewallService, _settingsStore, _installedAppsService);
        _activeConnectionsViewModel = new ActiveConnectionViewModel();
        _networkScannerViewModel = new NetworkScannerViewModel(ServiceContainer.NetworkScannerService, _settingsStore);
        ListeningPortService.SettingsProvider = () => ServiceContainer.Settings;
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _listeningPortsViewModel = new ListeningPortsViewModel(new ListeningPortService(), dq)
        {
            SettingsProvider = () => ServiceContainer.Settings,
            SettingsSaver = s => _settingsStore.Save(s)
        };
        _listeningPortsViewModel.LoadSettings();

        InitializeComponent();

        _listeningPortsViewModel.VisibilityOrFilterUpdated += UpdateListeningPortsVisibility;
        DataContext = _viewModel;
        Loaded += InternetAccessPage_Loaded;
        Unloaded += InternetAccessPage_Unloaded;
    }

    private void InternetAccessPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _activeConnectionsViewModel.StopAutoRefresh();
        _networkScannerViewModel.Dispose();
        _listeningPortsViewModel.Dispose();
    }

    private async void InternetAccessPage_Loaded(object sender, RoutedEventArgs e)
    {
        CheckElevation();
        await CheckFirewallStatusAsync();
        await _viewModel.LoadAppsAsync();
        UpdateAppsVisibility();

        // Wire the dispatcher queue so scanner callbacks can marshal to UI thread
        _networkScannerViewModel.SetDispatcherQueue(DispatcherQueue);
        _networkScannerViewModel.LoadDevices();
        UpdateNetworkDevicesVisibility();
        InitializeScanIntervalComboBox();
        MacApiKeyBox.Password = ServiceContainer.Settings.NetworkDeviceScanner.MacVendorsApiKey ?? string.Empty;

        // Resume the scan loop if it was left enabled
        if (ServiceContainer.Settings.NetworkDeviceScanner.ScannerEnabled)
            ServiceContainer.NetworkScannerService.Start();
    }

    private void CheckElevation()
    {
        _isElevated = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            _isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { }

        NotElevatedInfoBar.IsOpen = !_isElevated;
    }

    private async Task CheckFirewallStatusAsync()
    {
        try
        {
            var status = await _firewallService.GetFirewallStatusAsync();
            
            FirewallDisabledInfoBar.IsOpen = !status.IsEnabled && status.IsServiceRunning;
            GroupPolicyLockedInfoBar.IsOpen = status.IsGroupPolicyManaged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking firewall status: {ex.Message}");
            FirewallDisabledInfoBar.IsOpen = false;
            GroupPolicyLockedInfoBar.IsOpen = false;
        }
    }

    private void UpdateAppsVisibility()
    {
        if (_viewModel.HasApps)
        {
            AppsEmptyState.Visibility = Visibility.Collapsed;
            AppsListView.Visibility = Visibility.Visible;
            AppsListView.ItemsSource = _viewModel.Apps;
        }
        else
        {
            AppsEmptyState.Visibility = Visibility.Visible;
            AppsListView.Visibility = Visibility.Collapsed;
        }
    }

    // Tab switching
    private void AppsTabButton_Click(object sender, RoutedEventArgs e)
    {
        AppsTabContent.Visibility = Visibility.Visible;
        ActiveConnectionsTabContent.Visibility = Visibility.Collapsed;
        NetworkDevicesTabContent.Visibility = Visibility.Collapsed;
        ListeningPortsTabContent.Visibility = Visibility.Collapsed;
        AppsTabButton.Style = (Style)Resources["AccentButtonStyle"];
        ActiveConnectionsTabButton.Style = null;
        NetworkDevicesTabButton.Style = null;
        ListeningPortsTabButton.Style = null;
        
        // Stop auto-refresh when leaving tabs
        _activeConnectionsViewModel.StopAutoRefresh();
        _listeningPortsViewModel.StopAutoRefresh();
    }

    private void ActiveConnectionsTabButton_Click(object sender, RoutedEventArgs e)
    {
        AppsTabContent.Visibility = Visibility.Collapsed;
        ActiveConnectionsTabContent.Visibility = Visibility.Visible;
        NetworkDevicesTabContent.Visibility = Visibility.Collapsed;
        ListeningPortsTabContent.Visibility = Visibility.Collapsed;
        ActiveConnectionsTabButton.Style = (Style)Resources["AccentButtonStyle"];
        AppsTabButton.Style = null;
        NetworkDevicesTabButton.Style = null;
        ListeningPortsTabButton.Style = null;
        
        _listeningPortsViewModel.StopAutoRefresh();
        // Start auto-refresh when tab becomes visible
        _ = _activeConnectionsViewModel.LoadConnectionsAsync();
        _activeConnectionsViewModel.StartAutoRefresh(3);
    }

    private void NetworkDevicesTabButton_Click(object sender, RoutedEventArgs e)
    {
        AppsTabContent.Visibility = Visibility.Collapsed;
        ActiveConnectionsTabContent.Visibility = Visibility.Collapsed;
        NetworkDevicesTabContent.Visibility = Visibility.Visible;
        ListeningPortsTabContent.Visibility = Visibility.Collapsed;
        NetworkDevicesTabButton.Style = (Style)Resources["AccentButtonStyle"];
        AppsTabButton.Style = null;
        ActiveConnectionsTabButton.Style = null;
        ListeningPortsTabButton.Style = null;

        _activeConnectionsViewModel.StopAutoRefresh();
        _listeningPortsViewModel.StopAutoRefresh();
        UpdateNetworkDevicesVisibility();
    }

    private void ListeningPortsTabButton_Click(object sender, RoutedEventArgs e)
    {
        AppsTabContent.Visibility = Visibility.Collapsed;
        ActiveConnectionsTabContent.Visibility = Visibility.Collapsed;
        NetworkDevicesTabContent.Visibility = Visibility.Collapsed;
        ListeningPortsTabContent.Visibility = Visibility.Visible;
        ListeningPortsTabButton.Style = (Style)Resources["AccentButtonStyle"];
        AppsTabButton.Style = null;
        ActiveConnectionsTabButton.Style = null;
        NetworkDevicesTabButton.Style = null;

        _activeConnectionsViewModel.StopAutoRefresh();
        UpdateListeningPortsVisibility();
        _ = _listeningPortsViewModel.LoadPortsAsync(isManualOrInitial: true);
        _listeningPortsViewModel.StartAutoRefresh(5);
    }

    // Command bar actions
    private async void BrowseForProgram_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Use Win32 OpenFileDialog as workaround for broken FileOpenPicker in unpackaged apps
            var openFileDialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "Executable Files (*.exe)|*.exe|Shortcut Files (*.lnk)|*.lnk|All Files (*.*)|*.*",
                FilterIndex = 1,
                Title = "Select Program to Block",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            var result = openFileDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                string exePath = openFileDialog.FileName;
                
                // Resolve .lnk shortcuts
                if (exePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    exePath = ResolveShortcut(exePath);
                }

                if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                {
                    await ShowMessageAsync("Error", "Could not resolve file path.");
                    return;
                }

                string displayName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                
                // Check for system critical executables
                if (InstalledAppsService.IsSystemCritical(exePath))
                {
                    bool confirmed = await ShowSystemCriticalWarningAsync(displayName);
                    if (!confirmed)
                        return;
                }

                await _viewModel.AddAppAsync(
                    exePath, 
                    displayName, 
                    blockImmediately: true, 
                    direction: "Outgoing", 
                    packageFamilyName: null, 
                    icon: null);
                UpdateAppsVisibility();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error browsing for program: {ex.Message}");
            Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            await ShowMessageAsync("Error", $"Failed to add program:\n\n{ex.Message}\n\nType: {ex.GetType().Name}");
        }
    }

    private string ResolveShortcut(string shortcutPath)
    {
        try
        {
            // Use IWshShell COM to resolve shortcuts
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return shortcutPath;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return shortcutPath;

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string targetPath = shortcut.TargetPath;
            return string.IsNullOrEmpty(targetPath) ? shortcutPath : targetPath;
        }
        catch
        {
            return shortcutPath;
        }
    }

    private async void FromRunningPrograms_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Reuse WindowEnumerator from Protection page
            var runningApps = ServiceContainer.WindowEnumerator.EnumerateWindows()
                .Where(w => !string.IsNullOrEmpty(w.ProcessName) && 
                            !_viewModel.Apps.Any(a => a.ExePath.Equals(w.ProcessPath, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(w => w.ProcessPath)
                .OrderBy(w => w.ProcessName)
                .ToList();

            if (runningApps.Count == 0)
            {
                await ShowMessageAsync("No Programs", "All running programs are already added.");
                return;
            }

            // Show selection dialog
            var dialog = new ContentDialog
            {
                Title = "Select Running Program",
                CloseButtonText = "Cancel",
                PrimaryButtonText = "Add",
                XamlRoot = XamlRoot,
                RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme,
                Content = CreateRunningProgramsList(runningApps)
            };

            var result = await dialog.ShowAsync();
            
            if (result == ContentDialogResult.Primary && dialog.Content is ListView listView && listView.SelectedItem != null)
            {
                var selected = (RunningWindow)listView.SelectedItem;
                
                // Check for system critical executables
                if (InstalledAppsService.IsSystemCritical(selected.ProcessPath))
                {
                    bool confirmed = await ShowSystemCriticalWarningAsync(selected.ProcessName);
                    if (!confirmed)
                        return;
                }

                await _viewModel.AddAppAsync(
                    selected.ProcessPath, 
                    selected.ProcessName, 
                    blockImmediately: true, 
                    direction: "Outgoing", 
                    packageFamilyName: null, 
                    icon: null);
                UpdateAppsVisibility();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error showing running programs: {ex.Message}");
        }
    }

    private ListView CreateRunningProgramsList(System.Collections.Generic.List<RunningWindow> apps)
    {
        var listView = new ListView
        {
            ItemsSource = apps,
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 400
        };

        listView.ItemTemplate = (DataTemplate)XamlReader.Load(@"
            <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                <StackPanel Orientation=""Horizontal"" Spacing=""12"" Padding=""8"">
                    <Image Source=""{Binding Icon}"" Width=""32"" Height=""32""/>
                    <StackPanel>
                        <TextBlock Text=""{Binding ProcessName}"" FontWeight=""SemiBold""/>
                        <TextBlock Text=""{Binding ProcessPath}"" FontSize=""11"" Foreground=""{ThemeResource TextFillColorSecondaryBrush}""/>
                    </StackPanel>
                </StackPanel>
            </DataTemplate>");

        return listView;
    }

    private async void PickAWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Reuse WindowPicker from Protection page
            var hwndValue = WindowNative.GetWindowHandle(App.MainWindowInstance);
            var hwnd = new Windows.Win32.Foundation.HWND(hwndValue);
            var pickedWindow = await ServiceContainer.WindowPicker.PickWindowAsync(hwnd);
            
            if (pickedWindow != null && !string.IsNullOrEmpty(pickedWindow.ProcessPath))
            {
                string displayName = System.IO.Path.GetFileNameWithoutExtension(pickedWindow.ProcessPath);
                
                // Check for system critical executables
                if (InstalledAppsService.IsSystemCritical(pickedWindow.ProcessPath))
                {
                    bool confirmed = await ShowSystemCriticalWarningAsync(displayName);
                    if (!confirmed)
                        return;
                }

                await _viewModel.AddAppAsync(
                    pickedWindow.ProcessPath, 
                    displayName, 
                    blockImmediately: true, 
                    direction: "Outgoing", 
                    packageFamilyName: null, 
                    icon: null);
                UpdateAppsVisibility();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error picking window: {ex.Message}");
        }
    }

    private async void FromInstalledApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var existingPaths = _viewModel.GetExistingPaths();
            var dialog = new InstalledAppsDialog(XamlRoot, _installedAppsService, existingPaths);
            
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                foreach (var app in dialog.SelectedApps)
                {
                    // Check for system critical executables
                    if (!string.IsNullOrEmpty(app.ExecutablePath) && InstalledAppsService.IsSystemCritical(app.ExecutablePath))
                    {
                        bool confirmed = await ShowSystemCriticalWarningAsync(app.DisplayName);
                        if (!confirmed)
                            continue;
                    }

                    await _viewModel.AddAppAsync(
                        app.ExecutablePath ?? "",
                        app.DisplayName,
                        blockImmediately: true,
                        packageFamilyName: app.PackageFamilyName,
                        icon: app.Icon);
                }
                UpdateAppsVisibility();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error showing installed apps: {ex.Message}");
            await ShowMessageAsync("Error", $"Failed to load installed apps: {ex.Message}");
        }
    }

    private async Task<bool> ShowSystemCriticalWarningAsync(string appName)
    {
        var dialog = new ContentDialog
        {
            Title = "Warning: System Critical Process",
            Content = $"You are about to block internet access for {appName}, which is a system-critical process.\n\n" +
                     "Blocking this process may cause system instability, prevent Windows updates, " +
                     "or interfere with security features.\n\n" +
                     "Are you sure you want to continue?",
            PrimaryButtonText = "Block Anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        // Don't reload from scratch, just sync
        await _viewModel.RefreshAsync();
        
        // Trigger icon loading for all visible apps
        await LoadVisibleAppIcons();
        
        UpdateAppsVisibility();
    }

    private void AppsListView_Loaded(object sender, RoutedEventArgs e)
    {
        // Find the ScrollViewer inside the ListView
        _appsScrollViewer = FindScrollViewer(AppsListView);
        if (_appsScrollViewer != null)
        {
            _appsScrollViewer.ViewChanged += AppsScrollViewer_ViewChanged;
        }
    }

    private async void AppsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isLoadingAppIcons || _appsScrollViewer == null)
            return;

        await LoadVisibleAppIcons();
    }

    private async Task LoadVisibleAppIcons()
    {
        if (_isLoadingAppIcons || _appsScrollViewer == null)
            return;

        _isLoadingAppIcons = true;
        try
        {
            var itemHeight = 70.0; // Approximate height of each item
            var verticalOffset = _appsScrollViewer.VerticalOffset;
            var viewportHeight = _appsScrollViewer.ViewportHeight;

            var visibleStartIndex = Math.Max(0, (int)(verticalOffset / itemHeight) - 5);
            var visibleCount = (int)(viewportHeight / itemHeight) + 15;

            var visibleApps = _viewModel.Apps
                .Skip(visibleStartIndex)
                .Take(visibleCount)
                .ToList();

            await _viewModel.LoadIconsForVisibleAppsAsync(visibleApps);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppsScroll] Error: {ex.Message}");
        }
        finally
        {
            _isLoadingAppIcons = false;
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

    private async void BlockAllSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = AppsListView.SelectedItems.Cast<NetworkRuleViewModel>().ToList();
        if (selected.Count == 0)
        {
            await ShowMessageAsync("No Selection", "Please select one or more apps to block.");
            return;
        }

        _isBulkOperation = true;
        int succeeded = 0;
        int failed = 0;
        
        foreach (var app in selected)
        {
            if (!app.IsBlocked)
            {
                bool success = await _viewModel.BlockAppAsync(app);
                if (success)
                    succeeded++;
                else
                    failed++;
            }
        }
        _isBulkOperation = false;

        if (failed > 0)
        {
            await ShowMessageAsync("Block Complete", 
                $"Blocked: {succeeded}\nFailed: {failed}\n\nSome rules could not be created. Check the logs for details.");
        }
        else if (succeeded > 0)
        {
            await ShowMessageAsync("Success", $"Successfully blocked {succeeded} app(s).");
        }
    }

    private async void UnblockAllSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = AppsListView.SelectedItems.Cast<NetworkRuleViewModel>().ToList();
        if (selected.Count == 0)
        {
            await ShowMessageAsync("No Selection", "Please select one or more apps to unblock.");
            return;
        }

        _isBulkOperation = true;
        int succeeded = 0;
        int failed = 0;
        
        foreach (var app in selected)
        {
            if (app.IsBlocked)
            {
                bool success = await _viewModel.UnblockAppAsync(app);
                if (success)
                    succeeded++;
                else
                    failed++;
            }
        }
        _isBulkOperation = false;

        if (failed > 0)
        {
            await ShowMessageAsync("Unblock Complete", 
                $"Unblocked: {succeeded}\nFailed: {failed}\n\nSome rules could not be removed. Check the logs for details.");
        }
        else if (succeeded > 0)
        {
            await ShowMessageAsync("Success", $"Successfully unblocked {succeeded} app(s).");
        }
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = AppsListView.SelectedItems.Cast<NetworkRuleViewModel>().ToList();
        if (selected.Count == 0)
        {
            await ShowMessageAsync("No Selection", "Please select one or more apps to remove.");
            return;
        }

        // Confirm deletion
        var dialog = new ContentDialog
        {
            Title = "Remove Apps",
            Content = $"Are you sure you want to remove {selected.Count} app(s) from the list?\n\nThis will also remove all associated firewall rules.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        _isBulkOperation = true;
        int succeeded = 0;
        int failed = 0;

        foreach (var app in selected)
        {
            // First unblock to remove firewall rules
            if (app.IsBlocked)
            {
                bool unblockSuccess = await _viewModel.UnblockAppAsync(app);
                if (!unblockSuccess)
                {
                    failed++;
                    continue; // Skip removing from list if we can't remove rules
                }
            }

            // Then remove from list
            _viewModel.RemoveApp(app);
            succeeded++;
        }
        _isBulkOperation = false;

        UpdateAppsVisibility();

        if (failed > 0)
        {
            await ShowMessageAsync("Remove Complete",
                $"Removed: {succeeded}\nFailed: {failed}\n\nSome apps could not be removed because their firewall rules could not be deleted.");
        }
        else if (succeeded > 0)
        {
            await ShowMessageAsync("Success", $"Successfully removed {succeeded} app(s).");
        }
    }

    private void ImportRules_Click(object sender, RoutedEventArgs e) 
    { 
        _ = ImportRulesAsync();
    }
    
    private void ExportRules_Click(object sender, RoutedEventArgs e) 
    { 
        _ = ExportRulesAsync();
    }
    
    private void RepairRules_Click(object sender, RoutedEventArgs e) 
    { 
        _ = RepairAllDriftAsync();
    }

    private async Task RepairAllDriftAsync()
    {
        var result = await _viewModel.RepairAllDriftAsync();
        
        string message = $"Repair complete:\n\n" +
                        $"Repaired: {result.Repaired}\n" +
                        $"Failed: {result.Failed}";

        await ShowMessageAsync("Repair Rules", message);
    }
    
    private async void RemoveAllAppLockRules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove All AppLock Rules",
            Content = "This will remove all apps and their firewall rules from the list. This action cannot be undone.\n\nAre you sure?",
            PrimaryButtonText = "Remove All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _isBulkOperation = true;
            int total = _viewModel.Apps.Count;
            int succeeded = 0;
            int failed = 0;

            foreach (var app in _viewModel.Apps.ToList())
            {
                // Unblock first to remove rules
                if (app.IsBlocked)
                {
                    bool unblockSuccess = await _viewModel.UnblockAppAsync(app);
                    if (!unblockSuccess)
                    {
                        failed++;
                        continue;
                    }
                }

                _viewModel.RemoveApp(app);
                succeeded++;
            }
            _isBulkOperation = false;

            UpdateAppsVisibility();

            if (failed > 0)
            {
                await ShowMessageAsync("Remove All Complete",
                    $"Removed: {succeeded}\nFailed: {failed}\n\nSome firewall rules could not be deleted.");
            }
            else
            {
                await ShowMessageAsync("Success", $"All {succeeded} apps and their firewall rules have been removed.");
            }
        }
    }
    
    private async void FirewallStatus_Click(object sender, RoutedEventArgs e)
    {
        var status = await _firewallService.GetFirewallStatusAsync();
        var statusText = $"Firewall Enabled: {status.IsEnabled}\n" +
                        $"Service Running: {status.IsServiceRunning}\n" +
                        $"Group Policy Managed: {status.IsGroupPolicyManaged}\n" +
                        $"Current Profile: {status.CurrentProfile}";

        var dialog = new ContentDialog
        {
            Title = "Firewall Status",
            Content = statusText,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
        };

        await dialog.ShowAsync();
    }

    // Active connections filter (removed old placeholder handlers)
    // Info bar actions
    private async void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            
            // Signal authorized exit to prevent watchdog from restarting
            ProcessProtectionService.SignalAuthorizedExit();
            ProcessProtectionService.UnprotectCurrentProcess();
            
            // Start elevated process
            var process = Process.Start(psi);
            
            if (process != null)
            {
                // Wait a moment for elevated process to start, then close this instance
                await Task.Delay(300);
                
                // Hide window before exiting for cleaner UX
                if (App.MainWindowInstance != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        App.MainWindowInstance.Close();
                    });
                }
                else
                {
                    Application.Current.Exit();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error restarting as admin: {ex.Message}");
        }
    }

    private void OpenWindowsSecurityButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "windowsdefender://network",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
        };
        await dialog.ShowAsync();
    }

    private async void RepairSingle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is NetworkRuleViewModel app)
        {
            bool success = await _viewModel.RepairSingleAsync(app);
            if (success)
            {
                await ShowMessageAsync("Repair", $"Rule for {app.DisplayName} has been repaired.");
            }
            else
            {
                await ShowMessageAsync("Repair Failed", $"Failed to repair rule for {app.DisplayName}.");
            }
        }
    }

    private async void BlockConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is ActiveConnection connection)
        {
            // Verify not a system process
            if (connection.IsSystemProcess)
            {
                await ShowMessageAsync("Cannot Block", 
                    $"{connection.ProcessName} is a system-critical process and cannot be blocked from this view.\n\n" +
                    "Use the Apps tab to add it with an explicit warning.");
                return;
            }

            // Check for system critical executable
            if (InstalledAppsService.IsSystemCritical(connection.ProcessPath))
            {
                bool confirmed = await ShowSystemCriticalWarningAsync(connection.ProcessName);
                if (!confirmed)
                    return;
            }

            // Add to Apps list and block
            string displayName = connection.ProcessName;
            await _viewModel.AddAppAsync(
                connection.ProcessPath, 
                displayName, 
                blockImmediately: true, 
                direction: "Outgoing", 
                packageFamilyName: null, 
                icon: null);
            UpdateAppsVisibility();

            await ShowMessageAsync("Blocked", $"{displayName} has been added to the blocked apps list.");
        }
    }

    private void FilterToggleGrid_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Toggle the switch when the grid (including label) is tapped
        FilterToggle.IsOn = !FilterToggle.IsOn;
    }

    private async void DirectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.DataContext is NetworkRuleViewModel app)
        {
            // Only process if selection actually changed (not initial binding)
            if (e.AddedItems.Count > 0 && e.RemovedItems.Count > 0)
            {
                string newDirection = e.AddedItems[0] as string ?? "Outgoing";
                await _viewModel.UpdateDirectionAsync(app, newDirection);
            }
        }
    }

    private bool _isBulkOperation = false;

    private async void BlockedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isBulkOperation) return; // Skip during bulk operations
        
        if (sender is ToggleSwitch toggle && toggle.DataContext is NetworkRuleViewModel app)
        {
            // Only process if this is a user-initiated change (not initial binding)
            if (toggle.FocusState != FocusState.Unfocused || ReferenceEquals(e.OriginalSource, toggle))
            {
                bool isBlocked = toggle.IsOn;
                
                if (isBlocked)
                {
                    // Block the app
                    bool success = await _viewModel.BlockAppAsync(app);
                    if (!success)
                    {
                        // Revert on failure
                        toggle.IsOn = false;
                        await ShowMessageAsync("Error", $"Failed to block {app.DisplayName}. Make sure the app is running as administrator.");
                    }
                }
                else
                {
                    // Unblock the app (allow internet access)
                    bool success = await _viewModel.UnblockAppAsync(app);
                    if (!success)
                    {
                        // Revert on failure
                        toggle.IsOn = true;
                        await ShowMessageAsync("Error", $"Failed to unblock {app.DisplayName}. Make sure the app is running as administrator.");
                    }
                }
            }
        }
    }

    private async Task ImportRulesAsync()
    {
        try
        {
            // Workaround: List files from Documents\AppLock folder (file pickers are broken)
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var appLockFolder = System.IO.Path.Combine(documentsPath, "AppLock");
            
            if (!System.IO.Directory.Exists(appLockFolder))
            {
                await ShowMessageAsync("No Export Files", 
                    "No exported rule files found.\n\nExport some rules first, then try importing.");
                return;
            }

            var jsonFiles = System.IO.Directory.GetFiles(appLockFolder, "*.json")
                .OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                .Take(10)
                .Select(f => new { FileName = System.IO.Path.GetFileName(f), FullPath = f })
                .ToList();

            if (jsonFiles.Count == 0)
            {
                await ShowMessageAsync("No Export Files", 
                    "No JSON files found in Documents\\AppLock\\\n\nExport some rules first, then try importing.");
                return;
            }

            // Show file selection dialog with styled list
            var listView = new ListView
            {
                ItemsSource = jsonFiles.Select(f => f.FileName).ToList(),
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 400,
                MinWidth = 500
            };
            
            // Style the list items
            var itemTemplate = new DataTemplate();
            listView.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <Grid Padding=""12,8"" ColumnSpacing=""12"">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width=""Auto""/>
                            <ColumnDefinition Width=""*""/>
                        </Grid.ColumnDefinitions>
                        <FontIcon Grid.Column=""0"" 
                                 Glyph=""&#xE8A5;"" 
                                 FontSize=""16""
                                 Foreground=""{ThemeResource AccentFillColorDefaultBrush}""/>
                        <TextBlock Grid.Column=""1"" 
                                  Text=""{Binding}""
                                  TextWrapping=""NoWrap""
                                  TextTrimming=""CharacterEllipsis""/>
                    </Grid>
                </DataTemplate>");

            var stackPanel = new StackPanel { Spacing = 8 };
            stackPanel.Children.Add(new TextBlock 
            { 
                Text = $"Found {jsonFiles.Count} exported file(s) in Documents\\AppLock\\",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(0, 0, 0, 8)
            });
            stackPanel.Children.Add(listView);

            var dialog = new ContentDialog
            {
                Title = "Select File to Import",
                Content = stackPanel,
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || listView.SelectedIndex < 0)
                return;

            var selectedFile = jsonFiles[listView.SelectedIndex];
            var importService = _viewModel.ImportExportService;
            var exportData = await importService.ImportFromFileAsync(selectedFile.FullPath);
            
            if (exportData == null)
            {
                await ShowMessageAsync("Import Failed", "Invalid or corrupted import file.");
                return;
            }

            var rules = importService.ConvertToRules(exportData);
            var (newRules, conflicts) = importService.CheckConflicts(rules, _viewModel.Apps.Select(a => a.Entry).ToList());
            
            if (newRules.Count == 0 && conflicts.Count == 0)
            {
                await ShowMessageAsync("Import", "No rules to import.");
                return;
            }

            var importDialog = new Dialogs.ImportExportDialog(XamlRoot, newRules, conflicts);
            var importResult = await importDialog.ShowAsync();
            
            if (importResult == ContentDialogResult.Primary)
            {
                foreach (var rule in importDialog.SelectedRules)
                {
                    ServiceContainer.Settings.NetworkRules.Add(rule);
                }
                
                _settingsStore.Save(ServiceContainer.Settings);
                
                // Reload the entire list to ensure proper initialization and UI update
                await _viewModel.LoadAppsAsync();
                UpdateAppsVisibility();
                
                LogService.LogFirewallAction("IMPORT", $"{importDialog.SelectedRules.Count} rules", $"Imported from {selectedFile.FileName}");
                await ShowMessageAsync("Import Complete", $"Imported {importDialog.SelectedRules.Count} rules.");
            }
        }
        catch (Exception ex)
        {
            var errorDetails = $"Type: {ex.GetType().Name}\nMessage: {(string.IsNullOrEmpty(ex.Message) ? "No message" : ex.Message)}\nInner: {ex.InnerException?.Message}";
            LogService.LogFirewallAction("IMPORT_ERROR", "Import failed", errorDetails);
            await ShowMessageAsync("Import Error", $"Failed to import rules:\n\n{errorDetails}");
        }
    }
    
    private async Task ExportRulesAsync()
    {
        try
        {
            if (_viewModel.Apps.Count == 0)
            {
                await ShowMessageAsync("Export", "No rules to export.");
                return;
            }

            // Workaround: Save directly to Documents folder (file pickers are broken in unpackaged WinUI3 apps)
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var appLockFolder = System.IO.Path.Combine(documentsPath, "AppLock");
            System.IO.Directory.CreateDirectory(appLockFolder);
            
            var fileName = $"AppLock-Firewall-Rules-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";
            var filePath = System.IO.Path.Combine(appLockFolder, fileName);
            
            var rules = _viewModel.Apps.Select(a => a.Entry).ToList();
            await _viewModel.ImportExportService.ExportToFileAsync(rules, filePath);
            
            LogService.LogFirewallAction("EXPORT", $"{rules.Count} rules", $"Exported to {fileName}");
            
            await ShowMessageAsync("Export Complete", 
                $"Exported {rules.Count} rules to:\n\n{filePath}\n\nYou can find it in:\nDocuments\\AppLock\\");
        }
        catch (Exception ex)
        {
            var errorDetails = $"Type: {ex.GetType().Name}\nMessage: {(string.IsNullOrEmpty(ex.Message) ? "No message" : ex.Message)}\nInner: {ex.InnerException?.Message}";
            LogService.LogFirewallAction("EXPORT_ERROR", "Export failed", errorDetails);
            await ShowMessageAsync("Export Error", $"Failed to export rules:\n\n{errorDetails}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Network Devices tab
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateNetworkDevicesVisibility()
    {
        bool hasAnyDevices = _networkScannerViewModel.Devices.Count > 0;
        bool hasFiltered = _networkScannerViewModel.FilteredDevices.Count > 0;
        NetworkDevicesEmptyState.Visibility = hasFiltered ? Visibility.Collapsed : Visibility.Visible;
        NetworkDevicesListView.Visibility   = hasFiltered ? Visibility.Visible  : Visibility.Collapsed;

        if (hasAnyDevices && !hasFiltered)
        {
            NetworkDevicesEmptyStateTitle.Text = "No matching devices";
            NetworkDevicesEmptyStateSubtitle.Text = "Try adjusting your search query.";
        }
        else
        {
            NetworkDevicesEmptyStateTitle.Text = "No devices found yet";
            NetworkDevicesEmptyStateSubtitle.Text = "Enable the scanner or press Scan now to discover devices on your network.";
        }

        NetworkDevicesListView.ItemsSource = _networkScannerViewModel.FilteredDevices;
    }

    private void NetworkDeviceSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _networkScannerViewModel.SearchQuery = sender.Text;
        UpdateNetworkDevicesVisibility();
    }

    private void NetworkDeviceSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _networkScannerViewModel.SearchQuery = sender.Text;
        UpdateNetworkDevicesVisibility();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Listening Ports tab
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshListeningPorts_Click(object sender, RoutedEventArgs e)
    {
        _ = _listeningPortsViewModel.LoadPortsAsync(isManualOrInitial: true);
    }

    private void ListeningPortSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _listeningPortsViewModel.SearchQuery = sender.Text;
    }

    private void ListeningPortSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _listeningPortsViewModel.SearchQuery = sender.Text;
    }

    private void PortFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _listeningPortsViewModel.SelectedFilter = tag;
            UpdatePortFilterChipStyles(tag);
        }
    }

    private void UpdatePortFilterChipStyles(string activeTag)
    {
        Button[] buttons = {
            PortFilterAllButton,
            PortFilterReachableButton,
            PortFilterLocalhostButton,
            PortFilterBlockedButton,
            PortFilterTcpButton,
            PortFilterUdpButton,
            PortFilterIpv4Button,
            PortFilterIpv6Button
        };

        var accentStyle = (Style)Resources["AccentButtonStyle"];
        foreach (var btn in buttons)
        {
            if (btn != null && btn.Tag is string t)
            {
                btn.Style = t.Equals(activeTag, StringComparison.OrdinalIgnoreCase) ? accentStyle : null;
            }
        }
    }

    private void PortSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_listeningPortsViewModel == null || PortSortComboBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (Enum.TryParse<ListeningPortSortOrder>(tag, true, out var order))
        {
            _listeningPortsViewModel.SortBy = order;
        }
    }

    private void UpdateListeningPortsVisibility()
    {
        bool hasItems = _listeningPortsViewModel.HasItems;
        ListeningPortsEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

        if (_listeningPortsViewModel.GroupByApp)
        {
            ListeningPortsGroupedListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            ListeningPortsListView.Visibility = Visibility.Collapsed;
        }
        else
        {
            ListeningPortsGroupedListView.Visibility = Visibility.Collapsed;
            ListeningPortsListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!hasItems)
        {
            bool isFiltered = !string.IsNullOrWhiteSpace(_listeningPortsViewModel.SearchQuery) ||
                              _listeningPortsViewModel.SelectedFilter != "All";
            if (isFiltered)
            {
                ListeningPortsEmptyStateTitle.Text = "No matching ports";
                ListeningPortsEmptyStateSubtitle.Text = "No open ports match your current search query or filter.";
            }
            else
            {
                ListeningPortsEmptyStateTitle.Text = "No listening ports found";
                ListeningPortsEmptyStateSubtitle.Text = "No sockets are currently waiting for incoming connections.";
            }
        }
    }

    private void InitializeScanIntervalComboBox()
    {
        int saved = ServiceContainer.Settings.NetworkDeviceScanner.ScanIntervalSeconds;
        // Pick the closest option; default to 30 s
        int[] options = { 10, 30, 60, 120, 300 };
        int best = options.OrderBy(o => Math.Abs(o - saved)).First();
        for (int i = 0; i < ScanIntervalComboBox.Items.Count; i++)
        {
            if (ScanIntervalComboBox.Items[i] is ComboBoxItem item &&
                item.Tag is string tag && tag == best.ToString())
            {
                ScanIntervalComboBox.SelectedIndex = i;
                return;
            }
        }
        ScanIntervalComboBox.SelectedIndex = 1; // 30 s fallback
    }

    private void ScanIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_networkScannerViewModel == null || ScanIntervalComboBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (int.TryParse(tag, out int seconds))
        {
            _networkScannerViewModel.ScanIntervalSeconds = seconds;
        }
    }

    private async void ScanNowButton_Click(object sender, RoutedEventArgs e)
    {
        ScanNowButton.IsEnabled = false;
        try
        {
            await _networkScannerViewModel.ScanNowAsync();
            UpdateNetworkDevicesVisibility();
        }
        finally
        {
            ScanNowButton.IsEnabled = true;
        }
    }

    private async void ForgetAllDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_networkScannerViewModel.Devices.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "Forget all devices",
            Content = "This will remove all known devices from the list. The scanner will treat every device as new on the next scan.",
            PrimaryButtonText = "Forget all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _networkScannerViewModel.ForgetAll();
            UpdateNetworkDevicesVisibility();
        }
    }

    private void TrustDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NetworkDeviceViewModel vm)
        {
            _networkScannerViewModel.TrustDevice(vm);
        }
    }

    private void ForgetDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NetworkDeviceViewModel vm)
        {
            _networkScannerViewModel.ForgetDevice(vm);
            UpdateNetworkDevicesVisibility();
        }
    }

    private void DeviceName_LostFocus(object sender, RoutedEventArgs e)
    {
        // Friendly name is two-way bound; just persist when focus leaves the box
        _settingsStore.Save(ServiceContainer.Settings);
    }

    private void SaveMacApiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = MacApiKeyBox.Password?.Trim() ?? string.Empty;
        MacApiKeyFlyout.Hide();
        _ = _networkScannerViewModel.SaveMacVendorsApiKeyAsync(key);
    }

    private void ClearMacApiKey_Click(object sender, RoutedEventArgs e)
    {
        MacApiKeyBox.Password = string.Empty;
        MacApiKeyFlyout.Hide();
        _ = _networkScannerViewModel.SaveMacVendorsApiKeyAsync(string.Empty);
    }

    private async void PortBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ListeningPortItemViewModel vm)
        {
            if (vm.IsBlockedByAppLock)
            {
                await UnblockPortWithAuthAsync(vm);
            }
            else
            {
                await BlockPortWithSafetyAsync(vm);
            }
        }
    }

    private async Task<bool> EnsureFirewallAuthorizedAsync()
    {
        return await _viewModel.AuthService.AuthenticateForUnblockAsync(XamlRoot, "unblock");
    }

    private async Task<bool> ConfirmActionAsync(string title, string content, string primaryButtonText = "Proceed")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task BlockPortWithSafetyAsync(ListeningPortItemViewModel vm)
    {
        if (!_isElevated)
        {
            await ShowMessageAsync("Administrator Rights Required",
                "Creating Windows Firewall rules requires administrator privileges. Please restart AppLock as administrator.");
            return;
        }

        if (vm.IsCriticalPortOrApp)
        {
            bool proceed = await ConfirmActionAsync(
                "Critical System Port / App Warning",
                vm.CriticalWarningMessage,
                "Block Anyway");
            if (!proceed) return;
        }

        if (vm.Scope == PortScope.LocalhostOnly)
        {
            bool proceed = await ConfirmActionAsync(
                "Localhost Port Warning",
                vm.LocalhostWarningMessage,
                "Block Anyway");
            if (!proceed) return;
        }

        var (success, error) = await _listeningPortsViewModel.BlockPortAsync(vm, _firewallService);
        if (success)
        {
            await ShowMessageAsync("Port Blocked",
                $"Incoming network connections to port {vm.Port} ({vm.Protocol}) are now blocked by AppLock.\n\nNote: This affects new inbound connections; any currently active sessions may remain until terminated.");
        }
        else
        {
            await ShowMessageAsync("Block Failed", error ?? "Could not create firewall block rule.");
        }
    }

    private async Task UnblockPortWithAuthAsync(ListeningPortItemViewModel vm)
    {
        if (!_isElevated)
        {
            await ShowMessageAsync("Administrator Rights Required",
                "Removing Windows Firewall rules requires administrator privileges. Please restart AppLock as administrator.");
            return;
        }

        if (!await EnsureFirewallAuthorizedAsync())
            return;

        var (success, error) = await _listeningPortsViewModel.UnblockPortAsync(vm, _firewallService);
        if (success)
        {
            await ShowMessageAsync("Port Unblocked",
                $"Port {vm.Port} ({vm.Protocol}) block rule has been removed from Windows Firewall.");
        }
        else
        {
            await ShowMessageAsync("Unblock Failed", error ?? "Could not remove firewall rule.");
        }
    }

    private async Task BlockAppInboundWithSafetyAsync(ListeningPortItemViewModel vm)
    {
        if (!_isElevated)
        {
            await ShowMessageAsync("Administrator Rights Required",
                "Creating Windows Firewall rules requires administrator privileges. Please restart AppLock as administrator.");
            return;
        }

        if (vm.IsCriticalPortOrApp)
        {
            bool proceed = await ConfirmActionAsync(
                "Critical System App Warning",
                vm.CriticalWarningMessage,
                "Block Anyway");
            if (!proceed) return;
        }

        var (success, error) = await _listeningPortsViewModel.BlockAppInboundAsync(vm, _firewallService);
        if (success)
        {
            await ShowMessageAsync("App Inbound Traffic Blocked",
                $"Incoming network connections to {vm.DisplayAppName} are now blocked.\nThis app also appears on the Apps tab.");
            await _viewModel.LoadAppsAsync();
            UpdateAppsVisibility();
        }
        else
        {
            await ShowMessageAsync("Block Failed", error ?? "Could not create inbound firewall rule for this app.");
        }
    }

    private async Task UnblockAppInboundWithAuthAsync(ListeningPortItemViewModel vm)
    {
        if (!_isElevated)
        {
            await ShowMessageAsync("Administrator Rights Required",
                "Removing Windows Firewall rules requires administrator privileges. Please restart AppLock as administrator.");
            return;
        }

        if (!await EnsureFirewallAuthorizedAsync())
            return;

        var (success, error) = await _listeningPortsViewModel.UnblockAppInboundAsync(vm, _firewallService);
        if (success)
        {
            await ShowMessageAsync("App Inbound Traffic Unblocked",
                $"Inbound firewall rule for {vm.DisplayAppName} has been removed.");
            await _viewModel.LoadAppsAsync();
            UpdateAppsVisibility();
        }
        else
        {
            await ShowMessageAsync("Unblock Failed", error ?? "Could not remove inbound rule.");
        }
    }

    private async void RepairPortRules_Click(object sender, RoutedEventArgs e)
    {
        if (!_isElevated)
        {
            await ShowMessageAsync("Administrator Rights Required",
                "Checking and repairing firewall rules requires administrator privileges. Please restart AppLock as administrator.");
            return;
        }

        int repaired = await _listeningPortsViewModel.CheckAndRepairDriftedPortRulesAsync(_firewallService);
        if (repaired > 0)
        {
            await ShowMessageAsync("Port Rules Repaired",
                $"Successfully recreated {repaired} missing port block rule(s) in Windows Firewall.");
        }
        else
        {
            await ShowMessageAsync("Firewall In Sync",
                "All AppLock port block rules are active and verified in Windows Firewall. No drift detected.");
        }
    }

    private void PortMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ListeningPortItemViewModel vm)
        {
            var flyout = new MenuFlyout();

            if (vm.IsBlockedByAppLock)
            {
                var unblockPortItem = new MenuFlyoutItem
                {
                    Text = $"Unblock port {vm.Port}",
                    Icon = new FontIcon { Glyph = "\uE785" }
                };
                unblockPortItem.Click += async (_, _) => await UnblockPortWithAuthAsync(vm);
                flyout.Items.Add(unblockPortItem);
            }
            else
            {
                var blockPortItem = new MenuFlyoutItem
                {
                    Text = $"Block port {vm.Port}",
                    Icon = new FontIcon { Glyph = "\uF140" }
                };
                blockPortItem.Click += async (_, _) => await BlockPortWithSafetyAsync(vm);
                flyout.Items.Add(blockPortItem);
            }

            if (vm.HasAppPath)
            {
                var blockAppItem = new MenuFlyoutItem
                {
                    Text = $"Block app incoming ({vm.AppName})",
                    Icon = new FontIcon { Glyph = "\uE718" }
                };
                blockAppItem.Click += async (_, _) => await BlockAppInboundWithSafetyAsync(vm);
                flyout.Items.Add(blockAppItem);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            var detailsItem = new MenuFlyoutItem
            {
                Text = "Show details",
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            detailsItem.Click += async (_, _) => await ShowPortDetailsDialogAsync(vm);
            flyout.Items.Add(detailsItem);

            var copyItem = new MenuFlyoutItem
            {
                Text = "Copy details",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyItem.Click += (_, _) => CopyPortDetails(vm);
            flyout.Items.Add(copyItem);

            if (vm.HasAppPath)
            {
                var locItem = new MenuFlyoutItem
                {
                    Text = "Open file location",
                    Icon = new FontIcon { Glyph = "\uED25" }
                };
                locItem.Click += (_, _) => OpenFileLocation(vm);
                flyout.Items.Add(locItem);
            }

            if (vm.HasServices)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                var svcItem = new MenuFlyoutItem
                {
                    Text = "Open Services",
                    Icon = new FontIcon { Glyph = "\uE71D" }
                };
                svcItem.Click += (_, _) => OpenServices();
                flyout.Items.Add(svcItem);
            }

            flyout.ShowAt(btn);
        }
    }

    private async void ShowPortDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is ListeningPortItemViewModel vm)
        {
            await ShowPortDetailsDialogAsync(vm);
        }
    }

    private void OpenFileLocation(ListeningPortItemViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.AppPath)) return;
        try
        {
            if (System.IO.File.Exists(vm.AppPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{vm.AppPath}\"") { UseShellExecute = true });
            }
            else if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(vm.AppPath)))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(vm.AppPath)}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InternetAccessPage] OpenFileLocation failed: {ex.Message}");
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is ListeningPortItemViewModel vm)
        {
            OpenFileLocation(vm);
        }
    }

    private void CopyPortDetails(ListeningPortItemViewModel vm)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Port: {vm.Port} ({vm.Protocol})");
            sb.AppendLine($"Address: {vm.AddressAndPort}");
            sb.AppendLine($"Reach Scope: {vm.ScopeText}");
            sb.AppendLine($"Service: {vm.ServiceName ?? "Unknown"}");
            sb.AppendLine($"App Name: {vm.DisplayAppName}");
            sb.AppendLine($"Publisher: {vm.Publisher ?? "Unknown"}");
            sb.AppendLine($"Executable: {vm.AppPath ?? "Not available / System"}");
            sb.AppendLine($"PID: {vm.Pid}");
            sb.AppendLine($"User: {vm.ProcessUser}");
            sb.AppendLine($"Started: {vm.ProcessStartTimeText}");
            sb.AppendLine($"Firewall Exposure: {vm.ExposureText}");
            if (!string.IsNullOrEmpty(vm.MatchingRuleName))
                sb.AppendLine($"Matching Rule: {vm.MatchingRuleName}");
            sb.AppendLine($"Network Profile: {vm.ActiveProfileName}");

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(sb.ToString());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InternetAccessPage] CopyPortDetails failed: {ex.Message}");
        }
    }

    private void CopyPortDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is ListeningPortItemViewModel vm)
        {
            CopyPortDetails(vm);
        }
    }

    private void OpenServices()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("services.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InternetAccessPage] OpenServices failed: {ex.Message}");
        }
    }

    private void OpenServices_Click(object sender, RoutedEventArgs e)
    {
        OpenServices();
    }

    private async Task ShowPortDetailsDialogAsync(ListeningPortItemViewModel port)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };

        Border CreateCard(string header, (string Label, string Value)[] rows)
        {
            var card = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12)
            };

            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources["AccentTextFillColorPrimaryBrush"]
            });

            foreach (var (lbl, val) in rows)
            {
                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lblBlock = new TextBlock
                {
                    Text = lbl,
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources["TextFillColorSecondaryBrush"]
                };
                Grid.SetColumn(lblBlock, 0);
                grid.Children.Add(lblBlock);

                var valBlock = new TextBlock
                {
                    Text = val,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(valBlock, 1);
                grid.Children.Add(valBlock);

                stack.Children.Add(grid);
            }

            card.Child = stack;
            return card;
        }

        string appLockRulesSummary = "None";
        try
        {
            var settings = ServiceContainer.Settings;
            var matchedPort = settings.ListeningPorts?.PortRules?.FirstOrDefault(r =>
                string.Equals(r.Protocol, port.Protocol, StringComparison.OrdinalIgnoreCase) &&
                (r.LocalPorts == port.Port.ToString() || r.LocalPorts == "*"));
            var matchedApp = !string.IsNullOrEmpty(port.AppPath)
                ? settings.NetworkRules?.FirstOrDefault(r => string.Equals(r.ExePath, port.AppPath, StringComparison.OrdinalIgnoreCase))
                : null;

            var list = new List<string>();
            if (matchedPort != null) list.Add($"Port rule: {matchedPort.LocalPorts}/{matchedPort.Protocol} (Block Inbound)");
            if (matchedApp != null && matchedApp.BlockInbound) list.Add($"App rule: {System.IO.Path.GetFileName(port.AppPath)} (Block Inbound)");
            if (list.Count > 0) appLockRulesSummary = string.Join("; ", list);
        }
        catch { }

        // Process Card
        panel.Children.Add(CreateCard("PROCESS", new[]
        {
            ("Application", port.DisplayAppName),
            ("Publisher", port.Publisher ?? "Unknown"),
            ("Executable Path", port.AppPath ?? "Not available / System protected"),
            ("Process ID", $"PID {port.Pid}"),
            ("Process User", port.ProcessUser),
            ("Started Time", port.ProcessStartTimeText)
        }));

        // Socket & Reach Card
        panel.Children.Add(CreateCard("SOCKET & REACHABILITY", new[]
        {
            ("Protocol & Port", $"{port.Protocol} · Port {port.Port}"),
            ("Service Name", !string.IsNullOrEmpty(port.ServiceName) ? port.ServiceName : "Unknown"),
            ("Bind Address", port.AddressAndPort),
            ("Reach Scope", $"{port.ScopeText}\n{port.ScopeTooltip}")
        }));

        // Firewall Exposure Estimate Card
        panel.Children.Add(CreateCard("FIREWALL EXPOSURE (ESTIMATE)", new[]
        {
            ("Exposure Estimate", port.ExposureText),
            ("Matching Rule", !string.IsNullOrEmpty(port.MatchingRuleName) ? port.MatchingRuleName : "None"),
            ("Network Profile", port.ActiveProfileName),
            ("AppLock Rules", appLockRulesSummary),
            ("Notice", "AppLock estimates this from your firewall rules. Complex rules, third-party firewalls or group policy can change the real result.")
        }));

        // Advice Card
        var adviceCard = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Resources["ControlFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10)
        };
        var adviceStack = new StackPanel { Spacing = 3 };
        adviceStack.Children.Add(new TextBlock
        {
            Text = "How to close this port",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12
        });
        adviceStack.Children.Add(new TextBlock
        {
            Text = "To close a port permanently, stop the app or service that opens it, or change its settings to listen on localhost only.",
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });
        adviceCard.Child = adviceStack;
        panel.Children.Add(adviceCard);

        var dialog = new ContentDialog
        {
            Title = $"Port {port.Port} ({port.Protocol}) Details",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)XamlRoot.Content).ActualTheme
        };

        await dialog.ShowAsync();
    }
}

// ── Converter: bool (IsTrusted) → dot colour ──────────────────────────────────
public class BoolToTrustColorConverter : IValueConverter
{
    // Trusted → green, Untrusted → orange
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TrustedBrush
        = new(Color.FromArgb(255, 52, 199, 89));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush UntrustedBrush
        = new(Color.FromArgb(255, 255, 149, 0));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? TrustedBrush : UntrustedBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

// ── Converter: bool (IsOnline) → status pill color ────────────────────────────
public class BoolToStatusColorConverter : IValueConverter
{
    // Online → green, Offline → gray
    private static readonly Color OnlineColor = Color.FromArgb(255, 52, 199, 89);   // Green #34C759
    private static readonly Color OfflineColor = Color.FromArgb(255, 142, 142, 147); // Gray #8E8E93

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? OnlineColor : OfflineColor;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
