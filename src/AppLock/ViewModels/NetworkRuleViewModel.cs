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

        UpdateDirection();
        UpdateStatus();
        
        // Load icon asynchronously if not UWP
        if (string.IsNullOrEmpty(entry.PackageFamilyName) && !string.IsNullOrEmpty(entry.ExePath))
        {
            _ = LoadIconAsync();
        }
    }

    public async Task LoadIconAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(Entry.ExePath) && File.Exists(Entry.ExePath))
            {
                var icon = await Task.Run(() => ServiceContainer.WindowEnumerator.ExtractIcon(Entry.ExePath));
                if (icon != null)
                {
                    Icon = icon;
                }
            }
        }
        catch
        {
            // Icon loading failed, keep null
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
