using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace AppLock.Views;

public sealed partial class FeedbackPage : Page
{
    private const string RepoUrl = "https://github.com/bibekchandsah/win-app-lock";
    private const string IssuesUrl = "https://github.com/bibekchandsah/win-app-lock/issues";
    private const string ContributorsUrl = "https://github.com/bibekchandsah/win-app-lock/graphs/contributors";
    private const string InspirationUrl = "https://apps.microsoft.com/detail/9pg38sth0tks?hl=en-US&gl=US";
    private const string DeveloperUrl = "https://www.bibekchandsah.com.np/";

    public FeedbackPage()
    {
        InitializeComponent();
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

    private void ContributeGitHub_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(RepoUrl);
    }

    private void ViewContributors_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(ContributorsUrl);
    }

    private void ReportIssue_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(IssuesUrl);
    }

    private void ViewInspirationStore_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(InspirationUrl);
    }

    private void DeveloperWebsite_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(DeveloperUrl);
    }
}
