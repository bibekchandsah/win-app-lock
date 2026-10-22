using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class ChangePasswordDialog : ContentDialog
{
    private readonly AuthService _authService;
    public bool PasswordChanged { get; private set; }

    public ChangePasswordDialog(AuthService authService)
    {
        _authService = authService;
        InitializeComponent();
        
        Loaded += (s, e) => CurrentPasswordBox.Focus(FocusState.Programmatic);
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var currentPassword = CurrentPasswordBox.Password;
            var newPassword = NewPasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;

            // Validate inputs
            if (string.IsNullOrWhiteSpace(currentPassword))
            {
                ErrorText.Text = "Please enter your current password";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                ErrorText.Text = "Please enter a new password";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (newPassword.Length < 6)
            {
                ErrorText.Text = "New password must be at least 6 characters";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (newPassword != confirmPassword)
            {
                ErrorText.Text = "New passwords do not match";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            // Verify current password
            if (!_authService.VerifyPassword(currentPassword))
            {
                ErrorText.Text = "Current password is incorrect";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            // Change password
            _authService.SetPassword(newPassword);
            PasswordChanged = true;

            // Clear fields
            CurrentPasswordBox.Password = string.Empty;
            NewPasswordBox.Password = string.Empty;
            ConfirmPasswordBox.Password = string.Empty;

            Hide();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Error: {ex.Message}";
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
        
        // Length
        if (password.Length >= 6) strength += 20;
        if (password.Length >= 8) strength += 10;
        if (password.Length >= 12) strength += 10;

        // Complexity
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
