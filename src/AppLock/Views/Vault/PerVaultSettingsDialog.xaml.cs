using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;
using WinRT.Interop;

namespace AppLock.Views.Vault;

public sealed partial class PerVaultSettingsDialog : ContentDialog
{
    private readonly VaultRegistration _registration;
    private bool _helloEnrolled;
    private bool _helloAvailable;

    public PerVaultSettingsDialog(VaultRegistration registration)
    {
        _registration = registration;
        InitializeComponent();
        LoadRegistration();
        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
        Loaded += async (_, _) => await RefreshHelloStatusAsync();
    }

    private void ApplyTheme()
    {
        if (this.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            this.RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            this.RequestedTheme = ServiceContainer.CurrentTheme;
        }
    }

    private void LoadRegistration()
    {
        VaultNameBox.Text = _registration.Name;
        VaultPathBox.Text = _registration.Path;

        // AutoLock override
        AutoLockOverrideCombo.SelectedIndex = _registration.AutoLockMinutes switch
        {
            null => 0,
            0 => 1,
            5 => 2,
            15 => 3,
            30 => 4,
            60 => 5,
            _ => 0
        };

        // SecurePreview override
        SecurePreviewOverrideCombo.SelectedIndex = _registration.SecurePreviewOnly switch
        {
            null => 0,
            false => 1,
            true => 2
        };
    }

    private async Task RefreshHelloStatusAsync()
    {
        try
        {
            _helloAvailable = await HelloKeyService.IsAvailableAsync();
            _helloEnrolled  = HelloKeyService.IsEnrolledForVault(_registration.Id);

            if (!_helloAvailable)
            {
                HelloStatusText.Text = "Not available on this device";
                HelloActionButton.Visibility = Visibility.Collapsed;
            }
            else if (_helloEnrolled)
            {
                HelloStatusText.Text = "Enrolled on this PC";
                HelloActionButton.Content = "Remove Hello";
                HelloActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                HelloStatusText.Text = "Available for quick unlock";
                HelloActionButton.Content = "Enable Hello";
                HelloActionButton.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            HelloStatusText.Text = "Unavailable";
            HelloActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void HelloActionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_helloEnrolled)
            {
                var config = VaultStorage.LoadConfig(_registration.Path);
                HelloKeyService.Revoke(_registration.Id, config);

                var session = ServiceContainer.VaultSessionManager.GetSession(_registration.Id);
                if (session != null)
                {
                    VaultStorage.SaveConfig(_registration.Path, config, session.ConfigMacKey);
                }

                await RefreshHelloStatusAsync();
                ShowStatus("Windows Hello removed for this vault.", InfoBarSeverity.Informational);
            }
            else
            {
                var session = ServiceContainer.VaultSessionManager.GetSession(_registration.Id);
                if (session == null)
                {
                    Hide();
                    await Task.Delay(150);

                    var unlockDlg = new UnlockVaultDialog(_registration) { XamlRoot = this.XamlRoot };
                    await unlockDlg.ShowAsync();
                    session = unlockDlg.UnlockedSession;

                    if (session == null) return;

                    var config = VaultStorage.LoadConfig(_registration.Path);
                    HelloKeyService.Enroll(session, config);
                    VaultStorage.SaveConfig(_registration.Path, config, session.ConfigMacKey);

                    var newSettingsDlg = new PerVaultSettingsDialog(_registration) { XamlRoot = this.XamlRoot };
                    await newSettingsDlg.ShowAsync();
                    return;
                }

                var conf = VaultStorage.LoadConfig(_registration.Path);
                HelloKeyService.Enroll(session, conf);
                VaultStorage.SaveConfig(_registration.Path, conf, session.ConfigMacKey);

                await RefreshHelloStatusAsync();
                ShowStatus("Windows Hello enabled successfully.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Windows Hello error: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var session = ServiceContainer.VaultSessionManager.GetSession(_registration.Id);
        if (session == null)
        {
            Hide();
            await Task.Delay(150);

            var unlockDlg = new UnlockVaultDialog(_registration) { XamlRoot = this.XamlRoot };
            await unlockDlg.ShowAsync();
            session = unlockDlg.UnlockedSession;

            if (session == null) return;
        }
        else
        {
            Hide();
            await Task.Delay(150);
        }

        var changeDlg = new ChangeVaultPasswordDialog(session) { XamlRoot = this.XamlRoot };
        await changeDlg.ShowAsync();

        var newSettingsDlg = new PerVaultSettingsDialog(_registration) { XamlRoot = this.XamlRoot };
        await newSettingsDlg.ShowAsync();
    }

    private async void RegenerateRecoveryKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var session = ServiceContainer.VaultSessionManager.GetSession(_registration.Id);
        if (session == null)
        {
            Hide();
            await Task.Delay(150);

            var unlockDlg = new UnlockVaultDialog(_registration) { XamlRoot = this.XamlRoot };
            await unlockDlg.ShowAsync();
            session = unlockDlg.UnlockedSession;

            if (session == null) return;

            var newSettingsDlg = new PerVaultSettingsDialog(_registration) { XamlRoot = this.XamlRoot };
            await newSettingsDlg.ShowAsync();
            return;
        }

        try
        {
            string newKey = await VaultManager.RegenerateRecoveryKeyAsync(session);
            NewKeyText.Text = newKey;
            NewKeyPanel.Visibility = Visibility.Visible;
            ShowStatus("New recovery key generated. Save it in a safe place.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to regenerate recovery key: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void CopyNewKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(NewKeyText.Text);
        Clipboard.SetContent(pkg);
        ShowStatus("Recovery key copied to clipboard.", InfoBarSeverity.Success);
    }

    private async void BackupVaultButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            var backupDest = Path.Combine(folder.Path, $"{_registration.Name}_Backup_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(backupDest);

            ShowStatus("Backing up vault files…", InfoBarSeverity.Informational);

            await VaultManager.BackupVaultAsync(_registration.Path, backupDest);
            ShowStatus($"Backup completed to: {backupDest}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"Backup failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void VerifyVaultButton_Click(object sender, RoutedEventArgs e)
    {
        var session = ServiceContainer.VaultSessionManager.GetSession(_registration.Id);
        if (session == null)
        {
            Hide();
            await Task.Delay(150);

            var unlockDlg = new UnlockVaultDialog(_registration) { XamlRoot = this.XamlRoot };
            await unlockDlg.ShowAsync();
            session = unlockDlg.UnlockedSession;

            if (session == null) return;
        }
        else
        {
            Hide();
            await Task.Delay(150);
        }

        var verifyDlg = new VerifyVaultDialog(session) { XamlRoot = this.XamlRoot };
        await verifyDlg.ShowAsync();

        var newSettingsDlg = new PerVaultSettingsDialog(_registration) { XamlRoot = this.XamlRoot };
        await newSettingsDlg.ShowAsync();
    }

    private void DeleteVaultButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteConfirmPanel.Visibility = Visibility.Visible;
    }

    private void CancelDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Lock if active
            if (ServiceContainer.VaultSessionManager.IsUnlocked(_registration.Id))
                ServiceContainer.VaultSessionManager.Lock(_registration.Id);

            // Delete directory
            if (Directory.Exists(_registration.Path))
                Directory.Delete(_registration.Path, recursive: true);

            // Remove from settings
            ServiceContainer.Settings.Vault.Vaults.RemoveAll(v => v.Id == _registration.Id);
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

            LogService.LogAction("Vault", $"Vault '{_registration.Name}' deleted.");
            Hide();
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to delete vault files: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void SaveButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var newName = VaultNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName))
            _registration.Name = newName;

        _registration.AutoLockMinutes = AutoLockOverrideCombo.SelectedIndex switch
        {
            0 => null, // Use global
            1 => 0,    // Never
            2 => 5,
            3 => 15,
            4 => 30,
            5 => 60,
            _ => null
        };

        _registration.SecurePreviewOnly = SecurePreviewOverrideCombo.SelectedIndex switch
        {
            0 => null,  // Use global
            1 => false, // Allow external
            2 => true,  // Secure preview only
            _ => null
        };

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        LogService.LogAction("Vault", $"Per-vault settings saved for '{_registration.Name}'.");
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }
}
