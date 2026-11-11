using System;
using System.Collections.Generic;

namespace AppLock.Models;

public class AppSettings
{
    // Security
    public byte[] PasswordHash { get; set; } = Array.Empty<byte>();
    public byte[] PasswordSalt { get; set; } = Array.Empty<byte>();
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 10;
    public bool WindowsHelloEnabled { get; set; }
    
    // Security Questions
    public string SecurityQuestion { get; set; } = string.Empty;
    public byte[] SecurityAnswerHash { get; set; } = Array.Empty<byte>();
    public byte[] SecurityAnswerSalt { get; set; } = Array.Empty<byte>();
    
    // General
    public string Language { get; set; } = "System default";
    public string Theme { get; set; } = "System default";
    public bool StartWithWindows { get; set; }
    public bool MuteNotifications { get; set; }
    public bool IsNavPaneExpanded { get; set; } = false;
    
    // Auto & Emergency Lock
    public int AutoLockIdleMinutes { get; set; }
    public bool EmergencyHotkeyEnabled { get; set; } = true;
    public string EmergencyHotkey { get; set; } = "Ctrl+Alt+L";
    public bool KeepLockScreenOnTop { get; set; } = true;
    
    // Protection state
    public bool IsProtectionEnabled { get; set; }
    
    // Lock rules
    public List<LockRule> LockRules { get; set; } = new();

    // Network/Firewall rules
    public List<NetworkRuleEntry> NetworkRules { get; set; } = new();
    
    // Firewall settings
    public bool RequirePasswordForFirewallUnblock { get; set; } = true;
    public bool KeepRulesWhenUnblocking { get; set; } = false;
    public int ActiveConnectionsRefreshSeconds { get; set; } = 3;

    // BitLocker
    public bool BitLockerOpenDriveAfterUnlock { get; set; } = false;
    public bool BitLockerHasShownSavedPasswordNotice { get; set; } = false;
    public bool BitLockerAutoUnlockOnAppOpen { get; set; } = false;
    public List<string> BitLockerAutoUnlockVolumeGuids { get; set; } = new();
    public string BitLockerLastUnlockedSummary { get; set; } = string.Empty;
    public bool BitLockerToastNotifications { get; set; } = true;
    public int BitLockerRefreshIntervalSeconds { get; set; } = 5;
    public bool BitLockerAutoClearClipboard { get; set; } = true;
    public bool BitLockerRequireHelloOnAppStart { get; set; } = false;
    
    // Brightness
    public BrightnessSettings Brightness { get; set; } = new();
    
    // Eye Care
    public EyeCareSettings EyeCare { get; set; } = new();
    
    // Clipboard
    public ClipboardSettings Clipboard { get; set; } = new();

    // Ring Light
    public RingLightSettings RingLight { get; set; } = new();

    // Volume Mixer
    public VolumeMixerSettings VolumeMixer { get; set; } = new();

    // Notifications
    public NotificationCustomSettings NotificationCustom { get; set; } = new();

    // First run
    public bool IsFirstRun { get; set; } = true;
}
