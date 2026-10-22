using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AppLock.Views;
using AppLock.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace AppLock;

public sealed partial class MainWindow : Window
{
    private SystemBackdropConfiguration? _backdropConfiguration;
    private MicaController? _micaController;
    private bool _loginShown = false;
    private int _emergencyHotkeyId = -1;
    private HWND _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        
        // Get window handle
        var hWndNative = WindowNative.GetWindowHandle(this);
        _hwnd = new HWND(hWndNative);
        
        // Set window size
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndNative);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        
        // Set up Mica backdrop
        SetupMica();
        
        // Set theme from user settings
        if (Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = ServiceContainer.CurrentTheme;
        }
        
        // Initialize services
        InitializeServices();
        
        // Check first run
        if (ServiceContainer.AuthService.IsFirstRun)
        {
            ShowFirstRun();
        }
        else
        {
            // Show login immediately after loading completes
            ContentFrame.Loaded += async (s, e) =>
            {
                if (!_loginShown)
                {
                    _loginShown = true;
                    await ShowLogin();
                }
            };
        }
        
        // Handle window closing
        this.Closed += MainWindow_Closed;
    }

    private void SetupMica()
    {
        if (MicaController.IsSupported())
        {
            _backdropConfiguration = new SystemBackdropConfiguration();
            _micaController = new MicaController();
            _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
            
            SystemBackdrop = new MicaBackdrop() { Kind = MicaKind.Base };
        }
    }

    private void ShowFirstRun()
    {
        var firstRunPage = new FirstRunPage(ServiceContainer.AuthService);
        firstRunPage.FirstRunCompleted += (s, e) =>
        {
            OnFirstRunComplete();
        };
        ContentFrame.Content = firstRunPage;
        
        // Hide navigation rail during first run
        ContentFrame.SetValue(Grid.ColumnSpanProperty, 2);
    }

    public void OnFirstRunComplete()
    {
        // Restore navigation rail
        ContentFrame.ClearValue(Grid.ColumnSpanProperty);
        
        // Navigate to protection page
        ContentFrame.Navigate(typeof(ProtectionPage));
    }

    private async Task ShowLogin()
    {
        int attemptCount = 0;
        while (true)
        {
            attemptCount++;
            System.Diagnostics.Debug.WriteLine($"Login attempt #{attemptCount}");
            
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                XamlRoot = ContentFrame.XamlRoot
            };

            await dialog.ShowAsync();
            
            System.Diagnostics.Debug.WriteLine($"IsAuthenticated: {dialog.IsAuthenticated}");

            // Check IsAuthenticated - this is set to true only when password is correct
            if (dialog.IsAuthenticated)
            {
                System.Diagnostics.Debug.WriteLine("Authentication successful, navigating to ProtectionPage");
                
                try
                {
                    ContentFrame.Navigate(typeof(ProtectionPage));
                    
                    // Start overlay manager if protection is enabled
                    if (ServiceContainer.Settings.IsProtectionEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine("Starting OverlayManager...");
                        ServiceContainer.OverlayManager.Start();
                        System.Diagnostics.Debug.WriteLine("OverlayManager started successfully");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ERROR during initialization: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                    
                    // Show error to user
                    var errorDialog = new ContentDialog
                    {
                        Title = "Initialization Error",
                        Content = $"Failed to start AppLock:\n{ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = ContentFrame.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                    
                    // Exit app
                    Application.Current.Exit();
                    return;
                }
                
                break;
            }
            else
            {
                // User cancelled the login prompt on startup - close application
                Application.Current.Exit();
                return;
            }
        }
    }

    private void ProtectionNavButton_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(typeof(ProtectionPage));
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(typeof(SettingsPage));
    }

    private void InitializeServices()
    {
        // Set window handle for hotkey service
        ServiceContainer.HotkeyService.SetWindowHandle(_hwnd);

        // Register emergency hotkey if enabled
        RegisterEmergencyHotkey();

        // Start idle monitoring if configured
        StartIdleMonitoring();

        // Wire up idle service event
        ServiceContainer.IdleService.IdleTimeReached += IdleService_IdleTimeReached;

        // Initialize and wire up tray service
        InitializeTrayService();
    }

    private void InitializeTrayService()
    {
        try
        {
            ServiceContainer.TrayService.Initialize();
            
            // Wire up tray events
            ServiceContainer.TrayService.OpenMainWindowRequested += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() => Show());
            };
            
            ServiceContainer.TrayService.EmergencyLockRequested += (s, e) =>
            {
                OnEmergencyLockHotkey();
            };
            
            ServiceContainer.TrayService.ExitRequested += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(async () => await HandleExitRequestAsync());
            };
            
            ServiceContainer.TrayService.ProtectionToggleRequested += (s, newState) =>
            {
                DispatcherQueue.TryEnqueue(async () => await HandleProtectionToggleAsync(newState));
            };
            
            System.Diagnostics.Debug.WriteLine("Tray service initialized");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error initializing tray service: {ex.Message}");
        }
    }

    private async Task HandleProtectionToggleAsync(bool enable)
    {
        // Show auth dialog
        var dialog = new AuthDialog(ServiceContainer.AuthService)
        {
            Title = enable ? "Enable Protection" : "Disable Protection",
            XamlRoot = Content?.XamlRoot
        };

        await dialog.ShowAsync();

        if (dialog.IsAuthenticated)
        {
            if (enable)
            {
                ServiceContainer.LockManager.EnableProtection();
                ServiceContainer.OverlayManager.Start();
            }
            else
            {
                ServiceContainer.LockManager.DisableProtection();
                ServiceContainer.OverlayManager.Stop();
            }
            
            // Update tray
            ServiceContainer.TrayService.UpdateProtectionMenuItems();
            
            // Show notification
            await ShowNotificationAsync(
                "Protection " + (enable ? "Enabled" : "Disabled"),
                "AppLock protection is now " + (enable ? "active" : "inactive") + "."
            );
        }
    }

    private async Task HandleExitRequestAsync()
    {
        // Show auth dialog to confirm exit
        var dialog = new AuthDialog(ServiceContainer.AuthService)
        {
            Title = "Exit AppLock",
            XamlRoot = Content?.XamlRoot
        };

        await dialog.ShowAsync();

        if (dialog.IsAuthenticated)
        {
            // Perform cleanup
            MainWindow_ActualClose();
            
            // Exit app
            Application.Current.Exit();
        }
    }

    private void RegisterEmergencyHotkey()
    {
        var settings = ServiceContainer.Settings;
        
        if (settings.EmergencyHotkeyEnabled && !string.IsNullOrEmpty(settings.EmergencyHotkey))
        {
            try
            {
                var (modifiers, key) = HotkeyService.ParseHotkey(settings.EmergencyHotkey);
                _emergencyHotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(
                    modifiers,
                    key,
                    OnEmergencyLockHotkey
                );
                
                if (_emergencyHotkeyId > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Emergency hotkey registered: {settings.EmergencyHotkey}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to register emergency hotkey: {settings.EmergencyHotkey}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error registering emergency hotkey: {ex.Message}");
            }
        }
    }

    private void UnregisterEmergencyHotkey()
    {
        if (_emergencyHotkeyId > 0)
        {
            ServiceContainer.HotkeyService.UnregisterHotkey(_emergencyHotkeyId);
            _emergencyHotkeyId = -1;
        }
    }

    private void StartIdleMonitoring()
    {
        var settings = ServiceContainer.Settings;
        
        if (settings.AutoLockIdleMinutes > 0)
        {
            ServiceContainer.IdleService.StartMonitoring(settings.AutoLockIdleMinutes);
            System.Diagnostics.Debug.WriteLine($"Idle monitoring started: {settings.AutoLockIdleMinutes} minutes");
        }
    }

    private void OnEmergencyLockHotkey()
    {
        System.Diagnostics.Debug.WriteLine("Emergency lock hotkey pressed!");
        
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Stop all overlays
            ServiceContainer.OverlayManager.Stop();
            
            // Re-lock everything immediately (ignore unlock timers)
            ServiceContainer.LockManager.DisableProtection();
            ServiceContainer.LockManager.EnableProtection();
            
            // Restart overlay manager
            ServiceContainer.OverlayManager.Start();
            
            // Show notification
            await ShowNotificationAsync("Emergency Lock", "All protected apps have been re-locked.");
        });
    }

    private void IdleService_IdleTimeReached(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Idle time reached - locking app");
        
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Hide main window
            Hide();
            
            // Show auth dialog
            await ShowIdleLockDialog();
        });
    }

    private async Task ShowIdleLockDialog()
    {
        while (true)
        {
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                Title = "AppLock Idle Auto-Lock",
                XamlRoot = Content?.XamlRoot
            };

            if (Content?.XamlRoot == null)
            {
                // Window not ready, wait a bit
                await Task.Delay(500);
                continue;
            }

            await dialog.ShowAsync();

            if (dialog.IsAuthenticated)
            {
                // Show window again
                Show();
                
                // Restart idle monitoring
                StartIdleMonitoring();
                break;
            }
            
            await Task.Delay(100);
        }
    }

    private async Task ShowNotificationAsync(string title, string message)
    {
        if (ServiceContainer.Settings.MuteNotifications)
            return;

        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content?.XamlRoot
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing notification: {ex.Message}");
        }
    }

    private void MainWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        // Check if this is a minimize-to-tray or actual close
        // For now, treat window close as minimize to tray
        // Protection keeps running in background
        
        // Prevent actual close, just hide the window
        args.Handled = true;
        Hide();
        
        System.Diagnostics.Debug.WriteLine("Window minimized to tray");
    }
    
    private void MainWindow_ActualClose()
    {
        // Actual cleanup when exiting app
        UnregisterEmergencyHotkey();
        ServiceContainer.IdleService.StopMonitoring();
        ServiceContainer.OverlayManager.Stop();
        ServiceContainer.TrayService.Dispose();
    }

    private void Show()
    {
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(_hwnd);
    }

    private void Hide()
    {
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }
}
