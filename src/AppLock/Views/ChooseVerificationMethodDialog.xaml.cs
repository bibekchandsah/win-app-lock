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
        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (this.XamlRoot?.Content is Microsoft.UI.Xaml.FrameworkElement rootFe && rootFe.ActualTheme != Microsoft.UI.Xaml.ElementTheme.Default)
        {
            this.RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            this.RequestedTheme = AppLock.Services.ServiceContainer.CurrentTheme;
        }
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
