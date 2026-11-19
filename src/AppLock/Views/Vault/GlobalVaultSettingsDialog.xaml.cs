using System;
using Microsoft.UI.Xaml.Controls;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;

namespace AppLock.Views.Vault;

public sealed partial class GlobalVaultSettingsDialog : ContentDialog
{
    private readonly VaultSettings _settings;

    public GlobalVaultSettingsDialog()
    {
        InitializeComponent();
        _settings = ServiceContainer.Settings.Vault;
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        // 1. Idle Auto-lock
        DefaultAutoLockCombo.SelectedIndex = _settings.DefaultAutoLockMinutes switch
        {
            1 => 0,
            5 => 1,
            10 => 2,
            30 => 3,
            0 => 4,
            _ => 2
        };

        // 2. LockOnPcLock
        LockOnPcLockSwitch.IsOn = _settings.LockOnPcLock;

        // 3. Minimize
        LockOnMinimizeCombo.SelectedIndex = _settings.LockOnMinimizeMinutes switch
        {
            0 => 0,
            1 => 1,
            5 => 2,
            15 => 3,
            _ => 2
        };

        // 4. LockOnExit
        LockOnExitSwitch.IsOn = _settings.LockOnExit;

        // 5. RequireFreshAuth
        RequireFreshAuthSwitch.IsOn = _settings.RequireFreshAuth;

        // 6. Capture
        ExcludeCaptureSwitch.IsOn = _settings.ExcludeFromCapture;

        // 7. SecurePreviewOnly
        SecurePreviewOnlySwitch.IsOn = _settings.SecurePreviewOnly;

        // 8. Delete originals
        DeleteOriginalsSwitch.IsOn = _settings.DeleteOriginalsAfterVerify;
        OverwriteOriginalsSwitch.IsOn = _settings.OverwriteOriginals;

        // 9. Temp files
        TempGenericNamesSwitch.IsOn = _settings.TempGenericNames;
        TempOverwriteSwitch.IsOn = _settings.TempOverwriteOnDelete;

        // 10. Trash retention
        TrashRetentionBox.Value = _settings.TrashDays;

        // 11. SendTo shortcut
        SendToSwitch.IsOn = SendToShortcutService.IsEnabled();

        // 12. Explorer Context Menu
        ContextMenuSwitch.IsOn = ExplorerContextMenuService.IsEnabled();

        // 13. Size Padding
        SizePaddingSwitch.IsOn = _settings.EnableSizePadding;
    }

    private void SaveButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _settings.DefaultAutoLockMinutes = DefaultAutoLockCombo.SelectedIndex switch
        {
            0 => 1,
            1 => 5,
            2 => 10,
            3 => 30,
            4 => 0,
            _ => 10
        };

        _settings.LockOnPcLock = LockOnPcLockSwitch.IsOn;

        _settings.LockOnMinimizeMinutes = LockOnMinimizeCombo.SelectedIndex switch
        {
            0 => 0,
            1 => 1,
            2 => 5,
            3 => 15,
            _ => 5
        };

        _settings.LockOnExit = LockOnExitSwitch.IsOn;
        _settings.RequireFreshAuth = RequireFreshAuthSwitch.IsOn;
        _settings.ExcludeFromCapture = ExcludeCaptureSwitch.IsOn;
        _settings.SecurePreviewOnly = SecurePreviewOnlySwitch.IsOn;
        _settings.DeleteOriginalsAfterVerify = DeleteOriginalsSwitch.IsOn;
        _settings.OverwriteOriginals = OverwriteOriginalsSwitch.IsOn;
        _settings.TempGenericNames = TempGenericNamesSwitch.IsOn;
        _settings.TempOverwriteOnDelete = TempOverwriteSwitch.IsOn;
        _settings.TrashDays = (int)Math.Max(0, TrashRetentionBox.Value);
        _settings.EnableSizePadding = SizePaddingSwitch.IsOn;

        // Update SendTo shortcut
        bool sendToWanted = SendToSwitch.IsOn;
        _settings.SendToEnabled = sendToWanted;
        SendToShortcutService.SetEnabled(sendToWanted);

        // Update Explorer context menu
        bool contextMenuWanted = ContextMenuSwitch.IsOn;
        _settings.ContextMenuEnabled = contextMenuWanted;
        ExplorerContextMenuService.SetEnabled(contextMenuWanted);

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        LogService.LogAction("Vault", "Global vault settings updated.");
    }
}
