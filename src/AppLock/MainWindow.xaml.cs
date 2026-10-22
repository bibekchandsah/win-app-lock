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
using System.Runtime.InteropServices;

namespace AppLock;

public sealed partial class MainWindow : Window
{
    // Win32 subclassing for WM_HOTKEY interception
    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nint uIdSubclass, nint dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nint uIdSubclass);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLOSE = 0x0010;
    private SUBCLASSPROC? _subclassProc; // keep delegate alive to prevent GC

    private SystemBackdropConfiguration? _backdropConfiguration;
    private MicaController? _micaController;
    private Microsoft.UI.Windowing.AppWindow? _appWindow;
    private bool _isExplicitExit = false;
    private bool _loginShown = false;
    private int _emergencyHotkeyId = -1;
    private HWND _hwnd;

    internal static MainWindow? Instance { get; private set; }
    internal HWND Hwnd => _hwnd;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        
        // Get window handle
        var hWndNative = WindowNative.GetWindowHandle(this);
        _hwnd = new HWND(hWndNative);
        
        // Set window size & wire closing event
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndNative);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        _appWindow.Closing += AppWindow_Closing;
        
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

        // Subclass the window to intercept WM_HOTKEY messages
        InstallWndProcSubclass();

        // Register emergency hotkey if enabled
        RegisterEmergencyHotkey();

        // Start idle monitoring if configured
        StartIdleMonitoring();

        // Wire up idle service event
        ServiceContainer.IdleService.IdleTimeReached += IdleService_IdleTimeReached;

        // Initialize and wire up tray service
        InitializeTrayService();
    }

    private void InstallWndProcSubclass()
    {
        // Keep a strong reference to the delegate so GC doesn't collect it
        _subclassProc = WndProcSubclass;
        bool ok = SetWindowSubclass(_hwnd.Value, _subclassProc, 1, 0);
        System.Diagnostics.Debug.WriteLine($"WndProc subclass installed: {ok}");
    }

    private nint WndProcSubclass(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_CLOSE && !_isExplicitExit)
        {
            Hide();
            System.Diagnostics.Debug.WriteLine("WM_CLOSE intercepted -> Minimized to system tray");
            return 0; // Prevent Win32 window destruction
        }

        if (uMsg == WM_HOTKEY)
        {
            int hotkeyId = (int)wParam;
            System.Diagnostics.Debug.WriteLine($"WM_HOTKEY received: id={hotkeyId}");
            ServiceContainer.HotkeyService.ProcessHotkeyMessage(hotkeyId);
            return 0;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
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
        Show(); // Ensure window is visible so auth dialog can be displayed
        var dialog = new AuthDialog(ServiceContainer.AuthService)
        {
            Title = "Exit AppLock",
            XamlRoot = Content?.XamlRoot
        };

        await dialog.ShowAsync();

        if (dialog.IsAuthenticated)
        {
            _isExplicitExit = true;
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

    /// <summary>
    /// Re-register the emergency hotkey after a change in Settings.
    /// Called by SettingsPage after saving the new hotkey string.
    /// </summary>
    internal void ReregisterEmergencyHotkey()
    {
        UnregisterEmergencyHotkey();
        RegisterEmergencyHotkey();
        System.Diagnostics.Debug.WriteLine($"Emergency hotkey re-registered: {ServiceContainer.Settings.EmergencyHotkey}");
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

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!_isExplicitExit)
        {
            // Cancel close and hide to system tray
            args.Cancel = true;
            Hide();
            System.Diagnostics.Debug.WriteLine("Window minimized to system tray (AppWindow.Closing)");
        }
    }
    
    private void MainWindow_ActualClose()
    {
        // Actual cleanup when exiting app
        UnregisterEmergencyHotkey();
        // Remove WndProc subclass
        if (_subclassProc != null)
        {
            RemoveWindowSubclass(_hwnd.Value, _subclassProc, 1);
            _subclassProc = null;
        }
        ServiceContainer.IdleService.StopMonitoring();
        ServiceContainer.OverlayManager.Stop();
        ServiceContainer.TrayService.Dispose();
        Instance = null;
    }

    public void Show()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appWindow != null)
            {
                _appWindow.Show();
            }
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
            this.Activate();
            PInvoke.BringWindowToTop(_hwnd);
            PInvoke.SetForegroundWindow(_hwnd);
            System.Diagnostics.Debug.WriteLine("MainWindow restored and brought to foreground");
        });
    }

    public void Hide()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appWindow != null)
            {
                _appWindow.Hide();
            }
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            System.Diagnostics.Debug.WriteLine("MainWindow hidden to system tray");
        });
    }
}
