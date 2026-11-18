using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;

namespace AppLock.ViewModels.Vault;

public partial class VaultListViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<VaultCardViewModel> _vaults = new();

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _hasVaults = false;

    [ObservableProperty]
    private bool _hasNotConnectedVaults = false;

    [ObservableProperty]
    private bool _hasPendingRewrapVaults = false;

    [ObservableProperty]
    private bool _showBitLockerTip = false;

    [ObservableProperty]
    private string _bitLockerTipMessage = "Turn on BitLocker for extra protection on the drive holding your vaults.";

    public VaultListViewModel()
    {
        LoadVaults();
    }

    public void LoadVaults()
    {
        Vaults.Clear();
        var registered = ServiceContainer.Settings.Vault.Vaults;

        foreach (var reg in registered)
        {
            var card = new VaultCardViewModel(reg);
            if (!Directory.Exists(reg.Path))
            {
                card.Status = VaultStatus.NotConnected;
            }
            else if (ServiceContainer.VaultSessionManager.IsUnlocked(reg.Id))
            {
                card.IsUnlocked = true;
                card.Status = VaultStatus.Unlocked;
                var session = ServiceContainer.VaultSessionManager.GetSession(reg.Id);
                if (session != null)
                {
                    card.ItemCountDisplay = $"{session.Index.Items.Count} items";
                    card.SizeDisplay = "Open";
                }
            }
            else if (reg.PendingRewrap)
            {
                card.Status = VaultStatus.NeedsAttention;
            }
            Vaults.Add(card);
        }

        IsEmpty = Vaults.Count == 0;
        HasVaults = Vaults.Count > 0;
        HasNotConnectedVaults = Vaults.Any(v => v.Status == VaultStatus.NotConnected);
        HasPendingRewrapVaults = Vaults.Any(v => v.PendingRewrap);

        CheckBitLockerStatus();
    }

    private void CheckBitLockerStatus()
    {
        try
        {
            if (Vaults.Count > 0)
            {
                var firstPath = Vaults.FirstOrDefault(v => !string.IsNullOrEmpty(v.Path))?.Path;
                if (!string.IsNullOrEmpty(firstPath))
                {
                    var drive = Path.GetPathRoot(firstPath);
                    if (!string.IsNullOrEmpty(drive))
                    {
                        BitLockerTipMessage = $"Turn on BitLocker on drive {drive} for extra protection.";
                    }
                }
            }
            // By default false, user can enable tip or if detected
            ShowBitLockerTip = false;
        }
        catch
        {
            ShowBitLockerTip = false;
        }
    }
}
