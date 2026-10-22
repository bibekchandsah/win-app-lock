using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AppLock.Views;

public sealed partial class VerifySecurityQuestionDialog : ContentDialog
{
    private readonly AuthService _authService;
    public bool IsVerified { get; private set; }

    public VerifySecurityQuestionDialog(AuthService authService)
    {
        _authService = authService;
        InitializeComponent();

        ApplyTheme();
        QuestionTextBlock.Text = _authService.GetSecurityQuestion();
        Loaded += (s, e) =>
        {
            ApplyTheme();
            AnswerBox.Focus(FocusState.Programmatic);
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

    private void AnswerBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ValidateAndVerify();
        }
    }

    private void ValidateAndVerify()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        string answer = AnswerBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(answer))
        {
            ErrorText.Text = "Please enter your answer.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (_authService.VerifySecurityAnswer(answer))
        {
            IsVerified = true;
            Hide();
        }
        else
        {
            ErrorText.Text = "Incorrect answer. Please try again.";
            ErrorText.Visibility = Visibility.Visible;
            AnswerBox.SelectAll();
            AnswerBox.Focus(FocusState.Programmatic);
        }
    }
}
