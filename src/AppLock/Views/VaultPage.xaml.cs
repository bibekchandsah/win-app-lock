using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using AppLock.Models.Vault;
using AppLock.ViewModels.Vault;
using AppLock.Services;
using AppLock.Services.Vault;
using AppLock.Views.Vault;
using WinRT.Interop;

namespace AppLock.Views;

public sealed partial class VaultPage : Page
{
    public VaultListViewModel ViewModel { get; }

    private ElementTheme CurrentActualTheme =>
        (XamlRoot?.Content as FrameworkElement)?.ActualTheme ?? ServiceContainer.CurrentTheme;

    public VaultPage()
    {
        ViewModel = new VaultListViewModel();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadVaults();

        // Subscribe to vault lock/unlock events to refresh cards
        ServiceContainer.VaultSessionManager.VaultLocked   -= OnVaultStateChanged;
        ServiceContainer.VaultSessionManager.VaultUnlocked -= OnVaultStateChanged;
        ServiceContainer.VaultSessionManager.VaultLocked   += OnVaultStateChanged;
        ServiceContainer.VaultSessionManager.VaultUnlocked += OnVaultStateChanged;

        CheckCrashRecovery();
    }

    private async void CheckCrashRecovery()
    {
        try
        {
            var leftovers = TempOpenService.ScanCrashRecoverySessions();
            if (leftovers.Count > 0)
            {
                var dialog = new TempSessionRecoveryDialog(leftovers) { XamlRoot = this.XamlRoot };
                await dialog.ShowAsync();
                ViewModel.LoadVaults();
            }
        }
        catch { }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ServiceContainer.VaultSessionManager.VaultLocked   -= OnVaultStateChanged;
        ServiceContainer.VaultSessionManager.VaultUnlocked -= OnVaultStateChanged;
    }

    private void OnVaultStateChanged(object? sender, Guid vaultId)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.LoadVaults());
    }

    // ---- Create Vault -------------------------------------------------

    private async void CreateVaultButton_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new CreateVaultWizardDialog
        {
            XamlRoot = this.XamlRoot
        };

        var result = await wizard.ShowAsync();

        // If the user completed creation, reload the vault list
        if (wizard.CreatedRegistration != null)
        {
            ViewModel.LoadVaults();

            // Show success infobar or navigate to vault browser
            // (Vault browser is Milestone 4 — for now just show a success toast)
            var successBar = new InfoBar
            {
                Severity  = InfoBarSeverity.Success,
                IsOpen    = true,
                IsClosable = true,
                Title     = "Vault created",
                Message   = $"'{wizard.CreatedRegistration.Name}' is ready. Click Open to browse its files."
            };
        }
    }

    // ---- Open Existing Vault ------------------------------------------

    private async void OpenExistingVaultButton_Click(object sender, RoutedEventArgs e)
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

            var configPath = Path.Combine(folder.Path, "vault.config");
            var configBakPath = Path.Combine(folder.Path, "vault.config.bak");

            if (!File.Exists(configPath) && !File.Exists(configBakPath))
            {
                var errDlg = new ContentDialog
                {
                    Title = "Invalid Vault Folder",
                    Content = "The selected folder does not contain an AppLock vault (missing vault.config).",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = CurrentActualTheme
                };
                await errDlg.ShowAsync();
                return;
            }

            var config = VaultStorage.LoadConfig(folder.Path, fallbackToBak: true);

            // Check if already registered
            var existing = ServiceContainer.Settings.Vault.Vaults.FirstOrDefault(v =>
                v.Id == config.VaultId || string.Equals(v.Path, folder.Path, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                var existsDlg = new ContentDialog
                {
                    Title = "Vault Already Added",
                    Content = $"The vault '{existing.Name}' is already registered in your vaults list.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = CurrentActualTheme
                };
                await existsDlg.ShowAsync();
                return;
            }

            var reg = new VaultRegistration
            {
                Id = config.VaultId,
                Name = config.Name,
                Path = folder.Path,
                LastOpened = DateTime.UtcNow
            };

            ServiceContainer.Settings.Vault.Vaults.Add(reg);
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

            ViewModel.LoadVaults();

            var successDlg = new ContentDialog
            {
                Title = "Vault Added",
                Content = $"Vault '{reg.Name}' has been added to your vaults list.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
                RequestedTheme = CurrentActualTheme
            };
            await successDlg.ShowAsync();
        }
        catch (Exception ex)
        {
            var errDlg = new ContentDialog
            {
                Title = "Failed to Open Vault",
                Content = $"Could not open vault from folder: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
                RequestedTheme = CurrentActualTheme
            };
            await errDlg.ShowAsync();
        }
    }

    // ---- Lock All -----------------------------------------------------

    private void LockAllButton_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.VaultSessionManager.LockAll();
        ViewModel.LoadVaults();
    }

    // ---- Global vault settings ----------------------------------------

    private async void VaultSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GlobalVaultSettingsDialog { XamlRoot = this.XamlRoot };
        await dlg.ShowAsync();
        ViewModel.LoadVaults();
    }

    // ---- Help dialog --------------------------------------------------

    private async void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "How the vault protects you",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text =
                        "PROTECTED\n" +
                        "• File contents, file names and folder structure while the vault is locked.\n\n" +
                        "NOT PROTECTED\n" +
                        "• That a vault exists, the number of objects, and approximate file sizes.\n" +
                        "• Files while the vault is unlocked (malware running as you could read them).\n" +
                        "• Temporary decrypted copies while a file is open in another app.\n" +
                        "• Originals deleted from SSDs — recoverable until overwritten (use BitLocker).\n" +
                        "• Anything you export.\n\n" +
                        "NO RECOVERY WITHOUT YOUR PASSWORD OR RECOVERY KEY\n" +
                        "AppLock cannot reset your vault password. If you lose both, your data is permanently unreadable.\n\n" +
                        "WEAK PASSWORDS\n" +
                        "An attacker who copies the vault folder can try passwords offline. A long passphrase is your best protection.",
                    TextWrapping = TextWrapping.Wrap
                },
                MaxHeight = 400
            },
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot,
            RequestedTheme = CurrentActualTheme
        };
        await dialog.ShowAsync();
    }

    // ---- BitLocker shortcut -------------------------------------------

    private void GoToBitLockerButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(BitLockerPage));
    }

    // ---- Card buttons -------------------------------------------------

    private void CardBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VaultCardViewModel card) return;

        var session = ServiceContainer.VaultSessionManager.GetSession(card.Id);
        if (session != null)
        {
            Frame.Navigate(typeof(VaultBrowserPage), session);
        }
    }

    private async void CardOpenLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VaultCardViewModel card) return;

        var mgr = ServiceContainer.VaultSessionManager;

        if (mgr.IsUnlocked(card.Id))
        {
            // Lock it
            mgr.Lock(card.Id);
            ViewModel.LoadVaults();
            return;
        }

        // Show unlock dialog
        var reg = ServiceContainer.Settings.Vault.Vaults.Find(v => v.Id == card.Id);
        if (reg == null) return;

        var dialog = new UnlockVaultDialog(reg) { XamlRoot = this.XamlRoot };
        var result = await dialog.ShowAsync();

        if (dialog.UnlockedSession != null)
        {
            ViewModel.LoadVaults();
            Frame.Navigate(typeof(VaultBrowserPage), dialog.UnlockedSession);
        }
    }

    private async void CardSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VaultCardViewModel card) return;

        var reg = ServiceContainer.Settings.Vault.Vaults.Find(v => v.Id == card.Id);
        if (reg == null) return;

        var dlg = new PerVaultSettingsDialog(reg) { XamlRoot = this.XamlRoot };
        await dlg.ShowAsync();
        ViewModel.LoadVaults();
    }

    private async void CardBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not VaultCardViewModel card) return;

        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            var backupDest = Path.Combine(folder.Path, $"{card.Name}_Backup_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(backupDest);

            await VaultManager.BackupVaultAsync(card.Path, backupDest);

            var successDlg = new ContentDialog
            {
                Title = "Backup Complete",
                Content = $"Encrypted backup of '{card.Name}' was saved to:\n{backupDest}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
                RequestedTheme = CurrentActualTheme
            };
            await successDlg.ShowAsync();
        }
        catch (Exception ex)
        {
            var errDlg = new ContentDialog
            {
                Title = "Backup Failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
                RequestedTheme = CurrentActualTheme
            };
            await errDlg.ShowAsync();
        }
    }

    private async void CardVerify_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not VaultCardViewModel card) return;

        var reg = ServiceContainer.Settings.Vault.Vaults.Find(v => v.Id == card.Id);
        if (reg == null) return;

        var session = ServiceContainer.VaultSessionManager.GetSession(card.Id);
        if (session == null)
        {
            var unlockDlg = new UnlockVaultDialog(reg) { XamlRoot = this.XamlRoot };
            await unlockDlg.ShowAsync();
            session = unlockDlg.UnlockedSession;
            if (session == null) return;
        }

        var verifyDlg = new VerifyVaultDialog(session) { XamlRoot = this.XamlRoot };
        await verifyDlg.ShowAsync();
    }

    private void CardShowExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not VaultCardViewModel card) return;

        if (Directory.Exists(card.Path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{card.Path}\"",
                UseShellExecute = true
            });
        }
    }

    private void CardRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not VaultCardViewModel card) return;

        // Lock first if open
        if (ServiceContainer.VaultSessionManager.IsUnlocked(card.Id))
            ServiceContainer.VaultSessionManager.Lock(card.Id);

        ServiceContainer.Settings.Vault.Vaults.RemoveAll(v => v.Id == card.Id);
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ViewModel.LoadVaults();
    }

    private async void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not VaultCardViewModel card) return;

        var confirmDlg = new ContentDialog
        {
            Title = $"Permanently Delete '{card.Name}'?",
            Content = new TextBlock
            {
                Text = "WARNING: This will permanently delete the vault and ALL encrypted files within it from your disk.\n\nThis action cannot be undone.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
            RequestedTheme = CurrentActualTheme
        };

        var res = await confirmDlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            try
            {
                if (ServiceContainer.VaultSessionManager.IsUnlocked(card.Id))
                    ServiceContainer.VaultSessionManager.Lock(card.Id);

                if (Directory.Exists(card.Path))
                    Directory.Delete(card.Path, recursive: true);

                ServiceContainer.Settings.Vault.Vaults.RemoveAll(v => v.Id == card.Id);
                ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

                ViewModel.LoadVaults();
            }
            catch (Exception ex)
            {
                var errDlg = new ContentDialog
                {
                    Title = "Delete Failed",
                    Content = $"Failed to delete vault files: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = CurrentActualTheme
                };
                await errDlg.ShowAsync();
            }
        }
    }
}
