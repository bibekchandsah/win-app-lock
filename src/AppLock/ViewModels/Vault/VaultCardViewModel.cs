using System;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models.Vault;

namespace AppLock.ViewModels.Vault;

public enum VaultStatus
{
    Locked,
    Unlocked,
    NotConnected,
    NeedsAttention
}

public partial class VaultCardViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string? _icon;

    [ObservableProperty]
    private DateTime _lastOpened;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private VaultStatus _status = VaultStatus.Locked;

    [ObservableProperty]
    private string _sizeDisplay = "Locked";

    [ObservableProperty]
    private string _itemCountDisplay = "Locked";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenOrLockLabel))]
    [NotifyPropertyChangedFor(nameof(OpenOrLockGlyph))]
    private bool _isUnlocked;

    [ObservableProperty]
    private bool _pendingRewrap;

    public string ShortenedPath
    {
        get
        {
            if (string.IsNullOrEmpty(Path)) return string.Empty;
            if (Path.Length <= 45) return Path;
            return Path.Substring(0, 15) + "..." + Path.Substring(Path.Length - 25);
        }
    }

    public string LastOpenedDisplay =>
        LastOpened == DateTime.MinValue ? "Never opened" : $"Last opened: {LastOpened:g}";

    public string StatusText => Status switch
    {
        VaultStatus.Unlocked => "Unlocked",
        VaultStatus.NotConnected => "Not connected",
        VaultStatus.NeedsAttention => "Needs attention",
        _ => "Locked"
    };

    public string StatusGlyph => Status switch
    {
        VaultStatus.Unlocked => "\uE785",
        VaultStatus.NotConnected => "\uE711",
        VaultStatus.NeedsAttention => "\uE7BA",
        _ => "\uE72E"
    };

    public string OpenOrLockLabel => IsUnlocked ? "Lock" : "Open";
    public string OpenOrLockGlyph => IsUnlocked ? "\uE72E" : "\uE785";

    public VaultCardViewModel(VaultRegistration reg)
    {
        Id = reg.Id;
        Name = reg.Name;
        Path = reg.Path;
        Icon = reg.Icon;
        LastOpened = reg.LastOpened;
        PendingRewrap = reg.PendingRewrap;
        Status = VaultStatus.Locked;
        IsUnlocked = false;
        SizeDisplay = "Locked";
        ItemCountDisplay = "Locked";
    }
}
