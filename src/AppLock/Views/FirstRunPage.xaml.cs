using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class FirstRunPage : Page
{
    private readonly AuthService _authService;
    public event EventHandler? FirstRunCompleted;

    public FirstRunPage(AuthService authService)
    {
        _authService = authService;
        InitializeComponent();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var password = PasswordBox.Password;
        var confirmPassword = ConfirmPasswordBox.Password;

        // Enable button only if both fields match and password is valid
        SetupButton.IsEnabled = !string.IsNullOrEmpty(password) &&
                                 !string.IsNullOrEmpty(confirmPassword) &&
                                 password == confirmPassword &&
                                 password.Length >= 6;

        // Show error if passwords don't match (but only if confirm has content)
        if (!string.IsNullOrEmpty(confirmPassword) && password != confirmPassword)
        {
            ErrorText.Text = "Passwords do not match";
            ErrorText.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrEmpty(password) && password.Length < 6)
        {
            ErrorText.Text = "Password must be at least 6 characters";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetupButton.IsEnabled = false;
            ErrorText.Visibility = Visibility.Collapsed;

            var password = PasswordBox.Password;
            _authService.SetupPassword(password);

            // Clear password boxes
            PasswordBox.Password = string.Empty;
            ConfirmPasswordBox.Password = string.Empty;

            // Notify completion
            FirstRunCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Setup failed: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            SetupButton.IsEnabled = true;
        }
    }
}
