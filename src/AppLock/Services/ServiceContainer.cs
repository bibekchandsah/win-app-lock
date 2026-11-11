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
    private static AppLock.Services.Brightness.BrightnessService? _brightnessService;
    private static AppLock.Services.EyeCare.EyeCareScheduler? _eyeCareScheduler;
    private static AppLock.Services.EyeCare.EyeCareOverlayService? _eyeCareOverlayService;
    private static ClipboardStore? _clipboardStore;
    private static ClipboardMonitor? _clipboardMonitor;
    private static ClipboardPasteService? _clipboardPasteService;
    private static ClipboardExportService? _clipboardExportService;
    private static AppLock.Services.RingLight.RingLightService? _ringLightService;
    private static AppLock.Services.VolumeMixer.VolumeOverlayService? _volumeOverlayService;
    private static AppLock.Services.VolumeMixer.VolumeMixerHotkeyManager? _volumeMixerHotkeyManager;
    private static AppLock.Services.Notifications.NotificationCustomizerService? _notificationCustomizerService;

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
        _volumeMixerHotkeyManager?.Dispose();
        
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
        _volumeMixerHotkeyManager = null;
        _eyeCareScheduler?.Dispose();
        _eyeCareScheduler = null;
        _eyeCareOverlayService = null;
        _volumeOverlayService?.Dispose();
        _volumeOverlayService = null;
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

    public static AppLock.Services.Brightness.BrightnessService BrightnessService
    {
        get
        {
            if (_brightnessService == null)
            {
                var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                         ?? MainWindow.Instance?.DispatcherQueue;
                _brightnessService = new AppLock.Services.Brightness.BrightnessService(dq);
            }
            return _brightnessService;
        }
    }

    public static void InitializeBrightnessHotkeys(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        if (_brightnessHotkeyManager == null)
        {
            var brightnessService = BrightnessService;
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

    public static AppLock.Services.EyeCare.EyeCareOverlayService? EyeCareOverlayService => _eyeCareOverlayService;
    public static AppLock.Services.EyeCare.EyeCareScheduler? EyeCareScheduler => _eyeCareScheduler;

    public static ClipboardStore ClipboardStore
    {
        get
        {
            if (_clipboardStore == null)
            {
                _clipboardStore = new ClipboardStore();
            }
            return _clipboardStore;
        }
    }

    public static ClipboardMonitor ClipboardMonitor
    {
        get
        {
            if (_clipboardMonitor == null)
            {
                _clipboardMonitor = new ClipboardMonitor();
            }
            return _clipboardMonitor;
        }
    }

    public static ClipboardPasteService ClipboardPasteService
    {
        get
        {
            if (_clipboardPasteService == null)
            {
                _clipboardPasteService = new ClipboardPasteService();
            }
            return _clipboardPasteService;
        }
    }

    public static ClipboardExportService ClipboardExportService
    {
        get
        {
            if (_clipboardExportService == null)
            {
                _clipboardExportService = new ClipboardExportService();
            }
            return _clipboardExportService;
        }
    }

    public static event EventHandler<bool>? ClipboardEnabledChanged;

    public static void SetClipboardEnabled(bool enabled)
    {
        Settings.Clipboard.Enabled = enabled;
        SettingsStore.Save(Settings);

        if (enabled)
        {
            ClipboardMonitor.Start();
        }
        else
        {
            ClipboardMonitor.Stop();
        }

        ClipboardEnabledChanged?.Invoke(null, enabled);
    }

    public static event EventHandler<bool>? EyeCareEnabledChanged;
    public static event EventHandler<int>? EyeCareIntervalChanged;

    public static void SetEyeCareEnabled(bool enabled)
    {
        Settings.EyeCare.Enabled = enabled;
        SettingsStore.Save(Settings);

        if (enabled)
        {
            EyeCareScheduler?.Start();
        }
        else
        {
            EyeCareScheduler?.Stop();
        }

        EyeCareEnabledChanged?.Invoke(null, enabled);
    }

    public static void SetEyeCareInterval(int minutes)
    {
        if (minutes < 1 || minutes > 1440) return;

        Settings.EyeCare.IntervalMinutes = minutes;
        SettingsStore.Save(Settings);

        if (Settings.EyeCare.Enabled)
        {
            EyeCareScheduler?.Start();
        }

        EyeCareIntervalChanged?.Invoke(null, minutes);
    }

    // ── Ring Light ────────────────────────────────────────────────────────────

    public static AppLock.Services.RingLight.RingLightService RingLightService
    {
        get
        {
            if (_ringLightService == null)
                _ringLightService = new AppLock.Services.RingLight.RingLightService();
            return _ringLightService;
        }
    }

    public static event EventHandler<bool>? RingLightEnabledChanged;

    public static void SetRingLightEnabled(bool enabled)
    {
        RingLightService.SetEnabled(enabled, "user");
        RingLightEnabledChanged?.Invoke(null, enabled);
    }

    // ── Volume Mixer ──────────────────────────────────────────────────────────

    public static AppLock.Services.VolumeMixer.VolumeOverlayService? VolumeOverlayService => _volumeOverlayService;

    public static event EventHandler<bool>? VolumeMixerEnabledChanged;
    public static event EventHandler<string>? VolumeMixerPositionChanged;

    public static void SetVolumeMixerEnabled(bool enabled)
    {
        Settings.VolumeMixer.Enabled = enabled;
        SettingsStore.Save(Settings);

        if (enabled)
        {
            _volumeOverlayService?.Start();
        }
        else
        {
            _volumeOverlayService?.Stop();
        }
        
        // Update tray menu
        TrayService?.UpdateVolumeMixerMenuItem();
        
        VolumeMixerEnabledChanged?.Invoke(null, enabled);
    }

    /// <summary>
    /// Sets the volume mixer icon position, persists settings, refreshes the overlay,
    /// and raises <see cref="VolumeMixerPositionChanged"/> so both the settings page
    /// and the tray menu update in real-time.
    /// </summary>
    public static void SetVolumeMixerPosition(string positionId, double? customX = null, double? customY = null)
    {
        Settings.VolumeMixer.IconPosition = positionId;
        Settings.VolumeMixer.CustomX = customX;
        Settings.VolumeMixer.CustomY = customY;
        SettingsStore.Save(Settings);

        _volumeOverlayService?.RefreshSettings();

        // Sync tray checkmarks
        TrayService?.UpdateVolumeMixerPositionSubItem();

        // Notify all subscribers (settings page, etc.)
        VolumeMixerPositionChanged?.Invoke(null, positionId);
    }

    public static AppLock.Services.VolumeMixer.VolumeMixerHotkeyManager? VolumeMixerHotkeyManager => _volumeMixerHotkeyManager;

    public static void InitializeVolumeMixer(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        if (_volumeOverlayService == null)
        {
            _volumeOverlayService = new AppLock.Services.VolumeMixer.VolumeOverlayService(dispatcherQueue);

            // Auto-start if enabled
            if (Settings.VolumeMixer.Enabled)
            {
                _volumeOverlayService.Start();
            }
        }
    }

    public static void InitializeVolumeMixerHotkeys(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        System.Diagnostics.Debug.WriteLine("[ServiceContainer] InitializeVolumeMixerHotkeys called");
        
        if (_volumeMixerHotkeyManager == null)
        {
            System.Diagnostics.Debug.WriteLine("[ServiceContainer] Creating VolumeMixerHotkeyManager...");
            
            _volumeMixerHotkeyManager = new AppLock.Services.VolumeMixer.VolumeMixerHotkeyManager(
                HotkeyService,
                dispatcherQueue);
            
            System.Diagnostics.Debug.WriteLine("[ServiceContainer] Calling RegisterHotkey...");
            _volumeMixerHotkeyManager.RegisterHotkey();
            
            System.Diagnostics.Debug.WriteLine("[ServiceContainer] Volume Mixer hotkeys initialized");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[ServiceContainer] VolumeMixerHotkeyManager already exists");
        }
    }

    // ── Notifications ──────────────────────────────────────────────────────────

    public static AppLock.Services.Notifications.NotificationCustomizerService? NotificationCustomizerService => _notificationCustomizerService;

    public static event EventHandler<bool>? NotificationCustomizerEnabledChanged;

    public static void SetNotificationCustomizerEnabled(bool enabled)
    {
        Settings.NotificationCustom.Enabled = enabled;
        SettingsStore.Save(Settings);
        
        if (enabled)
        {
            _notificationCustomizerService?.Start();
        }
        else
        {
            _notificationCustomizerService?.Stop();
        }
        
        NotificationCustomizerEnabledChanged?.Invoke(null, enabled);
    }

    public static void InitializeNotificationCustomizer()
    {
        if (_notificationCustomizerService == null)
        {
            _notificationCustomizerService = new AppLock.Services.Notifications.NotificationCustomizerService(
                Settings.NotificationCustom);
            
            // Auto-start if enabled
            if (Settings.NotificationCustom.Enabled)
            {
                _notificationCustomizerService.Start();
            }
        }
    }

    public static void InitializeEyeCare(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        if (_eyeCareOverlayService == null)
        {
            _eyeCareOverlayService = new AppLock.Services.EyeCare.EyeCareOverlayService(
                dispatcherQueue,
                () => Settings.EyeCare);
        }

        if (_eyeCareScheduler == null)
        {
            _eyeCareScheduler = new AppLock.Services.EyeCare.EyeCareScheduler(
                _eyeCareOverlayService,
                dispatcherQueue,
                () => Settings.EyeCare,
                s => SettingsStore.Save(s));

            // Auto-start if enabled
            if (Settings.EyeCare.Enabled)
            {
                _eyeCareScheduler.Start();
            }
        }
    }
}
