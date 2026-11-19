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
        Loaded += async (_, _) => await RefreshHelloStatusAsync();
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

    private async Task<VaultSession?> EnsureUnlockedSessionAsync()
    {
        var session = ServiceContainer.VaultSessionManager.GetSession(_registration.Id);
        if (session != null) return session;

        var unlockDlg = new UnlockVaultDialog(_registration) { XamlRoot = this.XamlRoot };
        await unlockDlg.ShowAsync();
        return unlockDlg.UnlockedSession;
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
                var session = await EnsureUnlockedSessionAsync();
                if (session == null) return;

                var config = VaultStorage.LoadConfig(_registration.Path);
                HelloKeyService.Enroll(session, config);
                VaultStorage.SaveConfig(_registration.Path, config, session.ConfigMacKey);

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
        var session = await EnsureUnlockedSessionAsync();
        if (session == null) return;

        var changeDlg = new ChangeVaultPasswordDialog(session) { XamlRoot = this.XamlRoot };
        await changeDlg.ShowAsync();
    }

    private async void RegenerateRecoveryKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var session = await EnsureUnlockedSessionAsync();
        if (session == null) return;

        try
        {
            string newKey = await VaultManager.RegenerateRecoveryKeyAsync(session);

            var keyDialog = new ContentDialog
            {
                Title = "New Recovery Key Generated",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Your old recovery key is now invalid. Save your new recovery key in a secure place:",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBox
                        {
                            Text = newKey,
                            IsReadOnly = true,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                PrimaryButtonText = "Copy to clipboard",
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var res = await keyDialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                var pkg = new DataPackage();
                pkg.SetText(newKey);
                Clipboard.SetContent(pkg);
                ShowStatus("Recovery key copied to clipboard.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to regenerate recovery key: {ex.Message}", InfoBarSeverity.Error);
        }
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
        var session = await EnsureUnlockedSessionAsync();
        if (session == null) return;

        var verifyDlg = new VerifyVaultDialog(session) { XamlRoot = this.XamlRoot };
        await verifyDlg.ShowAsync();
    }

    private async void DeleteVaultButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmDlg = new ContentDialog
        {
            Title = $"Permanently Delete '{_registration.Name}'?",
            Content = new TextBlock
            {
                Text = "WARNING: This will permanently delete the vault and ALL encrypted files within it from your disk.\n\nThis action cannot be undone.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var res = await confirmDlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
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
