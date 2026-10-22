using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel();
        InitializeComponent();
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowChangePasswordDialog();
    }

    private async void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowChangeHotkeyDialog();
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAboutDialog();
    }

    private async Task ShowChangePasswordDialog()
    {
        var dialog = new ChangePasswordDialog(ServiceContainer.AuthService)
        {
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();

        if (dialog.PasswordChanged)
        {
            // Show success message
            var successDialog = new ContentDialog
            {
                Title = "Password Changed",
                Content = "Your admin password has been changed successfully.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await successDialog.ShowAsync();
        }
    }

    private async Task ShowChangeHotkeyDialog()
    {
        var currentHotkey = ViewModel.EmergencyHotkey;
        
        var dialog = new ChangeHotkeyDialog(currentHotkey)
        {
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();

        if (dialog.HotkeyChanged && !string.IsNullOrEmpty(dialog.NewHotkey))
        {
            // Update the hotkey
            var settings = ServiceContainer.Settings;
            var oldHotkey = settings.EmergencyHotkey;
            settings.EmergencyHotkey = dialog.NewHotkey;
            ServiceContainer.SettingsStore.Save(settings);
            
            // Update ViewModel
            ViewModel.EmergencyHotkey = dialog.NewHotkey;

            // Re-register hotkey in MainWindow (need to access main window)
            ReregisterEmergencyHotkey(oldHotkey, dialog.NewHotkey);

            // Show success message
            var successDialog = new ContentDialog
            {
                Title = "Hotkey Changed",
                Content = $"Emergency lock hotkey changed to: {dialog.NewHotkey}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await successDialog.ShowAsync();
        }
    }

    private void ReregisterEmergencyHotkey(string oldHotkey, string newHotkey)
    {
        try
        {
            var hotkeyService = ServiceContainer.HotkeyService;
            
            // Unregister old hotkey
            var (oldMods, oldKey) = HotkeyService.ParseHotkey(oldHotkey);
            // Note: We'd need to track the hotkey ID, for now we'll just clear and re-register
            
            // Register new hotkey
            var (newMods, newKey) = HotkeyService.ParseHotkey(newHotkey);
            // This would need MainWindow integration - simplified for now
            
            System.Diagnostics.Debug.WriteLine($"Hotkey changed from {oldHotkey} to {newHotkey}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error re-registering hotkey: {ex.Message}");
        }
    }

    private async void ShowAboutDialog()
    {
        var dialog = new AboutDialog
        {
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }
}
