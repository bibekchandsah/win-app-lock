using System;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class SetSecurityQuestionDialog : ContentDialog
{
    private readonly AuthService _authService;
    public bool QuestionSaved { get; private set; }

    public SetSecurityQuestionDialog(AuthService authService)
    {
        _authService = authService;
        InitializeComponent();
        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();

        // If a question is already set, select it or fill it
        if (_authService.HasSecurityQuestion)
        {
            var currentQ = _authService.GetSecurityQuestion();
            bool found = false;
            foreach (var item in QuestionComboBox.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), currentQ, StringComparison.OrdinalIgnoreCase))
                {
                    QuestionComboBox.SelectedItem = item;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // Custom question
                foreach (var item in QuestionComboBox.Items)
                {
                    if (item is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), "Custom question", StringComparison.OrdinalIgnoreCase))
                    {
                        QuestionComboBox.SelectedItem = item;
                        CustomQuestionBox.Text = currentQ;
                        CustomQuestionBox.Visibility = Visibility.Visible;
                        break;
                    }
                }
            }
        }
    }

    private void QuestionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QuestionComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            string text = selectedItem.Content?.ToString() ?? string.Empty;
            if (string.Equals(text, "Custom question", StringComparison.OrdinalIgnoreCase))
            {
                CustomQuestionBox.Visibility = Visibility.Visible;
                CustomQuestionBox.Focus(FocusState.Programmatic);
            }
            else
            {
                CustomQuestionBox.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ErrorText.Visibility = Visibility.Collapsed;

        string question = string.Empty;
        if (QuestionComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            string selectedText = selectedItem.Content?.ToString() ?? string.Empty;
            if (string.Equals(selectedText, "Custom question", StringComparison.OrdinalIgnoreCase))
            {
                question = CustomQuestionBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(question))
                {
                    ErrorText.Text = "Please enter your custom question.";
                    ErrorText.Visibility = Visibility.Visible;
                    return;
                }
            }
            else
            {
                question = selectedText;
            }
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            ErrorText.Text = "Please select or enter a security question.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        string answer = AnswerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(answer))
        {
            ErrorText.Text = "Please enter your answer.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _authService.SetSecurityQuestion(question, answer);
            QuestionSaved = true;
            Hide();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Error: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
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
}
