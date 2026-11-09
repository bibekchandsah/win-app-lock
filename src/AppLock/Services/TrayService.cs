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
    private MenuFlyoutSubItem? _ringLightPresetsSubItem;
    private MenuFlyoutItem? _presetWarmMenuItem;
    private MenuFlyoutItem? _presetNeutralMenuItem;
    private MenuFlyoutItem? _presetCoolMenuItem;
    private MenuFlyoutItem? _presetSoftMenuItem;
    private MenuFlyoutItem? _presetStudioMenuItem;
    private MenuFlyoutItem? _presetMaxMenuItem;
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
                                if (subItem is MenuFlyoutItem psmi)
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

                void WirePresetItem(MenuFlyoutItem? item, string presetId)
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
            
            UpdateTooltip();
            UpdateProtectionMenuItems();
            UpdateBrightnessMenuItems();
            UpdateClipboardMenuItems();
            UpdateEyeCareMenuItems();
            UpdateRingLightMenuItem();

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

    public void UpdateRingLightMenuItem()
    {
        try
        {
            if (_ringLightToggleMenuItem == null) return;
            bool on = ServiceContainer.RingLightService.IsOn;
            _ringLightToggleMenuItem.Text = on ? "Disable Ring Light" : "Enable Ring Light";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayService] Error updating ring light menu: {ex.Message}");
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

        if (_isVolumeWatcherSubscribed)
        {
            ServiceContainer.VolumeWatcher.VolumeChanged -= OnVolumeWatcherChanged;
            _isVolumeWatcherSubscribed = false;
        }

        // Don't dispose the tray icon as it's managed by App.xaml resources
        _trayIcon = null;
    }
}
