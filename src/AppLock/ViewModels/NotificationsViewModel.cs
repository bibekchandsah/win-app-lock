using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AppLock.Services;
using AppLock.Services.Notifications;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

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

    public string AnchorDisplayText => $"Current: {SelectedAnchor}";
    public string MarginText => $"{ServiceContainer.Settings.NotificationCustom.MarginPx} px";

    public ObservableCollection<ToastCandidateViewModel> Candidates { get; } = new();

    public NotificationsViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        LoadSettings();
    }

    private void LoadSettings()
    {
        Enabled = ServiceContainer.Settings.NotificationCustom.Enabled;
        RespectTaskbar = ServiceContainer.Settings.NotificationCustom.RespectTaskbar;
        SelectedAnchor = ServiceContainer.Settings.NotificationCustom.Anchor;
    }

    partial void OnEnabledChanged(bool value)
    {
        ServiceContainer.Settings.NotificationCustom.Enabled = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ServiceContainer.SetNotificationCustomizerEnabled(value);
    }

    partial void OnRespectTaskbarChanged(bool value)
    {
        ServiceContainer.Settings.NotificationCustom.RespectTaskbar = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    partial void OnSelectedAnchorChanged(string value)
    {
        ServiceContainer.Settings.NotificationCustom.Anchor = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        OnPropertyChanged(nameof(AnchorDisplayText));
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
