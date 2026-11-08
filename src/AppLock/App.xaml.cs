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
        _mainWindow = new MainWindow();
        MainWindowInstance = _mainWindow;
        _mainWindow.Activate();

        // Apply process tamper protection & launch independent watchdog after window is up
        AppLock.Services.ProcessProtectionService.ProtectCurrentProcess();
        AppLock.Services.ProcessProtectionService.StartWatchdog();

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
