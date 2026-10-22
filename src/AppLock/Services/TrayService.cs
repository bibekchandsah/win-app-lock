using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using H.NotifyIcon;
using AppLock.Models;

namespace AppLock.Services;

/// <summary>
/// Manages the system tray icon and its context menu.
/// Provides: Open, Enable/Disable protection (with auth), Emergency lock, Exit (with auth)
/// </summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private MenuFlyoutItem? _openMenuItem;
    private MenuFlyoutItem? _protectionMenuItem;
    private MenuFlyoutItem? _emergencyLockMenuItem;
    private MenuFlyoutItem? _exitMenuItem;
    
    private readonly SettingsStore _settingsStore;
    private readonly AuthService _authService;
    private readonly LockManager _lockManager;
    
    public event EventHandler? OpenMainWindowRequested;
    public event EventHandler? EmergencyLockRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? ProtectionToggleRequested;

    public TrayService(SettingsStore settingsStore, AuthService authService, LockManager lockManager)
    {
        _settingsStore = settingsStore;
        _authService = authService;
        _lockManager = lockManager;
    }

    public void Initialize()
    {
        try
        {
            // Get tray icon from App.xaml resources
            _trayIcon = Application.Current.Resources["AppTrayIcon"] as TaskbarIcon;
            
            if (_trayIcon == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: Could not find AppTrayIcon in resources");
                return;
            }

            // Load icon
            LoadIcon();
            
            // Get menu items from the context flyout by index (since x:Name might not work in resources)
            if (_trayIcon.ContextFlyout is MenuFlyout menuFlyout)
            {
                System.Diagnostics.Debug.WriteLine($"Context menu has {menuFlyout.Items.Count} items");
                
                // Items are in order: Open (0), Separator (1), Protection (2), Separator (3), Emergency (4), Separator (5), Exit (6)
                if (menuFlyout.Items.Count >= 7)
                {
                    _openMenuItem = menuFlyout.Items[0] as MenuFlyoutItem;
                    _protectionMenuItem = menuFlyout.Items[2] as MenuFlyoutItem;
                    _emergencyLockMenuItem = menuFlyout.Items[4] as MenuFlyoutItem;
                    _exitMenuItem = menuFlyout.Items[6] as MenuFlyoutItem;
                    
                    System.Diagnostics.Debug.WriteLine($"Open: {_openMenuItem != null}, Protection: {_protectionMenuItem != null}, Emergency: {_emergencyLockMenuItem != null}, Exit: {_exitMenuItem != null}");
                }
                
                // Wire up click events
                if (_openMenuItem != null)
                {
                    _openMenuItem.Click += OpenMenuItem_Click;
                    System.Diagnostics.Debug.WriteLine("Open menu item wired");
                }
                
                if (_protectionMenuItem != null)
                {
                    _protectionMenuItem.Click += ProtectionMenuItem_Click;
                    System.Diagnostics.Debug.WriteLine("Protection menu item wired");
                }
                
                if (_emergencyLockMenuItem != null)
                {
                    _emergencyLockMenuItem.Click += EmergencyLockMenuItem_Click;
                    System.Diagnostics.Debug.WriteLine("Emergency lock menu item wired");
                }
                
                if (_exitMenuItem != null)
                {
                    _exitMenuItem.Click += ExitMenuItem_Click;
                    System.Diagnostics.Debug.WriteLine("Exit menu item wired");
                }
            }
            
            UpdateTooltip();
            UpdateProtectionMenuItems();
            
            // IMPORTANT: Force create the tray icon - this makes it actually appear!
            _trayIcon.ForceCreate();
            
            System.Diagnostics.Debug.WriteLine("Tray icon initialized and ForceCreate() called");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TrayService initialization error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Open menu item clicked!");
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ProtectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Protection menu item clicked!");
        var settings = _settingsStore.Load();
        ProtectionToggleRequested?.Invoke(this, !settings.IsProtectionEnabled);
    }

    private void EmergencyLockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Emergency lock menu item clicked!");
        EmergencyLockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Exit menu item clicked!");
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private MenuFlyoutItem? FindMenuItemByName(MenuFlyout menu, string name)
    {
        System.Diagnostics.Debug.WriteLine($"Looking for menu item: {name}");
        System.Diagnostics.Debug.WriteLine($"Menu has {menu.Items.Count} items");
        
        foreach (var item in menu.Items)
        {
            System.Diagnostics.Debug.WriteLine($"  Item type: {item.GetType().Name}, Name: {(item is MenuFlyoutItem mfi ? mfi.Name : "N/A")}");
            
            if (item is MenuFlyoutItem menuItem && menuItem.Name == name)
            {
                System.Diagnostics.Debug.WriteLine($"  ✓ Found {name}!");
                return menuItem;
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"  ✗ Did not find {name}");
        return null;
    }

    private void LoadIcon()
    {
        if (_trayIcon == null) return;

        try
        {
            var assemblyLocation = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var iconPath = System.IO.Path.Combine(assemblyLocation ?? "", "icons", "icon.ico");
            
            if (System.IO.File.Exists(iconPath))
            {
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                System.Diagnostics.Debug.WriteLine($"Tray icon loaded from: {iconPath}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Tray icon not found at: {iconPath}");
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading icon: {ex.Message}");
        }
    }

    public void UpdateTooltip()
    {
        if (_trayIcon == null) return;

        var settings = _settingsStore.Load();
        var lockedCount = settings.LockRules.Count;
        
        string tooltip;
        if (settings.IsProtectionEnabled)
        {
            tooltip = $"AppLock - Protection Active ({lockedCount} {(lockedCount == 1 ? "rule" : "rules")})";
        }
        else
        {
            tooltip = "AppLock - Protection Disabled";
        }
        
        _trayIcon.ToolTipText = tooltip;
    }

    public void UpdateProtectionMenuItems()
    {
        if (_protectionMenuItem == null) return;
        
        var settings = _settingsStore.Load();
        _protectionMenuItem.Text = settings.IsProtectionEnabled ? "Disable Protection" : "Enable Protection";
        
        UpdateTooltip();
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"Notification: {title} - {message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing notification: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Don't dispose the tray icon as it's managed by App.xaml resources
        _trayIcon = null;
    }
}
