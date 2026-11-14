using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using H.NotifyIcon;
using AppLock.Models;
using AppLock.Services.BitLocker;
using AppLock.ViewModels;
using AppLock.Views;
using CommunityToolkit.Mvvm.Input;

namespace AppLock.Services;

/// <summary>
/// Manages the system tray icon and its context menu.
/// Provides: Open, Enable/Disable protection (with auth), Emergency lock, Exit (with auth), BitLocker drive locking/unlocking.
/// </summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private MenuFlyoutItem? _openMenuItem;
    private MenuFlyoutItem? _protectionMenuItem;
    private MenuFlyoutItem? _emergencyLockMenuItem;
    private MenuFlyoutItem? _clipboardToggleMenuItem;
    private MenuFlyoutItem? _clipboardHistoryMenuItem;
    private MenuFlyoutSubItem? _clipboardPauseSubItem;
    private MenuFlyoutItem? _clipboardPause5mItem;
    private MenuFlyoutItem? _clipboardPause15mItem;
    private MenuFlyoutItem? _clipboardPause1hItem;
    private MenuFlyoutItem? _clipboardPauseIndefiniteItem;
    private MenuFlyoutItem? _clipboardResumeItem;
    private MenuFlyoutItem? _brightnessHotkeysMenuItem;
    private MenuFlyoutItem? _blurHotkeyMenuItem;
    private MenuFlyoutItem? _eyeCareToggleMenuItem;
    private MenuFlyoutSubItem? _eyeCareIntervalSubItem;
    private ToggleMenuFlyoutItem? _eyeCareInterval1mItem;
    private ToggleMenuFlyoutItem? _eyeCareInterval20mItem;
    private ToggleMenuFlyoutItem? _eyeCareInterval25mItem;
    private ToggleMenuFlyoutItem? _eyeCareInterval30mItem;
    private ToggleMenuFlyoutItem? _eyeCareInterval60mItem;
    private ToggleMenuFlyoutItem? _eyeCareIntervalCustomItem;
    private MenuFlyoutItem? _ringLightToggleMenuItem;
    private MenuFlyoutItem? _volumeMixerToggleMenuItem;  // ADD THIS
    private MenuFlyoutSubItem? _volumeMixerPositionSubItem;
    private MenuFlyoutItem? _notificationEnabledMenuItem;
    private MenuFlyoutSubItem? _notificationPositionSubItem;
    private bool _isNotificationMenuInitialized;
    private ToggleMenuFlyoutItem? _vmPosTopLeftItem;
    private ToggleMenuFlyoutItem? _vmPosTopCenterItem;
    private ToggleMenuFlyoutItem? _vmPosTopRightItem;
    private ToggleMenuFlyoutItem? _vmPosBottomLeftItem;
    private ToggleMenuFlyoutItem? _vmPosBottomCenterItem;
    private ToggleMenuFlyoutItem? _vmPosBottomRightItem;
    private MenuFlyoutSubItem? _ringLightPresetsSubItem;
    private ToggleMenuFlyoutItem? _presetWarmMenuItem;
    private ToggleMenuFlyoutItem? _presetNeutralMenuItem;
    private ToggleMenuFlyoutItem? _presetCoolMenuItem;
    private ToggleMenuFlyoutItem? _presetSoftMenuItem;
    private ToggleMenuFlyoutItem? _presetStudioMenuItem;
    private ToggleMenuFlyoutItem? _presetMaxMenuItem;
    private readonly List<MenuFlyoutItemBase> _customPresetMenuItems = new();
    private ToggleMenuFlyoutItem? _speedMeterToggleMenuItem;
    private ToggleMenuFlyoutItem? _speedMeterLockMenuItem;
    private ToggleMenuFlyoutItem? _speedMeterClickThroughMenuItem;
    private MenuFlyoutItem? _speedMeterResetPositionMenuItem;
    private MenuFlyoutItem? _networkDetailsMenuItem;
    private MenuFlyoutItem? _exitMenuItem;
    
    private readonly List<MenuFlyoutItemBase> _bitLockerMenuItems = new();
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private bool _isVolumeWatcherSubscribed;
    
    private readonly SettingsStore _settingsStore;
    private readonly AuthService _authService;
    private readonly LockManager _lockManager;
    
    public event EventHandler? OpenMainWindowRequested;
    public event EventHandler? EmergencyLockRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? ProtectionToggleRequested;
    public event EventHandler<bool>? BrightnessHotkeysToggleRequested;
    public event EventHandler<bool>? BlurHotkeyToggleRequested;
    public event EventHandler<bool>? VolumeMixerToggleRequested;  // ADD THIS

    public MenuFlyout? ContextFlyout => _trayIcon?.ContextFlyout as MenuFlyout;

    public void ShowContextMenu()
    {
        try
        {
            var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
            dispatcher?.TryEnqueue(() =>
            {
                try
                {
                    // Get current cursor position for menu placement
                    if (Windows.Win32.PInvoke.GetCursorPos(out var pt))
                    {
                        var cursorPos = new System.Drawing.Point(pt.X, pt.Y);
                        _trayIcon?.ShowContextMenu(cursorPos);
                    }
                    else
                    {
                        _trayIcon?.ShowContextMenu(new System.Drawing.Point(0, 0));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayService] Error showing context menu: {ex.Message}");
                }
            });
        }
        catch { }
    }

    public void ShowBalloonNotification(string title, string message, NotificationSeverity severity = NotificationSeverity.Informational)
    {
        try
        {
            var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
            dispatcher?.TryEnqueue(() =>
            {
                try
                {
                    _trayIcon?.ShowNotification(title, message);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayService] Error showing balloon notification: {ex.Message}");
                }
            });
        }
        catch { }
    }

    public TrayService(SettingsStore settingsStore, AuthService authService, LockManager lockManager)
    {
        _settingsStore = settingsStore;
        _authService = authService;
        _lockManager = lockManager;
    }

    public void Initialize()
    {
        try
        {
            // Get tray icon from App.xaml resources
            _trayIcon = Application.Current.Resources["AppTrayIcon"] as TaskbarIcon;
            
            if (_trayIcon == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: Could not find AppTrayIcon in resources");
                return;
            }

            // Load icon
            LoadIcon();
            
            // Get menu items from the context flyout
            if (_trayIcon.ContextFlyout is MenuFlyout menuFlyout)
            {
                System.Diagnostics.Debug.WriteLine($"Context menu has {menuFlyout.Items.Count} items");
                
                foreach (var item in menuFlyout.Items)
                {
                    if (item is MenuFlyoutItem mfi)
                    {
                        var text = mfi.Text ?? "";
                        if (mfi.Name == "OpenMenuItem" || text.Contains("Open AppLock", StringComparison.OrdinalIgnoreCase))
                            _openMenuItem = mfi;
                        else if (mfi.Name == "ProtectionMenuItem" || text.Contains("Protection", StringComparison.OrdinalIgnoreCase))
                            _protectionMenuItem = mfi;
                        else if (mfi.Name == "EmergencyLockMenuItem" || text.Contains("Emergency", StringComparison.OrdinalIgnoreCase))
                            _emergencyLockMenuItem = mfi;
                        else if (mfi.Name == "ClipboardToggleMenuItem" || (text.Contains("Clipboard", StringComparison.OrdinalIgnoreCase) && !text.Contains("History", StringComparison.OrdinalIgnoreCase)))
                            _clipboardToggleMenuItem = mfi;
                        else if (mfi.Name == "ClipboardHistoryMenuItem" || text.Contains("Open history", StringComparison.OrdinalIgnoreCase) || text.Contains("History", StringComparison.OrdinalIgnoreCase))
                            _clipboardHistoryMenuItem = mfi;
                        else if (mfi.Name == "BrightnessHotkeysMenuItem" || text.Contains("Brightness Hotkeys", StringComparison.OrdinalIgnoreCase))
                            _brightnessHotkeysMenuItem = mfi;
                        else if (mfi.Name == "BlurHotkeyMenuItem" || text.Contains("Blur Hotkey", StringComparison.OrdinalIgnoreCase))
                            _blurHotkeyMenuItem = mfi;
                        else if (mfi.Name == "EyeCareToggleMenuItem" || (text.Contains("Eye Care", StringComparison.OrdinalIgnoreCase) && !text.Contains("interval", StringComparison.OrdinalIgnoreCase)))
                            _eyeCareToggleMenuItem = mfi;
                        else if (mfi.Name == "RingLightToggleMenuItem" || text.Contains("Ring Light", StringComparison.OrdinalIgnoreCase))
                            _ringLightToggleMenuItem = mfi;
                        else if (mfi.Name == "VolumeMixerToggleMenuItem" || text.Contains("Volume Mixer", StringComparison.OrdinalIgnoreCase))
                            _volumeMixerToggleMenuItem = mfi;
                        else if (mfi.Name == "NotificationEnabledMenuItem" || (text.Contains("Notification", StringComparison.OrdinalIgnoreCase) && !text.Contains("Position", StringComparison.OrdinalIgnoreCase)))
                            _notificationEnabledMenuItem = mfi;
                        else if (mfi is ToggleMenuFlyoutItem speedToggle && speedToggle.Name == "SpeedMeterToggleMenuItem")
                            _speedMeterToggleMenuItem = speedToggle;
                        else if (mfi is ToggleMenuFlyoutItem speedLock && speedLock.Name == "SpeedMeterLockMenuItem")
                            _speedMeterLockMenuItem = speedLock;
                        else if (mfi is ToggleMenuFlyoutItem speedClickThrough && speedClickThrough.Name == "SpeedMeterClickThroughMenuItem")
                            _speedMeterClickThroughMenuItem = speedClickThrough;
                        else if (mfi.Name == "SpeedMeterResetPositionMenuItem")
                            _speedMeterResetPositionMenuItem = mfi;
                        else if (mfi.Name == "NetworkDetailsMenuItem")
                            _networkDetailsMenuItem = mfi;
                        else if (mfi.Name == "ExitMenuItem" || text.Contains("Exit", StringComparison.OrdinalIgnoreCase))
                            _exitMenuItem = mfi;
                    }
                    else if (item is MenuFlyoutSubItem sub)
                    {
                        if (sub.Name == "RingLightPresetsSubItem" || (sub.Text != null && (sub.Text.Contains("Presets", StringComparison.OrdinalIgnoreCase) || sub.Text.Contains("Ring Light", StringComparison.OrdinalIgnoreCase))))
                        {
                            _ringLightPresetsSubItem = sub;
                            foreach (var subItem in sub.Items)
                            {
                                if (subItem is ToggleMenuFlyoutItem psmi)
                                {
                                    var pText = psmi.Text ?? "";
                                    if (psmi.Name == "PresetWarmMenuItem" || pText.Equals("Warm", StringComparison.OrdinalIgnoreCase))
                                        _presetWarmMenuItem = psmi;
                                    else if (psmi.Name == "PresetNeutralMenuItem" || pText.Equals("Neutral", StringComparison.OrdinalIgnoreCase))
                                        _presetNeutralMenuItem = psmi;
                                    else if (psmi.Name == "PresetCoolMenuItem" || pText.Equals("Cool", StringComparison.OrdinalIgnoreCase))
                                        _presetCoolMenuItem = psmi;
                                    else if (psmi.Name == "PresetSoftMenuItem" || pText.Equals("Soft", StringComparison.OrdinalIgnoreCase))
                                        _presetSoftMenuItem = psmi;
                                    else if (psmi.Name == "PresetStudioMenuItem" || pText.Equals("Studio", StringComparison.OrdinalIgnoreCase))
                                        _presetStudioMenuItem = psmi;
                                    else if (psmi.Name == "PresetMaxMenuItem" || pText.Contains("Max", StringComparison.OrdinalIgnoreCase))
                                        _presetMaxMenuItem = psmi;
                                }
                            }
                        }
                        else if (sub.Name == "EyeCareIntervalSubItem" || (sub.Text != null && sub.Text.Contains("Reminder interval", StringComparison.OrdinalIgnoreCase)))
                        {
                            _eyeCareIntervalSubItem = sub;
                            foreach (var subItem in sub.Items)
                            {
                                if (subItem is ToggleMenuFlyoutItem tmi)
                                {
                                    var st = (tmi.Text ?? "").Trim();
                                    if (tmi.Name == "EyeCareInterval1mItem" || st.StartsWith("1 min", StringComparison.OrdinalIgnoreCase))
                                        _eyeCareInterval1mItem = tmi;
                                    else if (tmi.Name == "EyeCareInterval20mItem" || st.StartsWith("20 min", StringComparison.OrdinalIgnoreCase))
                                        _eyeCareInterval20mItem = tmi;
                                    else if (tmi.Name == "EyeCareInterval25mItem" || st.StartsWith("25 min", StringComparison.OrdinalIgnoreCase))
                                        _eyeCareInterval25mItem = tmi;
                                    else if (tmi.Name == "EyeCareInterval30mItem" || st.StartsWith("30 min", StringComparison.OrdinalIgnoreCase))
                                        _eyeCareInterval30mItem = tmi;
                                    else if (tmi.Name == "EyeCareInterval60mItem" || st.StartsWith("60 min", StringComparison.OrdinalIgnoreCase))
                                        _eyeCareInterval60mItem = tmi;
                                    else if (tmi.Name == "EyeCareIntervalCustomItem" || st.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                                        _eyeCareIntervalCustomItem = tmi;
                                }
                            }
                        }
                        else if (sub.Name == "VolumeMixerPositionSubItem" || (sub.Text != null && sub.Text.Contains("Icon Position", StringComparison.OrdinalIgnoreCase)))
                        {
                            _volumeMixerPositionSubItem = sub;
                            foreach (var subItem in sub.Items)
                            {
                                if (subItem is ToggleMenuFlyoutItem tmi)
                                {
                                    var st = (tmi.Text ?? "").Trim();
                                    if (tmi.Name == "VmPosTopLeftItem" || st.Equals("Top Left", StringComparison.OrdinalIgnoreCase))
                                        _vmPosTopLeftItem = tmi;
                                    else if (tmi.Name == "VmPosTopCenterItem" || st.Equals("Top Center", StringComparison.OrdinalIgnoreCase))
                                        _vmPosTopCenterItem = tmi;
                                    else if (tmi.Name == "VmPosTopRightItem" || st.Equals("Top Right", StringComparison.OrdinalIgnoreCase))
                                        _vmPosTopRightItem = tmi;
                                    else if (tmi.Name == "VmPosBottomLeftItem" || st.Equals("Bottom Left", StringComparison.OrdinalIgnoreCase))
                                        _vmPosBottomLeftItem = tmi;
                                    else if (tmi.Name == "VmPosBottomCenterItem" || st.Equals("Bottom Center", StringComparison.OrdinalIgnoreCase))
                                        _vmPosBottomCenterItem = tmi;
                                    else if (tmi.Name == "VmPosBottomRightItem" || st.Equals("Bottom Right", StringComparison.OrdinalIgnoreCase))
                                        _vmPosBottomRightItem = tmi;
                                }
                            }
                        }
                        else if (sub.Name == "NotificationPositionSubItem" || (sub.Text != null && (sub.Text.Contains("Anchor", StringComparison.OrdinalIgnoreCase) || sub.Text.Contains("Notification", StringComparison.OrdinalIgnoreCase))))
                        {
                            _notificationPositionSubItem = sub;
                        }
                        else if (sub.Name == "ClipboardPauseSubItem" || (sub.Text != null && sub.Text.Contains("Pause", StringComparison.OrdinalIgnoreCase)))
                        {
                            _clipboardPauseSubItem = sub;
                            foreach (var subItem in sub.Items)
                            {
                                if (subItem is MenuFlyoutItem smi)
                                {
                                    var st = (smi.Text ?? "").Trim();
                                    // Match 15 minutes specifically before 5 minutes to avoid substring collision
                                    if (smi.Name == "ClipboardPause15mItem" || st.Equals("Pause for 15 minutes", StringComparison.OrdinalIgnoreCase) || st.Contains("15 min", StringComparison.OrdinalIgnoreCase))
                                        _clipboardPause15mItem = smi;
                                    else if (smi.Name == "ClipboardPause5mItem" || st.Equals("Pause for 5 minutes", StringComparison.OrdinalIgnoreCase) || (st.Contains("5 min", StringComparison.OrdinalIgnoreCase) && !st.Contains("15")))
                                        _clipboardPause5mItem = smi;
                                    else if (smi.Name == "ClipboardPause1hItem" || st.Equals("Pause for 1 hour", StringComparison.OrdinalIgnoreCase) || st.Contains("1 hour", StringComparison.OrdinalIgnoreCase))
                                        _clipboardPause1hItem = smi;
                                    else if (smi.Name == "ClipboardPauseIndefiniteItem" || st.Contains("until resumed", StringComparison.OrdinalIgnoreCase))
                                        _clipboardPauseIndefiniteItem = smi;
                                    else if (smi.Name == "ClipboardResumeItem" || st.Contains("Resume", StringComparison.OrdinalIgnoreCase))
                                        _clipboardResumeItem = smi;
                                }
                            }

                            // Positional fallback to guarantee correct binding even if XAML text or name differs
                            if (_clipboardPauseSubItem.Items.Count >= 6)
                            {
                                _clipboardPause5mItem ??= _clipboardPauseSubItem.Items[0] as MenuFlyoutItem;
                                _clipboardPause15mItem ??= _clipboardPauseSubItem.Items[1] as MenuFlyoutItem;
                                _clipboardPause1hItem ??= _clipboardPauseSubItem.Items[2] as MenuFlyoutItem;
                                _clipboardPauseIndefiniteItem ??= _clipboardPauseSubItem.Items[3] as MenuFlyoutItem;
                                _clipboardResumeItem ??= _clipboardPauseSubItem.Items[5] as MenuFlyoutItem;
                            }
                        }
                    }
                }

                // Fallback to index if needed
                if (_openMenuItem == null && menuFlyout.Items.Count > 0)
                    _openMenuItem = menuFlyout.Items[0] as MenuFlyoutItem;
                if (_protectionMenuItem == null && menuFlyout.Items.Count > 2)
                    _protectionMenuItem = menuFlyout.Items[2] as MenuFlyoutItem;
                if (_emergencyLockMenuItem == null && menuFlyout.Items.Count > 4)
                    _emergencyLockMenuItem = menuFlyout.Items[4] as MenuFlyoutItem;
                if (_exitMenuItem == null && menuFlyout.Items.Count > 6)
                    _exitMenuItem = menuFlyout.Items[6] as MenuFlyoutItem;
                
                WireSpeedMeterMenuItems();

                // Wire up commands exclusively (fires reliably without duplicate events)
                if (_openMenuItem != null)
                {
                    _openMenuItem.Click -= OpenMenuItem_Click;
                    _openMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Open menu item Command executed!");
                        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
                    });
                    System.Diagnostics.Debug.WriteLine("Open menu item wired");
                }
                
                if (_protectionMenuItem != null)
                {
                    _protectionMenuItem.Click -= ProtectionMenuItem_Click;
                    _protectionMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Protection menu item Command executed!");
                        var settings = _settingsStore.Load();
                        ProtectionToggleRequested?.Invoke(this, !settings.IsProtectionEnabled);
                    });
                    System.Diagnostics.Debug.WriteLine("Protection menu item wired");
                }
                
                if (_emergencyLockMenuItem != null)
                {
                    _emergencyLockMenuItem.Click -= EmergencyLockMenuItem_Click;
                    _emergencyLockMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Emergency lock menu item Command executed!");
                        EmergencyLockRequested?.Invoke(this, EventArgs.Empty);
                    });
                    System.Diagnostics.Debug.WriteLine("Emergency lock menu item wired");
                }

                if (_clipboardToggleMenuItem != null)
                {
                    Action toggleClipboard = () =>
                    {
                        bool newEnabled = !ServiceContainer.Settings.Clipboard.Enabled;
                        ServiceContainer.SetClipboardEnabled(newEnabled);
                        UpdateClipboardMenuItems();
                    };
                    _clipboardToggleMenuItem.Click += (s, e) => toggleClipboard();
                    _clipboardToggleMenuItem.Command = new RelayCommand(toggleClipboard);
                }

                if (_clipboardHistoryMenuItem != null)
                {
                    Action openHistory = () =>
                    {
                        var dispatcher = _dispatcherQueue ?? _trayIcon.DispatcherQueue;
                        dispatcher?.TryEnqueue(() => ClipboardPopupWindow.Toggle());
                    };
                    _clipboardHistoryMenuItem.Click += (s, e) => openHistory();
                    _clipboardHistoryMenuItem.Command = new RelayCommand(openHistory);
                }

                if (_clipboardPause5mItem != null)
                {
                    Action pause5m = () =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TrayService] Pause for 5 minutes executed!");
                        ServiceContainer.ClipboardMonitor.SetPause(TimeSpan.FromMinutes(5));
                        UpdateClipboardMenuItems();
                    };
                    _clipboardPause5mItem.Click += (s, e) => pause5m();
                    _clipboardPause5mItem.Command = new RelayCommand(pause5m);
                }

                if (_clipboardPause15mItem != null)
                {
                    Action pause15m = () =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TrayService] Pause for 15 minutes executed!");
                        ServiceContainer.ClipboardMonitor.SetPause(TimeSpan.FromMinutes(15));
                        UpdateClipboardMenuItems();
                    };
                    _clipboardPause15mItem.Click += (s, e) => pause15m();
                    _clipboardPause15mItem.Command = new RelayCommand(pause15m);
                }

                if (_clipboardPause1hItem != null)
                {
                    Action pause1h = () =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TrayService] Pause for 1 hour executed!");
                        ServiceContainer.ClipboardMonitor.SetPause(TimeSpan.FromHours(1));
                        UpdateClipboardMenuItems();
                    };
                    _clipboardPause1hItem.Click += (s, e) => pause1h();
                    _clipboardPause1hItem.Command = new RelayCommand(pause1h);
                }

                if (_clipboardPauseIndefiniteItem != null)
                {
                    Action pauseIndefinite = () =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TrayService] Pause indefinite executed!");
                        ServiceContainer.ClipboardMonitor.SetPause(TimeSpan.FromDays(365));
                        UpdateClipboardMenuItems();
                    };
                    _clipboardPauseIndefiniteItem.Click += (s, e) => pauseIndefinite();
                    _clipboardPauseIndefiniteItem.Command = new RelayCommand(pauseIndefinite);
                }

                if (_clipboardResumeItem != null)
                {
                    Action resume = () =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TrayService] Resume capture executed!");
                        ServiceContainer.ClipboardMonitor.Resume();
                        UpdateClipboardMenuItems();
                    };
                    _clipboardResumeItem.Click += (s, e) => resume();
                    _clipboardResumeItem.Command = new RelayCommand(resume);
                }
                
                if (_brightnessHotkeysMenuItem != null)
                {
                    _brightnessHotkeysMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Brightness hotkeys menu item Command executed!");
                        var settings = _settingsStore.Load();
                        BrightnessHotkeysToggleRequested?.Invoke(this, !settings.Brightness.HotkeysEnabled);
                    });
                    System.Diagnostics.Debug.WriteLine("Brightness hotkeys menu item wired");
                }
                
                if (_blurHotkeyMenuItem != null)
                {
                    _blurHotkeyMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Blur hotkey menu item Command executed!");
                        var settings = _settingsStore.Load();
                        BlurHotkeyToggleRequested?.Invoke(this, !settings.Brightness.Blur.HotkeyEnabled);
                    });
                    System.Diagnostics.Debug.WriteLine("Blur hotkey menu item wired");
                }

                if (_eyeCareToggleMenuItem != null)
                {
                    Action toggleEyeCare = () =>
                    {
                        bool newEnabled = !ServiceContainer.Settings.EyeCare.Enabled;
                        ServiceContainer.SetEyeCareEnabled(newEnabled);
                        UpdateEyeCareMenuItems();
                    };
                    _eyeCareToggleMenuItem.Click += (s, e) => toggleEyeCare();
                    _eyeCareToggleMenuItem.Command = new RelayCommand(toggleEyeCare);
                }

                if (_ringLightToggleMenuItem != null)
                {
                    Action toggleRingLight = () =>
                    {
                        bool newEnabled = !ServiceContainer.RingLightService.IsOn;
                        ServiceContainer.SetRingLightEnabled(newEnabled);
                        UpdateRingLightMenuItem();
                    };
                    _ringLightToggleMenuItem.Click += (s, e) => toggleRingLight();
                    _ringLightToggleMenuItem.Command = new RelayCommand(toggleRingLight);
                }

                if (_volumeMixerToggleMenuItem != null)
                {
                    Action toggleVolumeMixer = () =>
                    {
                        bool newEnabled = !ServiceContainer.Settings.VolumeMixer.Enabled;
                        VolumeMixerToggleRequested?.Invoke(this, newEnabled);
                        UpdateVolumeMixerMenuItem();
                    };
                    _volumeMixerToggleMenuItem.Click += (s, e) => toggleVolumeMixer();
                    _volumeMixerToggleMenuItem.Command = new RelayCommand(toggleVolumeMixer);
                }

                // Wire Volume Mixer icon position submenu.
                // Uses ServiceContainer.SetVolumeMixerPosition so the central event
                // (VolumeMixerPositionChanged) fires and both the settings page and the
                // tray checkmarks update in real-time without duplicating logic.
                void WireVmPosItem(ToggleMenuFlyoutItem? item, string positionId)
                {
                    if (item == null) return;
                    Action action = () => ServiceContainer.SetVolumeMixerPosition(positionId);
                    item.Click += (s, e) => action();
                    item.Command = new RelayCommand(action);
                }

                WireVmPosItem(_vmPosTopLeftItem,      "top-left");
                WireVmPosItem(_vmPosTopCenterItem,    "top-center");
                WireVmPosItem(_vmPosTopRightItem,     "top-right");
                WireVmPosItem(_vmPosBottomLeftItem,   "bottom-left");
                WireVmPosItem(_vmPosBottomCenterItem, "bottom-center");
                WireVmPosItem(_vmPosBottomRightItem,  "bottom-right");

                void WirePresetItem(ToggleMenuFlyoutItem? item, string presetId)
                {
                    if (item == null) return;
                    var preset = AppLock.Services.RingLight.RingPresets.BuiltIn
                        .FirstOrDefault(p => p.Id == presetId);
                    if (preset == null) return;
                    Action action = () =>
                    {
                        var s = ServiceContainer.Settings.RingLight;
                        // ApplyTo already sets ActivePresetId so UI chip highlights stay in sync
                        AppLock.Services.RingLight.RingPresets.ApplyTo(preset, s);
                        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                        ServiceContainer.RingLightService.ApplySettingsLive(s);
                        if (!ServiceContainer.RingLightService.IsOn)
                        {
                            ServiceContainer.SetRingLightEnabled(true);
                        }
                        UpdateRingLightMenuItem();
                    };
                    item.Click += (s, e) => action();
                    item.Command = new RelayCommand(action);
                }

                WirePresetItem(_presetWarmMenuItem,    AppLock.Services.RingLight.RingPresets.WarmId);
                WirePresetItem(_presetNeutralMenuItem, AppLock.Services.RingLight.RingPresets.NeutralId);
                WirePresetItem(_presetCoolMenuItem,    AppLock.Services.RingLight.RingPresets.CoolId);
                WirePresetItem(_presetSoftMenuItem,    AppLock.Services.RingLight.RingPresets.SoftId);
                WirePresetItem(_presetStudioMenuItem,  AppLock.Services.RingLight.RingPresets.StudioId);
                WirePresetItem(_presetMaxMenuItem,     AppLock.Services.RingLight.RingPresets.MaxId);

                void WireIntervalItem(ToggleMenuFlyoutItem? item, int minutes)
                {
                    if (item == null) return;
                    Action action = () =>
                    {
                        ServiceContainer.SetEyeCareInterval(minutes);
                        UpdateEyeCareMenuItems();
                    };
                    item.Click += (s, e) => action();
                    item.Command = new RelayCommand(action);
                }

                WireIntervalItem(_eyeCareInterval1mItem, 1);
                WireIntervalItem(_eyeCareInterval20mItem, 20);
                WireIntervalItem(_eyeCareInterval25mItem, 25);
                WireIntervalItem(_eyeCareInterval30mItem, 30);
                WireIntervalItem(_eyeCareInterval60mItem, 60);

                if (_eyeCareIntervalCustomItem != null)
                {
                    Action customAction = () =>
                    {
                        var dispatcher = _dispatcherQueue ?? _trayIcon.DispatcherQueue;
                        dispatcher?.TryEnqueue(async () =>
                        {
                            await PromptCustomIntervalAsync();
                        });
                    };
                    _eyeCareIntervalCustomItem.Click += (s, e) => customAction();
                    _eyeCareIntervalCustomItem.Command = new RelayCommand(customAction);
                }
                
                if (_exitMenuItem != null)
                {
                    _exitMenuItem.Click -= ExitMenuItem_Click;
                    _exitMenuItem.Command = new RelayCommand(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("Exit menu item Command executed!");
                        ExitRequested?.Invoke(this, EventArgs.Empty);
                    });
                    System.Diagnostics.Debug.WriteLine("Exit menu item wired");
                }
            }

            // Double click on tray icon opens main window
            _trayIcon.DoubleClickCommand = new RelayCommand(() =>
            {
                System.Diagnostics.Debug.WriteLine("Tray icon double clicked!");
                OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
            });

            // Explicit right-click command ensures menu opens reliably every time (fixes not showing on first click)
            // TrayRightMouseUp is a WinUI RoutedEvent; use RightClickCommand which is the supported property.
            _trayIcon.RightClickCommand = new RelayCommand(() =>
            {
                System.Diagnostics.Debug.WriteLine("[TrayService] Tray icon right-clicked via RightClickCommand!");
                ShowContextMenu();
            });
            
            UpdateTooltip();
            UpdateProtectionMenuItems();
            UpdateBrightnessMenuItems();
            UpdateClipboardMenuItems();
            UpdateEyeCareMenuItems();
            UpdateRingLightMenuItem();
            UpdateVolumeMixerMenuItem();
            UpdateVolumeMixerPositionSubItem();
            
            // Initialize notification menu (M6)
            InitializeNotificationMenu();

            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() ?? _trayIcon.DispatcherQueue;

            ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
            ServiceContainer.ClipboardMonitor.PauseStateChanged += OnClipboardPauseStateChanged;

            ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;
            ServiceContainer.ClipboardEnabledChanged += OnClipboardEnabledChanged;

            ServiceContainer.EyeCareEnabledChanged -= OnEyeCareEnabledChanged;
            ServiceContainer.EyeCareEnabledChanged += OnEyeCareEnabledChanged;

            ServiceContainer.EyeCareIntervalChanged -= OnEyeCareIntervalChanged;
            ServiceContainer.EyeCareIntervalChanged += OnEyeCareIntervalChanged;

            ServiceContainer.RingLightEnabledChanged -= OnRingLightEnabledChanged;
            ServiceContainer.RingLightEnabledChanged += OnRingLightEnabledChanged;

            ServiceContainer.RingLightService.SettingsLiveChanged -= OnRingLightSettingsLiveChanged;
            ServiceContainer.RingLightService.SettingsLiveChanged += OnRingLightSettingsLiveChanged;

            ServiceContainer.VolumeMixerEnabledChanged -= OnVolumeMixerEnabledChanged;
            ServiceContainer.VolumeMixerEnabledChanged += OnVolumeMixerEnabledChanged;

            ServiceContainer.VolumeMixerPositionChanged -= OnVolumeMixerPositionChanged;
            ServiceContainer.VolumeMixerPositionChanged += OnVolumeMixerPositionChanged;

            ServiceContainer.NotificationCustomizerEnabledChanged -= OnNotificationCustomizerEnabledChanged;
            ServiceContainer.NotificationCustomizerEnabledChanged += OnNotificationCustomizerEnabledChanged;

            ServiceContainer.NotificationAnchorChanged -= OnNotificationAnchorChanged;
            ServiceContainer.NotificationAnchorChanged += OnNotificationAnchorChanged;

            // Wire up volume change listener to keep BitLocker tray actions synced
            if (!_isVolumeWatcherSubscribed)
            {
                ServiceContainer.VolumeWatcher.VolumeChanged += OnVolumeWatcherChanged;
                _isVolumeWatcherSubscribed = true;
            }
            ServiceContainer.VolumeWatcher.Start();
            _ = UpdateBitLockerMenuItemsAsync();
            
            // IMPORTANT: Force create the tray icon - this makes it actually appear!
            _trayIcon.ForceCreate();
            
            System.Diagnostics.Debug.WriteLine("Tray icon initialized and ForceCreate() called");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TrayService initialization error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>Wires the tray's Speed Meter entries; they act on the shared service directly.</summary>
    private void WireSpeedMeterMenuItems()
    {
        if (_speedMeterToggleMenuItem is not null)
        {
            _speedMeterToggleMenuItem.Command = new RelayCommand(() =>
            {
                ServiceContainer.SetSpeedMeterEnabled(!ServiceContainer.Settings.SpeedMeter.Enabled);
                UpdateSpeedMeterMenuItems();
            });
        }

        if (_speedMeterLockMenuItem is not null)
        {
            _speedMeterLockMenuItem.Command = new RelayCommand(() =>
            {
                var settings = ServiceContainer.Settings.SpeedMeter;
                settings.LockPosition = !settings.LockPosition;
                _settingsStore.Save(ServiceContainer.Settings);
                ServiceContainer.SpeedMeterService?.ApplySettings();
                UpdateSpeedMeterMenuItems();
            });
        }

        if (_speedMeterClickThroughMenuItem is not null)
        {
            _speedMeterClickThroughMenuItem.Command = new RelayCommand(() =>
            {
                var settings = ServiceContainer.Settings.SpeedMeter;
                settings.ClickThrough = !settings.ClickThrough;
                _settingsStore.Save(ServiceContainer.Settings);
                ServiceContainer.SpeedMeterService?.ApplySettings();
                UpdateSpeedMeterMenuItems();
            });
        }

        if (_speedMeterResetPositionMenuItem is not null)
        {
            _speedMeterResetPositionMenuItem.Command = new RelayCommand(() =>
            {
                ServiceContainer.SpeedMeterService?.ResetWidgetPosition();
            });
        }

        if (_networkDetailsMenuItem is not null)
        {
            _networkDetailsMenuItem.Command = new RelayCommand(() =>
            {
                ServiceContainer.SpeedMeterService?.ToggleFlyout();
            });
        }

        UpdateSpeedMeterMenuItems();
    }

    /// <summary>Keeps the tray check marks in sync with the saved speed meter settings.</summary>
    public void UpdateSpeedMeterMenuItems()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;

        if (_speedMeterToggleMenuItem is not null) _speedMeterToggleMenuItem.IsChecked = settings.Enabled;
        if (_speedMeterLockMenuItem is not null) _speedMeterLockMenuItem.IsChecked = settings.LockPosition;
        if (_speedMeterClickThroughMenuItem is not null) _speedMeterClickThroughMenuItem.IsChecked = settings.ClickThrough;
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Open menu item clicked!");
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ProtectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Protection menu item clicked!");
        var settings = _settingsStore.Load();
        ProtectionToggleRequested?.Invoke(this, !settings.IsProtectionEnabled);
    }

    private void EmergencyLockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Emergency lock menu item clicked!");
        EmergencyLockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Exit menu item clicked!");
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private MenuFlyoutItem? FindMenuItemByName(MenuFlyout menu, string name)
    {
        System.Diagnostics.Debug.WriteLine($"Looking for menu item: {name}");
        System.Diagnostics.Debug.WriteLine($"Menu has {menu.Items.Count} items");
        
        foreach (var item in menu.Items)
        {
            System.Diagnostics.Debug.WriteLine($"  Item type: {item.GetType().Name}, Name: {(item is MenuFlyoutItem mfi ? mfi.Name : "N/A")}");
            
            if (item is MenuFlyoutItem menuItem && menuItem.Name == name)
            {
                System.Diagnostics.Debug.WriteLine($"  ✓ Found {name}!");
                return menuItem;
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"  ✗ Did not find {name}");
        return null;
    }

    private void LoadIcon()
    {
        if (_trayIcon == null) return;

        try
        {
            var assemblyLocation = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var iconPath = System.IO.Path.Combine(assemblyLocation ?? "", "icons", "icon.ico");
            
            if (System.IO.File.Exists(iconPath))
            {
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                System.Diagnostics.Debug.WriteLine($"Tray icon loaded from: {iconPath}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Tray icon not found at: {iconPath}");
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading icon: {ex.Message}");
        }
    }

    public void UpdateTooltip()
    {
        if (_trayIcon == null) return;

        var settings = _settingsStore.Load();
        var lockedCount = settings.LockRules.Count;
        
        string tooltip;
        if (settings.IsProtectionEnabled)
        {
            tooltip = $"AppLock - Protection Active ({lockedCount} {(lockedCount == 1 ? "rule" : "rules")})";
        }
        else
        {
            tooltip = "AppLock - Protection Disabled";
        }
        
        _trayIcon.ToolTipText = tooltip;
    }

    public void UpdateProtectionMenuItems()
    {
        if (_protectionMenuItem == null) return;
        
        var settings = _settingsStore.Load();
        _protectionMenuItem.Text = settings.IsProtectionEnabled ? "Disable Protection" : "Enable Protection";
        
        UpdateTooltip();
    }

    public void UpdateBrightnessMenuItems()
    {
        var settings = _settingsStore.Load();
        
        if (_brightnessHotkeysMenuItem != null)
        {
            _brightnessHotkeysMenuItem.Text = settings.Brightness.HotkeysEnabled 
                ? "Disable Brightness Hotkeys" 
                : "Enable Brightness Hotkeys";
        }
        
        if (_blurHotkeyMenuItem != null)
        {
            _blurHotkeyMenuItem.Text = settings.Brightness.Blur.HotkeyEnabled 
                ? "Disable Blur Hotkey" 
                : "Enable Blur Hotkey";
        }
    }

    private void OnClipboardPauseStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(UpdateClipboardMenuItems);
    }

    private void OnClipboardEnabledChanged(object? sender, bool enabled)
    {
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(UpdateClipboardMenuItems);
    }

    public void UpdateClipboardMenuItems()
    {
        try
        {
            bool enabled = ServiceContainer.Settings.Clipboard.Enabled;
            bool isPaused = ServiceContainer.ClipboardMonitor.IsPaused;

            if (_clipboardToggleMenuItem != null)
            {
                _clipboardToggleMenuItem.Text = enabled ? "Disable Clipboard" : "Enable Clipboard";
            }

            if (_clipboardHistoryMenuItem != null)
            {
                _clipboardHistoryMenuItem.IsEnabled = enabled;
            }

            if (_clipboardPauseSubItem != null)
            {
                _clipboardPauseSubItem.IsEnabled = enabled;
                if (!enabled)
                {
                    _clipboardPauseSubItem.Text = "Pause capture";
                }
                else if (isPaused)
                {
                    var pausedUntil = ServiceContainer.ClipboardMonitor.PausedUntil;
                    if (pausedUntil.HasValue && pausedUntil.Value < DateTime.Now.AddYears(1))
                    {
                        var rem = pausedUntil.Value - DateTime.Now;
                        string remText = rem.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(rem.TotalMinutes)}m" : "<1m";
                        _clipboardPauseSubItem.Text = $"Pause capture ({remText})";
                    }
                    else
                    {
                        _clipboardPauseSubItem.Text = "Pause capture (Paused)";
                    }
                }
                else
                {
                    _clipboardPauseSubItem.Text = "Pause capture";
                }
            }

            if (_clipboardResumeItem != null)
            {
                _clipboardResumeItem.IsEnabled = enabled && isPaused;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating clipboard menu: {ex.Message}");
        }
    }

    private void OnEyeCareEnabledChanged(object? sender, bool enabled)
    {
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(UpdateEyeCareMenuItems);
    }

    private void OnEyeCareIntervalChanged(object? sender, int minutes)
    {
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(UpdateEyeCareMenuItems);
    }

    private void OnRingLightEnabledChanged(object? sender, bool enabled)
    {
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(UpdateRingLightMenuItem);
    }

    private void OnRingLightSettingsLiveChanged(object? sender, EventArgs e)
    {
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(UpdateRingLightMenuItem);
    }

    private void OnVolumeMixerEnabledChanged(object? sender, bool enabled)
    {
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(() =>
        {
            try
            {
                UpdateVolumeMixerMenuItem();
                UpdateVolumeMixerPositionSubItem();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayService] Error in OnVolumeMixerEnabledChanged: {ex.Message}");
            }
        });
    }

    private void OnVolumeMixerPositionChanged(object? sender, string positionId)
    {
        // Position changed externally (e.g. from settings page) — sync tray checkmarks.
        var dispatcher = _dispatcherQueue ?? _trayIcon?.DispatcherQueue;
        dispatcher?.TryEnqueue(() =>
        {
            try
            {
                UpdateVolumeMixerPositionSubItem();
                System.Diagnostics.Debug.WriteLine($"[TrayService] Tray position checkmarks synced → {positionId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayService] Error in OnVolumeMixerPositionChanged: {ex.Message}");
            }
        });
    }

    public void UpdateVolumeMixerMenuItem()
    {
        try
        {
            if (_volumeMixerToggleMenuItem == null || _trayIcon == null) return;
            
            bool enabled = ServiceContainer.Settings.VolumeMixer.Enabled;
            _volumeMixerToggleMenuItem.Text = enabled ? "Disable Volume Mixer" : "Enable Volume Mixer";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating volume mixer menu: {ex.Message}");
        }
    }

    public void UpdateVolumeMixerPositionSubItem()
    {
        try
        {
            if (_volumeMixerPositionSubItem == null) return;
            var pos = ServiceContainer.Settings.VolumeMixer.IconPosition;
            if (_vmPosTopLeftItem      != null) _vmPosTopLeftItem.IsChecked      = pos == "top-left";
            if (_vmPosTopCenterItem    != null) _vmPosTopCenterItem.IsChecked    = pos == "top-center";
            if (_vmPosTopRightItem     != null) _vmPosTopRightItem.IsChecked     = pos == "top-right";
            if (_vmPosBottomLeftItem   != null) _vmPosBottomLeftItem.IsChecked   = pos == "bottom-left";
            if (_vmPosBottomCenterItem != null) _vmPosBottomCenterItem.IsChecked = pos == "bottom-center";
            if (_vmPosBottomRightItem  != null) _vmPosBottomRightItem.IsChecked  = pos == "bottom-right";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating volume mixer position menu: {ex.Message}");
        }
    }

    public void UpdateRingLightMenuItem()
    {
        try
        {
            if (_ringLightToggleMenuItem != null)
            {
                bool on = ServiceContainer.RingLightService.IsOn;
                _ringLightToggleMenuItem.Text = on ? "Disable Ring Light" : "Enable Ring Light";
            }
            UpdateRingLightPresetMenuItems();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating ring light menu: {ex.Message}");
        }
    }

    public void UpdateRingLightPresetMenuItems()
    {
        try
        {
            if (_ringLightPresetsSubItem == null) return;
            var s = ServiceContainer.Settings.RingLight;
            var activeId = s.ActivePresetId;

            // 1. Update checkmarks for built-in items
            if (_presetWarmMenuItem != null)
                _presetWarmMenuItem.IsChecked = activeId == AppLock.Services.RingLight.RingPresets.WarmId;
            if (_presetNeutralMenuItem != null)
                _presetNeutralMenuItem.IsChecked = activeId == AppLock.Services.RingLight.RingPresets.NeutralId;
            if (_presetCoolMenuItem != null)
                _presetCoolMenuItem.IsChecked = activeId == AppLock.Services.RingLight.RingPresets.CoolId;
            if (_presetSoftMenuItem != null)
                _presetSoftMenuItem.IsChecked = activeId == AppLock.Services.RingLight.RingPresets.SoftId;
            if (_presetStudioMenuItem != null)
                _presetStudioMenuItem.IsChecked = activeId == AppLock.Services.RingLight.RingPresets.StudioId;
            if (_presetMaxMenuItem != null)
                _presetMaxMenuItem.IsChecked = activeId == AppLock.Services.RingLight.RingPresets.MaxId;

            // 2. Remove previously added custom preset menu items
            foreach (var old in _customPresetMenuItems)
            {
                _ringLightPresetsSubItem.Items.Remove(old);
            }
            _customPresetMenuItems.Clear();

            // 3. Add separator and custom preset items if any exist
            if (s.CustomPresets.Count > 0)
            {
                var sep = new MenuFlyoutSeparator();
                _ringLightPresetsSubItem.Items.Add(sep);
                _customPresetMenuItems.Add(sep);

                foreach (var custom in s.CustomPresets)
                {
                    var customItem = new ToggleMenuFlyoutItem
                    {
                        Text = custom.Name,
                        IsChecked = activeId == custom.Id
                    };
                    string pid = custom.Id;
                    Action action = () =>
                    {
                        var set = ServiceContainer.Settings.RingLight;
                        var target = set.CustomPresets.FirstOrDefault(p => p.Id == pid);
                        if (target != null)
                        {
                            AppLock.Services.RingLight.RingPresets.ApplyTo(target, set);
                            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                            ServiceContainer.RingLightService.ApplySettingsLive(set);
                            if (!ServiceContainer.RingLightService.IsOn)
                            {
                                ServiceContainer.SetRingLightEnabled(true);
                            }
                            UpdateRingLightMenuItem();
                        }
                    };
                    customItem.Click += (sender, args) => action();
                    customItem.Command = new RelayCommand(action);

                    _ringLightPresetsSubItem.Items.Add(customItem);
                    _customPresetMenuItems.Add(customItem);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating ring light presets: {ex.Message}");
        }
    }

    public void UpdateEyeCareMenuItems()
    {
        try
        {
            var settings = ServiceContainer.Settings.EyeCare;
            bool enabled = settings.Enabled;
            int interval = settings.IntervalMinutes;

            if (_eyeCareToggleMenuItem != null)
            {
                _eyeCareToggleMenuItem.Text = enabled ? "Disable Eye Care" : "Enable Eye Care";
            }

            if (_eyeCareIntervalSubItem != null)
            {
                _eyeCareIntervalSubItem.Text = $"Reminder interval ({interval}m)";
            }

            if (_eyeCareInterval1mItem != null)
                _eyeCareInterval1mItem.IsChecked = interval == 1;

            if (_eyeCareInterval20mItem != null)
                _eyeCareInterval20mItem.IsChecked = interval == 20;

            if (_eyeCareInterval25mItem != null)
                _eyeCareInterval25mItem.IsChecked = interval == 25;

            if (_eyeCareInterval30mItem != null)
                _eyeCareInterval30mItem.IsChecked = interval == 30;

            if (_eyeCareInterval60mItem != null)
                _eyeCareInterval60mItem.IsChecked = interval == 60;

            if (_eyeCareIntervalCustomItem != null)
            {
                bool isCustom = interval != 1 && interval != 20 && interval != 25 && interval != 30 && interval != 60;
                _eyeCareIntervalCustomItem.IsChecked = isCustom;
                _eyeCareIntervalCustomItem.Text = isCustom ? $"Custom ({interval}m)..." : "Custom...";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating eye care menu: {ex.Message}");
        }
    }

    private async Task PromptCustomIntervalAsync()
    {
        try
        {
            if (MainWindow.Instance == null) return;

            MainWindow.Instance.Show();

            var numberBox = new NumberBox
            {
                Minimum = 1,
                Maximum = 1440,
                Value = ServiceContainer.Settings.EyeCare.IntervalMinutes,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                SmallChange = 1,
                LargeChange = 5,
                Header = "Interval (1 to 1440 minutes):",
                Width = 250
            };

            var dialog = new ContentDialog
            {
                Title = "Custom Reminder Interval",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = MainWindow.Instance.Content?.XamlRoot,
                Content = numberBox
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var value = (int)numberBox.Value;
                if (value >= 1 && value <= 1440)
                {
                    ServiceContainer.SetEyeCareInterval(value);
                    UpdateEyeCareMenuItems();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error showing custom interval dialog: {ex.Message}");
        }
    }

    private void OnVolumeWatcherChanged(object? sender, EventArgs e)
    {
        _ = UpdateBitLockerMenuItemsAsync();
    }

    public async Task UpdateBitLockerMenuItemsAsync()
    {
        try
        {
            if (_trayIcon?.ContextFlyout is not MenuFlyout menuFlyout) return;

            var support = ServiceContainer.BitLockerSupportService;
            if (!support.IsSupported) return;

            var service = ServiceContainer.BitLockerService;
            var drives = await service.GetDrivesAsync();

            var dispatcher = _dispatcherQueue ?? _trayIcon.DispatcherQueue;
            if (dispatcher == null) return;

            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    // 1. Remove previous BitLocker items
                    foreach (var oldItem in _bitLockerMenuItems)
                    {
                        menuFlyout.Items.Remove(oldItem);
                    }
                    _bitLockerMenuItems.Clear();

                    // 2. Identify BitLocker drives to display:
                    // - Locked drive: can be unlocked (e.g., "Unlock Drive C", "Unlock Drive D")
                    // - Unlocked drive: non-OS BitLocker drive can be locked (e.g., "Lock Drive D")
                    var actionableDrives = drives.Where(d =>
                        d.LockStatus == LockStatus.Locked ||
                        (d.LockStatus == LockStatus.Unlocked &&
                         d.DriveType != AppLock.Models.DriveType.OperatingSystem &&
                         d.ProtectionStatus != ProtectionStatus.Off)
                    ).ToList();

                    if (actionableDrives.Count == 0) return;

                    // 3. Find insertion position (before BrightnessHotkeysMenuItem, or before Exit)
                    int insertIndex = -1;
                    if (_brightnessHotkeysMenuItem != null)
                    {
                        insertIndex = menuFlyout.Items.IndexOf(_brightnessHotkeysMenuItem);
                        if (insertIndex > 0 && menuFlyout.Items[insertIndex - 1] is MenuFlyoutSeparator)
                        {
                            insertIndex--;
                        }
                    }
                    else if (_exitMenuItem != null)
                    {
                        insertIndex = menuFlyout.Items.IndexOf(_exitMenuItem);
                        if (insertIndex > 0 && menuFlyout.Items[insertIndex - 1] is MenuFlyoutSeparator)
                        {
                            insertIndex--;
                        }
                    }

                    if (insertIndex < 0 || insertIndex > menuFlyout.Items.Count)
                    {
                        insertIndex = menuFlyout.Items.Count;
                    }

                    // 4. Insert separator
                    var sep = new MenuFlyoutSeparator();
                    menuFlyout.Items.Insert(insertIndex++, sep);
                    _bitLockerMenuItems.Add(sep);

                    // 5. Add actionable drive items
                    foreach (var drive in actionableDrives)
                    {
                        var driveLetter = drive.DriveLetter;
                        var cleanLetter = driveLetter.TrimEnd(':');

                        if (drive.LockStatus == LockStatus.Locked)
                        {
                            var item = new MenuFlyoutItem
                            {
                                Text = $"Unlock Drive {cleanLetter}",
                                Icon = new FontIcon { Glyph = "\uE785" },
                                Command = new RelayCommand(async () => await HandleUnlockDriveFromTrayAsync(driveLetter))
                            };
                            menuFlyout.Items.Insert(insertIndex++, item);
                            _bitLockerMenuItems.Add(item);
                        }
                        else
                        {
                            var item = new MenuFlyoutItem
                            {
                                Text = $"Lock Drive {cleanLetter}",
                                Icon = new FontIcon { Glyph = "\uE72E" },
                                Command = new RelayCommand(async () => await HandleLockDriveFromTrayAsync(driveLetter))
                            };
                            menuFlyout.Items.Insert(insertIndex++, item);
                            _bitLockerMenuItems.Add(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayService] UI error updating BitLocker menu: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating BitLocker menu: {ex.Message}");
        }
    }

    private async Task HandleLockDriveFromTrayAsync(string driveLetter)
    {
        try
        {
            var service = ServiceContainer.BitLockerService;
            var result = await service.LockAsync(driveLetter, forceDismount: true);
            if (result.Success)
            {
                ShowNotification("BitLocker", $"Drive {driveLetter} is now locked.");
                await UpdateBitLockerMenuItemsAsync();
            }
            else
            {
                ShowNotification("BitLocker", $"Failed to lock drive {driveLetter}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error locking drive {driveLetter}: {ex.Message}");
        }
    }

    private async Task HandleUnlockDriveFromTrayAsync(string driveLetter)
    {
        try
        {
            // Check if there is a saved password for this drive
            var savedStore = ServiceContainer.SavedPasswordStore;
            var savedEntry = await savedStore.GetForDriveAsync(driveLetter);

            if (savedEntry != null && savedEntry.IsValid)
            {
                var hello = new HelloService();
                bool verified = false;
                try
                {
                    if (await hello.IsAvailableAsync())
                    {
                        verified = await hello.RequestVerificationAsync($"Unlock BitLocker drive {driveLetter}");
                    }
                }
                catch { }

                if (verified)
                {
                    var passphrase = await savedStore.GetDecryptedPassphraseAsync(savedEntry.VolumeGuid, driveLetter);
                    if (!string.IsNullOrEmpty(passphrase))
                    {
                        var unlockResult = await ServiceContainer.BitLockerService.UnlockWithPassphraseAsync(driveLetter, passphrase);
                        if (unlockResult.Success)
                        {
                            ShowNotification("BitLocker", $"Drive {driveLetter} unlocked successfully.");
                            if (ServiceContainer.Settings.BitLockerOpenDriveAfterUnlock)
                            {
                                BitLockerViewModel.TryOpenDriveInExplorer(driveLetter);
                            }
                            await UpdateBitLockerMenuItemsAsync();
                            return;
                        }
                    }
                }
            }

            // Fallback: If no saved password or verification was not completed, open the main window
            OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error unlocking drive {driveLetter}: {ex.Message}");
            OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"Notification: {title} - {message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing notification: {ex.Message}");
        }
    }

    public void Dispose()
    {
        ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
        ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;

        ServiceContainer.EyeCareEnabledChanged -= OnEyeCareEnabledChanged;
        ServiceContainer.EyeCareIntervalChanged -= OnEyeCareIntervalChanged;

        ServiceContainer.RingLightEnabledChanged -= OnRingLightEnabledChanged;
        ServiceContainer.RingLightService.SettingsLiveChanged -= OnRingLightSettingsLiveChanged;

        ServiceContainer.VolumeMixerEnabledChanged -= OnVolumeMixerEnabledChanged;
        ServiceContainer.VolumeMixerPositionChanged -= OnVolumeMixerPositionChanged;

        ServiceContainer.NotificationCustomizerEnabledChanged -= OnNotificationCustomizerEnabledChanged;
        ServiceContainer.NotificationAnchorChanged -= OnNotificationAnchorChanged;

        if (_isVolumeWatcherSubscribed)
        {
            ServiceContainer.VolumeWatcher.VolumeChanged -= OnVolumeWatcherChanged;
            _isVolumeWatcherSubscribed = false;
        }

        // Dispose the tray icon here, while the UI thread and the message loop are still alive.
        // Leaving it to App.xaml resource teardown runs H.NotifyIcon's window procedure during
        // shutdown, which shows "Exception Processing Message 0xc0000005 - Unexpected parameters".
        try
        {
            _trayIcon?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error disposing tray icon: {ex.Message}");
        }
        finally
        {
            _trayIcon = null;
        }
    }

    // ── Notification Position Tray Menu (M6) ──────────────────────────────────
    
    public void InitializeNotificationMenu()
    {
        try
        {
            if (_trayIcon?.ContextFlyout is not MenuFlyout menuFlyout)
                return;

            if (_notificationEnabledMenuItem == null || _notificationPositionSubItem == null)
            {
                foreach (var item in menuFlyout.Items)
                {
                    if (item is MenuFlyoutItem mfi && (mfi.Name == "NotificationEnabledMenuItem" || (mfi.Text != null && mfi.Text.Contains("Notification", StringComparison.OrdinalIgnoreCase) && !mfi.Text.Contains("Position", StringComparison.OrdinalIgnoreCase))))
                    {
                        _notificationEnabledMenuItem = mfi;
                    }
                    else if (item is MenuFlyoutSubItem sub && (sub.Name == "NotificationPositionSubItem" || (sub.Text != null && (sub.Text.Contains("Anchor", StringComparison.OrdinalIgnoreCase) || sub.Text.Contains("Notification", StringComparison.OrdinalIgnoreCase)))))
                    {
                        _notificationPositionSubItem = sub;
                    }
                }
            }

            if (!_isNotificationMenuInitialized)
            {
                if (_notificationEnabledMenuItem != null)
                {
                    Action toggleNotif = () => OnNotificationEnabledToggle(this, new RoutedEventArgs());
                    _notificationEnabledMenuItem.Click += (s, e) => toggleNotif();
                    _notificationEnabledMenuItem.Command = new RelayCommand(toggleNotif);
                }

                if (_notificationPositionSubItem != null)
                {
                    foreach (var item in _notificationPositionSubItem.Items)
                    {
                        if (item is MenuFlyoutItem mfi)
                        {
                            string? anchor = mfi.Name switch
                            {
                                "NotifTopLeftMenuItem" => "TopLeft",
                                "NotifTopCenterMenuItem" => "TopCenter",
                                "NotifTopRightMenuItem" => "TopRight",
                                "NotifMiddleLeftMenuItem" => "MiddleLeft",
                                "NotifCenterMenuItem" => "Center",
                                "NotifMiddleRightMenuItem" => "MiddleRight",
                                "NotifBottomLeftMenuItem" => "BottomLeft",
                                "NotifBottomCenterMenuItem" => "BottomCenter",
                                "NotifBottomRightMenuItem" => "BottomRight",
                                "NotifCustomMenuItem" => "Custom",
                                _ => null
                            };

                            if (anchor == null && mfi.Text != null)
                            {
                                var t = mfi.Text.Trim();
                                if (t.Equals("Top Left", StringComparison.OrdinalIgnoreCase)) anchor = "TopLeft";
                                else if (t.Equals("Top Center", StringComparison.OrdinalIgnoreCase)) anchor = "TopCenter";
                                else if (t.Equals("Top Right", StringComparison.OrdinalIgnoreCase)) anchor = "TopRight";
                                else if (t.Equals("Middle Left", StringComparison.OrdinalIgnoreCase)) anchor = "MiddleLeft";
                                else if (t.Equals("Center", StringComparison.OrdinalIgnoreCase)) anchor = "Center";
                                else if (t.Equals("Middle Right", StringComparison.OrdinalIgnoreCase)) anchor = "MiddleRight";
                                else if (t.Equals("Bottom Left", StringComparison.OrdinalIgnoreCase)) anchor = "BottomLeft";
                                else if (t.Equals("Bottom Center", StringComparison.OrdinalIgnoreCase)) anchor = "BottomCenter";
                                else if (t.Equals("Bottom Right", StringComparison.OrdinalIgnoreCase)) anchor = "BottomRight";
                                else if (t.StartsWith("Custom", StringComparison.OrdinalIgnoreCase)) anchor = "Custom";
                            }

                            if (anchor != null)
                            {
                                string anchorCaptured = anchor;
                                Action anchorAction = () => OnNotificationAnchorSelected(anchorCaptured);
                                mfi.Click += (s, e) => anchorAction();
                                mfi.Command = new RelayCommand(anchorAction);
                            }
                        }
                    }
                }

                _isNotificationMenuInitialized = true;
            }

            // Update initial state
            UpdateNotificationPositionMenuItems();
        }
        catch (Exception ex)
        {
            LogService.LogAction("tray", $"Error initializing notification menu: {ex.Message}");
        }
    }

    private void OnNotificationCustomizerEnabledChanged(object? sender, bool enabled)
    {
        UpdateNotificationPositionMenuItems();
    }

    private void OnNotificationAnchorChanged(object? sender, string anchor)
    {
        UpdateNotificationPositionMenuItems();
    }

    private void OnNotificationEnabledToggle(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = ServiceContainer.Settings.NotificationCustom.Enabled;
            ServiceContainer.SetNotificationCustomizerEnabled(!enabled);
            UpdateNotificationPositionMenuItems();
            
            LogService.LogAction("notifications", $"Notification positioning {(!enabled ? "enabled" : "disabled")} from tray");
        }
        catch (Exception ex)
        {
            LogService.LogAction("tray", $"Error toggling notification enabled: {ex.Message}");
        }
    }

    private async void OnNotificationAnchorSelected(string anchor)
    {
        try
        {
            if (anchor == "Custom")
            {
                await TriggerCustomPositionPickerAsync();
            }
            else
            {
                ServiceContainer.SetNotificationAnchor(anchor);
                UpdateNotificationPositionMenuItems();
                
                LogService.LogAction("notifications", $"Anchor changed from tray: {anchor}");
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("tray", $"Error changing notification anchor: {ex.Message}");
        }
    }

    private async Task TriggerCustomPositionPickerAsync()
    {
        try
        {
            if (_dispatcherQueue == null) return;

            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // Get primary monitor bounds
                    var primaryMonitor = Windows.Win32.PInvoke.MonitorFromPoint(
                        new System.Drawing.Point(0, 0), 
                        Windows.Win32.Graphics.Gdi.MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

                    var info = new Windows.Win32.Graphics.Gdi.MONITORINFO
                    {
                        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Windows.Win32.Graphics.Gdi.MONITORINFO>()
                    };
                    
                    unsafe
                    {
                        Windows.Win32.PInvoke.GetMonitorInfo(primaryMonitor, ref info);
                    }

                    var bounds = new Windows.Graphics.RectInt32(
                        info.rcMonitor.left,
                        info.rcMonitor.top,
                        info.rcMonitor.right - info.rcMonitor.left,
                        info.rcMonitor.bottom - info.rcMonitor.top);

                    var picker = new Views.PickToastPositionOverlay(primaryMonitor, bounds);
                    picker.Activate();

                    await picker.WaitForCloseAsync();

                    if (picker.WasConfirmed)
                    {
                        ServiceContainer.SetNotificationCustomPosition(picker.SelectedPercentX, picker.SelectedPercentY);
                        UpdateNotificationPositionMenuItems();
                        
                        LogService.LogAction("notifications",
                            $"Custom position saved from tray: X={picker.SelectedPercentX:P1} Y={picker.SelectedPercentY:P1}");
                    }
                    else
                    {
                        UpdateNotificationPositionMenuItems();
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogAction("tray", $"Error during custom position picker: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            LogService.LogAction("tray", $"Error opening custom position picker: {ex.Message}");
        }
    }

    public void UpdateNotificationPositionMenuItems()
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(UpdateNotificationPositionMenuItems);
            return;
        }

        try
        {
            if (_trayIcon?.ContextFlyout is not MenuFlyout menuFlyout)
                return;

            MenuFlyoutItem? enabledMenuItem = _notificationEnabledMenuItem;
            MenuFlyoutSubItem? notifSubMenu = _notificationPositionSubItem;
            
            if (enabledMenuItem == null || notifSubMenu == null)
            {
                foreach (var item in menuFlyout.Items)
                {
                    if (item is MenuFlyoutItem mfi && (mfi.Name == "NotificationEnabledMenuItem" || (mfi.Text != null && mfi.Text.Contains("Notification", StringComparison.OrdinalIgnoreCase) && !mfi.Text.Contains("Position", StringComparison.OrdinalIgnoreCase))))
                    {
                        enabledMenuItem = mfi;
                        _notificationEnabledMenuItem = mfi;
                    }
                    else if (item is MenuFlyoutSubItem sub && (sub.Name == "NotificationPositionSubItem" || (sub.Text != null && (sub.Text.Contains("Anchor", StringComparison.OrdinalIgnoreCase) || sub.Text.Contains("Notification", StringComparison.OrdinalIgnoreCase)))))
                    {
                        notifSubMenu = sub;
                        _notificationPositionSubItem = sub;
                    }
                }
            }

            var settings = ServiceContainer.Settings.NotificationCustom;
            
            // Update enable/disable menu item
            if (enabledMenuItem != null)
            {
                enabledMenuItem.Text = settings.Enabled 
                    ? "Disable Notification Positioning" 
                    : "Enable Notification Positioning";
                enabledMenuItem.Icon = settings.Enabled 
                    ? new FontIcon { Glyph = "\uE73E" } 
                    : new FontIcon { Glyph = "\uE91C" };
            }

            if (notifSubMenu == null)
                return;

            var currentAnchor = settings.Anchor;

            // Update checkmarks for position items
            foreach (var item in notifSubMenu.Items)
            {
                if (item is MenuFlyoutItem mfi)
                {
                    string? anchor = mfi.Name switch
                    {
                        "NotifTopLeftMenuItem" => "TopLeft",
                        "NotifTopCenterMenuItem" => "TopCenter",
                        "NotifTopRightMenuItem" => "TopRight",
                        "NotifMiddleLeftMenuItem" => "MiddleLeft",
                        "NotifCenterMenuItem" => "Center",
                        "NotifMiddleRightMenuItem" => "MiddleRight",
                        "NotifBottomLeftMenuItem" => "BottomLeft",
                        "NotifBottomCenterMenuItem" => "BottomCenter",
                        "NotifBottomRightMenuItem" => "BottomRight",
                        "NotifCustomMenuItem" => "Custom",
                        _ => null
                    };

                    if (anchor == null && mfi.Text != null)
                    {
                        var t = mfi.Text.Trim();
                        if (t.Equals("Top Left", StringComparison.OrdinalIgnoreCase)) anchor = "TopLeft";
                        else if (t.Equals("Top Center", StringComparison.OrdinalIgnoreCase)) anchor = "TopCenter";
                        else if (t.Equals("Top Right", StringComparison.OrdinalIgnoreCase)) anchor = "TopRight";
                        else if (t.Equals("Middle Left", StringComparison.OrdinalIgnoreCase)) anchor = "MiddleLeft";
                        else if (t.Equals("Center", StringComparison.OrdinalIgnoreCase)) anchor = "Center";
                        else if (t.Equals("Middle Right", StringComparison.OrdinalIgnoreCase)) anchor = "MiddleRight";
                        else if (t.Equals("Bottom Left", StringComparison.OrdinalIgnoreCase)) anchor = "BottomLeft";
                        else if (t.Equals("Bottom Center", StringComparison.OrdinalIgnoreCase)) anchor = "BottomCenter";
                        else if (t.Equals("Bottom Right", StringComparison.OrdinalIgnoreCase)) anchor = "BottomRight";
                        else if (t.StartsWith("Custom", StringComparison.OrdinalIgnoreCase)) anchor = "Custom";
                    }

                    bool isSelected = anchor != null && string.Equals(anchor, currentAnchor, StringComparison.OrdinalIgnoreCase);

                    if (mfi is ToggleMenuFlyoutItem tmfi)
                    {
                        tmfi.IsChecked = isSelected;
                    }
                    else
                    {
                        mfi.Icon = isSelected ? new FontIcon { Glyph = "\uE73E" } : null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("tray", $"Error updating notification menu: {ex.Message}");
        }
    }
}
