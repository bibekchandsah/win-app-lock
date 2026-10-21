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

    public static void Reset()
    {
        _settingsStore = null;
        _settings = null;
        _authService = null;
        _lockManager = null;
    }
}
