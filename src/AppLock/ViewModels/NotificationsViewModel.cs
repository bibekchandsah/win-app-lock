using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AppLock.Services;
using AppLock.Services.Notifications;
using AppLock.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.ViewModels;

public partial class NotificationsViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _statusText = "Waiting for a notification";

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private bool _respectTaskbar;

    [ObservableProperty]
    private string _selectedAnchor = "BottomRight";

    [ObservableProperty]
    private BitmapImage? _wallpaperImageSource;

    [ObservableProperty]
    private double _previewToastLeft;

    [ObservableProperty]
    private double _previewToastTop;

    [ObservableProperty]
    private int _opacityPercent;

    [ObservableProperty]
    private string _toggleHotkeyText = "Not set";

    public string AnchorDisplayText => $"Current: {SelectedAnchor}";
    public string MarginText => $"{ServiceContainer.Settings.NotificationCustom.MarginPx} px";
    public string OpacityText => $"{OpacityPercent}%";
    
    // Convert opacity percentage to 0.0-1.0 for XAML Opacity binding
    public double PreviewOpacity => OpacityPercent / 100.0;

    public ObservableCollection<ToastCandidateViewModel> Candidates { get; } = new();

    // Smaller, more realistic preview size (16:9 ratio, scaled down from 400x225)
    private const double PreviewWidth = 320;
    private const double PreviewHeight = 180;
    // Realistic toast size scaled to preview (actual ~364x90, scaled to preview ratio)
    private const double ToastPreviewWidth = 120;
    private const double ToastPreviewHeight = 32;

    public NotificationsViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        LoadSettings();
        LoadWallpaperAsync();
        UpdatePreviewPosition();
    }

    private void LoadSettings()
    {
        Enabled = ServiceContainer.Settings.NotificationCustom.Enabled;
        RespectTaskbar = ServiceContainer.Settings.NotificationCustom.RespectTaskbar;
        SelectedAnchor = ServiceContainer.Settings.NotificationCustom.Anchor;
        OpacityPercent = ServiceContainer.Settings.NotificationCustom.OpacityPercent;
        
        // Load hotkey text
        UpdateToggleHotkeyText();
    }

    private void UpdateToggleHotkeyText()
    {
        var hotkey = ServiceContainer.Settings.NotificationCustom.ToggleHotkey;
        if (hotkey != null && hotkey.VirtualKey != 0)
        {
            ToggleHotkeyText = HotkeyParser.FormatHotkey(hotkey.Modifiers, hotkey.VirtualKey);
        }
        else
        {
            ToggleHotkeyText = "Not set";
        }
    }

    [RelayCommand]
    private async Task ChangeToggleHotkeyAsync()
    {
        try
        {
            var currentHotkey = ServiceContainer.Settings.NotificationCustom.ToggleHotkey?.ToString() ?? "Not set";
            var dialog = new Views.ChangeHotkeyDialog(currentHotkey);
            
            // Wait for XamlRoot to be available
            if (App.MainWindowInstance?.Content?.XamlRoot != null)
            {
                dialog.XamlRoot = App.MainWindowInstance.Content.XamlRoot;
            }
            
            var result = await dialog.ShowAsync();
            
            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && dialog.HotkeyChanged)
            {
                // Parse the hotkey string
                var (modifiers, key) = HotkeyService.ParseHotkey(dialog.NewHotkey ?? "");
                
                if (key != 0)
                {
                    ServiceContainer.Settings.NotificationCustom.ToggleHotkey = new HotkeyBinding
                    {
                        VirtualKey = key,
                        Modifiers = (uint)modifiers
                    };
                    ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                    UpdateToggleHotkeyText();
                    
                    // Re-register the hotkey
                    (App.MainWindowInstance as MainWindow)?.RegisterNotificationToggleHotkey();
                    
                    LogService.LogAction("notifications", $"Toggle hotkey changed to: {dialog.NewHotkey}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Error changing hotkey: {ex.Message}");
        }
    }

    private async void LoadWallpaperAsync()
    {
        try
        {
            var wallpaperPath = GetWallpaperPath();
            if (!string.IsNullOrEmpty(wallpaperPath) && System.IO.File.Exists(wallpaperPath))
            {
                var bitmap = new BitmapImage();
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(wallpaperPath);
                using var stream = await file.OpenReadAsync();
                await bitmap.SetSourceAsync(stream);
                WallpaperImageSource = bitmap;
            }
        }
        catch
        {
            // Fallback: use a solid color if wallpaper can't be loaded
            WallpaperImageSource = null;
        }
    }

    private string? GetWallpaperPath()
    {
        try
        {
            // Get wallpaper path from registry
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string;
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePreviewPosition()
    {
        var margin = ServiceContainer.Settings.NotificationCustom.MarginPx;
        var scaledMargin = margin * (PreviewWidth / 1920.0); // Scale margin to preview size

        switch (SelectedAnchor)
        {
            case "TopLeft":
                PreviewToastLeft = scaledMargin;
                PreviewToastTop = scaledMargin;
                break;
            case "TopCenter":
                PreviewToastLeft = (PreviewWidth - ToastPreviewWidth) / 2;
                PreviewToastTop = scaledMargin;
                break;
            case "TopRight":
                PreviewToastLeft = PreviewWidth - ToastPreviewWidth - scaledMargin;
                PreviewToastTop = scaledMargin;
                break;
            case "MiddleLeft":
                PreviewToastLeft = scaledMargin;
                PreviewToastTop = (PreviewHeight - ToastPreviewHeight) / 2;
                break;
            case "Center":
                PreviewToastLeft = (PreviewWidth - ToastPreviewWidth) / 2;
                PreviewToastTop = (PreviewHeight - ToastPreviewHeight) / 2;
                break;
            case "MiddleRight":
                PreviewToastLeft = PreviewWidth - ToastPreviewWidth - scaledMargin;
                PreviewToastTop = (PreviewHeight - ToastPreviewHeight) / 2;
                break;
            case "BottomLeft":
                PreviewToastLeft = scaledMargin;
                PreviewToastTop = PreviewHeight - ToastPreviewHeight - scaledMargin;
                break;
            case "BottomCenter":
                PreviewToastLeft = (PreviewWidth - ToastPreviewWidth) / 2;
                PreviewToastTop = PreviewHeight - ToastPreviewHeight - scaledMargin;
                break;
            case "BottomRight":
                PreviewToastLeft = PreviewWidth - ToastPreviewWidth - scaledMargin;
                PreviewToastTop = PreviewHeight - ToastPreviewHeight - scaledMargin;
                break;
            case "Custom":
                // For custom, use fractional positioning (0.0-1.0, not 0-100)
                var customX = ServiceContainer.Settings.NotificationCustom.CustomPercentX;
                var customY = ServiceContainer.Settings.NotificationCustom.CustomPercentY;
                // customX/Y are already 0.0-1.0, so multiply directly
                // Note: We position by top-left corner to match ToastPositioner behavior
                PreviewToastLeft = customX * PreviewWidth;
                PreviewToastTop = customY * PreviewHeight;
                break;
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        ServiceContainer.Settings.NotificationCustom.Enabled = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SetNotificationCustomizerEnabled(value);
        
        // Update tray menu in real-time
        ServiceContainer.TrayService?.UpdateNotificationPositionMenuItems();
    }

    partial void OnRespectTaskbarChanged(bool value)
    {
        ServiceContainer.Settings.NotificationCustom.RespectTaskbar = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    partial void OnOpacityPercentChanged(int value)
    {
        ServiceContainer.Settings.NotificationCustom.OpacityPercent = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        OnPropertyChanged(nameof(OpacityText));
        OnPropertyChanged(nameof(PreviewOpacity));
        // Apply immediately to any currently-visible toast (no toggle required)
        ServiceContainer.NotificationCustomizerService?.ReapplyOpacityToActiveToasts();
    }

    partial void OnSelectedAnchorChanged(string value)
    {
        ServiceContainer.Settings.NotificationCustom.Anchor = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        OnPropertyChanged(nameof(AnchorDisplayText));
        UpdatePreviewPosition();
        
        // Apply immediately to active toasts in real-time
        ServiceContainer.NotificationCustomizerService?.ReapplyPositionToActiveToasts();

        // Update tray menu in real-time
        ServiceContainer.TrayService?.UpdateNotificationPositionMenuItems();
    }

    [RelayCommand]
    private async Task PickCustomPositionAsync()
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

            // Wait for the picker window to actually close (confirmed or cancelled).
            // WaitForCloseAsync completes when the user clicks or presses Esc.
            await picker.WaitForCloseAsync();

            if (picker.WasConfirmed)
            {
                // Save percentages BEFORE updating the anchor so the positioner
                // already has the correct values when settings are read.
                ServiceContainer.Settings.NotificationCustom.CustomPercentX = picker.SelectedPercentX;
                ServiceContainer.Settings.NotificationCustom.CustomPercentY = picker.SelectedPercentY;

                // Setting SelectedAnchor triggers OnSelectedAnchorChanged which saves + notifies
                SelectedAnchor = "Custom";

                // Persist (SelectedAnchor setter already calls Save, but call again to be sure
                // the CustomPercent values are written together with the anchor)
                ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

                // Update preview to show custom position
                UpdatePreviewPosition();
                
                // Update tray menu in real-time
                ServiceContainer.TrayService?.UpdateNotificationPositionMenuItems();

                LogService.LogAction("notifications",
                    $"Custom position saved: X={picker.SelectedPercentX:P1} Y={picker.SelectedPercentY:P1}");
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Error picking position: {ex.Message}");
        }
    }


    [RelayCommand]
    private async Task SendTestNotificationAsync()
    {
        try
        {
            IsSending = true;
            StatusText = "Sending test notification...";
            
            await TestToastService.SendTestNotificationAsync();
            
            // Wait a moment for the toast to appear
            await Task.Delay(500);
            
            RefreshCandidates();
            
            StatusText = Candidates.Count > 0 ? $"Detected {Candidates.Count} candidate(s)" : "No candidates detected";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            LogService.LogAction("notifications", $"Failed to send test notification: {ex.Message}");
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task SendDelayedTestNotificationAsync()
    {
        try
        {
            IsSending = true;
            StatusText = "Sending notification in 3 seconds...";
            
            await TestToastService.SendTestNotificationAsync(3);
            
            // Wait for the toast to appear
            await Task.Delay(500);
            
            RefreshCandidates();
            
            StatusText = Candidates.Count > 0 ? $"Detected {Candidates.Count} candidate(s)" : "No candidates detected";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            LogService.LogAction("notifications", $"Failed to send delayed test notification: {ex.Message}");
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void RefreshCandidates()
    {
        try
        {
            Candidates.Clear();
            var candidates = ToastDiagnostics.FindCandidates();
            
            foreach (var candidate in candidates)
            {
                Candidates.Add(new ToastCandidateViewModel(candidate));
            }
            
            // Also check if service is running
            var isServiceRunning = ServiceContainer.NotificationCustomizerService != null;
            var isEnabled = ServiceContainer.Settings.NotificationCustom.Enabled;
            
            if (Candidates.Count == 0)
            {
                StatusText = $"No candidates detected. Service running: {isServiceRunning}, Enabled: {isEnabled}. Check logs for details.";
            }
            else
            {
                StatusText = $"Found {Candidates.Count} candidate(s). Service running: {isServiceRunning}, Enabled: {isEnabled}";
            }
            
            LogService.LogAction("notifications", $"Refresh: Found {Candidates.Count} candidates, Service running: {isServiceRunning}, Enabled: {isEnabled}");
        }
        catch (Exception ex)
        {
            StatusText = $"Error finding candidates: {ex.Message}";
            LogService.LogAction("notifications", $"Failed to find candidates: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CheckServiceStatus()
    {
        try
        {
            var service = ServiceContainer.NotificationCustomizerService;
            var settings = ServiceContainer.Settings.NotificationCustom;
            
            if (service == null)
            {
                StatusText = "⚠️ Service is NULL! Forcing initialization...";
                ServiceContainer.InitializeNotificationCustomizer();
                service = ServiceContainer.NotificationCustomizerService;
            }
            
            if (service != null)
            {
                var isRunning = service.IsRunning;
                StatusText = $"✓ Service exists. Running: {isRunning}, Enabled: {settings.Enabled}, Anchor: {settings.Anchor}";
                
                if (!isRunning && settings.Enabled)
                {
                    StatusText += " | Starting service...";
                    service.Start();
                    StatusText = $"✓ Service started. Running: {service.IsRunning}";
                }
            }
            else
            {
                StatusText = "❌ Service failed to initialize!";
            }
            
            LogService.LogAction("notifications", StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Error checking service: {ex.Message}";
            LogService.LogAction("notifications", $"Service check failed: {ex.Message}");
        }
    }
}

public partial class ToastCandidateViewModel : ObservableObject
{
    public string Hwnd { get; }
    public string ClassName { get; }
    public string WindowTitle { get; }
    public string ProcessName { get; }
    public int ProcessId { get; }
    public string Rect { get; }
    public string Size { get; }
    public string Styles { get; }
    public string ExStyles { get; }

    public ToastCandidateViewModel(ToastCandidate candidate)
    {
        Hwnd = $"0x{candidate.Hwnd:X}";
        ClassName = candidate.ClassName;
        WindowTitle = string.IsNullOrEmpty(candidate.WindowTitle) ? "(no title)" : candidate.WindowTitle;
        ProcessName = string.IsNullOrEmpty(candidate.ProcessName) ? "(unknown)" : candidate.ProcessName;
        ProcessId = candidate.ProcessId;
        Rect = $"({candidate.WindowRect.left}, {candidate.WindowRect.top}, {candidate.WindowRect.right}, {candidate.WindowRect.bottom})";
        Size = $"{candidate.Width} × {candidate.Height}";
        Styles = $"0x{candidate.Styles:X8}";
        ExStyles = $"0x{candidate.ExStyles:X8}";
    }
}
