using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;

namespace AppLock.Views;

public sealed partial class AuthDialog : ContentDialog
{
    private readonly AuthService _authService;
    public bool IsAuthenticated { get; private set; }

    public AuthDialog(AuthService authService)
    {
        _authService = authService;
        InitializeComponent();
        CheckLockout();
        
        // Set focus to password box when loaded
        Loaded += (s, e) => PasswordBox.Focus(FocusState.Programmatic);
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
        Debug.WriteLine("PrimaryButtonClick triggered");
        
        // Always cancel the default close behavior initially
        args.Cancel = true;

        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            if (_authService.IsLockedOut)
            {
                CheckLockout();
                return;
            }

            var password = PasswordBox.Password;
            
            Debug.WriteLine($"Password entered: {!string.IsNullOrWhiteSpace(password)}");
            
            if (string.IsNullOrWhiteSpace(password))
            {
                ErrorText.Text = "Please enter your password";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            var isValid = _authService.VerifyPassword(password);
            Debug.WriteLine($"Password valid: {isValid}");
            
            if (isValid)
            {
                IsAuthenticated = true;
                PasswordBox.Password = string.Empty;
                Debug.WriteLine("Authentication successful, closing dialog");
                // Close the dialog manually with success
                Hide();
            }
            else
            {
                ErrorText.Text = "Incorrect password";
                ErrorText.Visibility = Visibility.Visible;
                PasswordBox.Password = string.Empty;
                PasswordBox.Focus(FocusState.Programmatic);

                // Check if now locked out
                CheckLockout();
                
                // Don't close the dialog, let user retry
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
            // Manually verify password on Enter key
            VerifyPasswordAndClose();
        }
    }

    private void VerifyPasswordAndClose()
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
            
            Debug.WriteLine($"Password entered: {!string.IsNullOrWhiteSpace(password)}");
            
            if (string.IsNullOrWhiteSpace(password))
            {
                ErrorText.Text = "Please enter your password";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            var isValid = _authService.VerifyPassword(password);
            Debug.WriteLine($"Password valid: {isValid}");
            
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

                // Check if now locked out
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
}
