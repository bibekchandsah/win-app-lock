using System.Collections.ObjectModel;
using AppLock.Models;
using AppLock.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppLock.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly StartupService _startupService;
    private readonly HelloService _helloService;

    [ObservableProperty]
    private string _selectedLanguage = "System default";

    [ObservableProperty]
    private string _selectedTheme = "System default";

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _muteNotifications;

    [ObservableProperty]
    private int _lockoutDuration;

    [ObservableProperty]
    private bool _windowsHelloEnabled;

    [ObservableProperty]
    private string _windowsHelloStatus = "Checking...";

    [ObservableProperty]
    private bool _windowsHelloAvailable;

    [ObservableProperty]
    private int _autoLockIdleMinutes;

    [ObservableProperty]
    private bool _emergencyHotkeyEnabled;

    [ObservableProperty]
    private string _emergencyHotkey = "Ctrl+Alt+L";

    [ObservableProperty]
    private bool _keepLockScreenOnTop;

    public ObservableCollection<string> Languages { get; } = new()
    {
        "System default",
        "English",
        "Spanish",
        "French",
        "German",
        "Italian",
        "Portuguese",
        "Chinese",
        "Japanese"
    };

    public ObservableCollection<string> Themes { get; } = new()
    {
        "System default",
        "Light",
        "Dark"
    };

    public SettingsViewModel()
    {
        _settingsStore = ServiceContainer.SettingsStore;
        _settings = ServiceContainer.Settings;
        _startupService = ServiceContainer.StartupService;
        _helloService = ServiceContainer.HelloService;

        LoadSettings();
        _ = CheckWindowsHelloAvailabilityAsync();
    }

    private void LoadSettings()
    {
        SelectedLanguage = _settings.Language;
        SelectedTheme = _settings.Theme;
        StartWithWindows = _startupService.IsStartupEnabled();
        MuteNotifications = _settings.MuteNotifications;
        LockoutDuration = _settings.LockoutDurationMinutes;
        WindowsHelloEnabled = _settings.WindowsHelloEnabled;
        AutoLockIdleMinutes = _settings.AutoLockIdleMinutes;
        EmergencyHotkeyEnabled = _settings.EmergencyHotkeyEnabled;
        EmergencyHotkey = _settings.EmergencyHotkey;
        KeepLockScreenOnTop = _settings.KeepLockScreenOnTop;
    }

    private async Task CheckWindowsHelloAvailabilityAsync()
    {
        WindowsHelloAvailable = await _helloService.IsAvailableAsync();
        WindowsHelloStatus = await _helloService.GetAvailabilityDescriptionAsync();
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        _settings.Language = value;
        SaveSettings();
    }

    partial void OnSelectedThemeChanged(string value)
    {
        _settings.Theme = value;
        SaveSettings();
        ApplyTheme(value);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        _startupService.SetStartupEnabled(value);
    }

    partial void OnMuteNotificationsChanged(bool value)
    {
        _settings.MuteNotifications = value;
        SaveSettings();
    }

    partial void OnLockoutDurationChanged(int value)
    {
        _settings.LockoutDurationMinutes = value;
        SaveSettings();
    }

    partial void OnWindowsHelloEnabledChanged(bool value)
    {
        _settings.WindowsHelloEnabled = value;
        SaveSettings();
    }

    partial void OnAutoLockIdleMinutesChanged(int value)
    {
        _settings.AutoLockIdleMinutes = value;
        SaveSettings();
        UpdateIdleMonitoring();
    }

    partial void OnEmergencyHotkeyEnabledChanged(bool value)
    {
        _settings.EmergencyHotkeyEnabled = value;
        SaveSettings();
    }

    partial void OnKeepLockScreenOnTopChanged(bool value)
    {
        _settings.KeepLockScreenOnTop = value;
        SaveSettings();
        ServiceContainer.OverlayManager.UpdateKeepOnTop(value);
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        // Dialog will be shown from UI
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ManageSecurityQuestionsAsync()
    {
        // Security questions feature - TBD
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ChangeEmergencyHotkeyAsync()
    {
        // Dialog will be shown from UI
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ResetAllPrompts()
    {
        // Reset any stored dialog preferences
        System.Diagnostics.Debug.WriteLine("Reset all prompts");
    }

    [RelayCommand]
    private void ShowAbout()
    {
        // Dialog will be shown from UI
        System.Diagnostics.Debug.WriteLine("Show about");
    }

    private void SaveSettings()
    {
        _settingsStore.Save(_settings);
    }

    private void ApplyTheme(string theme)
    {
        var requestedTheme = theme switch
        {
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            "Dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
            _ => Microsoft.UI.Xaml.ElementTheme.Default
        };

        // Apply to main window
        if (Microsoft.UI.Xaml.Application.Current is App app)
        {
            var mainWindow = app.FindMainWindow();
            if (mainWindow?.Content is Microsoft.UI.Xaml.FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = requestedTheme;
            }
        }
    }

    private void UpdateIdleMonitoring()
    {
        var idleService = ServiceContainer.IdleService;
        
        if (AutoLockIdleMinutes > 0)
        {
            idleService.StartMonitoring(AutoLockIdleMinutes);
        }
        else
        {
            idleService.StopMonitoring();
        }
    }
}
