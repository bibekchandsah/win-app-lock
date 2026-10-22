using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using H.NotifyIcon;
using AppLock.Models;
using CommunityToolkit.Mvvm.Input;

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
            
            // Get menu items from the context flyout
            if (_trayIcon.ContextFlyout is MenuFlyout menuFlyout)
            {
                System.Diagnostics.Debug.WriteLine($"Context menu has {menuFlyout.Items.Count} items");
                
                foreach (var item in menuFlyout.Items)
                {
                    if (item is MenuFlyoutItem mfi)
                    {
                        var text = mfi.Text ?? "";
                        if (text.Contains("Open", StringComparison.OrdinalIgnoreCase))
                            _openMenuItem = mfi;
                        else if (text.Contains("Protection", StringComparison.OrdinalIgnoreCase))
                            _protectionMenuItem = mfi;
                        else if (text.Contains("Emergency", StringComparison.OrdinalIgnoreCase))
                            _emergencyLockMenuItem = mfi;
                        else if (text.Contains("Exit", StringComparison.OrdinalIgnoreCase))
                            _exitMenuItem = mfi;
                    }
                }

                // Fallback to index if needed
                if (_openMenuItem == null && menuFlyout.Items.Count > 0)
                    _openMenuItem = menuFlyout.Items[0] as MenuFlyoutItem;
                if (_protectionMenuItem == null && menuFlyout.Items.Count > 2)
                    _protectionMenuItem = menuFlyout.Items[2] as MenuFlyoutItem;
                if (_emergencyLockMenuItem == null && menuFlyout.Items.Count > 4)
                    _emergencyLockMenuItem = menuFlyout.Items[4] as MenuFlyoutItem;
                if (_exitMenuItem == null && menuFlyout.Items.Count > 6)
                    _exitMenuItem = menuFlyout.Items[6] as MenuFlyoutItem;
                
                // Wire up commands exclusively (fires reliably without duplicate events)
                if (_openMenuItem != null)
                {
                    _openMenuItem.Click -= OpenMenuItem_Click;
                    _openMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Open menu item Command executed!");
                        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
                    });
                    System.Diagnostics.Debug.WriteLine("Open menu item wired");
                }
                
                if (_protectionMenuItem != null)
                {
                    _protectionMenuItem.Click -= ProtectionMenuItem_Click;
                    _protectionMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Protection menu item Command executed!");
                        var settings = _settingsStore.Load();
                        ProtectionToggleRequested?.Invoke(this, !settings.IsProtectionEnabled);
                    });
                    System.Diagnostics.Debug.WriteLine("Protection menu item wired");
                }
                
                if (_emergencyLockMenuItem != null)
                {
                    _emergencyLockMenuItem.Click -= EmergencyLockMenuItem_Click;
                    _emergencyLockMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Emergency lock menu item Command executed!");
                        EmergencyLockRequested?.Invoke(this, EventArgs.Empty);
                    });
                    System.Diagnostics.Debug.WriteLine("Emergency lock menu item wired");
                }
                
                if (_exitMenuItem != null)
                {
                    _exitMenuItem.Click -= ExitMenuItem_Click;
                    _exitMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Exit menu item Command executed!");
                        ExitRequested?.Invoke(this, EventArgs.Empty);
                    });
                    System.Diagnostics.Debug.WriteLine("Exit menu item wired");
                }
            }

            // Double click on tray icon opens main window
            _trayIcon.DoubleClickCommand = new RelayCommand(() =>
            {
                System.Diagnostics.Debug.WriteLine("Tray icon double clicked!");
                OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
            });
            
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
