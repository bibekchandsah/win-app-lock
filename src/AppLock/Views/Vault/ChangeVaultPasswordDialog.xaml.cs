using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;

namespace AppLock.Views.Vault;

public sealed partial class ChangeVaultPasswordDialog : ContentDialog
{
    private readonly VaultSession _session;

    public ChangeVaultPasswordDialog(VaultSession session)
    {
        _session = session;
        InitializeComponent();
        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
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

    private void NewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var pw = NewPasswordBox.Password;
        var result = PasswordStrength.Evaluate(pw);

        StrengthBar.Value = result.Score;
        StrengthLabel.Text = $"Strength: {result.Description}";

        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private async void UpdatePasswordButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var newPw = NewPasswordBox.Password;
            var confirmPw = ConfirmPasswordBox.Password;

            if (string.IsNullOrEmpty(newPw))
            {
                ShowError("Please enter a new password.");
                args.Cancel = true;
                return;
            }

            if (newPw.Length < PasswordStrength.MinLength)
            {
                ShowError($"Password must be at least {PasswordStrength.MinLength} characters long.");
                args.Cancel = true;
                return;
            }

            if (newPw != confirmPw)
            {
                ShowError("Passwords do not match.");
                args.Cancel = true;
                return;
            }

            SetWorking(true);

            await VaultManager.ChangePasswordAsync(_session, newPw);

            // Clear passwords from UI immediately
            NewPasswordBox.Password = "";
            ConfirmPasswordBox.Password = "";
        }
        catch (Exception ex)
        {
            ShowError($"Failed to change password: {ex.Message}");
            args.Cancel = true;
        }
        finally
        {
            SetWorking(false);
            deferral.Complete();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SetWorking(bool working)
    {
        ProgressPanel.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = !working;
        IsSecondaryButtonEnabled = !working;
        NewPasswordBox.IsEnabled = !working;
        ConfirmPasswordBox.IsEnabled = !working;
    }
}
