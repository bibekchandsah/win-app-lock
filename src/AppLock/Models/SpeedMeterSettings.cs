namespace AppLock.Models;

/// <summary>
/// Settings for the floating network-speed widget (Others → Speed Meter).
/// Stored in the AppLock settings JSON like every other section.
/// </summary>
public class SpeedMeterSettings
{
    public bool Enabled { get; set; }
    public bool StartWithAppLock { get; set; } = true;

    /// <summary>Toggle hotkey, in the same "Ctrl+Alt+N" form the other hotkey settings use.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+N";

    // Appearance
    public string ThemePreset { get; set; } = "Glass";        // Transparent|Glass|Solid|FollowWindows|Custom
    public string BackgroundColor { get; set; } = "#202020";
    public int BackgroundOpacityPercent { get; set; } = 70;
    public string TextColor { get; set; } = "#FFFFFF";
    public string DownloadColor { get; set; } = "#4ADE80";
    public string UploadColor { get; set; } = "#60A5FA";
    public int ScalePercent { get; set; } = 100;
    public string FontFamily { get; set; } = "Segoe UI Variable";
    public string FontWeight { get; set; } = "SemiBold";    // Normal|SemiBold|Bold
    public int CornerRadiusPx { get; set; } = 14;
    public bool ShowBorder { get; set; }
    public bool ShowShadow { get; set; } = true;
    public string Layout { get; set; } = "Horizontal";        // Horizontal|Vertical
    public bool Compact { get; set; }
    public string Units { get; set; } = "Auto";               // Auto|Bytes|Bits
    public int Decimals { get; set; } = 1;
    public double IntervalSeconds { get; set; } = 1.0;
    public bool ShowSparkline { get; set; }
    public bool UseIcons { get; set; } = true;

    // Extra stats
    public bool ShowCpu { get; set; }
    public bool ShowRam { get; set; }
    public string RamFormat { get; set; } = "Percent";        // Percent|UsedTotal
    public bool ShowDisk { get; set; }
    public bool ShowGpu { get; set; }
    public bool ShowPing { get; set; }
    public bool ShowBattery { get; set; }
    public bool ShowTodayTotal { get; set; }
    public int WarnCpuPercent { get; set; } = 85;
    public int WarnRamPercent { get; set; } = 85;
    public List<string> RowOrder { get; set; } = new();

    // Behavior
    public bool AlwaysOnTop { get; set; } = true;
    public string MonitorMode { get; set; } = "Primary";      // Primary|Specific|ActiveMonitor
    public string? MonitorId { get; set; }
    public float PositionPercentX { get; set; } = 98;
    public float PositionPercentY { get; set; } = 90;
    public bool Draggable { get; set; } = true;
    public bool LockPosition { get; set; }
    public bool SnapToEdges { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool HideWhenIdle { get; set; }
    public double IdleThresholdKBps { get; set; } = 1;
    public int IdleSeconds { get; set; } = 30;
    public bool HideWhenDisconnected { get; set; }
    public bool HideFromCapture { get; set; } = true;
    public bool FadeOnHover { get; set; } = true;
    public string AdapterMode { get; set; } = "Auto";         // Auto|AllPhysical|Specific
    public string? AdapterId { get; set; }
    public bool IncludeVirtualAdapters { get; set; }
    public string DoubleClickAction { get; set; } = "Flyout"; // Flyout|Compact|None

    // Alerts
    public ConnectionAlertSettings Alerts { get; set; } = new();

    // Data usage
    public bool TrackUsage { get; set; } = true;
    public double? DailyCapGb { get; set; }
    public double? MonthlyCapGb { get; set; }
    public int MonthlyResetDay { get; set; } = 1;
    public string? CapAdapterId { get; set; }

    public List<SpeedMeterPreset> CustomPresets { get; set; } = new();
}

/// <summary>Connection lost / restored notifications shown by the speed meter.</summary>
public class ConnectionAlertSettings
{
    public bool NotifyLost { get; set; } = true;
    public bool NotifyRestored { get; set; } = true;
    public string Style { get; set; } = "AppLockToast";       // AppLockToast|Windows
    public string Anchor { get; set; } = "TopCenter";
    public int MarginPx { get; set; } = 16;
    public int DurationSeconds { get; set; } = 5;
    public string? LostSoundPath { get; set; }
    public string? RestoredSoundPath { get; set; }
    public bool SoundEnabled { get; set; } = true;
    public int VolumePercent { get; set; } = 70;
    public bool SuppressInFullscreen { get; set; } = true;
    public string ToastTheme { get; set; } = "FollowSystem";  // FollowSystem|Dark|Light|Custom
    public int ToastOpacityPercent { get; set; } = 95;
    public string Animation { get; set; } = "SlideTop";       // SlideTop|Fade|None
    public bool ShowAdapterName { get; set; } = true;
    public bool ShowOutageDuration { get; set; } = true;
    public bool ShowPingOnRestore { get; set; }
    public int DebounceSeconds { get; set; } = 3;
    public int MinOutageToAnnounceSeconds { get; set; } = 5;
    public bool CheckRealInternet { get; set; } = true;

    /// <summary>Opt-in: the only settings-driven network request the feature can make.</summary>
    public bool ActiveProbe { get; set; }
    public string? ProbeUrl { get; set; }
}

/// <summary>A user-saved appearance preset (serialized settings blob).</summary>
public class SpeedMeterPreset
{
    public string Name { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = string.Empty;
}
