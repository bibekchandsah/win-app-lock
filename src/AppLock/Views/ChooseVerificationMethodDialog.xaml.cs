using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public enum VerificationMethodChoice
{
    Cancel,
    SecurityQuestion,
    AdminPassword
}

public sealed partial class ChooseVerificationMethodDialog : ContentDialog
{
    public VerificationMethodChoice SelectedChoice { get; private set; } = VerificationMethodChoice.Cancel;

    public ChooseVerificationMethodDialog()
    {
        InitializeComponent();
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        SelectedChoice = VerificationMethodChoice.SecurityQuestion;
    }

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        SelectedChoice = VerificationMethodChoice.AdminPassword;
    }

    private void ContentDialog_CloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        SelectedChoice = VerificationMethodChoice.Cancel;
    }
}
