using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.ScreenTime;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace AppLock.Views;

/// <summary>
/// Section 2 of the Screen Time spec: range bar, summary cards, chart, app list and status line.
/// All actions that weaken a limit go through the existing password/Hello verification.
/// </summary>
public sealed partial class ScreenTimeTab : UserControl
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _consentChecked;

    public ScreenTimeViewModel ViewModel { get; }

    public ScreenTimeTab()
    {
        ViewModel = new ScreenTimeViewModel();
        InitializeComponent();

        ViewModel.ChartChanged += (_, _) => UpdateChart();
        BarChart.DayClicked += (_, day) =>
        {
            ViewModel.SelectDay(day);
            RefreshView();
        };

        Loaded += (_, _) =>
        {
            RefreshView();
            _refreshTimer.Start();
        };

        Unloaded += (_, _) => _refreshTimer.Stop();
        _refreshTimer.Tick += (_, _) =>
        {
            if (IsLoaded) RefreshView();
        };
    }

    /// <summary>Called when the Protection page selects the Screen Time tab.</summary>
    public async Task OnActivatedAsync()
    {
        RefreshView();
        _refreshTimer.Start();

        if (ViewModel.Settings.ConsentShown || _consentChecked || XamlRoot == null)
            return;

        _consentChecked = true;
        await ShowConsentAsync();
        RefreshView();
    }

    private void RefreshView()
    {
        ViewModel.Refresh();
        UpdateChart();
    }

    private void UpdateChart()
    {
        bool month = ViewModel.IsMonthView;
        BarChart.Visibility = month ? Visibility.Collapsed : Visibility.Visible;
        MonthHeat.Visibility = month ? Visibility.Visible : Visibility.Collapsed;

        ChartSubtitle.Text = ViewModel.RangeKind switch
        {
            UsageRangeKind.Today => "Hours of the day",
            UsageRangeKind.Week => "Daily total",
            _ => "Click a day to open it"
        };

        if (month)
            MonthHeat.SetDays(ViewModel.MonthDays, DateOnly.FromDateTime(DateTime.Now));
        else
            BarChart.SetBars(ViewModel.Bars, ViewModel.ChartMax, ViewModel.AverageLine);

        DonutChart.SetSlices(ViewModel.DonutSlices, ViewModel.DonutCenterValue, ViewModel.DonutCenterLabel);

        NextButton.IsEnabled = ViewModel.CanGoNext;
        ShowHiddenMenuItem.IsChecked = ViewModel.ShowHidden;

        TrackingOffBar.IsOpen = !ViewModel.IsTrackingEnabled;
        ProtectionOffBar.IsOpen = !ViewModel.IsProtectionEnabled;

        MostUsedTile.Background = UsagePalette.BrushFor(ViewModel.MostUsedColorIndex);
        MostUsedInitial.Text = string.IsNullOrEmpty(ViewModel.MostUsedName) || ViewModel.MostUsedName == "—"
            ? "—"
            : ViewModel.MostUsedName.Substring(0, 1).ToUpperInvariant();
    }

    // ── Range bar ─────────────────────────────────────────────────────────────

    private void RangeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        ViewModel.SetRange((UsageRangeKind)Math.Clamp(RangeSelector.SelectedIndex, 0, 2));
        RefreshView();
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Shift(-1);
        RefreshView();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Shift(1);
        RefreshView();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ViewModel.SearchText = SearchBox.Text;

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowAll();
        UpdateChart();
    }

    private void ShowHidden_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleShowHidden();
        UpdateChart();
    }

    // ── Consent and settings ──────────────────────────────────────────────────

    private async Task ShowConsentAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Screen Time",
            Content = new TextBlock
            {
                Text = "AppLock can record which app is in front and for how long, so you can see your screen time and set limits. " +
                       "This is stored only on this PC. Window titles and website addresses are not recorded.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            },
            PrimaryButtonText = "Turn on",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ServiceContainer.CurrentTheme
        };

        var result = await dialog.ShowAsync();

        var settings = ViewModel.Settings;
        settings.ConsentShown = true;
        settings.TrackingEnabled = result == ContentDialogResult.Primary;
        ServiceContainer.ScreenTimeService.Save();

        if (result == ContentDialogResult.Primary)
            ServiceContainer.NotificationService.ShowSuccess("Screen Time", "Tracking is on. Only the app in front counts, and only while you are active.");
    }

    private void TurnOnTracking_Click(object sender, RoutedEventArgs e)
    {
        var settings = ViewModel.Settings;
        settings.TrackingEnabled = true;
        settings.ConsentShown = true;
        ServiceContainer.ScreenTimeService.Save();
        RefreshView();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
        => await ShowSettingsAsync();

    private async Task ShowSettingsAsync()
    {
        var settings = ViewModel.Settings;
        int oldRetention = settings.RetentionDays;
        bool oldTitles = settings.RememberWindowTitles;

        var tracking = new ToggleSwitch
        {
            Header = "Tracking",
            OnContent = "On",
            OffContent = "Off",
            IsOn = settings.TrackingEnabled
        };

        var idleOptions = new (int Seconds, string Label)[]
        {
            (30, "30 seconds"), (60, "1 minute"), (120, "2 minutes"),
            (300, "5 minutes"), (600, "10 minutes"), (900, "15 minutes")
        };
        var idle = new ComboBox
        {
            Header = "Count as idle after",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var option in idleOptions) idle.Items.Add(option.Label);
        idle.SelectedIndex = Math.Max(0, Array.FindIndex(idleOptions, o => o.Seconds == settings.EffectiveIdlePauseSeconds));

        var media = new ToggleSwitch
        {
            Header = "Keep watching videos and listening as active",
            OnContent = "On",
            OffContent = "Off",
            IsOn = settings.CountWhileMediaPlaying
        };

        var retentionOptions = new (int Days, string Label)[]
        {
            (30, "30 days"), (90, "90 days"), (365, "1 year"), (0, "Forever")
        };
        var retention = new ComboBox
        {
            Header = "Keep history for",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var option in retentionOptions) retention.Items.Add(option.Label);
        retention.SelectedIndex = Math.Max(0, Array.FindIndex(retentionOptions, o => o.Days == settings.RetentionDays));

        var titles = new ToggleSwitch
        {
            Header = "Remember window titles",
            OnContent = "On",
            OffContent = "Off",
            IsOn = settings.RememberWindowTitles
        };

        var warnings = new ToggleSwitch
        {
            Header = "Limit warnings",
            OnContent = "On",
            OffContent = "Off",
            IsOn = settings.LimitWarningsEnabled
        };

        var startWithWindows = new ToggleSwitch
        {
            Header = "Start AppLock with Windows",
            OnContent = "On",
            OffContent = "Off",
            IsOn = SafeStartupEnabled()
        };

        var note = new TextBlock
        {
            Text = "Screen time is only recorded while AppLock is running. Titles can contain private information such as document names; " +
                   "they are stored only on this PC. The idle setting is also used by daily limits.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = UsagePalette.SecondaryText
        };

        var panel = new StackPanel { Spacing = 14, MinWidth = 360 };
        panel.Children.Add(tracking);
        panel.Children.Add(idle);
        panel.Children.Add(media);
        panel.Children.Add(retention);
        panel.Children.Add(titles);
        panel.Children.Add(warnings);
        panel.Children.Add(startWithWindows);
        panel.Children.Add(note);

        var dialog = new ContentDialog
        {
            Title = "Screen Time settings",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ServiceContainer.CurrentTheme
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        int newRetention = retentionOptions[Math.Clamp(retention.SelectedIndex, 0, retentionOptions.Length - 1)].Days;
        bool newTitles = titles.IsOn;

        bool shortening = newRetention > 0 && (oldRetention == 0 || newRetention < oldRetention);
        bool deletingTitles = oldTitles && !newTitles;

        if (shortening || deletingTitles)
        {
            if (!await EnsureAuthAsync("Confirm shortening your screen time history"))
                return;
        }

        settings.TrackingEnabled = tracking.IsOn;
        settings.ConsentShown = true;
        settings.IdlePauseSeconds = idleOptions[Math.Clamp(idle.SelectedIndex, 0, idleOptions.Length - 1)].Seconds;
        settings.CountWhileMediaPlaying = media.IsOn;
        settings.RetentionDays = newRetention;
        settings.RememberWindowTitles = newTitles;
        settings.LimitWarningsEnabled = warnings.IsOn;

        ServiceContainer.ScreenTimeService.Save();

        if (deletingTitles)
            ServiceContainer.ScreenTimeService.Store.ClearTitles();

        if (shortening)
            ServiceContainer.ScreenTimeService.Store.Prune(newRetention);

        try
        {
            if (SafeStartupEnabled() != startWithWindows.IsOn)
                ServiceContainer.StartupService.SetStartupEnabled(startWithWindows.IsOn);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenTime] start-with-Windows change failed: {ex.Message}");
        }

        RefreshView();
    }

    private static bool SafeStartupEnabled()
    {
        try
        {
            return ServiceContainer.StartupService.IsStartupEnabled();
        }
        catch
        {
            return false;
        }
    }

    // ── App row actions ───────────────────────────────────────────────────────

    private async void AppList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppUsageRowViewModel row)
            await ShowDetailAsync(row);
    }

    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AppUsageRowViewModel row } button)
            return;

        var flyout = new MenuFlyout();

        var limitItem = new MenuFlyoutItem { Text = row.HasLimit ? "Edit limit…" : "Set limit…" };
        limitItem.Click += async (_, _) => await ShowLimitDialogAsync(row);
        flyout.Items.Add(limitItem);

        if (row.HasLimit)
        {
            var removeItem = new MenuFlyoutItem { Text = "Remove limit" };
            removeItem.Click += async (_, _) => await RemoveLimitAsync(row);
            flyout.Items.Add(removeItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var lockItem = new MenuFlyoutItem { Text = "Lock this app" };
        lockItem.Click += (_, _) => LockThisApp(row);
        flyout.Items.Add(lockItem);

        var hideItem = new MenuFlyoutItem { Text = row.IsHidden ? "Show in lists" : "Hide from lists" };
        hideItem.Click += (_, _) => ToggleHide(row);
        flyout.Items.Add(hideItem);

        var excludeItem = new MenuFlyoutItem
        {
            Text = "Exclude from tracking",
            IsEnabled = row.CanExclude
        };
        ToolTipService.SetToolTip(excludeItem, row.CanExclude
            ? "Stop recording this app. It will not count toward limits either."
            : "Remove the limit first");
        excludeItem.Click += (_, _) => ExcludeFromTracking(row);
        flyout.Items.Add(excludeItem);

        flyout.ShowAt(button);
    }

    private async Task ShowDetailAsync(AppUsageRowViewModel row)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var week = ServiceContainer.ScreenTimeService.Store.GetDayTotals(today.AddDays(-6), today);

        var lines = new StackPanel { Spacing = 6 };
        lines.Children.Add(new TextBlock
        {
            Text = $"{UsageFormat.Duration(row.Seconds)} in the selected range · {row.OpensText}",
            FontSize = 14
        });
        if (row.HasLimit)
        {
            lines.Children.Add(new TextBlock
            {
                Text = $"Daily limit {row.LimitText} · {row.LimitStateText}",
                Foreground = row.LimitBrush
            });
        }

        lines.Children.Add(new TextBlock
        {
            Text = "Last 7 days",
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = UsagePalette.SecondaryText
        });

        foreach (var day in week)
        {
            lines.Children.Add(new TextBlock
            {
                Text = $"{day.Day:ddd d MMM} · {(day.Seconds > 0 ? UsageFormat.Duration(day.Seconds) : "No data")}",
                FontSize = 12
            });
        }

        var dialog = new ContentDialog
        {
            Title = row.DisplayName,
            Content = lines,
            PrimaryButtonText = row.HasLimit ? "Edit limit" : "Set limit…",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ServiceContainer.CurrentTheme
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ShowLimitDialogAsync(row);
    }

    private async Task ShowLimitDialogAsync(AppUsageRowViewModel row)
    {
        int? current = row.HasLimit ? row.LimitMinutes : null;

        var numberBox = new NumberBox
        {
            Header = "Minutes per day",
            Minimum = 1,
            Maximum = 1440,
            Value = current ?? 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (int minutes in new[] { 15, 30, 60, 120, 180 })
        {
            var preset = new Button
            {
                Content = minutes < 60 ? $"{minutes} m" : $"{minutes / 60} h",
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 0
            };
            preset.Click += (_, _) => numberBox.Value = minutes;
            presets.Children.Add(preset);
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(presets);
        panel.Children.Add(numberBox);
        panel.Children.Add(new TextBlock
        {
            Text = $"When the limit is reached, {row.DisplayName} is locked until you enter the AppLock password. " +
                   "Only time in front, while you are active, is counted.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = UsagePalette.SecondaryText
        });

        var more = new HyperlinkButton
        {
            Content = "More options (schedule, idle relock)…",
            Padding = new Thickness(0),
            Visibility = row.Rule != null ? Visibility.Visible : Visibility.Collapsed
        };

        var dialog = new ContentDialog
        {
            Title = $"Daily limit for {row.DisplayName}",
            Content = panel,
            PrimaryButtonText = "Save limit",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ServiceContainer.CurrentTheme
        };

        more.Click += async (_, _) =>
        {
            dialog.Hide();
            if (row.Rule == null) return;

            await new SelfDisciplineDialog(row.Rule)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ServiceContainer.CurrentTheme
            }.ShowAsync();
            RefreshView();
        };
        panel.Children.Add(more);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (double.IsNaN(numberBox.Value) || numberBox.Value < 1 || numberBox.Value > 1440)
        {
            await ShowMessageAsync("Invalid limit", "The daily limit must be between 1 and 1440 minutes.");
            return;
        }

        int minutesValue = (int)numberBox.Value;
        var rule = row.Rule ?? CreateRuleFor(row, LockRuleMode.LimitOnly);

        if (LimitPolicy.RequiresAuth(current, minutesValue))
        {
            if (LimitPolicy.IsBlockedDuringSchedule(rule, DateTime.Now))
            {
                await ShowMessageAsync("Limit is active",
                    "This app is inside a scheduled lock period, so its limit cannot be weakened right now.");
                return;
            }

            if (IsLimitReached(rule))
            {
                await ShowMessageAsync("App is locked",
                    "This app is currently locked by its daily limit. Unlock it before raising or removing the limit.");
                return;
            }

            if (!await EnsureAuthAsync("Confirm raising the daily limit"))
                return;
        }

        bool wasEnabled = rule.SelfDiscipline.QuotaEnabled;
        rule.SelfDiscipline.QuotaEnabled = true;
        rule.SelfDiscipline.DailyLimitMinutes = minutesValue;

        if (!wasEnabled)
        {
            rule.SelfDiscipline.QuotaDate = DateOnly.FromDateTime(DateTime.Now);
            rule.SelfDiscipline.UsedSecondsToday = 0;
            rule.SelfDiscipline.ExtraSecondsGrantedToday = 0;
        }

        SaveRule(rule);
        ServiceContainer.NotificationService.ShowSuccess(
            row.DisplayName,
            $"Daily limit set to {UsageFormat.Duration(minutesValue * 60L)}. It locks when the limit is reached.");
        RefreshView();
    }

    private async Task RemoveLimitAsync(AppUsageRowViewModel row)
    {
        var rule = row.Rule;
        if (rule == null || !rule.SelfDiscipline.QuotaEnabled) return;

        if (LimitPolicy.IsBlockedDuringSchedule(rule, DateTime.Now))
        {
            await ShowMessageAsync("Limit is active",
                "This app is inside a scheduled lock period, so its limit cannot be removed right now.");
            return;
        }

        if (IsLimitReached(rule))
        {
            await ShowMessageAsync("App is locked",
                "This app is currently locked by its daily limit. Unlock it before removing the limit.");
            return;
        }

        if (!await EnsureAuthAsync("Confirm removing the daily limit"))
            return;

        rule.SelfDiscipline.QuotaEnabled = false;

        if (LimitPolicy.ShouldDeleteRuleOnRemoveLimit(rule))
        {
            ServiceContainer.LockManager.RemoveRule(rule.Id);
            ServiceContainer.NotificationService.ShowSuccess(row.DisplayName, "Daily limit removed; the limit-only rule was deleted.");
        }
        else
        {
            SaveRule(rule);
            ServiceContainer.NotificationService.ShowSuccess(row.DisplayName, "Daily limit removed.");
        }

        RefreshView();
    }

    private void LockThisApp(AppUsageRowViewModel row)
    {
        var rule = row.Rule ?? CreateRuleFor(row, LockRuleMode.AlwaysLocked);
        rule.Mode = LockRuleMode.AlwaysLocked;
        SaveRule(rule);
        ServiceContainer.NotificationService.ShowSuccess(
            row.DisplayName,
            "This app is now in Protected Applications and locks when opened.");
        RefreshView();
    }

    private void ToggleHide(AppUsageRowViewModel row)
    {
        var settings = ViewModel.Settings;
        var hidden = settings.HiddenAppKeys;

        if (row.IsHidden)
            hidden.RemoveAll(k => string.Equals(k, row.AppKey, StringComparison.OrdinalIgnoreCase));
        else if (!hidden.Any(k => string.Equals(k, row.AppKey, StringComparison.OrdinalIgnoreCase)))
            hidden.Add(row.AppKey);

        ServiceContainer.ScreenTimeService.Save();
        RefreshView();
    }

    private void ExcludeFromTracking(AppUsageRowViewModel row)
    {
        if (!row.CanExclude)
        {
            ServiceContainer.NotificationService.ShowWarning(row.DisplayName, "Remove the daily limit before excluding this app.");
            return;
        }

        var settings = ViewModel.Settings;
        if (!settings.ExcludedAppKeys.Any(k => string.Equals(k, row.AppKey, StringComparison.OrdinalIgnoreCase)))
            settings.ExcludedAppKeys.Add(row.AppKey);

        ServiceContainer.ScreenTimeService.Save();
        ServiceContainer.NotificationService.ShowInfo(row.DisplayName, "Screen Time no longer records this app.");
        RefreshView();
    }

    private LockRule CreateRuleFor(AppUsageRowViewModel row, LockRuleMode mode)
    {
        string processName = !string.IsNullOrEmpty(row.ExePath)
            ? Path.GetFileNameWithoutExtension(row.ExePath)
            : row.DisplayName;

        var rule = new LockRule
        {
            Type = LockRuleType.ProcessName,
            Mode = mode,
            ProcessName = processName,
            ProcessPath = row.ExePath ?? string.Empty,
            DisplayName = row.DisplayName,
            AppKey = row.AppKey
        };
        rule.SelfDiscipline.QuotaDate = DateOnly.FromDateTime(DateTime.Now);

        ServiceContainer.LockManager.AddRule(rule);
        return rule;
    }

    private static bool IsLimitReached(LockRule rule)
    {
        var sd = rule.SelfDiscipline;
        if (!sd.QuotaEnabled) return false;
        if (sd.QuotaDate != DateOnly.FromDateTime(DateTime.Now)) return false;

        int limit = Math.Max(1, sd.DailyLimitMinutes) * 60 + Math.Max(0, sd.ExtraSecondsGrantedToday);
        return sd.UsedSecondsToday >= limit;
    }

    private static void SaveRule(LockRule rule)
    {
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        rule.NotifySelfDisciplineChanged();
    }

    // ── Maintenance ───────────────────────────────────────────────────────────

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var range = new UsageRange(ViewModel.RangeKind, ViewModel.AnchorDate);
            string suggested = $"screen-time-{range.From:yyyy-MM-dd}-to-{range.To:yyyy-MM-dd}.csv";

            string? path = NativeFileDialog.Save(
                "Export screen time",
                "CSV files (*.csv)\0*.csv\0All files (*.*)\0*.*\0",
                "csv",
                suggested);

            if (string.IsNullOrEmpty(path)) return;

            var hidden = ViewModel.Settings.HiddenAppKeys;
            string csv = ServiceContainer.ScreenTimeService.Store.ExportCsv(
                range.From, range.To, ViewModel.ShowHidden ? null : hidden);

            File.WriteAllText(path, csv);
            ServiceContainer.NotificationService.ShowSuccess("Screen time exported", path);
        }
        catch (Exception ex)
        {
            ServiceContainer.NotificationService.ShowError("Export failed", ex.Message);
        }
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Clear all screen time data?",
            Content = new TextBlock
            {
                Text = "This deletes the recorded history on this PC. It does not reset today's limits or any quota; those stay in the protected settings.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = "Clear data",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ServiceContainer.CurrentTheme
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (!await EnsureAuthAsync("Confirm clearing screen time data"))
            return;

        ServiceContainer.ScreenTimeService.Store.ClearAll();
        RefreshView();
        ServiceContainer.NotificationService.ShowSuccess("Screen time", "History cleared. Today's limits were not changed.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> EnsureAuthAsync(string reason)
    {
        try
        {
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ServiceContainer.CurrentTheme
            };
            await dialog.ShowAsync();
            return dialog.IsAuthenticated;
        }
        catch (Exception ex)
        {
            ServiceContainer.NotificationService.ShowError("Verification failed", ex.Message);
            return false;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
            RequestedTheme = ServiceContainer.CurrentTheme
        }.ShowAsync();
    }
}
