namespace AppLock.Models;

public class AppSettings
{
    // Security
    public byte[] PasswordHash { get; set; } = Array.Empty<byte>();
    public byte[] PasswordSalt { get; set; } = Array.Empty<byte>();
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 10;
    public bool WindowsHelloEnabled { get; set; }
    
    // General
    public string Language { get; set; } = "System default";
    public string Theme { get; set; } = "System default";
    public bool StartWithWindows { get; set; }
    public bool MuteNotifications { get; set; }
    
    // Auto & Emergency Lock
    public int AutoLockIdleMinutes { get; set; }
    public bool EmergencyHotkeyEnabled { get; set; } = true;
    public string EmergencyHotkey { get; set; } = "Ctrl+Alt+L";
    public bool KeepLockScreenOnTop { get; set; } = true;
    
    // Protection state
    public bool IsProtectionEnabled { get; set; }
    
    // Lock rules
    public List<LockRule> LockRules { get; set; } = new();
    
    // First run
    public bool IsFirstRun { get; set; } = true;
}
