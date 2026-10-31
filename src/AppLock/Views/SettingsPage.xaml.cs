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

        ServiceContainer.LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Loaded += (s, e) => ApplyLocalization();
        Unloaded += (s, e) => ServiceContainer.LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        ApplyLocalization();
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ApplyLocalization());
    }

    private void ApplyLocalization()
    {
        var loc = ServiceContainer.LocalizationService;
        if (PageTitle != null) PageTitle.Text = loc["SettingsTitle"];
        if (ExpanderGeneral != null) ExpanderGeneral.Header = loc["SectionGeneral"];
        if (TxtLanguage != null) TxtLanguage.Text = loc["Language"];
        if (TxtTheme != null) TxtTheme.Text = loc["Theme"];
        if (TxtStartWithWindows != null) TxtStartWithWindows.Text = loc["StartWithWindows"];
        if (TxtMuteNotifications != null) TxtMuteNotifications.Text = loc["MuteNotifications"];
        if (TxtMuteNotificationsDesc != null) TxtMuteNotificationsDesc.Text = loc["MuteNotificationsDesc"];
        if (TxtNavPane != null) TxtNavPane.Text = loc["NavigationPane"];
        if (TxtNavPaneDesc != null) TxtNavPaneDesc.Text = loc["NavigationPaneDesc"];

        if (ExpanderSecurity != null) ExpanderSecurity.Header = loc["SectionSecurity"];
        if (TxtAdminPassword != null) TxtAdminPassword.Text = loc["ChangePassword"];
        if (BtnChangePassword != null) BtnChangePassword.Content = loc["ChangePassword"];
        if (TxtSecurityQuestions != null) TxtSecurityQuestions.Text = loc["SecurityQuestions"];
        if (BtnSecurityQuestions != null) BtnSecurityQuestions.Content = loc["SecurityQuestions"];
        if (TxtLockoutDuration != null) TxtLockoutDuration.Text = loc["LockoutDuration"];
        if (TxtLockoutDurationDesc != null) TxtLockoutDurationDesc.Text = loc["LockoutDurationDesc"];
        if (TxtWindowsHello != null) TxtWindowsHello.Text = loc["WindowsHello"];
        if (TxtWindowsHelloDesc != null) TxtWindowsHelloDesc.Text = loc["WindowsHelloDesc"];

        if (ExpanderAutoLock != null) ExpanderAutoLock.Header = loc["SectionAdvanced"];
        if (TxtAutoLockIdle != null) TxtAutoLockIdle.Text = loc["AutoLockIdle"];
        if (TxtAutoLockIdleDesc != null) TxtAutoLockIdleDesc.Text = loc["AutoLockIdleDesc"];
        if (TxtEmergencyHotkey != null) TxtEmergencyHotkey.Text = loc["EmergencyHotkey"];
        if (TxtEmergencyHotkeyDesc != null) TxtEmergencyHotkeyDesc.Text = loc["EmergencyHotkeyDesc"];
        if (TxtKeepOnTop != null) TxtKeepOnTop.Text = loc["KeepOnTop"];
        if (TxtKeepOnTopDesc != null) TxtKeepOnTopDesc.Text = loc["KeepOnTopDesc"];

        if (ExpanderAbout != null) ExpanderAbout.Header = loc["SectionAbout"];
        if (TxtAbout != null) TxtAbout.Text = loc["AboutAppLock"];
        if (TxtAboutDesc != null) TxtAboutDesc.Text = loc["AboutAppLockDesc"];
        if (BtnAbout != null) BtnAbout.Content = loc["AboutAppLock"];
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowChangePasswordDialog();
    }

    private async void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowChangeHotkeyDialog();
    }

    private async void SecurityQuestionsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowSecurityQuestionsFlow();
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAboutDialog();
    }

    private async Task ShowSecurityQuestionsFlow()
    {
        var authService = ServiceContainer.AuthService;

        if (!authService.HasSecurityQuestion)
        {
            // First-time setup: open SetSecurityQuestionDialog directly (Screenshot 4 & 5)
            var setupDialog = new SetSecurityQuestionDialog(authService)
            {
                XamlRoot = this.XamlRoot
            };
            await setupDialog.ShowAsync();

            if (setupDialog.QuestionSaved)
            {
                var successDialog = new ContentDialog
                {
                    Title = "Security Questions",
                    Content = "Security question has been set successfully.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await successDialog.ShowAsync();
            }
        }
        else
        {
            // Already configured: prompt verification method (Screenshot 1)
            var chooseDialog = new ChooseVerificationMethodDialog
            {
                XamlRoot = this.XamlRoot
            };
            await chooseDialog.ShowAsync();
            await Task.Delay(150);

            bool verified = false;

            if (chooseDialog.SelectedChoice == VerificationMethodChoice.SecurityQuestion)
            {
                // Verify via security question (Screenshot 2)
                var verifyQDialog = new VerifySecurityQuestionDialog(authService)
                {
                    XamlRoot = this.XamlRoot
                };
                await verifyQDialog.ShowAsync();
                await Task.Delay(150);
                verified = verifyQDialog.IsVerified;
            }
            else if (chooseDialog.SelectedChoice == VerificationMethodChoice.AdminPassword)
            {
                // Verify via admin password (Screenshot 3)
                var verifyPassDialog = new VerifyAdminPasswordDialog(authService)
                {
                    XamlRoot = this.XamlRoot
                };
                await verifyPassDialog.ShowAsync();
                await Task.Delay(150);
                verified = verifyPassDialog.IsVerified;
            }

            if (verified)
            {
                // Identity verified! Open SetSecurityQuestionDialog to update (Screenshot 4)
                var setupDialog = new SetSecurityQuestionDialog(authService)
                {
                    XamlRoot = this.XamlRoot
                };
                await setupDialog.ShowAsync();

                if (setupDialog.QuestionSaved)
                {
                    await Task.Delay(150);
                    var successDialog = new ContentDialog
                    {
                        Title = "Security Questions",
                        Content = "Security question updated successfully.",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await successDialog.ShowAsync();
                }
            }
        }
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
            // Delegate to MainWindow which owns the hotkey registration state
            MainWindow.Instance?.ReregisterEmergencyHotkey();
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
            XamlRoot = this.XamlRoot,
            RequestedTheme = this.ActualTheme
        };

        await dialog.ShowAsync();
    }
}
