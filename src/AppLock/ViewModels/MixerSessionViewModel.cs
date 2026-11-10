using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.ViewModels;

/// <summary>
/// View model for a single app in the volume mixer (represents one or more audio sessions grouped by process).
/// </summary>
public sealed class MixerSessionViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _processName = string.Empty;
    private float _volume;
    private bool _isMuted;
    private BitmapImage? _icon;
    private bool _isUpdating;

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (_processName != value)
            {
                _processName = value;
                OnPropertyChanged();
            }
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(_volume - value) > 0.001f)
            {
                _volume = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VolumePercent));
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted != value)
            {
                _isMuted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MuteIconGlyph));
            }
        }
    }

    public BitmapImage? Icon
    {
        get => _icon;
        set
        {
            if (_icon != value)
            {
                _icon = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        set
        {
            if (_isUpdating != value)
            {
                _isUpdating = value;
                OnPropertyChanged();
            }
        }
    }

    // Computed properties
    public int VolumePercent => (int)(Volume * 100);
    public bool HasIcon => Icon != null;
    public string MuteIconGlyph => IsMuted ? "\uE74F" : "\uE767";
    public string ToolTipText => $"{Name}\n{ProcessName}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
