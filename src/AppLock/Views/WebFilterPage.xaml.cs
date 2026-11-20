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
    private bool _updatingToggle;

    public WebFilterPage()
    {
        ViewModel = new WebFilterViewModel(
            ServiceContainer.Settings,
            ServiceContainer.SettingsStore,
            ServiceContainer.WebFilterListService);

        InitializeComponent();
        DataContext = ViewModel;
        Loaded += WebFilterPage_Loaded;
        Unloaded += WebFilterPage_Unloaded;
    }

    private async void WebFilterPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isElevated = KeyboardService.IsAdministrator();
        NotElevatedInfoBar.IsOpen = !_isElevated;

        ServiceContainer.WebFilterService.StatusChanged += OnFilterStatusChanged;
        ServiceContainer.WebFilterService.TamperDetected += OnFilterTamperDetected;
        ServiceContainer.WebFilterService.ListenerFailed += OnFilterListenerFailed;

        SetToggleWithoutEvent(ViewModel.IsEnabled);
        PopulateStatus();
        ShowTab("categories");

        // Build initial (empty) cards immediately so the repeater is visible
        ViewModel.BuildCategoryCards();
        ViewModel.BuildServicesAndSites();

        await ViewModel.InitializeListsAsync();

        // Refresh cards with real domain counts after lists are loaded
        ViewModel.RefreshCategoryDomainCounts();
        ViewModel.BuildServicesAndSites();
        PopulateStatus();
    }

    private void WebFilterPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.WebFilterService.StatusChanged -= OnFilterStatusChanged;
        ServiceContainer.WebFilterService.TamperDetected -= OnFilterTamperDetected;
        ServiceContainer.WebFilterService.ListenerFailed -= OnFilterListenerFailed;
    }

    private void OnFilterStatusChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetToggleWithoutEvent(ViewModel.IsEnabled);
            PopulateStatus();
        });
    }

    private void OnFilterTamperDetected(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TamperInfoBar.Message = message;
            TamperInfoBar.IsOpen = true;
        });
    }

    private void OnFilterListenerFailed(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            FilterErrorInfoBar.Message = message;
            FilterErrorInfoBar.IsOpen = true;
            SetToggleWithoutEvent(false);
            PopulateStatus();
        });
    }

    private void SetToggleWithoutEvent(bool isOn)
    {
        _updatingToggle = true;
        try
        {
            MasterToggle.IsOn = isOn;
        }
        finally
        {
            _updatingToggle = false;
        }
    }

    private async void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingToggle) return;

        bool turningOn = MasterToggle.IsOn;

        if (turningOn)
        {
            if (!_isElevated)
            {
                NotElevatedInfoBar.IsOpen = true;
                SetToggleWithoutEvent(false);
                return;
            }

            if (!ViewModel.Filter.DefaultsSeeded)
            {
                var dialog = new WebFilter.Dialogs.SetupWizardDialog(ViewModel.Filter)
                {
                    XamlRoot = this.Content.XamlRoot
                };

                await dialog.ShowAsync();
                if (!dialog.WasApplied)
                {
                    SetToggleWithoutEvent(false);
                    return;
                }
            }

            // Seed category defaults so cards reflect the wizard's choices
            ViewModel.SeedCategoryDefaults();
            ViewModel.BuildCategoryCards();

            ViewModel.SetEnabled(true);
            bool ok = await ServiceContainer.WebFilterService.StartAsync();
            if (!ok)
            {
                FilterErrorInfoBar.Message = ServiceContainer.WebFilterService.LastError ?? "Could not start DNS filter server.";
                FilterErrorInfoBar.IsOpen = true;
                ViewModel.SetEnabled(false);
                SetToggleWithoutEvent(false);
            }
            PopulateStatus();
        }
        else
        {
            if (ViewModel.IsEnabled)
            {
                if (!await RequestAuthAsync("Enter your password to turn off Web Filter"))
                {
                    SetToggleWithoutEvent(true);
                    return;
                }
            }

            ViewModel.SetEnabled(false);
            await ServiceContainer.WebFilterService.StopAsync();
            PopulateStatus();
        }
    }

    private void RestoreNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.WebFilterService.RestoreNetworkSettings();
        ViewModel.SetEnabled(false);
        SetToggleWithoutEvent(false);
        PopulateStatus();
        RestoreSuccessBar.IsOpen = true;
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
            _lastCheckedHost = result.Host;
            CheckBlockedActionPanel.Visibility = Visibility.Visible;

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
            _lastCheckedHost = string.Empty;
            CheckBlockedActionPanel.Visibility = Visibility.Collapsed;

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

    // ── Pause button ──────────────────────────────────────────────────────────

    private async void PauseButton_30_Click(object sender, RoutedEventArgs e)
    {
        if (!await RequestAuthAsync("Enter your password to pause Web Filter")) return;
        ViewModel.TogglePause(30);
        PopulateStatus();
        UpdatePauseButtonLabel();
    }

    private async void PauseButton_60_Click(object sender, RoutedEventArgs e)
    {
        if (!await RequestAuthAsync("Enter your password to pause Web Filter")) return;
        ViewModel.TogglePause(60);
        PopulateStatus();
        UpdatePauseButtonLabel();
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        // Force resume (clear pause) by passing 0 minutes when already paused
        if (ViewModel.IsPaused) ViewModel.TogglePause(0);
        PopulateStatus();
        UpdatePauseButtonLabel();
    }

    private void UpdatePauseButtonLabel()
    {
        PauseButton.Content = ViewModel.IsPaused ? "Resume" : "Pause";
    }

    // ── Authentication Helper ─────────────────────────────────────────────────

    private async Task<bool> RequestAuthAsync(string prompt = "Authentication required")
    {
        if (ServiceContainer.AuthService.IsFirstRun) return true;
        var dialog = new AuthDialog(ServiceContainer.AuthService)
        {
            XamlRoot = this.Content.XamlRoot,
            IntroText = prompt
        };
        await dialog.ShowAsync();
        return dialog.IsAuthenticated;
    }

    // ── Always Block Actions ──────────────────────────────────────────────────

    private void AddAlwaysBlockButton_Click(object sender, RoutedEventArgs e)
    {
        PerformAddAlwaysBlock();
    }

    private void AlwaysBlockInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            PerformAddAlwaysBlock();
        }
    }

    private void PerformAddAlwaysBlock()
    {
        string domain = AlwaysBlockDomainBox.Text;
        string note = AlwaysBlockNoteBox.Text;

        if (ViewModel.AddAlwaysBlock(domain, note, out var message))
        {
            AlwaysBlockDomainBox.Text = string.Empty;
            AlwaysBlockNoteBox.Text = string.Empty;
            AlwaysBlockInfoBar.Severity = InfoBarSeverity.Success;
            AlwaysBlockInfoBar.Title = "Added";
            AlwaysBlockInfoBar.Message = $"{domain} will always be blocked.";
            AlwaysBlockInfoBar.IsOpen = true;
        }
        else
        {
            AlwaysBlockInfoBar.Severity = InfoBarSeverity.Error;
            AlwaysBlockInfoBar.Title = "Could not add";
            AlwaysBlockInfoBar.Message = message;
            AlwaysBlockInfoBar.IsOpen = true;
        }
    }

    private async void ImportAlwaysBlockButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WebFilter.Dialogs.ImportDomainsDialog("Always Block")
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
        if (!string.IsNullOrWhiteSpace(dialog.ImportedText))
        {
            int added = ViewModel.ImportAlwaysBlock(dialog.ImportedText);
            AlwaysBlockInfoBar.Severity = InfoBarSeverity.Success;
            AlwaysBlockInfoBar.Title = "Imported";
            AlwaysBlockInfoBar.Message = $"Added {added} new domain(s) to Always Block.";
            AlwaysBlockInfoBar.IsOpen = true;
        }
    }

    private async void ExportAlwaysBlockButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportTextFileAsync("AlwaysBlock_Domains.txt", ViewModel.ExportAlwaysBlock());
    }

    // ── Always Allow Actions ──────────────────────────────────────────────────

    private async void AddAlwaysAllowButton_Click(object sender, RoutedEventArgs e)
    {
        await PerformAddAlwaysAllowAsync();
    }

    private async void AlwaysAllowInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await PerformAddAlwaysAllowAsync();
        }
    }

    private async Task PerformAddAlwaysAllowAsync()
    {
        string domain = AlwaysAllowDomainBox.Text;
        string note = AlwaysAllowNoteBox.Text;

        if (string.IsNullOrWhiteSpace(domain))
        {
            AlwaysAllowInfoBar.Severity = InfoBarSeverity.Error;
            AlwaysAllowInfoBar.Title = "Enter a domain";
            AlwaysAllowInfoBar.Message = "Please type a domain name or URL to allow.";
            AlwaysAllowInfoBar.IsOpen = true;
            return;
        }

        // Authenticate before allowing a domain
        if (!await RequestAuthAsync("Enter your password to add a domain to Always Allow"))
        {
            return;
        }

        if (ViewModel.AddAlwaysAllow(domain, note, out var message, out bool isNeverBlock))
        {
            AlwaysAllowDomainBox.Text = string.Empty;
            AlwaysAllowNoteBox.Text = string.Empty;
            AlwaysAllowInfoBar.Severity = InfoBarSeverity.Success;
            AlwaysAllowInfoBar.Title = "Added";
            AlwaysAllowInfoBar.Message = $"{domain} will always be allowed.";
            AlwaysAllowInfoBar.IsOpen = true;
        }
        else
        {
            AlwaysAllowInfoBar.Severity = isNeverBlock ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
            AlwaysAllowInfoBar.Title = isNeverBlock ? "Already protected" : "Could not add";
            AlwaysAllowInfoBar.Message = message;
            AlwaysAllowInfoBar.IsOpen = true;
        }
    }

    private async void ImportAlwaysAllowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await RequestAuthAsync("Enter your password to import domains into Always Allow"))
        {
            return;
        }

        var dialog = new WebFilter.Dialogs.ImportDomainsDialog("Always Allow")
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
        if (!string.IsNullOrWhiteSpace(dialog.ImportedText))
        {
            int added = ViewModel.ImportAlwaysAllow(dialog.ImportedText);
            AlwaysAllowInfoBar.Severity = InfoBarSeverity.Success;
            AlwaysAllowInfoBar.Title = "Imported";
            AlwaysAllowInfoBar.Message = $"Added {added} new domain(s) to Always Allow.";
            AlwaysAllowInfoBar.IsOpen = true;
        }
    }

    private async void ExportAlwaysAllowButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportTextFileAsync("AlwaysAllow_Domains.txt", ViewModel.ExportAlwaysAllow());
    }

    // ── Delete Domain Action ──────────────────────────────────────────────────

    private async void DeleteDomain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DomainItemViewModel item }) return;

        bool isBlock = ViewModel.AlwaysBlockItems.Contains(item);
        string listName = isBlock ? "Always Block" : "Always Allow";

        // Removing from either list requires authentication
        if (!await RequestAuthAsync($"Enter your password to remove {item.Domain} from {listName}"))
        {
            return;
        }

        if (isBlock)
        {
            ViewModel.RemoveAlwaysBlock(item);
        }
        else
        {
            ViewModel.RemoveAlwaysAllow(item);
        }
    }

    // ── Export helper ─────────────────────────────────────────────────────────

    private async Task ExportTextFileAsync(string suggestedName, string text)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var targetWin = App.MainWindowInstance ?? MainWindow.Instance;
            if (targetWin != null)
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(targetWin);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Text document", new List<string> { ".txt" });
            picker.SuggestedFileName = suggestedName;

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await Windows.Storage.FileIO.WriteTextAsync(file, text);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Export failed: {ex.Message}");
        }
    }

    // ── Check Tool Allow Actions ──────────────────────────────────────────────

    private string _lastCheckedHost = string.Empty;

    private async void Allow15Min_Click(object sender, RoutedEventArgs e)
    {
        await ApplyCheckTempAllowAsync(15, "15 minutes");
    }

    private async void Allow1Hour_Click(object sender, RoutedEventArgs e)
    {
        await ApplyCheckTempAllowAsync(60, "1 hour");
    }

    private async void AllowToday_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var midnight = now.Date.AddDays(1);
        int minutes = Math.Max(1, (int)(midnight - now).TotalMinutes);
        await ApplyCheckTempAllowAsync(minutes, "today (until midnight)");
    }

    private async Task ApplyCheckTempAllowAsync(int minutes, string durationLabel)
    {
        if (string.IsNullOrEmpty(_lastCheckedHost)) return;

        if (!await RequestAuthAsync($"Enter your password to temporarily allow {_lastCheckedHost} for {durationLabel}"))
        {
            return;
        }

        if (ViewModel.AddTempAllow(_lastCheckedHost, minutes, out var host, out var expiresUtc))
        {
            CheckBlockedActionPanel.Visibility = Visibility.Collapsed;
            ShowCheckResult(
                InfoBarSeverity.Success,
                "Temporarily Allowed",
                $"{host} is allowed until {expiresUtc.ToLocalTime():HH:mm} ({durationLabel}).");
        }
    }

    private async void CheckAlwaysAllow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastCheckedHost)) return;

        if (!await RequestAuthAsync($"Enter your password to add {_lastCheckedHost} to Always Allow"))
        {
            return;
        }

        if (ViewModel.AddAlwaysAllow(_lastCheckedHost, "Allowed via check tool", out var message, out bool isNeverBlock))
        {
            CheckBlockedActionPanel.Visibility = Visibility.Collapsed;
            ShowCheckResult(
                InfoBarSeverity.Success,
                "Always Allowed",
                $"{_lastCheckedHost} has been added to the Always Allow list.");
        }
        else
        {
            ShowCheckResult(
                isNeverBlock ? InfoBarSeverity.Informational : InfoBarSeverity.Error,
                isNeverBlock ? "Already protected" : "Could not allow",
                message);
        }
    }

    // ── Temporary Allow Revoke ────────────────────────────────────────────────

    private async void RevokeTempAllow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TempAllowItemViewModel item }) return;

        if (!await RequestAuthAsync($"Enter your password to revoke temporary allow for {item.Domain}"))
        {
            return;
        }

        ViewModel.RevokeTempAllow(item);
    }

    // ── Schedule Dialogs ──────────────────────────────────────────────────────

    private async void CategorySchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CategoryCardViewModel card }) return;

        if (!await RequestAuthAsync($"Enter your password to modify schedule for {card.Name}"))
        {
            return;
        }

        var dialog = new WebFilter.Dialogs.ScheduleEditorDialog(card.Name, card.GetSchedule())
        {
            XamlRoot = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            card.SetSchedule(dialog.ResultBlocks);
        }
    }

    private async void ServiceSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServiceCardViewModel svc }) return;

        if (!await RequestAuthAsync($"Enter your password to modify schedule for {svc.Name}"))
        {
            return;
        }

        var dialog = new WebFilter.Dialogs.ScheduleEditorDialog(svc.Name, svc.GetSchedule())
        {
            XamlRoot = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            svc.SetSchedule(dialog.ResultBlocks);
        }
    }
}
