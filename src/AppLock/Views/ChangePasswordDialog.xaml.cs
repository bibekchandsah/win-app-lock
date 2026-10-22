using System;
using System.Linq;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AppLock.Views;

public sealed partial class ChangePasswordDialog : ContentDialog
{
    private enum DialogMode
    {
        ChangePassword,
        SecurityQuestion,
        ResetPassword
    }

    private readonly AuthService _authService;
    private DialogMode _currentMode = DialogMode.ChangePassword;
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

        switch (_currentMode)
        {
            case DialogMode.ChangePassword:
                HandleChangePasswordSubmit();
                break;
            case DialogMode.SecurityQuestion:
                HandleSecurityQuestionSubmit();
                break;
            case DialogMode.ResetPassword:
                HandleResetPasswordSubmit();
                break;
        }
    }

    private void HandleChangePasswordSubmit()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var currentPassword = CurrentPasswordBox.Password;
            var newPassword = NewPasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;

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

            if (!_authService.VerifyPassword(currentPassword))
            {
                ErrorText.Text = "Current password is incorrect";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            _authService.SetPassword(newPassword);
            PasswordChanged = true;
            Hide();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Error: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void HandleResetPasswordSubmit()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var newPassword = NewPasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;

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

            _authService.SetPassword(newPassword);
            PasswordChanged = true;
            Hide();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Error: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ForgotCurrentPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_authService.HasSecurityQuestion)
        {
            ErrorText.Text = "No security question has been set. You can set one in Settings.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _currentMode = DialogMode.SecurityQuestion;
        Title = "Verify Identity";
        PrimaryButtonText = "Verify";

        ChangePasswordPanel.Visibility = Visibility.Collapsed;
        SecurityQuestionPanel.Visibility = Visibility.Visible;

        SecurityQuestionText.Text = _authService.GetSecurityQuestion();
        SecurityAnswerBox.Text = string.Empty;
        QuestionErrorText.Visibility = Visibility.Collapsed;

        SecurityAnswerBox.Focus(FocusState.Programmatic);
    }

    private void SecurityAnswerBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            HandleSecurityQuestionSubmit();
        }
    }

    private void HandleSecurityQuestionSubmit()
    {
        QuestionErrorText.Visibility = Visibility.Collapsed;
        string answer = SecurityAnswerBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(answer))
        {
            QuestionErrorText.Text = "Please enter your answer.";
            QuestionErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (_authService.VerifySecurityAnswer(answer))
        {
            // Identity verified! Transition to reset mode (without needing old password)
            _currentMode = DialogMode.ResetPassword;
            Title = "Reset Admin Password";
            PrimaryButtonText = "Reset Password";

            SecurityQuestionPanel.Visibility = Visibility.Collapsed;
            ChangePasswordPanel.Visibility = Visibility.Visible;

            CurrentPasswordBox.Visibility = Visibility.Collapsed;
            ForgotCurrentPasswordButton.Visibility = Visibility.Collapsed;
            InstructionText.Text = "Identity verified. Enter and confirm your new admin password.";

            NewPasswordBox.Password = string.Empty;
            ConfirmPasswordBox.Password = string.Empty;
            StrengthBar.Value = 0;
            StrengthText.Text = string.Empty;
            ErrorText.Visibility = Visibility.Collapsed;

            NewPasswordBox.Focus(FocusState.Programmatic);
        }
        else
        {
            QuestionErrorText.Text = "Incorrect answer. Please try again.";
            QuestionErrorText.Visibility = Visibility.Visible;
            SecurityAnswerBox.SelectAll();
            SecurityAnswerBox.Focus(FocusState.Programmatic);
        }
    }

    private void BackToChangePassword_Click(object sender, RoutedEventArgs e)
    {
        _currentMode = DialogMode.ChangePassword;
        Title = "Change Admin Password";
        PrimaryButtonText = "Change Password";

        SecurityQuestionPanel.Visibility = Visibility.Collapsed;
        ChangePasswordPanel.Visibility = Visibility.Visible;

        CurrentPasswordBox.Visibility = Visibility.Visible;
        ForgotCurrentPasswordButton.Visibility = Visibility.Visible;
        InstructionText.Text = "Enter your current password and choose a new password";

        ErrorText.Visibility = Visibility.Collapsed;
        CurrentPasswordBox.Focus(FocusState.Programmatic);
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
