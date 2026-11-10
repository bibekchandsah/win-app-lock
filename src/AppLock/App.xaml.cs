using Microsoft.UI.Xaml;

namespace AppLock;

public partial class App : Application
{
    /// <summary>Static reference to the main window — used by RestartAsAdminButton_Click to hide before Environment.Exit.</summary>
    public static Window? MainWindowInstance { get; private set; }

    private Window? _mainWindow;

    public App()
    {
        this.UnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Exception: {e.Message}");
            System.Diagnostics.Debug.WriteLine(e.Exception);
            e.Handled = true;
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Check for command-line arguments (--minimized for startup)
        var cmdArgs = Environment.GetCommandLineArgs();
        var startMinimized = cmdArgs.Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) || 
                                                  arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        _mainWindow = new MainWindow();
        MainWindowInstance = _mainWindow;
        
        // Only activate (show) window if not starting minimized
        if (!startMinimized)
        {
            _mainWindow.Activate();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[App] Started minimized (startup mode)");
        }

        // Apply process tamper protection & launch independent watchdog after window is up
        AppLock.Services.ProcessProtectionService.ProtectCurrentProcess();
        AppLock.Services.ProcessProtectionService.StartWatchdog();

        // Start clipboard monitor if enabled
        if (AppLock.Services.ServiceContainer.Settings.Clipboard.Enabled)
        {
            AppLock.Services.ServiceContainer.ClipboardMonitor.Start();
        }

        // Wire clipboard global hotkey to popup window
        AppLock.Services.ServiceContainer.ClipboardMonitor.HotkeyTriggered += (s, e) =>
        {
            if (AppLock.Services.ServiceContainer.ClipboardMonitor.IsSessionLocked)
            {
                return;
            }

            _mainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                AppLock.Views.ClipboardPopupWindow.Toggle();
            });
        };

        // Initialize Volume Mixer overlay
        if (_mainWindow?.DispatcherQueue != null)
        {
            AppLock.Services.ServiceContainer.InitializeVolumeMixer(_mainWindow.DispatcherQueue);
        }

        // Wire session lock change to dismiss popup immediately on lock screen
        AppLock.Services.ServiceContainer.ClipboardMonitor.SessionLockChanged += (s, isLocked) =>
        {
            if (isLocked)
            {
                _mainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    AppLock.Views.ClipboardPopupWindow.Instance.HidePopup();
                });
            }
        };

        // Wire clipboard UIPI fallback toast
        AppLock.Services.ServiceContainer.ClipboardPasteService.FallbackToastRequested += (s, msg) =>
        {
            _mainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                AppLock.Views.ClipboardToastWindow.ShowToast(msg);
            });
        };

        // BitLocker auto-unlock on launch if enabled
        if (AppLock.Services.ServiceContainer.Settings.BitLockerAutoUnlockOnAppOpen &&
            AppLock.Services.ServiceContainer.BitLockerSupportService.IsSupported)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);

                var bitLocker = AppLock.Services.ServiceContainer.BitLockerService;
                var drives = await bitLocker.GetDrivesAsync();
                var savedPasswords = await AppLock.Services.ServiceContainer.SavedPasswordStore.GetAllAsync();
                var settings = AppLock.Services.ServiceContainer.Settings;

                bool hasLocked = drives.Any(d =>
                    d.LockStatus == AppLock.Models.LockStatus.Locked &&
                    (settings.BitLockerAutoUnlockVolumeGuids.Count == 0 ||
                     (!string.IsNullOrEmpty(d.VolumeGuid) && settings.BitLockerAutoUnlockVolumeGuids.Contains(d.VolumeGuid, StringComparer.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrEmpty(d.DriveLetter) && settings.BitLockerAutoUnlockVolumeGuids.Contains(d.DriveLetter, StringComparer.OrdinalIgnoreCase))) &&
                    savedPasswords.Any(p => p.IsValid && (
                        (!string.IsNullOrEmpty(d.VolumeGuid) && string.Equals(p.VolumeGuid, d.VolumeGuid, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(d.DriveLetter) && string.Equals(p.DriveLetter, d.DriveLetter, StringComparison.OrdinalIgnoreCase)))));

                if (!hasLocked) return;

                if (AppLock.Services.ServiceContainer.Settings.BitLockerRequireHelloOnAppStart)
                {
                    _mainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        int retries = 0;
                        while (_mainWindow.Content?.XamlRoot == null && retries++ < 25)
                        {
                            await Task.Delay(200);
                        }

                        if (_mainWindow.Content?.XamlRoot != null)
                        {
                            var auth = await AppLock.Services.BitLocker.BitLockerSecurityGate.EnsureAuthenticatedAsync(_mainWindow.Content.XamlRoot);
                            if (auth)
                            {
                                await AppLock.Services.ServiceContainer.AutoUnlockService.UnlockEligibleDrivesAsync(showNotifications: true);
                            }
                        }
                    });
                }
                else
                {
                    await AppLock.Services.ServiceContainer.AutoUnlockService.UnlockEligibleDrivesAsync(showNotifications: true);
                }
            });
        }
    }

    public Window? FindMainWindow()
    {
        return _mainWindow;
    }
}
