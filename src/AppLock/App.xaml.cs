using Microsoft.UI.Xaml;

namespace AppLock;

public partial class App : Application
{
    /// <summary>Static reference to the main window — used by RestartAsAdminButton_Click to hide before Environment.Exit.</summary>
    public static Window? MainWindowInstance { get; private set; }

    private MainWindow? _mainWindow;

    public App()
    {
        this.UnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Exception: {e.Message}");
            System.Diagnostics.Debug.WriteLine(e.Exception);

            // Handled exceptions would otherwise leave the window half-built with no trace of why.
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "Logs");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}\r\n{e.Exception}\r\n\r\n");
            }
            catch { }

            e.Handled = true;
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {

        // Refresh a Run entry written by an older build so the next sign-in opens in the tray.
        AppLock.Services.ServiceContainer.StartupService.UpdateStartupCommandLine();

        // Check for command-line arguments (--minimized for startup)
        var cmdArgs = Environment.GetCommandLineArgs();
        var startMinimized = cmdArgs.Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) || 
                                                  arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        _mainWindow = new MainWindow(startMinimized);
        MainWindowInstance = _mainWindow;

        // Activate so the XAML tree is realized, then hide before the message loop runs — an
        // autostart launch ends up showing only the tray icon.
        _mainWindow.Activate();

        if (startMinimized)
        {
            _mainWindow.HideImmediately();
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
            AppLock.Services.ServiceContainer.InitializeNotificationCustomizer();
            AppLock.Services.ServiceContainer.InitializeLockKeyIndicator(_mainWindow.DispatcherQueue);
            AppLock.Services.ServiceContainer.InitializeSpeedMeter(_mainWindow.DispatcherQueue);
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

    private static void ProbeSettingsRoundTrip()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "Logs");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "settings_probe.log");

        try
        {
            var speed = AppLock.Services.ServiceContainer.Settings.SpeedMeter;
            speed.BackgroundColor = "#123456";
            speed.ScalePercent = 137;
            speed.CornerRadiusPx = 23;
            speed.ThemePreset = "Solid";

            AppLock.Services.ServiceContainer.SettingsStore.Save(AppLock.Services.ServiceContainer.Settings);

            var page = new Views.SpeedMeterPage();
            var window = new Window { Content = page };
            window.Activate();

            var timer = page.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                System.IO.File.WriteAllText(path,
                    $"OK color={speed.BackgroundColor} scale={speed.ScalePercent} radius={speed.CornerRadiusPx} preset={speed.ThemePreset}");

                try { window.Close(); } catch { }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(path, ex.ToString());
        }
    }

    public Window? FindMainWindow()
    {
        return _mainWindow;
    }
}
