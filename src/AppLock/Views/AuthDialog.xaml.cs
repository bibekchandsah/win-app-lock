using System;
using System.Linq;
using System.Threading.Tasks;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AppLock.Views;

public sealed partial class AuthDialog : ContentDialog
{
    private enum AuthStep
    {
        Password,
        SecurityQuestion,
        ResetPassword
    }

    private readonly AuthService _authService;
    private readonly HelloService _helloService;
    private AuthStep _currentStep = AuthStep.Password;
    private System.Threading.CancellationTokenSource? _helloCts;

    public bool IsAuthenticated { get; private set; }

    public AuthDialog(AuthService authService)
    {
        _authService = authService;
        _helloService = ServiceContainer.HelloService;
        InitializeComponent();
        CheckLockout();
        _ = InitializeWindowsHelloAsync();

        ApplyTheme();
        Loaded += (s, e) =>
        {
            ApplyTheme();
            PasswordBox.Focus(FocusState.Programmatic);
        };
        Closing += (s, e) => _helloCts?.Cancel();
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

    private async Task InitializeWindowsHelloAsync()
    {
        if (ServiceContainer.Settings.WindowsHelloEnabled)
        {
            bool available = await _helloService.IsAvailableAsync();
            if (available)
            {
                WindowsHelloButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void CheckLockout()
    {
        if (_authService.IsLockedOut)
        {
            var timeRemaining = _authService.LockoutTimeRemaining;
            if (timeRemaining.HasValue)
            {
                LockoutText.Text = $"Too many failed attempts. Locked out for {Math.Ceiling(timeRemaining.Value.TotalMinutes)} more minute(s).";
                LockoutText.Visibility = Visibility.Visible;
                PasswordBox.IsEnabled = false;
                IsPrimaryButtonEnabled = false;
            }
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;

        switch (_currentStep)
        {
            case AuthStep.Password:
                HandlePasswordSubmit();
                break;
            case AuthStep.SecurityQuestion:
                HandleSecurityQuestionSubmit();
                break;
            case AuthStep.ResetPassword:
                HandleResetPasswordSubmit();
                break;
        }
    }

    private void HandlePasswordSubmit()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            if (_authService.IsLockedOut)
            {
                CheckLockout();
                return;
            }

            var password = PasswordBox.Password;
            if (string.IsNullOrWhiteSpace(password))
            {
                ErrorText.Text = "Please enter your password";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            var isValid = _authService.VerifyPassword(password);
            if (isValid)
            {
                IsAuthenticated = true;
                PasswordBox.Password = string.Empty;
                Hide();
            }
            else
            {
                ErrorText.Text = "Incorrect password";
                ErrorText.Visibility = Visibility.Visible;
                PasswordBox.Password = string.Empty;
                PasswordBox.Focus(FocusState.Programmatic);
                CheckLockout();
            }
        }
        catch (InvalidOperationException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            CheckLockout();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Error: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void PasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && IsPrimaryButtonEnabled)
        {
            HandlePasswordSubmit();
        }
    }

    private void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_authService.HasSecurityQuestion)
        {
            ErrorText.Text = "No security question has been set. You can set one in Settings.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Switch to Security Question step in-place (no dialog collision!)
        SwitchToSecurityQuestionView();
    }

    private void SwitchToSecurityQuestionView()
    {
        _currentStep = AuthStep.SecurityQuestion;
        Title = "Verify Identity";
        PrimaryButtonText = "Verify";
        DefaultButton = ContentDialogButton.Primary;

        PasswordPanel.Visibility = Visibility.Collapsed;
        SecurityQuestionPanel.Visibility = Visibility.Visible;
        ResetPasswordPanel.Visibility = Visibility.Collapsed;

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
            // Verified! Transition to Reset Password view in-place
            SwitchToResetPasswordView();
        }
        else
        {
            QuestionErrorText.Text = "Incorrect answer. Please try again.";
            QuestionErrorText.Visibility = Visibility.Visible;
            SecurityAnswerBox.SelectAll();
            SecurityAnswerBox.Focus(FocusState.Programmatic);
        }
    }

    private void SwitchToResetPasswordView()
    {
        _currentStep = AuthStep.ResetPassword;
        Title = "Reset Admin Password";
        PrimaryButtonText = "Reset & Unlock";
        DefaultButton = ContentDialogButton.Primary;

        PasswordPanel.Visibility = Visibility.Collapsed;
        SecurityQuestionPanel.Visibility = Visibility.Collapsed;
        ResetPasswordPanel.Visibility = Visibility.Visible;

        NewPasswordBox.Password = string.Empty;
        ConfirmPasswordBox.Password = string.Empty;
        StrengthBar.Value = 0;
        StrengthText.Text = string.Empty;
        ResetErrorText.Visibility = Visibility.Collapsed;

        NewPasswordBox.Focus(FocusState.Programmatic);
    }

    private void NewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var password = NewPasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            StrengthBar.Value = 0;
            StrengthText.Text = string.Empty;
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

    private void HandleResetPasswordSubmit()
    {
        ResetErrorText.Visibility = Visibility.Collapsed;

        var newPassword = NewPasswordBox.Password;
        var confirmPassword = ConfirmPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            ResetErrorText.Text = "Please enter a new password.";
            ResetErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (newPassword.Length < 6)
        {
            ResetErrorText.Text = "Password must be at least 6 characters.";
            ResetErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (newPassword != confirmPassword)
        {
            ResetErrorText.Text = "Passwords do not match.";
            ResetErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _authService.SetPassword(newPassword);
            IsAuthenticated = true;
            Hide();
        }
        catch (Exception ex)
        {
            ResetErrorText.Text = ex.Message;
            ResetErrorText.Visibility = Visibility.Visible;
        }
    }

    private void BackToPassword_Click(object sender, RoutedEventArgs e)
    {
        _currentStep = AuthStep.Password;
        Title = "Verify Admin Password";
        PrimaryButtonText = "Unlock";
        DefaultButton = ContentDialogButton.Primary;

        PasswordPanel.Visibility = Visibility.Visible;
        SecurityQuestionPanel.Visibility = Visibility.Collapsed;
        ResetPasswordPanel.Visibility = Visibility.Collapsed;
        HelloPendingPanel.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = true;

        ErrorText.Visibility = Visibility.Collapsed;
        PasswordBox.Password = string.Empty;
        PasswordBox.Focus(FocusState.Programmatic);
    }

    private async void WindowsHelloButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        WindowsHelloButton.IsEnabled = false;

        // Switch dialog into Windows Hello pending view
        PasswordPanel.Visibility = Visibility.Collapsed;
        HelloPendingPanel.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = false;

        try
        {
            _helloCts = new System.Threading.CancellationTokenSource();
            bool verified = await _helloService.RequestVerificationAsync(
                "Verify your identity to unlock AppLock",
                _helloCts.Token);

            if (verified)
            {
                IsAuthenticated = true;
                Hide();
                return;
            }

            if (_helloCts?.IsCancellationRequested != true)
            {
                ErrorText.Text = "Windows Hello verification failed. Try your password.";
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user clicking "Cancel Windows Hello"
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Windows Hello error: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _helloCts?.Dispose();
            _helloCts = null;

            if (!IsAuthenticated)
            {
                HelloPendingPanel.Visibility = Visibility.Collapsed;
                PasswordPanel.Visibility = Visibility.Visible;
                IsPrimaryButtonEnabled = true;
                PasswordBox.Focus(FocusState.Programmatic);
            }
            WindowsHelloButton.IsEnabled = true;
        }
    }

    private void HelloCancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _helloCts?.Cancel();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cancel Windows Hello error: {ex.Message}");
        }
    }
}
