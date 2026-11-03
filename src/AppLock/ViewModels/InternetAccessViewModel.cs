using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.Firewall;
using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace AppLock.ViewModels;

public partial class InternetAccessViewModel : ObservableObject
{
    private readonly IFirewallService _firewallService;
    private readonly SettingsStore _settingsStore;
    private readonly RuleSyncService _ruleSyncService;
    private readonly InstalledAppsService _installedAppsService;
    private readonly FirewallAuthService _authService;
    private readonly FirewallImportExportService _importExportService;
    private readonly HashSet<NetworkRuleViewModel> _iconsLoaded = new();

    [ObservableProperty]
    private ObservableCollection<NetworkRuleViewModel> _apps = new();

    [ObservableProperty]
    private bool _hasApps;

    [ObservableProperty]
    private int _driftCount;

    public InternetAccessViewModel(IFirewallService firewallService, SettingsStore settingsStore, InstalledAppsService installedAppsService)
    {
        _firewallService = firewallService;
        _settingsStore = settingsStore;
        _ruleSyncService = new RuleSyncService(firewallService);
        _installedAppsService = installedAppsService;
        _authService = new FirewallAuthService();
        _importExportService = new FirewallImportExportService();
    }

    public FirewallAuthService AuthService => _authService;
    public FirewallImportExportService ImportExportService => _importExportService;

    public async Task LoadAppsAsync()
    {
        try
        {
            var settings = ServiceContainer.Settings;
            Apps.Clear();
            _iconsLoaded.Clear();

            foreach (var entry in settings.NetworkRules)
            {
                var viewModel = new NetworkRuleViewModel(entry);
                Apps.Add(viewModel);
            }

            HasApps = Apps.Count > 0;

            // Load icons for first 15 apps immediately
            await LoadInitialIconsAsync();

            // Sync with actual firewall rules
            await SyncWithFirewallAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading apps: {ex.Message}");
        }
    }

    private async Task LoadInitialIconsAsync()
    {
        try
        {
            var initialApps = Apps.Take(15).ToList();
            Debug.WriteLine($"[InternetAccess] Loading icons for {initialApps.Count} initial apps...");

            foreach (var app in initialApps)
            {
                if (!_iconsLoaded.Contains(app))
                {
                    _iconsLoaded.Add(app);
                    await app.LoadIconAsync();
                }
            }

            Debug.WriteLine("[InternetAccess] Initial icon loading complete");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InternetAccess] LoadInitialIconsAsync error: {ex}");
        }
    }

    public async Task LoadIconsForVisibleAppsAsync(IEnumerable<NetworkRuleViewModel> visibleApps)
    {
        var appsToLoad = visibleApps
            .Where(app => !_iconsLoaded.Contains(app))
            .ToList();

        if (appsToLoad.Count == 0)
            return;

        Debug.WriteLine($"[InternetAccess] Loading {appsToLoad.Count} icons...");

        foreach (var app in appsToLoad)
        {
            _iconsLoaded.Add(app);
        }

        // Load in batches of 5
        const int batchSize = 5;
        for (int i = 0; i < appsToLoad.Count; i += batchSize)
        {
            var batch = appsToLoad.Skip(i).Take(batchSize);
            var tasks = batch.Select(app => app.LoadIconAsync()).ToList();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InternetAccess] Batch error: {ex.Message}");
            }

            if (i + batchSize < appsToLoad.Count)
            {
                await Task.Delay(100);
            }
        }

        Debug.WriteLine($"[InternetAccess] Completed loading {appsToLoad.Count} icons");
    }

    public async Task AddAppAsync(string exePath, string displayName, bool blockImmediately = true, string direction = "Outgoing", string? packageFamilyName = null, BitmapImage? icon = null)
    {
        try
        {
            LogService.LogFirewallAction("ADD_START", displayName, $"Starting add: {exePath}");
            Debug.WriteLine($"[AddApp] Step 1: Checking duplicates for {displayName}");
            
            // Check for duplicates
            var key = packageFamilyName ?? exePath;
            if (Apps.Any(a => 
                (a.Entry.PackageFamilyName != null && a.Entry.PackageFamilyName.Equals(packageFamilyName, StringComparison.OrdinalIgnoreCase)) ||
                (a.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase))))
            {
                LogService.LogFirewallAction("ADD_DUPLICATE", displayName, "Already exists");
                Debug.WriteLine($"[AddApp] Already added: {displayName}");
                return; // Already added
            }

            LogService.LogFirewallAction("ADD_STEP2", displayName, "Creating NetworkRuleEntry");
            Debug.WriteLine($"[AddApp] Step 2: Creating NetworkRuleEntry for {displayName}");
            
            var entry = new NetworkRuleEntry
            {
                Id = Guid.NewGuid(),
                DisplayName = displayName,
                ExePath = exePath,
                PackageFamilyName = packageFamilyName,
                BlockInbound = direction == "Both" || direction == "Incoming",
                BlockOutbound = direction == "Both" || direction == "Outgoing",
                Blocked = blockImmediately,
                Profiles = 0x7
            };

            LogService.LogFirewallAction("ADD_STEP3", displayName, "Creating ViewModel");
            Debug.WriteLine($"[AddApp] Step 3: Creating ViewModel for {displayName}");
            
            // Create ViewModel - this must be done on UI thread to capture DispatcherQueue
            NetworkRuleViewModel viewModel;
            try
            {
                viewModel = new NetworkRuleViewModel(entry);
                LogService.LogFirewallAction("ADD_VM_OK", displayName, "ViewModel created");
                Debug.WriteLine($"[AddApp] ViewModel created successfully");
            }
            catch (Exception vmEx)
            {
                LogService.LogFirewallAction("ADD_VM_ERROR", displayName, $"{vmEx.GetType().Name}: {vmEx.Message}");
                Debug.WriteLine($"[AddApp] ViewModel creation failed: {vmEx.GetType().Name} - {vmEx.Message}");
                throw;
            }
            
            LogService.LogFirewallAction("ADD_STEP4", displayName, "Setting icon");
            Debug.WriteLine($"[AddApp] Step 4: Setting icon for {displayName}");
            
            // Set icon if provided, otherwise it will load asynchronously
            if (icon != null)
            {
                viewModel.Icon = icon;
            }
            else
            {
                // Trigger icon loading asynchronously
                _ = viewModel.LoadIconAsync();
            }
            
            LogService.LogFirewallAction("ADD_STEP5", displayName, "Adding to collection");
            Debug.WriteLine($"[AddApp] Step 5: Adding to collection");
            Apps.Add(viewModel);
            HasApps = true;

            LogService.LogFirewallAction("ADD_STEP6", displayName, "Saving to settings");
            Debug.WriteLine($"[AddApp] Step 6: Saving to settings");
            ServiceContainer.Settings.NetworkRules.Add(entry);
            _settingsStore.Save(ServiceContainer.Settings);

            LogService.LogFirewallAction("ADD_STEP7", displayName, blockImmediately ? "Blocked immediately" : "Added without blocking");
            Debug.WriteLine($"[AddApp] Step 7: Logging action");

            if (blockImmediately)
            {
                LogService.LogFirewallAction("ADD_STEP8", displayName, "Calling BlockAppAsync");
                Debug.WriteLine($"[AddApp] Step 8: Blocking app");
                await BlockAppAsync(viewModel);
                LogService.LogFirewallAction("ADD_BLOCK_DONE", displayName, "BlockAppAsync completed");
            }
            
            LogService.LogFirewallAction("ADD_COMPLETE", displayName, "Successfully added");
            Debug.WriteLine($"[AddApp] Completed successfully for {displayName}");
        }
        catch (Exception ex)
        {
            LogService.LogFirewallAction("ADD_ERROR", displayName, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            Debug.WriteLine($"[AddApp] ERROR: {ex.GetType().Name} - {ex.Message}");
            Debug.WriteLine($"[AddApp] Stack trace: {ex.StackTrace}");
            throw; // Re-throw to let caller handle it
        }
    }

    public HashSet<string> GetExistingPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in Apps)
        {
            if (!string.IsNullOrEmpty(app.Entry.PackageFamilyName))
                paths.Add(app.Entry.PackageFamilyName);
            else if (!string.IsNullOrEmpty(app.ExePath))
                paths.Add(app.ExePath);
        }
        return paths;
    }

    public async Task<bool> BlockAppAsync(NetworkRuleViewModel app)
    {
        try
        {
            string ruleName = app.Entry.CustomRuleName ?? $"AppLock - {app.DisplayName}";
            
            bool success = await _firewallService.CreateRuleAsync(
                ruleName,
                app.ExePath,
                app.Entry.BlockInbound,
                app.Entry.BlockOutbound,
                app.Entry.Profiles,
                app.Entry.Id);

            if (success)
            {
                app.SetBlocked(true);
                _settingsStore.Save(ServiceContainer.Settings);
                LogService.LogFirewallAction("BLOCK", app.DisplayName, $"Direction: {(app.Entry.BlockInbound && app.Entry.BlockOutbound ? "Both" : app.Entry.BlockInbound ? "Inbound" : "Outbound")}");
            }

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error blocking app: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UnblockAppAsync(NetworkRuleViewModel app)
    {
        try
        {
            string ruleName = app.Entry.CustomRuleName ?? $"AppLock - {app.DisplayName}";
            
            bool success = true;

            // Remove both inbound and outbound rules
            if (app.Entry.BlockOutbound)
            {
                success &= await _firewallService.RemoveRuleAsync($"{ruleName} (Outbound)");
            }

            if (app.Entry.BlockInbound)
            {
                success &= await _firewallService.RemoveRuleAsync($"{ruleName} (Inbound)");
            }

            if (success)
            {
                app.SetBlocked(false);
                _settingsStore.Save(ServiceContainer.Settings);
                LogService.LogFirewallAction("UNBLOCK", app.DisplayName);
            }

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error unblocking app: {ex.Message}");
            return false;
        }
    }

    public void RemoveApp(NetworkRuleViewModel app)
    {
        Apps.Remove(app);
        ServiceContainer.Settings.NetworkRules.Remove(app.Entry);
        _settingsStore.Save(ServiceContainer.Settings);
        HasApps = Apps.Count > 0;
        LogService.LogFirewallAction("REMOVE", app.DisplayName);
    }

    public async Task<bool> UpdateDirectionAsync(NetworkRuleViewModel app, string newDirection)
    {
        try
        {
            string ruleName = app.Entry.CustomRuleName ?? $"AppLock - {app.DisplayName}";
            
            // Remove existing rules first
            await _firewallService.RemoveRuleAsync($"{ruleName} (Outbound)");
            await _firewallService.RemoveRuleAsync($"{ruleName} (Inbound)");

            // Update the entry
            app.SetDirection(newDirection);

            // Create new rules based on direction if blocked
            if (app.IsBlocked)
            {
                bool success = await _firewallService.CreateRuleAsync(
                    ruleName,
                    app.ExePath,
                    app.Entry.BlockInbound,
                    app.Entry.BlockOutbound,
                    app.Entry.Profiles,
                    app.Entry.Id);

                if (success)
                {
                    _settingsStore.Save(ServiceContainer.Settings);
                    LogService.LogFirewallAction("UPDATE_DIRECTION", app.DisplayName, $"Direction: {newDirection}");
                    return true;
                }
            }
            else
            {
                // Just save the preference, no rules to create
                _settingsStore.Save(ServiceContainer.Settings);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating direction: {ex.Message}");
            return false;
        }
    }

    public async Task RefreshAsync()
    {
        await LoadAppsAsync();
    }

    private async Task SyncWithFirewallAsync()
    {
        try
        {
            var entries = ServiceContainer.Settings.NetworkRules;
            var driftList = await _ruleSyncService.DetectDriftAsync(entries);

            DriftCount = driftList.Count;

            foreach (var drift in driftList)
            {
                var app = Apps.FirstOrDefault(a => a.Entry.Id == drift.Entry.Id);
                if (app != null)
                {
                    app.StatusText = drift.GetDescription();
                    app.StatusBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 193, 7)); // Yellow
                    app.HasDrift = true;
                }
            }

            // Clear drift flag for apps without drift
            foreach (var app in Apps.Where(a => !driftList.Any(d => d.Entry.Id == a.Entry.Id)))
            {
                app.HasDrift = false;
                app.UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error syncing with firewall: {ex.Message}");
        }
    }

    public async Task<RepairResult> RepairAllDriftAsync()
    {
        try
        {
            var entries = ServiceContainer.Settings.NetworkRules;
            var driftList = await _ruleSyncService.DetectDriftAsync(entries);
            
            var result = await _ruleSyncService.RepairAllDriftAsync(driftList);
            
            // Refresh to update UI
            await RefreshAsync();
            
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error repairing all drift: {ex.Message}");
            return new RepairResult { Repaired = 0, Failed = 0 };
        }
    }

    public async Task<bool> RepairSingleAsync(NetworkRuleViewModel app)
    {
        try
        {
            var drift = await _ruleSyncService.CheckEntryDriftAsync(app.Entry);
            if (drift != null)
            {
                bool success = await _ruleSyncService.RepairDriftAsync(drift);
                if (success)
                {
                    app.HasDrift = false;
                    app.UpdateStatus();
                    await SyncWithFirewallAsync();
                }
                return success;
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error repairing single drift: {ex.Message}");
            return false;
        }
    }
}
