using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

/// <summary>
/// Web Filter page. Milestone 1 is the shell: the header, the status area and the four tabs.
/// Applying DNS, lists, schedules and the activity log arrive in the following milestones.
/// </summary>
public sealed partial class WebFilterPage : Page
{
    public WebFilterViewModel ViewModel { get; }

    private bool _isElevated;

    public WebFilterPage()
    {
        ViewModel = new WebFilterViewModel(ServiceContainer.Settings, ServiceContainer.SettingsStore);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += WebFilterPage_Loaded;
    }

    private void WebFilterPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isElevated = KeyboardService.IsAdministrator();
        NotElevatedInfoBar.IsOpen = !_isElevated;

        PopulateStatus();
        ShowTab("categories");
    }

    private void PopulateStatus()
    {
        StatusTitle.Text = ViewModel.StatusTitle;
        StatusDetail.Text = ViewModel.StatusDetail;
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
