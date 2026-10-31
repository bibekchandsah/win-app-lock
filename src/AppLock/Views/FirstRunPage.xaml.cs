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
        ValidateInputs();
    }

    private void InputFields_Changed(object sender, RoutedEventArgs e)
    {
        ValidateInputs();
    }

    private void ValidateInputs()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var password = PasswordBox.Password;
        var confirmPassword = ConfirmPasswordBox.Password;

        // Check password length
        if (!string.IsNullOrEmpty(password) && password.Length < 6)
        {
            ErrorText.Text = "Password must be at least 6 characters";
            ErrorText.Visibility = Visibility.Visible;
            SetupButton.IsEnabled = false;
            return;
        }

        // Check password matching
        if (!string.IsNullOrEmpty(confirmPassword) && password != confirmPassword)
        {
            ErrorText.Text = "Passwords do not match";
            ErrorText.Visibility = Visibility.Visible;
            SetupButton.IsEnabled = false;
            return;
        }

        bool hasValidPassword = !string.IsNullOrEmpty(password) &&
                                !string.IsNullOrEmpty(confirmPassword) &&
                                password == confirmPassword &&
                                password.Length >= 6;

        // Check security question
        bool hasValidQuestion = false;
        if (QuestionComboBox.SelectedItem is ComboBoxItem item)
        {
            string qText = item.Content?.ToString() ?? string.Empty;
            if (string.Equals(qText, "Custom question", StringComparison.OrdinalIgnoreCase))
            {
                hasValidQuestion = !string.IsNullOrWhiteSpace(CustomQuestionBox.Text);
            }
            else
            {
                hasValidQuestion = !string.IsNullOrWhiteSpace(qText);
            }
        }

        // Check answer
        bool hasValidAnswer = !string.IsNullOrWhiteSpace(AnswerBox.Text);

        SetupButton.IsEnabled = hasValidPassword && hasValidQuestion && hasValidAnswer;
    }

    private void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetupButton.IsEnabled = false;
            ErrorText.Visibility = Visibility.Collapsed;

            var password = PasswordBox.Password;
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            {
                ErrorText.Text = "Password must be at least 6 characters";
                ErrorText.Visibility = Visibility.Visible;
                SetupButton.IsEnabled = true;
                return;
            }

            if (password != ConfirmPasswordBox.Password)
            {
                ErrorText.Text = "Passwords do not match";
                ErrorText.Visibility = Visibility.Visible;
                SetupButton.IsEnabled = true;
                return;
            }

            string question = string.Empty;
            if (QuestionComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                string selectedText = selectedItem.Content?.ToString() ?? string.Empty;
                if (string.Equals(selectedText, "Custom question", StringComparison.OrdinalIgnoreCase))
                {
                    question = CustomQuestionBox.Text.Trim();
                }
                else
                {
                    question = selectedText;
                }
            }

            if (string.IsNullOrWhiteSpace(question))
            {
                ErrorText.Text = "Please select or enter a security question";
                ErrorText.Visibility = Visibility.Visible;
                SetupButton.IsEnabled = true;
                return;
            }

            string answer = AnswerBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(answer))
            {
                ErrorText.Text = "Please provide an answer to the security question";
                ErrorText.Visibility = Visibility.Visible;
                SetupButton.IsEnabled = true;
                return;
            }

            // Save password and security question
            _authService.SetupPassword(password);
            _authService.SetSecurityQuestion(question, answer);

            // Clear inputs
            PasswordBox.Password = string.Empty;
            ConfirmPasswordBox.Password = string.Empty;
            AnswerBox.Text = string.Empty;
            CustomQuestionBox.Text = string.Empty;

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
