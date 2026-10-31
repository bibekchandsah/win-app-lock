using AppLock.Services;
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

        ServiceContainer.LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Loaded += (s, e) => ApplyLocalization();
        Unloaded += (s, e) => ServiceContainer.LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        ApplyLocalization();
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ApplyLocalization());
    }

    private void ApplyLocalization()
    {
        var loc = ServiceContainer.LocalizationService;
        if (PageTitle != null) PageTitle.Text = loc["FeedbackTitle"];
        if (PageSubtitle != null) PageSubtitle.Text = loc["FeedbackSubtitle"];
        if (TxtContributeTitle != null) TxtContributeTitle.Text = loc["ContributeCardTitle"];
        if (TxtContributeSubtitle != null) TxtContributeSubtitle.Text = loc["ContributeCardSubtitle"];
        if (TxtContributeDesc != null) TxtContributeDesc.Text = loc["ContributeCardDesc"];
        if (BtnContributeText != null) BtnContributeText.Text = loc["BtnContribute"];
        if (BtnContributorsText != null) BtnContributorsText.Text = loc["BtnContributors"];
        if (BtnReportIssueText != null) BtnReportIssueText.Text = loc["BtnFeedbackIssues"];

        if (TxtInspirationTitle != null) TxtInspirationTitle.Text = loc["InspirationCardTitle"];
        if (TxtInspirationSubtitle != null) TxtInspirationSubtitle.Text = loc["InspirationCardSubtitle"];
        if (TxtInspirationDesc != null) TxtInspirationDesc.Text = loc["InspirationCardDesc"];
        if (BtnInspirationText != null) BtnInspirationText.Text = loc["BtnViewInspiration"];

        if (TxtDeveloperTitle != null) TxtDeveloperTitle.Text = loc["DeveloperCardTitle"];
        if (TxtDeveloperSubtitle != null) TxtDeveloperSubtitle.Text = loc["DeveloperCardSubtitle"];
        if (BtnWebsiteText != null) BtnWebsiteText.Text = loc["BtnVisitWebsite"];
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
