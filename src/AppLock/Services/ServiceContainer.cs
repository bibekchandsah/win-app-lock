using AppLock.Models;

namespace AppLock.Services;

/// <summary>
/// Simple service container to share service instances across the app
/// </summary>
public static class ServiceContainer
{
    private static SettingsStore? _settingsStore;
    private static AppSettings? _settings;
    private static AuthService? _authService;
    private static LockManager? _lockManager;
    private static OverlayManager? _overlayManager;
    private static WindowEnumerator? _windowEnumerator;
    private static WindowPicker? _windowPicker;
    private static HotkeyService? _hotkeyService;
    private static IdleService? _idleService;
    private static StartupService? _startupService;
    private static HelloService? _helloService;
    private static TrayService? _trayService;
    private static ThumbnailService? _thumbnailService;
    private static UwpWindowResolver? _uwpWindowResolver;
    private static DisplayHelper? _displayHelper;
    private static WindowEventMonitor? _windowEventMonitor;
    private static LocalizationService? _localizationService;
    private static NotificationService? _notificationService;
    private static SelfDisciplineService? _selfDisciplineService;
    private static AppLock.Services.BitLocker.BitLockerSupportService? _bitLockerSupportService;
    private static AppLock.Services.BitLocker.BitLockerService? _bitLockerService;
    private static AppLock.Services.BitLocker.VolumeWatcher? _volumeWatcher;
    private static AppLock.Services.BitLocker.SavedPasswordStore? _savedPasswordStore;
    private static AppLock.Services.BitLocker.StartupTaskService? _startupTaskService;
    private static AppLock.Services.BitLocker.BitLockerAutoUnlockService? _autoUnlockService;
    private static AppLock.Services.Brightness.BrightnessHotkeyManager? _brightnessHotkeyManager;

    public static NotificationService NotificationService
    {
        get
        {
            if (_notificationService == null)
            {
                _notificationService = new NotificationService();
            }
            return _notificationService;
        }
    }

    public static LocalizationService LocalizationService
    {
        get
        {
            if (_localizationService == null)
            {
                _localizationService = new LocalizationService(Settings.Language);
            }
            return _localizationService;
        }
    }

    public static SettingsStore SettingsStore
    {
        get
        {
            if (_settingsStore == null)
            {
                _settingsStore = new SettingsStore();
            }
            return _settingsStore;
        }
    }

    public static AppSettings Settings
    {
        get
        {
            if (_settings == null)
            {
                _settings = SettingsStore.Load();
            }
            return _settings;
        }
    }

    public static AuthService AuthService
    {
        get
        {
            if (_authService == null)
            {
                _authService = new AuthService(SettingsStore, Settings);
            }
            return _authService;
        }
    }

    public static WindowEnumerator WindowEnumerator
    {
        get
        {
            if (_windowEnumerator == null)
            {
                _windowEnumerator = new WindowEnumerator();
            }
            return _windowEnumerator;
        }
    }

    public static WindowPicker WindowPicker
    {
        get
        {
            if (_windowPicker == null)
            {
                _windowPicker = new WindowPicker();
            }
            return _windowPicker;
        }
    }

    public static LockManager LockManager
    {
        get
        {
            if (_lockManager == null)
            {
                _lockManager = new LockManager(SettingsStore, Settings);
            }
            return _lockManager;
        }
    }

    public static OverlayManager OverlayManager
    {
        get
        {
            if (_overlayManager == null)
            {
                _overlayManager = new OverlayManager(LockManager, AuthService);
            }
            return _overlayManager;
        }
    }

    public static HotkeyService HotkeyService
    {
        get
        {
            if (_hotkeyService == null)
            {
                _hotkeyService = new HotkeyService();
            }
            return _hotkeyService;
        }
    }

    public static IdleService IdleService
    {
        get
        {
            if (_idleService == null)
            {
                _idleService = new IdleService();
            }
            return _idleService;
        }
    }

    public static StartupService StartupService
    {
        get
        {
            if (_startupService == null)
            {
                _startupService = new StartupService();
            }
            return _startupService;
        }
    }

    public static HelloService HelloService
    {
        get
        {
            if (_helloService == null)
            {
                _helloService = new HelloService();
            }
            return _helloService;
        }
    }

    public static TrayService TrayService
    {
        get
        {
            if (_trayService == null)
            {
                _trayService = new TrayService(SettingsStore, AuthService, LockManager);
            }
            return _trayService;
        }
    }

    public static ThumbnailService ThumbnailService
    {
        get
        {
            if (_thumbnailService == null)
            {
                _thumbnailService = new ThumbnailService();
            }
            return _thumbnailService;
        }
    }

    public static UwpWindowResolver UwpWindowResolver
    {
        get
        {
            if (_uwpWindowResolver == null)
            {
                _uwpWindowResolver = new UwpWindowResolver();
            }
            return _uwpWindowResolver;
        }
    }

    public static Microsoft.UI.Xaml.ElementTheme CurrentTheme
    {
        get
        {
            return Settings.Theme switch
            {
                "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
                "Dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
                _ => Microsoft.UI.Xaml.ElementTheme.Default
            };
        }
    }

    public static DisplayHelper DisplayHelper
    {
        get
        {
            if (_displayHelper == null)
            {
                _displayHelper = new DisplayHelper();
            }
            return _displayHelper;
        }
    }

    public static WindowEventMonitor WindowEventMonitor
    {
        get
        {
            if (_windowEventMonitor == null)
            {
                _windowEventMonitor = new WindowEventMonitor();
            }
            return _windowEventMonitor;
        }
    }

    public static void Reset()
    {
        // Stop and dispose services that need cleanup
        _selfDisciplineService?.Dispose(); // flushes quota on exit
        _overlayManager?.Dispose();
        _hotkeyService?.Dispose();
        _idleService?.Dispose();
        _trayService?.Dispose();
        _thumbnailService?.Dispose();
        _windowEventMonitor?.Dispose();
        _brightnessHotkeyManager?.Dispose();
        
        _settingsStore = null;
        _settings = null;
        _authService = null;
        _lockManager = null;
        _windowEnumerator = null;
        _overlayManager = null;
        _windowPicker = null;
        _hotkeyService = null;
        _idleService = null;
        _startupService = null;
        _helloService = null;
        _trayService = null;
        _thumbnailService = null;
        _uwpWindowResolver = null;
        _displayHelper = null;
        _windowEventMonitor = null;
        _localizationService = null;
        _notificationService = null;
        _selfDisciplineService = null;
        _bitLockerSupportService = null;
        _bitLockerService = null;
        _volumeWatcher?.Dispose();
        _volumeWatcher = null;
        _savedPasswordStore = null;
        _startupTaskService = null;
        _autoUnlockService = null;
        _brightnessHotkeyManager = null;
    }

    public static SelfDisciplineService GetOrCreateSelfDisciplineService(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        if (_selfDisciplineService == null)
        {
            _selfDisciplineService = new SelfDisciplineService(LockManager, OverlayManager, SettingsStore, Settings, dispatcherQueue);
        }
        return _selfDisciplineService;
    }

    public static SelfDisciplineService? SelfDisciplineService => _selfDisciplineService;

    public static AppLock.Services.BitLocker.BitLockerSupportService BitLockerSupportService
    {
        get
        {
            if (_bitLockerSupportService == null)
                _bitLockerSupportService = new AppLock.Services.BitLocker.BitLockerSupportService();
            return _bitLockerSupportService;
        }
    }

    public static AppLock.Services.BitLocker.BitLockerService BitLockerService
    {
        get
        {
            if (_bitLockerService == null)
                _bitLockerService = new AppLock.Services.BitLocker.BitLockerService(BitLockerSupportService);
            return _bitLockerService;
        }
    }

    public static AppLock.Services.BitLocker.VolumeWatcher VolumeWatcher
    {
        get
        {
            if (_volumeWatcher == null)
                _volumeWatcher = new AppLock.Services.BitLocker.VolumeWatcher();
            return _volumeWatcher;
        }
    }

    public static AppLock.Services.BitLocker.SavedPasswordStore SavedPasswordStore
    {
        get
        {
            if (_savedPasswordStore == null)
                _savedPasswordStore = new AppLock.Services.BitLocker.SavedPasswordStore();
            return _savedPasswordStore;
        }
    }

    public static AppLock.Services.BitLocker.StartupTaskService StartupTaskService
    {
        get
        {
            if (_startupTaskService == null)
                _startupTaskService = new AppLock.Services.BitLocker.StartupTaskService();
            return _startupTaskService;
        }
    }

    public static AppLock.Services.BitLocker.BitLockerAutoUnlockService AutoUnlockService
    {
        get
        {
            if (_autoUnlockService == null)
            {
                _autoUnlockService = new AppLock.Services.BitLocker.BitLockerAutoUnlockService(
                    BitLockerSupportService,
                    BitLockerService,
                    SavedPasswordStore,
                    Settings,
                    s => SettingsStore.Save(s),
                    (t, m) => NotificationService.ShowSuccess(t, m),
                    (t, m) => NotificationService.ShowWarning(t, m));
            }
            return _autoUnlockService;
        }
    }

    public static AppLock.Services.Brightness.BrightnessHotkeyManager? BrightnessHotkeyManager => _brightnessHotkeyManager;

    public static void InitializeBrightnessHotkeys(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        if (_brightnessHotkeyManager == null)
        {
            var brightnessService = new AppLock.Services.Brightness.BrightnessService(dispatcherQueue);
            var osdService = new AppLock.Services.Brightness.OsdService(dispatcherQueue);
            var blurOverlayService = new AppLock.Services.Brightness.BlurOverlayService(dispatcherQueue);

            _brightnessHotkeyManager = new AppLock.Services.Brightness.BrightnessHotkeyManager(
                HotkeyService,
                brightnessService,
                osdService,
                blurOverlayService,
                dispatcherQueue);

            _brightnessHotkeyManager.RegisterAllHotkeys();
        }
    }
}
