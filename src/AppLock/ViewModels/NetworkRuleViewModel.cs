using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.ViewModels;

public partial class NetworkRuleViewModel : ObservableObject
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    
    [ObservableProperty]
    private NetworkRuleEntry _entry;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _exePath = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private SolidColorBrush _statusBrush = new SolidColorBrush(Colors.Green);

    [ObservableProperty]
    private bool _isBlocked;

    [ObservableProperty]
    private string _direction = "Outgoing";

    public event EventHandler<string>? DirectionChanged;

    partial void OnDirectionChanged(string value)
    {
        DirectionChanged?.Invoke(this, value);
    }

    [ObservableProperty]
    private bool _fileExists;

    [ObservableProperty]
    private bool _hasDrift;

    [ObservableProperty]
    private BitmapImage? _icon;

    public Visibility RepairButtonVisibility => HasDrift ? Visibility.Visible : Visibility.Collapsed;

    partial void OnHasDriftChanged(bool value)
    {
        OnPropertyChanged(nameof(RepairButtonVisibility));
    }

    public NetworkRuleViewModel(NetworkRuleEntry entry)
    {
        _entry = entry;
        _displayName = entry.DisplayName;
        _exePath = entry.ExePath;
        _isBlocked = entry.Blocked;
        _fileExists = File.Exists(entry.ExePath);
        
        // Try to capture dispatcher queue - may be null if not on UI thread
        try
        {
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcherQueue = null;
            System.Diagnostics.Debug.WriteLine($"[NetworkRule] Could not capture DispatcherQueue for {entry.DisplayName}");
        }

        UpdateDirection();
        UpdateStatus();
    }

    public async Task LoadIconAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[NetworkRule] Loading icon for: {DisplayName}");
            
            if (string.IsNullOrEmpty(Entry.ExePath))
            {
                System.Diagnostics.Debug.WriteLine($"[NetworkRule] No exe path for: {DisplayName}");
                return;
            }

            if (!File.Exists(Entry.ExePath))
            {
                System.Diagnostics.Debug.WriteLine($"[NetworkRule] File not found: {Entry.ExePath}");
                return;
            }

            // ExtractIcon needs UI thread for BitmapImage
            if (_dispatcherQueue != null)
            {
                var tcs = new TaskCompletionSource<bool>();

                _dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var icon = ServiceContainer.WindowEnumerator.ExtractIcon(Entry.ExePath);
                        if (icon != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[NetworkRule] Icon loaded for: {DisplayName}");
                            Icon = icon;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[NetworkRule] Icon extraction returned null for: {DisplayName}");
                        }
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NetworkRule] ExtractIcon failed: {ex.Message}");
                        tcs.SetException(ex);
                    }
                });

                await tcs.Task;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[NetworkRule] No dispatcher queue for: {DisplayName}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NetworkRule] LoadIconAsync exception: {ex.Message}");
        }
    }

    private void UpdateDirection()
    {
        if (Entry.BlockInbound && Entry.BlockOutbound)
            Direction = "Both";
        else if (Entry.BlockOutbound)
            Direction = "Outgoing";
        else if (Entry.BlockInbound)
            Direction = "Incoming";
        else
            Direction = "None";
    }

    public void UpdateStatus()
    {
        if (!FileExists)
        {
            StatusText = "App not found";
            StatusBrush = new SolidColorBrush(Color.FromArgb(255, 108, 117, 125)); // Gray
            return;
        }

        if (IsBlocked)
        {
            if (Entry.BlockInbound && Entry.BlockOutbound)
            {
                StatusText = "Blocked";
                StatusBrush = new SolidColorBrush(Color.FromArgb(255, 220, 53, 69)); // Red
            }
            else
            {
                StatusText = "Partially blocked";
                StatusBrush = new SolidColorBrush(Color.FromArgb(255, 255, 193, 7)); // Yellow
            }
        }
        else
        {
            StatusText = "Allowed";
            StatusBrush = new SolidColorBrush(Color.FromArgb(255, 40, 167, 69)); // Green
        }
    }

    public void SetDirection(string direction)
    {
        Direction = direction;
        
        switch (direction)
        {
            case "Both":
                Entry.BlockInbound = true;
                Entry.BlockOutbound = true;
                break;
            case "Outgoing":
                Entry.BlockInbound = false;
                Entry.BlockOutbound = true;
                break;
            case "Incoming":
                Entry.BlockInbound = true;
                Entry.BlockOutbound = false;
                break;
        }
    }

    public void SetBlocked(bool blocked)
    {
        IsBlocked = blocked;
        Entry.Blocked = blocked;
        UpdateStatus();
    }
}
