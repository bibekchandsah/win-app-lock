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

    private static WindowEnumerator? _windowEnumerator;

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

    public static void Reset()
    {
        // Stop and dispose services that need cleanup
        _overlayManager?.Dispose();
        
        _settingsStore = null;
        _settings = null;
        _authService = null;
        _lockManager = null;
        _windowEnumerator = null;
        _overlayManager = null;
    }
}
