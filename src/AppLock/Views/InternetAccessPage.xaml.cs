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

namespace AppLock.Views;

public sealed partial class InternetAccessPage : Page
{
    private readonly IFirewallService _firewallService;
    private readonly InternetAccessViewModel _viewModel;
    private readonly ActiveConnectionViewModel _activeConnectionsViewModel;
    private readonly InstalledAppsService _installedAppsService;
    private readonly SettingsStore _settingsStore;
    private bool _isElevated;

    public InternetAccessViewModel ViewModel => _viewModel;
    public ActiveConnectionViewModel ActiveConnectionsViewModel => _activeConnectionsViewModel;

    public InternetAccessPage()
    {
        InitializeComponent();
        _firewallService = new WindowsFirewallService();
        _installedAppsService = new InstalledAppsService();
        _settingsStore = ServiceContainer.SettingsStore;
        _viewModel = new InternetAccessViewModel(_firewallService, _settingsStore, _installedAppsService);
        _activeConnectionsViewModel = new ActiveConnectionViewModel();
        DataContext = _viewModel;
        Loaded += InternetAccessPage_Loaded;
        Unloaded += InternetAccessPage_Unloaded;
    }

    private void InternetAccessPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _activeConnectionsViewModel.StopAutoRefresh();
    }

    private async void InternetAccessPage_Loaded(object sender, RoutedEventArgs e)
    {
        CheckElevation();
        await CheckFirewallStatusAsync();
        await _viewModel.LoadAppsAsync();
        UpdateAppsVisibility();
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
        AppsTabButton.Style = (Style)Resources["AccentButtonStyle"];
        ActiveConnectionsTabButton.Style = null;
        
        // Stop auto-refresh when leaving Active connections tab
        _activeConnectionsViewModel.StopAutoRefresh();
    }

    private void ActiveConnectionsTabButton_Click(object sender, RoutedEventArgs e)
    {
        AppsTabContent.Visibility = Visibility.Collapsed;
        ActiveConnectionsTabContent.Visibility = Visibility.Visible;
        ActiveConnectionsTabButton.Style = (Style)Resources["AccentButtonStyle"];
        AppsTabButton.Style = null;
        
        // Start auto-refresh when tab becomes visible
        _ = _activeConnectionsViewModel.LoadConnectionsAsync();
        _activeConnectionsViewModel.StartAutoRefresh(3);
    }

    // Command bar actions
    private async void BrowseForProgram_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);
            
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");
            
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                string exePath = file.Path;
                
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

                await _viewModel.AddAppAsync(exePath, displayName, blockImmediately: true);
                UpdateAppsVisibility();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error browsing for program: {ex.Message}");
            await ShowMessageAsync("Error", $"Failed to add program: {ex.Message}");
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

                await _viewModel.AddAppAsync(selected.ProcessPath, selected.ProcessName, blockImmediately: true);
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

                await _viewModel.AddAppAsync(pickedWindow.ProcessPath, displayName, blockImmediately: true);
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
                        packageFamilyName: app.PackageFamilyName);
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
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
        UpdateAppsVisibility();
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

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = AppsListView.SelectedItems.Cast<NetworkRuleViewModel>().ToList();
        foreach (var app in selected)
        {
            _viewModel.RemoveApp(app);
        }
        UpdateAppsVisibility();
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
            Content = "This will remove all firewall rules created by AppLock. This action cannot be undone.\n\nAre you sure?",
            PrimaryButtonText = "Remove All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            foreach (var app in _viewModel.Apps.ToList())
            {
                await _viewModel.UnblockAppAsync(app);
                _viewModel.RemoveApp(app);
            }
            UpdateAppsVisibility();
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
            XamlRoot = XamlRoot
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
            XamlRoot = XamlRoot
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
            await _viewModel.AddAppAsync(connection.ProcessPath, displayName, blockImmediately: true);
            UpdateAppsVisibility();

            await ShowMessageAsync("Blocked", $"{displayName} has been added to the blocked apps list.");
        }
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
            if (toggle.FocusState != FocusState.Unfocused || e.OriginalSource == toggle)
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
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            
            if (file != null)
            {
                var importService = _viewModel.ImportExportService;
                var exportData = await importService.ImportFromFileAsync(file.Path);
                
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

                var dialog = new Dialogs.ImportExportDialog(XamlRoot, newRules, conflicts);
                var result = await dialog.ShowAsync();
                
                if (result == ContentDialogResult.Primary)
                {
                    foreach (var rule in dialog.SelectedRules)
                    {
                        ServiceContainer.Settings.NetworkRules.Add(rule);
                        _viewModel.Apps.Add(new NetworkRuleViewModel(rule));
                    }
                    
                    _settingsStore.Save(ServiceContainer.Settings);
                    UpdateAppsVisibility();
                    
                    await ShowMessageAsync("Import Complete", $"Imported {dialog.SelectedRules.Count} rules.");
                }
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Import Error", $"Failed to import rules: {ex.Message}");
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

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON file", new List<string> { ".json" });
            picker.SuggestedFileName = $"AppLock-Firewall-Rules-{DateTime.Now:yyyy-MM-dd}";
            
            var file = await picker.PickSaveFileAsync();
            
            if (file != null)
            {
                var rules = _viewModel.Apps.Select(a => a.Entry).ToList();
                await _viewModel.ImportExportService.ExportToFileAsync(rules, file.Path);
                await ShowMessageAsync("Export Complete", $"Exported {rules.Count} rules to {file.Name}");
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Export Error", $"Failed to export rules: {ex.Message}");
        }
    }
}