using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AppLock.ViewModels.Vault;
using AppLock.Services;
using AppLock.Services.Vault;
using AppLock.Views.Vault;

namespace AppLock.Views;

public sealed partial class VaultPage : Page
{
    public VaultListViewModel ViewModel { get; }

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

    private void OpenExistingVaultButton_Click(object sender, RoutedEventArgs e)
    {
        // Handled in Milestone 9 (Open existing vault)
    }

    // ---- Lock All -----------------------------------------------------

    private void LockAllButton_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.VaultSessionManager.LockAll();
        ViewModel.LoadVaults();
    }

    // ---- Global vault settings ----------------------------------------

    private void VaultSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Global vault settings dialog — Milestone 9
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
            XamlRoot = this.XamlRoot
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

    private void CardSettings_Click(object sender, RoutedEventArgs e)
    {
        // Per-vault settings — Milestone 9
    }

    private void CardBackup_Click(object sender, RoutedEventArgs e)
    {
        // Milestone 9
    }

    private void CardVerify_Click(object sender, RoutedEventArgs e)
    {
        // Milestone 9
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

    private void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        // Full delete with confirmation — Milestone 9
    }
}
