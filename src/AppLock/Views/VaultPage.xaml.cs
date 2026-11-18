using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AppLock.ViewModels.Vault;
using AppLock.Services;

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
    }

    private void CreateVaultButton_Click(object sender, RoutedEventArgs e)
    {
        // Handled in Milestone 3 (Create wizard)
    }

    private void OpenExistingVaultButton_Click(object sender, RoutedEventArgs e)
    {
        // Handled in Milestone 9 (Open existing vault)
    }

    private void LockAllButton_Click(object sender, RoutedEventArgs e)
    {
        // Handled in Milestone 7 (Lock all)
    }

    private void VaultSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Global vault settings dialog
    }

    private async void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "How the vault protects you",
            Content = new TextBlock
            {
                Text = "• Protected: file contents, file names and folder structure while the vault is locked.\n\n" +
                       "• Not protected: that a vault exists, approximate total size, and temporary decrypted copies while a file is open in another app.\n\n" +
                       "• No recovery without your password or recovery key: AppLock cannot reset your vault password.\n\n" +
                       "• For maximum protection, turn on BitLocker on the drive holding your vaults.",
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void GoToBitLockerButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(BitLockerPage));
    }

    private void CardOpenLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VaultCardViewModel card)
        {
            // Open/Lock handled in Milestone 3 & 4
        }
    }

    private void CardSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VaultCardViewModel card)
        {
            // Settings handled in Milestone 9
        }
    }

    private void CardBackup_Click(object sender, RoutedEventArgs e)
    {
        // Handled in Milestone 9
    }

    private void CardVerify_Click(object sender, RoutedEventArgs e)
    {
        // Handled in Milestone 9
    }

    private void CardShowExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is VaultCardViewModel card)
        {
            if (System.IO.Directory.Exists(card.Path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{card.Path}\"",
                    UseShellExecute = true
                });
            }
        }
    }

    private void CardRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is VaultCardViewModel card)
        {
            ServiceContainer.Settings.Vault.Vaults.RemoveAll(v => v.Id == card.Id);
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            ViewModel.LoadVaults();
        }
    }

    private void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        // Handled in Milestone 9
    }
}
