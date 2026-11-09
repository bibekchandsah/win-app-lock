using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class RingLightPage : Page
{
    public RingLightPage()
    {
        this.InitializeComponent();
    }

    private void BackButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }
}

