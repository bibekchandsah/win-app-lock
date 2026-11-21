using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AppLock.Models;
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
    private System.Threading.Timer? _activityRefreshTimer;

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
        InitializeSettingsControls();
        ShowTab(ViewModel.IsProviderMode ? "settings" : "categories");

        ActivityDatePicker.DateChanged += ActivityDatePicker_DateChanged;

        // Build initial (empty) cards immediately so the repeater is visible
        ViewModel.BuildCategoryCards();
        ViewModel.BuildServicesAndSites();

        await ViewModel.InitializeListsAsync();

        // Refresh cards with real domain counts after lists are loaded
        ViewModel.RefreshCategoryDomainCounts();
        ViewModel.BuildServicesAndSites();
        PopulateStatus();

        // Initialize activity log service
        await ServiceContainer.WebFilterLogService.InitializeAsync();
        InitializeActivityControls();
    }

    private void WebFilterPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.WebFilterService.StatusChanged -= OnFilterStatusChanged;
        ServiceContainer.WebFilterService.TamperDetected -= OnFilterTamperDetected;
        ServiceContainer.WebFilterService.ListenerFailed -= OnFilterListenerFailed;
        ServiceContainer.WebFilterLogService.LogChanged -= OnLogChanged;
        _activityRefreshTimer?.Dispose();
        _activityRefreshTimer = null;
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

        var activeStyle = (Style)Resources["SegmentedTabButtonActive"];
        var inactiveStyle = (Style)Resources["SegmentedTabButtonInactive"];

        CategoriesTabButton.Style = tag == "categories" ? activeStyle : inactiveStyle;
        SitesTabButton.Style = tag == "sites" ? activeStyle : inactiveStyle;
        ActivityTabButton.Style = tag == "activity" ? activeStyle : inactiveStyle;
        SettingsTabButton.Style = tag == "settings" ? activeStyle : inactiveStyle;

        if (tag == "activity")
            _ = RefreshActivityUiAsync();
    }

    private void SitesSubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        ShowSitesSubTab(tag);
    }

    private void ShowSitesSubTab(string tag)
    {
        ServicesSection.Visibility = tag == "services" ? Visibility.Visible : Visibility.Collapsed;
        AlwaysBlockSection.Visibility = tag == "always_block" ? Visibility.Visible : Visibility.Collapsed;
        AlwaysAllowSection.Visibility = tag == "always_allow" ? Visibility.Visible : Visibility.Collapsed;
        TempAllowSection.Visibility = tag == "temp_allow" ? Visibility.Visible : Visibility.Collapsed;

        var activeStyle = (Style)Resources["SegmentedTabButtonActive"];
        var inactiveStyle = (Style)Resources["SegmentedTabButtonInactive"];

        ServicesSubTabButton.Style = tag == "services" ? activeStyle : inactiveStyle;
        AlwaysBlockSubTabButton.Style = tag == "always_block" ? activeStyle : inactiveStyle;
        AlwaysAllowSubTabButton.Style = tag == "always_allow" ? activeStyle : inactiveStyle;
        TempAllowSubTabButton.Style = tag == "temp_allow" ? activeStyle : inactiveStyle;
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

        if (ViewModel.IsProviderMode)
        {
            if (result.IsBlocked)
            {
                _lastCheckedHost = result.Host;
                CheckBlockedActionPanel.Visibility = Visibility.Collapsed;
                ShowCheckResult(
                    InfoBarSeverity.Error,
                    $"Blocked \u2014 {result.Reason}",
                    $"{result.Host} is blocked by {result.SourceName ?? "the provider"}. Verdict is from the DNS provider.{preview}");
            }
            else
            {
                _lastCheckedHost = string.Empty;
                CheckBlockedActionPanel.Visibility = Visibility.Collapsed;
                ShowCheckResult(
                    InfoBarSeverity.Success,
                    "Allowed",
                    $"{result.Host} is allowed by {result.SourceName ?? "the provider"}. Verdict is from the DNS provider.{preview}");
            }
            return;
        }

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

    private void AlwaysBlockSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.AlwaysBlockSearchQuery = AlwaysBlockSearchBox.Text;
    }

    private async void ClearAlwaysBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AlwaysBlockItems.Count == 0) return;

        if (!await RequestAuthAsync("Enter your password to delete all domains from Always Block"))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Delete all always blocked domains?",
            Content = $"Are you sure you want to remove all {ViewModel.Filter.AlwaysBlock.Count} domain(s) from the Always Block list? This action cannot be undone.",
            PrimaryButtonText = "Delete All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ClearAlwaysBlock();
            AlwaysBlockInfoBar.Severity = InfoBarSeverity.Success;
            AlwaysBlockInfoBar.Title = "Deleted";
            AlwaysBlockInfoBar.Message = "All domains have been removed from the Always Block list.";
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

    private void AlwaysAllowSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.AlwaysAllowSearchQuery = AlwaysAllowSearchBox.Text;
    }

    private async void ClearAlwaysAllowButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AlwaysAllowItems.Count == 0) return;

        if (!await RequestAuthAsync("Enter your password to delete all domains from Always Allow"))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Delete all always allowed domains?",
            Content = $"Are you sure you want to remove all {ViewModel.Filter.AlwaysAllow.Count} domain(s) from the Always Allow list? This action cannot be undone.",
            PrimaryButtonText = "Delete All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ClearAlwaysAllow();
            AlwaysAllowInfoBar.Severity = InfoBarSeverity.Success;
            AlwaysAllowInfoBar.Title = "Deleted";
            AlwaysAllowInfoBar.Message = "All domains have been removed from the Always Allow list.";
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

    // ── Activity Tab ──────────────────────────────────────────────────────────

    private void InitializeActivityControls()
    {
        var filter = ServiceContainer.Settings.WebFilter;
        LogBlockedToggle.IsOn = filter.LogBlocked;
        LogAllowedToggle.IsOn = filter.LogAllowed;
        NotifyOnBlockToggle.IsOn = filter.NotifyOnBlock;

        Retain7Days.IsChecked = filter.LogRetentionDays == 7;
        Retain90Days.IsChecked = filter.LogRetentionDays == 90;
        Retain30Days.IsChecked = filter.LogRetentionDays != 7 && filter.LogRetentionDays != 90;

        // Start a 500 ms batch-refresh timer when Activity tab goes live
        _activityRefreshTimer = new System.Threading.Timer(_ =>
        {
            DispatcherQueue.TryEnqueue(async () => await RefreshActivityUiAsync());
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        ServiceContainer.WebFilterLogService.LogChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        _activityRefreshTimer?.Change(400, System.Threading.Timeout.Infinite);
    }

    private async Task RefreshActivityUiAsync()
    {
        var logSvc = ServiceContainer.WebFilterLogService;
        var stats = await logSvc.GetStatsAsync().ConfigureAwait(true);
        BlockedTodayNumber.Text = stats.BlockedToday.ToString("N0");
        BlockedWeekNumber.Text = stats.BlockedThisWeek.ToString("N0");
        TopCategoryText.Text = stats.TopBlockedCategory;

        await ViewModel.Activity.RefreshAsync().ConfigureAwait(true);
        UpdateActivityPagingUi();

        var top = await logSvc.GetTopBlockedDomainsAsync(7, 10).ConfigureAwait(true);
        var topRows = top.Select(t => new TopDomainRowViewModel(t)).ToList();
        TopDomainsList.ItemsSource = topRows;
        NoTopDomainsText.Visibility = topRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActivityPagingUi()
    {
        BlockedList.ItemsSource = null;
        BlockedList.ItemsSource = ViewModel.Activity.RecentBlocked;
        ActivityPrevPageButton.IsEnabled = ViewModel.Activity.CanGoPrevious;
        ActivityNextPageButton.IsEnabled = ViewModel.Activity.CanGoNext;
        ActivityPageSummaryText.Text = ViewModel.Activity.PageSummary;
        NoBlockedText.Visibility = ViewModel.Activity.TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilterToday_Click(object sender, RoutedEventArgs e)
    {
        FilterTodayButton.Style = (Style)Resources["SegmentedTabButtonActive"];
        FilterAllButton.Style = (Style)Resources["SegmentedTabButtonInactive"];
        ActivityDatePicker.Date = null;
        ViewModel.Activity.SetDateFilterMode("today");
        UpdateActivityPagingUi();
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        FilterAllButton.Style = (Style)Resources["SegmentedTabButtonActive"];
        FilterTodayButton.Style = (Style)Resources["SegmentedTabButtonInactive"];
        ActivityDatePicker.Date = null;
        ViewModel.Activity.SetDateFilterMode("all");
        UpdateActivityPagingUi();
    }

    private void ActivityDatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (args.NewDate.HasValue)
        {
            FilterTodayButton.Style = (Style)Resources["SegmentedTabButtonInactive"];
            FilterAllButton.Style = (Style)Resources["SegmentedTabButtonInactive"];
            ViewModel.Activity.SetDateFilterMode("custom", args.NewDate.Value);
            UpdateActivityPagingUi();
        }
    }

    private void SortActivityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortActivityComboBox == null) return;
        ViewModel.Activity.SetSortOption(SortActivityComboBox.SelectedIndex);
        UpdateActivityPagingUi();
    }

    private void ActivityPrevPage_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Activity.PreviousPage();
        UpdateActivityPagingUi();
    }

    private void ActivityNextPage_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Activity.NextPage();
        UpdateActivityPagingUi();
    }

    private async void RefreshActivity_Click(object sender, RoutedEventArgs e)
    {
        await RefreshActivityUiAsync();
    }

    private void LogBlocked_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        ServiceContainer.Settings.WebFilter.LogBlocked = ts.IsOn;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private void LogAllowed_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        ServiceContainer.Settings.WebFilter.LogAllowed = ts.IsOn;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private void LogRetention_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (!int.TryParse(tag, out int days)) return;
        ServiceContainer.Settings.WebFilter.LogRetentionDays = days;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        _ = ServiceContainer.WebFilterLogService.PruneOldEntriesAsync();
    }

    private void NotifyOnBlock_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        ServiceContainer.Settings.WebFilter.NotifyOnBlock = ts.IsOn;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private async void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (!await RequestAuthAsync("Enter your password to clear the activity log")) return;
        await ServiceContainer.WebFilterLogService.ClearLogAsync();
        await RefreshActivityUiAsync();
    }

    private async Task ApplyCheckTempAllowAsync(int minutes, string durationLabel, string domain)
    {
        if (!await RequestAuthAsync($"Enter your password to temporarily allow {domain} for {durationLabel}")) return;
        if (ViewModel.AddTempAllow(domain, minutes, out var host, out var expiresUtc))
        {
            ShowCheckResult(InfoBarSeverity.Success, "Temporarily Allowed",
                $"{host} is allowed until {expiresUtc.ToLocalTime():HH:mm} ({durationLabel}).");
        }
    }

    private void LogRow_AllowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LogRowViewModel row }) return;

        var flyout = new MenuFlyout();

        var allow15 = new MenuFlyoutItem { Text = "Allow for 15 min" };
        allow15.Click += async (s, _) => await ApplyCheckTempAllowAsync(15, "15 minutes", row.Domain);

        var allow1h = new MenuFlyoutItem { Text = "Allow for 1 hour" };
        allow1h.Click += async (s, _) => await ApplyCheckTempAllowAsync(60, "1 hour", row.Domain);

        var alwaysAllow = new MenuFlyoutItem { Text = "Add to always allow" };
        alwaysAllow.Click += async (s, _) =>
        {
            if (!await RequestAuthAsync($"Enter your password to always allow {row.Domain}")) return;
            ViewModel.AddAlwaysAllow(row.Domain, "Allowed from activity log", out string _, out bool _);
        };

        var copy = new MenuFlyoutItem { Text = "Copy domain" };
        copy.Click += (s, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(row.Domain);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        };

        flyout.Items.Add(allow15);
        flyout.Items.Add(allow1h);
        flyout.Items.Add(alwaysAllow);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(copy);

        if (sender is FrameworkElement fe)
            flyout.ShowAt(fe);
    }

    // ── Bypass Protection Toggles (M9) ────────────────────────────────────────

    private async void BlockOtherDns_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        if (ts.IsOn == ViewModel.BlockOtherDns) return;

        if (!ts.IsOn && ViewModel.IsEnabled)
        {
            if (!await RequestAuthAsync("Enter your password to disable DNS server blocking"))
            {
                ts.IsOn = true;
                return;
            }
        }

        ViewModel.BlockOtherDns = ts.IsOn;
    }

    private async void BlockKnownDoh_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        if (ts.IsOn == ViewModel.BlockKnownDoh) return;

        if (!ts.IsOn && ViewModel.IsEnabled)
        {
            if (!await RequestAuthAsync("Enter your password to disable public DoH blocking"))
            {
                ts.IsOn = true;
                return;
            }
        }

        ViewModel.BlockKnownDoh = ts.IsOn;
    }

    private async void BrowserPolicies_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        if (ts.IsOn == ViewModel.BrowserPolicies) return;

        if (!ts.IsOn && ViewModel.IsEnabled)
        {
            if (!await RequestAuthAsync("Enter your password to disable browser Secure DNS policies"))
            {
                ts.IsOn = true;
                return;
            }
        }

        ViewModel.BrowserPolicies = ts.IsOn;
    }

    // ── Helper functions for XAML bindings (M10) ─────────────────────────────

    public Visibility GetProviderOptionsVisibility(bool isProvider) => isProvider ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetLocalOptionsVisibility(bool isLocal) => isLocal ? Visibility.Visible : Visibility.Collapsed;

    public string GetTabToolTip(bool isLocal) => isLocal ? string.Empty : "Available in Local filter mode";

    // ── Milestone 10: Filtering Mode & Provider Selection ────────────────────

    private bool _isUpdatingModeUi;

    private void InitializeSettingsControls()
    {
        _isUpdatingModeUi = true;
        try
        {
            LocalFilterModeRadio.IsChecked = ViewModel.IsLocalMode;
            ProviderModeRadio.IsChecked = ViewModel.IsProviderMode;

            string pid = ViewModel.Filter.ProviderId;
            CloudflareFamilyRadio.IsChecked = string.Equals(pid, "cloudflare-family", StringComparison.OrdinalIgnoreCase);
            CleanBrowsingFamilyRadio.IsChecked = string.Equals(pid, "cleanbrowsing-family", StringComparison.OrdinalIgnoreCase);
            CleanBrowsingAdultRadio.IsChecked = string.Equals(pid, "cleanbrowsing-adult", StringComparison.OrdinalIgnoreCase);
            OpenDnsFamilyRadio.IsChecked = string.Equals(pid, "opendns-familyshield", StringComparison.OrdinalIgnoreCase);
            AdGuardFamilyRadio.IsChecked = string.Equals(pid, "adguard-family", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _isUpdatingModeUi = false;
        }
    }

    private async void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingModeUi) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        var targetMode = tag == "provider" ? WebFilterMode.Provider : WebFilterMode.LocalFilter;
        if (targetMode == ViewModel.Filter.Mode) return;

        bool switched = await ViewModel.SetModeAsync(targetMode, () =>
            RequestAuthAsync($"Enter your password to switch to {(targetMode == WebFilterMode.Provider ? "Filtered DNS provider" : "Local filter")} mode"));

        if (!switched)
        {
            _isUpdatingModeUi = true;
            try
            {
                LocalFilterModeRadio.IsChecked = ViewModel.IsLocalMode;
                ProviderModeRadio.IsChecked = ViewModel.IsProviderMode;
            }
            finally
            {
                _isUpdatingModeUi = false;
            }
            return;
        }

        if (ViewModel.IsProviderMode)
        {
            ShowTab("settings");
        }

        PopulateStatus();
    }

    private async void ProviderRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingModeUi) return;
        if (sender is not RadioButton rb || rb.Tag is not string providerId) return;

        await ViewModel.SetProviderAsync(providerId);
        PopulateStatus();
    }

    private void UpstreamComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UpstreamComboBox == null) return;
        ViewModel.UpstreamIndex = UpstreamComboBox.SelectedIndex;
    }

    private async void TestUpstreamButton_Click(object sender, RoutedEventArgs e)
    {
        TestUpstreamResultText.Visibility = Visibility.Visible;
        TestUpstreamResultText.Text = "Testing upstream DNS...";
        string result = await ViewModel.TestUpstreamAsync();
        TestUpstreamResultText.Text = result;
    }

    private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearCache();
        ServiceContainer.NotificationService.ShowSuccess("Web Filter", "DNS cache cleared.");
    }

    private async void UpdateListsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateListsButton.IsEnabled = false;
        UpdateListsResultText.Visibility = Visibility.Visible;
        UpdateListsResultText.Text = "Checking for list updates...";

        try
        {
            int updated = await ViewModel.UpdateListsAsync();
            UpdateListsResultText.Text = updated > 0
                ? $"Successfully updated {updated} list(s)."
                : "All block lists are already up to date.";
        }
        catch (Exception ex)
        {
            UpdateListsResultText.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            UpdateListsButton.IsEnabled = true;
        }
    }

    private async void CreditsAndLicenses_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AppLock.Views.WebFilter.Dialogs.CreditsAndLicensesDialog(ViewModel.Catalog, XamlRoot);
        await dialog.ShowAsync();
    }
}
