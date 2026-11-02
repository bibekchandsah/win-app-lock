using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AppLock.Views;

public sealed partial class SelfDisciplineDialog : ContentDialog
{
    private static int _lastUsedTabIndex = 0;

    private static readonly SolidColorBrush SelectedTabBg = new(Color.FromArgb(255, 0xD9, 0xA0, 0xE8)); // Lilac
    private static readonly SolidColorBrush SelectedTabFg = new(Color.FromArgb(255, 0x1A, 0x1A, 0x1A)); // Dark text
    private static readonly SolidColorBrush UnselectedTabBg = new(Color.FromArgb(0, 0, 0, 0));

    private readonly LockRule _rule;
    private readonly SelfDisciplineSettings _settingsCopy;
    private readonly List<TimeBlockCard> _cards = new();
    private bool _isHelloVerified = false;

    public SelfDisciplineDialog(LockRule rule)
    {
        InitializeComponent();
        _rule = rule;
        _settingsCopy = rule.SelfDiscipline.Clone();

        PrimaryButtonStyle = (Style)Resources["LilacPrimaryButtonStyle"];

        string appName = !string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.DisplayName : rule.ProcessName;
        TargetAppSubheader.Text = $"Settings for {appName}";

        LoadSettingsIntoUI();
        SelectTab(_lastUsedTabIndex);

        // Check Windows Hello availability
        CheckHelloAvailabilityAsync();

        // Live ring update while dialog is open
        QuotaTracker.QuotaUpdated += OnQuotaUpdated;
        Closed += (_, _) => QuotaTracker.QuotaUpdated -= OnQuotaUpdated;
    }

    private async void CheckHelloAvailabilityAsync()
    {
        try
        {
            bool available = await ServiceContainer.HelloService.IsAvailableAsync();
            HelloVerifyButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            HelloVerifyButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnQuotaUpdated(LockRule updatedRule)
    {
        if (updatedRule.Id != _rule.Id) return;
        // Copy the latest counter into our working copy so the ring reflects real usage
        _settingsCopy.UsedSecondsToday = updatedRule.SelfDiscipline.UsedSecondsToday;

        // Must update UI on the dispatcher thread
        DispatcherQueue.TryEnqueue(UpdateProgressRing);
    }

    private void LoadSettingsIntoUI()
    {
        // 1. Quota tab
        QuotaToggle.IsOn = _settingsCopy.QuotaEnabled;
        QuotaLimitNumberBox.Value = _settingsCopy.DailyLimitMinutes > 0 ? _settingsCopy.DailyLimitMinutes : 60;
        QuotaLimitNumberBox.IsEnabled = _settingsCopy.QuotaEnabled;
        UpdateProgressRing();

        // 2. Scheduled tab
        RefreshTimeBlocksUI();

        // 3. Idle tab
        IdleToggle.IsOn = _settingsCopy.IdleRelockEnabled;
        IdleLimitNumberBox.Value = _settingsCopy.IdleRelockMinutes > 0 ? _settingsCopy.IdleRelockMinutes : 15;
        IdleLimitNumberBox.IsEnabled = _settingsCopy.IdleRelockEnabled;
    }

    private void SelectTab(int index)
    {
        _lastUsedTabIndex = index;

        Tab0Panel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        Tab1Panel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Tab2Panel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        UpdateTabButtonStyle(TabButton0, index == 0);
        UpdateTabButtonStyle(TabButton1, index == 1);
        UpdateTabButtonStyle(TabButton2, index == 2);
    }

    private void UpdateTabButtonStyle(Button button, bool isSelected)
    {
        if (isSelected)
        {
            button.Background = SelectedTabBg;
            button.Foreground = SelectedTabFg;
            button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
        else
        {
            button.Background = UnselectedTabBg;
            button.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            button.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int tabIndex))
        {
            SelectTab(tabIndex);
        }
    }

    private void QuotaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        QuotaLimitNumberBox.IsEnabled = QuotaToggle.IsOn;
        UpdateProgressRing();
    }

    private void QuotaLimitNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateProgressRing();
    }

    private void UpdateProgressRing()
    {
        int usedSec = _settingsCopy.UsedSecondsToday;
        int hours = usedSec / 3600;
        int minutes = (usedSec % 3600) / 60;
        QuotaUsedTimeText.Text = $"{hours}:{minutes:D2}";

        double limitMin = double.IsNaN(QuotaLimitNumberBox.Value) || QuotaLimitNumberBox.Value <= 0
            ? 60.0
            : QuotaLimitNumberBox.Value;

        double usedMin = usedSec / 60.0;
        QuotaProgressRing.Maximum = limitMin;
        QuotaProgressRing.Value = usedMin;

        double ratio = limitMin > 0 ? (usedMin / limitMin) : 0;
        if (ratio >= 0.9)
        {
            QuotaProgressRing.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x9F, 0x0A)); // Warning amber
        }
        else
        {
            QuotaProgressRing.Foreground = SelectedTabBg; // Lilac
        }

        QuotaRingContainer.Opacity = QuotaToggle.IsOn ? 1.0 : 0.4;
    }

    private void RefreshTimeBlocksUI()
    {
        TimeBlocksContainer.Children.Clear();
        _cards.Clear();

        if (_settingsCopy.TimeBlocks == null || _settingsCopy.TimeBlocks.Count == 0)
        {
            ScheduledEmptyPanel.Visibility = Visibility.Visible;
            ScheduledBlocksPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ScheduledEmptyPanel.Visibility = Visibility.Collapsed;
        ScheduledBlocksPanel.Visibility = Visibility.Visible;

        foreach (var block in _settingsCopy.TimeBlocks)
        {
            var card = new TimeBlockCard(block);
            card.DeleteRequested += (s, e) =>
            {
                _settingsCopy.TimeBlocks.Remove(block);
                RefreshTimeBlocksUI();
            };
            _cards.Add(card);
            TimeBlocksContainer.Children.Add(card);
        }

        ListAddBlockButton.IsEnabled = _settingsCopy.TimeBlocks.Count < 10;
    }

    private void AddBlockButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsCopy.TimeBlocks ??= new();

        if (_settingsCopy.TimeBlocks.Count >= 10)
        {
            InlineErrorBar.Message = "Maximum of 10 time blocks reached.";
            InlineErrorBar.IsOpen = true;
            return;
        }

        InlineErrorBar.IsOpen = false;
        var newBlock = new TimeBlock
        {
            Start = new TimeOnly(20, 0),
            End = new TimeOnly(23, 0),
            Days = new bool[] { true, true, true, true, true, true, true }
        };

        _settingsCopy.TimeBlocks.Add(newBlock);
        RefreshTimeBlocksUI();
    }

    private void IdleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        IdleLimitNumberBox.IsEnabled = IdleToggle.IsOn;
    }

    private void AdminAuthPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (TryVerifyAdminPassword())
            {
                if (CommitAndSave())
                    Hide();
            }
        }
    }

    private async void HelloVerifyButton_Click(object sender, RoutedEventArgs e)
    {
        InlineErrorBar.IsOpen = false;
        try
        {
            bool ok = await ServiceContainer.HelloService.RequestVerificationAsync("Verify to weaken Self-Discipline settings");
            if (ok)
            {
                _isHelloVerified = true;
                WeakeningAuthPanel.Visibility = Visibility.Collapsed;
                if (CommitAndSave())
                    Hide();
            }
            else
            {
                InlineErrorBar.Message = "Windows Hello verification failed.";
                InlineErrorBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            InlineErrorBar.Message = $"Windows Hello error: {ex.Message}";
            InlineErrorBar.IsOpen = true;
        }
    }

    private bool TryVerifyAdminPassword()
    {
        InlineErrorBar.IsOpen = false;
        string pw = AdminAuthPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(pw))
        {
            InlineErrorBar.Message = "Please enter your admin password to confirm changes.";
            InlineErrorBar.IsOpen = true;
            AdminAuthPasswordBox.Focus(FocusState.Programmatic);
            return false;
        }

        if (ServiceContainer.AuthService.VerifyPassword(pw))
        {
            _isHelloVerified = true;
            WeakeningAuthPanel.Visibility = Visibility.Collapsed;
            return true;
        }
        else
        {
            InlineErrorBar.Message = "Incorrect admin password. Changes not saved.";
            InlineErrorBar.IsOpen = true;
            AdminAuthPasswordBox.SelectAll();
            AdminAuthPasswordBox.Focus(FocusState.Programmatic);
            return false;
        }
    }

    private bool IsWeakeningSettings()
    {
        var orig = _rule.SelfDiscipline;
        if (orig == null) return false;

        // 1. Quota weakening
        if (orig.QuotaEnabled && !QuotaToggle.IsOn)
            return true; // Disabling quota toggle

        if (orig.QuotaEnabled && QuotaToggle.IsOn && (int)QuotaLimitNumberBox.Value > orig.DailyLimitMinutes)
            return true; // Raising daily limit

        // 2. Idle weakening
        if (orig.IdleRelockEnabled && !IdleToggle.IsOn)
            return true; // Disabling idle toggle

        if (orig.IdleRelockEnabled && IdleToggle.IsOn && (int)IdleLimitNumberBox.Value > orig.IdleRelockMinutes)
            return true; // Raising idle relock timeout

        // 3. Time blocks (Scheduled lock) weakening
        int origCount = orig.TimeBlocks?.Count ?? 0;
        int currCount = _settingsCopy.TimeBlocks?.Count ?? 0;
        if (currCount < origCount)
            return true; // Deleted a time block

        if (orig.TimeBlocks != null && _settingsCopy.TimeBlocks != null)
        {
            foreach (var origBlock in orig.TimeBlocks)
            {
                var currBlock = _settingsCopy.TimeBlocks.FirstOrDefault(b => b.Id == origBlock.Id);
                if (currBlock == null)
                    return true; // Deleted this specific block

                // Fewer active days
                int origDays = origBlock.Days?.Count(d => d) ?? 0;
                int currDays = currBlock.Days?.Count(d => d) ?? 0;
                if (currDays < origDays)
                    return true;

                // Reduced duration
                int origMin = GetBlockDurationMinutes(origBlock);
                int currMin = GetBlockDurationMinutes(currBlock);
                if (currMin < origMin)
                    return true;
            }
        }

        return false;
    }

    private static int GetBlockDurationMinutes(TimeBlock block)
    {
        if (block.Start == block.End) return 0;
        if (block.Start < block.End)
            return (int)(block.End - block.Start).TotalMinutes;
        else
            return (int)((TimeSpan.FromHours(24) - block.Start.ToTimeSpan()) + block.End.ToTimeSpan()).TotalMinutes;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        InlineErrorBar.IsOpen = false;

        // 1. Validate Quota tab
        if (QuotaToggle.IsOn)
        {
            if (double.IsNaN(QuotaLimitNumberBox.Value) || QuotaLimitNumberBox.Value < 1 || QuotaLimitNumberBox.Value > 1440)
            {
                SelectTab(0);
                InlineErrorBar.Message = "Daily limit must be between 1 and 1440 minutes.";
                InlineErrorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
        }

        // 2. Validate Scheduled lock tab
        foreach (var card in _cards)
        {
            if (!card.Validate(out string cardError))
            {
                SelectTab(1);
                InlineErrorBar.Message = cardError;
                InlineErrorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
        }

        // 3. Validate Idle tab
        if (IdleToggle.IsOn)
        {
            if (double.IsNaN(IdleLimitNumberBox.Value) || IdleLimitNumberBox.Value < 1 || IdleLimitNumberBox.Value > 240)
            {
                SelectTab(2);
                InlineErrorBar.Message = "Auto-relock timeout must be between 1 and 240 minutes.";
                InlineErrorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
        }

        // 4. Check for weakening rules
        if (IsWeakeningSettings())
        {
            // Block weakening completely during an active schedule period
            if (ScheduleEvaluator.IsInsideSchedule(_rule, DateTime.Now, out var until))
            {
                InlineErrorBar.Message = $"Settings cannot be weakened during an active scheduled lock period (active until {until:HH:mm}).";
                InlineErrorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            // Require admin password verification
            if (!_isHelloVerified)
            {
                if (WeakeningAuthPanel.Visibility != Visibility.Visible)
                {
                    WeakeningAuthPanel.Visibility = Visibility.Visible;
                    AdminAuthPasswordBox.Password = string.Empty;
                    AdminAuthPasswordBox.Focus(FocusState.Programmatic);
                    args.Cancel = true;
                    return;
                }

                if (!TryVerifyAdminPassword())
                {
                    args.Cancel = true;
                    return;
                }
            }
        }

        if (!CommitAndSave())
        {
            args.Cancel = true;
        }
    }

    private bool CommitAndSave()
    {
        // Commit working copy
        _settingsCopy.QuotaEnabled = QuotaToggle.IsOn;
        _settingsCopy.DailyLimitMinutes = (int)QuotaLimitNumberBox.Value;
        _settingsCopy.IdleRelockEnabled = IdleToggle.IsOn;
        _settingsCopy.IdleRelockMinutes = (int)IdleLimitNumberBox.Value;

        _rule.SelfDiscipline = _settingsCopy;

        try
        {
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            _rule.NotifySelfDisciplineChanged();

            string appName = !string.IsNullOrWhiteSpace(_rule.DisplayName) ? _rule.DisplayName : _rule.ProcessName;
            ServiceContainer.NotificationService.ShowSuccess(appName, "Self-Discipline settings updated.");
            return true;
        }
        catch (Exception ex)
        {
            InlineErrorBar.Message = $"Failed to save settings: {ex.Message}";
            InlineErrorBar.IsOpen = true;
            return false;
        }
    }
}

