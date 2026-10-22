using System;
using System.Linq;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class ResetPasswordDialog : ContentDialog
{
    private readonly AuthService _authService;
    public bool PasswordReset { get; private set; }

    public ResetPasswordDialog(AuthService authService)
    {
        _authService = authService;
        InitializeComponent();

        Loaded += (s, e) => NewPasswordBox.Focus(FocusState.Programmatic);
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ErrorText.Visibility = Visibility.Collapsed;

        var newPassword = NewPasswordBox.Password;
        var confirmPassword = ConfirmPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            ErrorText.Text = "Please enter a new password.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (newPassword.Length < 6)
        {
            ErrorText.Text = "Password must be at least 6 characters.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (newPassword != confirmPassword)
        {
            ErrorText.Text = "Passwords do not match.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _authService.SetPassword(newPassword);
            PasswordReset = true;
            Hide();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdatePasswordStrength();
    }

    private void UpdatePasswordStrength()
    {
        var password = NewPasswordBox.Password;

        if (string.IsNullOrEmpty(password))
        {
            StrengthBar.Value = 0;
            StrengthText.Text = "";
            return;
        }

        int strength = 0;
        if (password.Length >= 6) strength += 20;
        if (password.Length >= 8) strength += 10;
        if (password.Length >= 12) strength += 10;

        if (password.Any(char.IsUpper)) strength += 20;
        if (password.Any(char.IsLower)) strength += 20;
        if (password.Any(char.IsDigit)) strength += 10;
        if (password.Any(ch => !char.IsLetterOrDigit(ch))) strength += 10;

        StrengthBar.Value = strength;

        StrengthText.Text = strength switch
        {
            < 40 => "Weak",
            < 70 => "Medium",
            _ => "Strong"
        };
    }
}
