using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Reflection;

namespace AppLock.Views;

public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();
        LoadVersionInfo();
    }

    private void LoadVersionInfo()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            VersionText.Text = $"Version {version?.Major}.{version?.Minor}.{version?.Build}";
        }
        catch
        {
            VersionText.Text = "Version 1.0.0";
        }
    }

    private async void GitHubButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/yourusername/applock"));
        }
        catch
        {
            // Ignore if can't open browser
        }
    }
}
