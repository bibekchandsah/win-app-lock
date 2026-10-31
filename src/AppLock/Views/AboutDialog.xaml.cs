using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Reflection;

namespace AppLock.Views;

public sealed partial class AboutDialog : ContentDialog
{
    private const string RepoUrl = "https://github.com/bibekchandsah/win-app-lock";
    private const string DeveloperUrl = "https://www.bibekchandsah.com.np/";
    private const string ContributorsUrl = "https://github.com/bibekchandsah/win-app-lock/graphs/contributors";
    private const string InspirationUrl = "https://apps.microsoft.com/detail/9pg38sth0tks?hl=en-US&gl=US";

    public AboutDialog()
    {
        InitializeComponent();
        LoadVersionInfo();

        Title = ServiceContainer.LocalizationService["AboutAppLock"];
        CloseButtonText = ServiceContainer.LocalizationService["Close"];

        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
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

    private async void OpenUrl(string url)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open URL '{url}': {ex.Message}");
        }
    }

    private void RepoLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(RepoUrl);
    }

    private void DeveloperLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(DeveloperUrl);
    }

    private void ContributorsLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(ContributorsUrl);
    }

    private void InspirationLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(InspirationUrl);
    }
}
