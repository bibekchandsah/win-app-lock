using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        }
        catch (Exception ex)
        {
            InlineErrorBar.Message = $"Failed to save settings: {ex.Message}";
            InlineErrorBar.IsOpen = true;
            args.Cancel = true;
        }
    }
}

