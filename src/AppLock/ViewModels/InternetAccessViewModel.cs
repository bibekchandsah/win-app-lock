using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
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

            foreach (var entry in settings.NetworkRules)
            {
                Apps.Add(new NetworkRuleViewModel(entry));
            }

            HasApps = Apps.Count > 0;

            // Sync with actual firewall rules
            await SyncWithFirewallAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading apps: {ex.Message}");
        }
    }

    public async Task AddAppAsync(string exePath, string displayName, bool blockImmediately = true, string direction = "Outgoing", string? packageFamilyName = null)
    {
        try
        {
            // Check for duplicates
            var key = packageFamilyName ?? exePath;
            if (Apps.Any(a => 
                (a.Entry.PackageFamilyName != null && a.Entry.PackageFamilyName.Equals(packageFamilyName, StringComparison.OrdinalIgnoreCase)) ||
                (a.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase))))
            {
                return; // Already added
            }

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

            var viewModel = new NetworkRuleViewModel(entry);
            Apps.Add(viewModel);
            HasApps = true;

            ServiceContainer.Settings.NetworkRules.Add(entry);
            _settingsStore.Save(ServiceContainer.Settings);

            LogService.LogFirewallAction("ADD", displayName, blockImmediately ? "Blocked immediately" : "Added without blocking");

            if (blockImmediately)
            {
                await BlockAppAsync(viewModel);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error adding app: {ex.Message}");
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
