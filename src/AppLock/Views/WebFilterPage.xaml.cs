using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AppLock.Views;

/// <summary>
/// Web Filter page. Milestone 3 adds the block-list pipeline and the "Check a website" tool; the
/// category cards, system DNS changes, schedules and activity log arrive in later milestones.
/// </summary>
public sealed partial class WebFilterPage : Page
{
    public WebFilterViewModel ViewModel { get; }

    private bool _isElevated;

    public WebFilterPage()
    {
        ViewModel = new WebFilterViewModel(
            ServiceContainer.Settings,
            ServiceContainer.SettingsStore,
            ServiceContainer.WebFilterListService);

        InitializeComponent();
        DataContext = ViewModel;
        Loaded += WebFilterPage_Loaded;
    }

    private async void WebFilterPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isElevated = KeyboardService.IsAdministrator();
        NotElevatedInfoBar.IsOpen = !_isElevated;

        PopulateStatus();
        ShowTab("categories");

        await ViewModel.InitializeListsAsync();
        PopulateStatus();
    }

    private void PopulateStatus()
    {
        StatusTitle.Text = ViewModel.StatusTitle;
        StatusDetail.Text = ViewModel.StatusDetail;

        string dotKey = ViewModel.IsEnabled ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush";
        if (Application.Current.Resources.TryGetValue(dotKey, out var brush) && brush is Microsoft.UI.Xaml.Media.Brush fill)
        {
            StatusDot.Fill = fill;
        }
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        ShowTab(tag);
    }

    private void ShowTab(string tag)
    {
        CategoriesPanel.Visibility = tag == "categories" ? Visibility.Visible : Visibility.Collapsed;
        SitesPanel.Visibility = tag == "sites" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        var selected = (Style)Application.Current.Resources["AccentButtonStyle"];
        CategoriesTabButton.Style = tag == "categories" ? selected : null;
        SitesTabButton.Style = tag == "sites" ? selected : null;
        ActivityTabButton.Style = tag == "activity" ? selected : null;
        SettingsTabButton.Style = tag == "settings" ? selected : null;
    }

    private void CheckWebsiteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.CheckInput = CheckWebsiteBox.Text;
    }

    private async void CheckWebsiteBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        e.Handled = true;
        await RunCheckAsync();
    }

    private async void CheckWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        ViewModel.CheckInput = CheckWebsiteBox.Text;

        if (!ViewModel.CanCheck) return;

        var result = await ViewModel.CheckAsync();
        if (result == null) return;

        if (!result.IsValid)
        {
            ShowCheckResult(
                InfoBarSeverity.Warning,
                "Enter a website",
                "Type a domain such as example.com or a URL such as https://example.com/page.");
            return;
        }

        string preview = ViewModel.IsEnabled
            ? string.Empty
            : " Web Filter is off; this shows what it will block when it is on.";

        if (result.IsBlocked)
        {
            string source = string.IsNullOrWhiteSpace(result.SourceName)
                ? string.Empty
                : $" Matched by {result.SourceName}.";

            ShowCheckResult(
                InfoBarSeverity.Error,
                $"Blocked \u2014 {result.Reason}",
                $"{result.Host} is on a blocked list.{source}{preview}");
        }
        else
        {
            ShowCheckResult(
                InfoBarSeverity.Success,
                "Allowed",
                $"{result.Host} is not on any enabled list.{preview}");
        }
    }

    private void ShowCheckResult(InfoBarSeverity severity, string title, string message)
    {
        CheckResultBar.Severity = severity;
        CheckResultBar.Title = title;
        CheckResultBar.Message = message;
        CheckResultBar.IsOpen = true;
    }

    private async void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (KeyboardService.IsAdministrator()) return;

            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            // Mark this exit as authorized so the watchdog does not revive the non-elevated instance.
            ProcessProtectionService.SignalAuthorizedExit();
            ProcessProtectionService.UnprotectCurrentProcess();

            var process = Process.Start(psi);
            if (process != null)
            {
                // Give the elevated instance a moment to come up, then close this one.
                await Task.Delay(400);
                Environment.Exit(0);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user dismissed the UAC prompt - keep running unelevated.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Restart as administrator failed: {ex.Message}");
        }
    }
}
