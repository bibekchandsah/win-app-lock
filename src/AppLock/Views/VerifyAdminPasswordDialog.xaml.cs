using System;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AppLock.Views;

public sealed partial class VerifyAdminPasswordDialog : ContentDialog
{
    private readonly AuthService _authService;
    public bool IsVerified { get; private set; }

    public VerifyAdminPasswordDialog(AuthService authService)
    {
        _authService = authService;
        InitializeComponent();

        ApplyTheme();
        Loaded += (s, e) =>
        {
            ApplyTheme();
            PasswordBox.Focus(FocusState.Programmatic);
        };
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

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ValidateAndVerify();
    }

    private void PasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ValidateAndVerify();
        }
    }

    private void ValidateAndVerify()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        string password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "Please enter your admin password.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (_authService.VerifyPassword(password))
            {
                IsVerified = true;
                Hide();
            }
            else
            {
                ErrorText.Text = "Incorrect password. Please try again.";
                ErrorText.Visibility = Visibility.Visible;
                PasswordBox.SelectAll();
                PasswordBox.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
