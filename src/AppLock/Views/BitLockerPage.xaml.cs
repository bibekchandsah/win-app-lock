using AppLock.Models;
using AppLock.Services.BitLocker;
using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
namespace AppLock.Views;
/// <summary>Converts bool to "Yes" / "No" string for the Saved Password indicator in the details pane.</summary>
public sealed class BoolToYesNoConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? "Yes" : "No";
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
public sealed partial class BitLockerPage : Page
{
    public BitLockerViewModel ViewModel { get; }
    private readonly DispatcherTimer _refreshTimer;
    private DispatcherTimer? _cooldownTimer;
    public BitLockerPage()
    {
        ViewModel = new BitLockerViewModel(
            ServiceContainer.BitLockerSupportService,
            ServiceContainer.BitLockerService,
            ServiceContainer.VolumeWatcher,
            ServiceContainer.Settings,
            settings => ServiceContainer.SettingsStore.Save(settings),
            ServiceContainer.SavedPasswordStore,
            ServiceContainer.StartupTaskService);
        InitializeComponent();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ViewModel.RefreshIntervalSeconds > 0 ? ViewModel.RefreshIntervalSeconds : 5)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        ViewModel.OnCooldownStarted = StartCooldownUiTimer;
        ViewModel.OnCooldownEnded = StopCooldownUiTimer;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }
    private void StartCooldownUiTimer()
    {
        _cooldownTimer?.Stop();
        _cooldownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _cooldownTimer.Tick += (s, e) =>
        {
            ViewModel.TickCooldown();
        };
        _cooldownTimer.Start();
    }
    private void StopCooldownUiTimer()
    {
        _cooldownTimer?.Stop();
        _cooldownTimer = null;
    }
    private void ConfigureDialog(ContentDialog dialog)
    {
        dialog.XamlRoot = this.XamlRoot;
        if (this.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            dialog.RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            dialog.RequestedTheme = ServiceContainer.CurrentTheme;
        }
    }

    private bool IsDarkTheme()
    {
        if (this.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            return rootFe.ActualTheme == ElementTheme.Dark;
        }
        var current = ServiceContainer.CurrentTheme;
        if (current != ElementTheme.Default)
        {
            return current == ElementTheme.Dark;
        }
        return Application.Current.RequestedTheme == ApplicationTheme.Dark;
    }

    private Brush GetCardBackgroundBrush() => GetCardBackgroundBrush(IsDarkTheme());
    private static Brush GetCardBackgroundBrush(bool isDark) =>
        isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 43, 43))       // #2B2B2B
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 249, 250));    // #F8F9FA

    private Brush GetCardBorderBrush() => GetCardBorderBrush(IsDarkTheme());
    private static Brush GetCardBorderBrush(bool isDark) =>
        isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 60))       // #3C3C3C
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 232, 240));   // #E2E8F0

    private Brush GetAccentBrush() => GetAccentBrush(IsDarkTheme());
    private static Brush GetAccentBrush(bool isDark) =>
        isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 217, 160, 232))    // #D9A0E8 (Lilac)
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 142, 68, 173));    // #8E44AD (Purple with high contrast)

    private Brush GetSuccessBrush() => GetSuccessBrush(IsDarkTheme());
    private static Brush GetSuccessBrush(bool isDark) =>
        isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95))     // #6CCB5F
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 134, 46));     // #16862E

    private Brush GetCautionBrush() => GetCautionBrush(IsDarkTheme());
    private static Brush GetCautionBrush(bool isDark) =>
        isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 252, 225, 0))      // #FCE100
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 157, 93, 0));      // #9D5D00

    public static string FormatTaskResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "N/A") return "Never run";
        var trimmed = raw.Trim();
        if (trimmed == "0" || trimmed == "0x0") return "0 (Successful)";
        if (trimmed == "1" || trimmed == "0x1") return "1 (Failed / Error)";
        if (trimmed == "2" || trimmed == "0x2") return "2 (File not found)";
        if (trimmed == "267009" || trimmed == "0x41301") return "Running";
        if (trimmed == "267011" || trimmed == "0x41303") return "Never run";
        if (trimmed == "267014" || trimmed == "0x41306") return "Terminated by user";
        if (trimmed == "-2147024891" || trimmed == "2147942405" || trimmed.Contains("80070005")) return "Access denied";
        return trimmed;
    }
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ElevationInfoBar.IsOpen = !ViewModel.IsElevated;
        ServiceContainer.VolumeWatcher.VolumeChanged += OnVolumeWatcherChanged;
        ServiceContainer.VolumeWatcher.Start();
        _refreshTimer.Start();
        _ = ViewModel.RefreshDrivesAsync();
    }
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        StopCooldownUiTimer();
        ServiceContainer.VolumeWatcher.VolumeChanged -= OnVolumeWatcherChanged;
    }
    private void RefreshTimer_Tick(object? sender, object e)
    {
        _ = ViewModel.RefreshDrivesAsync();
    }
    private void OnVolumeWatcherChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await ViewModel.RefreshDrivesAsync();
        });
    }
    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshDrivesAsync();
    }
    private void OpenExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        var driveLetter = ViewModel.SelectedDrive?.DriveLetter;
        if (!string.IsNullOrEmpty(driveLetter))
        {
            BitLockerViewModel.TryOpenDriveInExplorer(driveLetter);
        }
    }
    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null || !ViewModel.CanLockSelected) return;

        var stack = new StackPanel { Spacing = 12, MinWidth = 360 };
        stack.Children.Add(new TextBlock
        {
            Text = $"Are you sure you want to lock drive {drive.DisplayName}? Any open files or applications on this drive may be forced to close.",
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(CreateActiveTasksPanel(drive.DriveLetter));

        // Confirmation dialog
        var dialog = new ContentDialog
        {
            Title = $"Lock drive {drive.DriveLetter}?",
            Content = stack,
            PrimaryButtonText = "Lock (Force dismount)",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        ConfigureDialog(dialog);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.LockSelectedDriveAsync(forceDismount: true);
        }
    }

    private FrameworkElement CreateActiveTasksPanel(string driveLetter)
    {
        var card = new Border
        {
            Background = GetCardBackgroundBrush(),
            BorderBrush = GetCardBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10)
        };

        var rootStack = new StackPanel { Spacing = 8 };

        // Header Grid: Title on Left, Refresh Button on Right
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTitle = new TextBlock
        {
            Text = $"Active applications on {driveLetter}:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headerTitle, 0);
        headerGrid.Children.Add(headerTitle);

        var refreshBtn = new Button
        {
            Padding = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 12 }
        };
        ToolTipService.SetToolTip(refreshBtn, "Refresh running applications on this drive");
        AutomationProperties.SetName(refreshBtn, "Refresh running applications on this drive");
        Grid.SetColumn(refreshBtn, 1);
        headerGrid.Children.Add(refreshBtn);

        rootStack.Children.Add(headerGrid);

        // Process List Container
        var itemsStack = new StackPanel { Spacing = 4 };
        itemsStack.Children.Add(new TextBlock
        {
            Text = "Scanning active applications...",
            FontSize = 11,
            Opacity = 0.6,
            FontStyle = Windows.UI.Text.FontStyle.Italic
        });
        rootStack.Children.Add(itemsStack);
        card.Child = rootStack;

        async Task RefreshListAsync()
        {
            refreshBtn.IsEnabled = false;
            itemsStack.Children.Clear();
            itemsStack.Children.Add(new TextBlock
            {
                Text = "Scanning active applications...",
                FontSize = 11,
                Opacity = 0.6,
                FontStyle = Windows.UI.Text.FontStyle.Italic
            });

            var procs = await BitLockerService.GetProcessesOnDriveAsync(driveLetter);

            itemsStack.Children.Clear();
            if (procs.Count == 0)
            {
                headerTitle.Text = $"Active applications on {driveLetter} (0):";
                itemsStack.Children.Add(new TextBlock
                {
                    Text = "No running applications detected on this drive.",
                    FontSize = 12,
                    Opacity = 0.75
                });
            }
            else
            {
                headerTitle.Text = $"Active applications on {driveLetter} ({procs.Count}):";
                foreach (var p in procs.Take(8))
                {
                    itemsStack.Children.Add(new TextBlock
                    {
                        Text = $"• {p}",
                        FontSize = 12,
                        Opacity = 0.85
                    });
                }
                if (procs.Count > 8)
                {
                    itemsStack.Children.Add(new TextBlock
                    {
                        Text = $"...and {procs.Count - 8} more",
                        FontSize = 11,
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        Opacity = 0.6
                    });
                }
            }
            refreshBtn.IsEnabled = true;
        }

        refreshBtn.Click += async (s, e) => await RefreshListAsync();

        // Initial scan
        _ = RefreshListAsync();

        return card;
    }
    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null || !ViewModel.CanAttemptUnlock) return;
        // Prompt dialog to enter password or choose recovery key
        var passwordBox = new PasswordBox
        {
            PlaceholderText = "Enter BitLocker password",
            Width = 300
        };
        AutomationProperties.SetName(passwordBox, "Enter BitLocker password");
        var saveCheckBox = new CheckBox
        {
            Content = "Save password for this drive",
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetName(saveCheckBox, "Save password for this drive");
        ToolTipService.SetToolTip(saveCheckBox, "Password encrypted with DPAPI and stored locally");

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(passwordBox);
        panel.Children.Add(saveCheckBox);

        ContentDialog? dialog = null;

        if (ViewModel.HasSavedPasswordForSelected)
        {
            var useSavedBtn = new HyperlinkButton
            {
                Content = "Unlock with saved password",
                FontSize = 12,
                Padding = new Thickness(0)
            };
            AutomationProperties.SetName(useSavedBtn, "Unlock with saved password");
            useSavedBtn.Click += async (s, e) =>
            {
                dialog?.Hide();
                var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
                if (auth)
                {
                    await ViewModel.UnlockWithSavedPassphraseAsync();
                }
            };
            panel.Children.Add(useSavedBtn);
        }
        dialog = new ContentDialog
        {
            Title = $"Unlock drive {drive.DisplayName}",
            Content = panel,
            PrimaryButtonText = "Unlock",
            SecondaryButtonText = "Use recovery key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        ConfigureDialog(dialog);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var pwd = passwordBox.Password;
            var shouldSave = saveCheckBox.IsChecked == true;
            passwordBox.Password = string.Empty;
            if (!string.IsNullOrEmpty(pwd))
            {
                var unlockResult = await ViewModel.UnlockWithPassphraseAsync(pwd);
                if (unlockResult.Success && shouldSave)
                {
                    var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
                    if (auth)
                    {
                        if (ViewModel.NeedsSavedPasswordNotice)
                        {
                            var noticeDialog = new ContentDialog
                            {
                                Title = "BitLocker Saved Passwords Notice",
                                Content = new TextBlock
                                {
                                    Text = "Saving a password allows anyone using this Windows account to unlock this drive with one click.\n\nThe password is encrypted using Windows DPAPI specifically for your user profile and stored on this computer only.",
                                    TextWrapping = TextWrapping.Wrap
                                },
                                CloseButtonText = "I understand",
                                DefaultButton = ContentDialogButton.Close
                            };
                            ConfigureDialog(noticeDialog);
                            await noticeDialog.ShowAsync();
                            ViewModel.MarkSavedPasswordNoticeShown();
                        }
                        await ViewModel.SavePassphraseForSelectedAsync(pwd);
                    }
                }
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await ShowUnlockWithRecoveryKeyDialogAsync();
        }
    }
    private async void QuickUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanAttemptUnlock) return;
        var pwd = QuickUnlockPasswordBox.Password;
        QuickUnlockPasswordBox.Password = string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            ViewModel.StatusMessage = "Please enter a BitLocker password.";
            return;
        }
        var result = await ViewModel.UnlockWithPassphraseAsync(pwd);
        if (result.Success && SavePasswordCheckBox.IsChecked == true)
        {
            await ViewModel.SavePassphraseForSelectedAsync(pwd);
            SavePasswordCheckBox.IsChecked = false;
        }
    }
    private void QuickUnlockPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ViewModel.CanAttemptUnlock)
        {
            QuickUnlockButton_Click(sender, e);
        }
    }
    private async void UnlockWithRecoveryKey_Click(object sender, RoutedEventArgs e)
    {
        await ShowUnlockWithRecoveryKeyDialogAsync();
    }
    private async Task ShowUnlockWithRecoveryKeyDialogAsync()
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null) return;
        var keyBox = new TextBox
        {
            PlaceholderText = "48-digit numerical recovery password (e.g. 123456-123456-...)",
            Width = 360
        };
        AutomationProperties.SetName(keyBox, "48-digit numerical recovery key");
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Enter the 48-digit numerical BitLocker recovery key to unlock this drive:",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });
        panel.Children.Add(keyBox);
        var dialog = new ContentDialog
        {
            Title = $"Unlock {drive.DisplayName} with Recovery Key",
            Content = panel,
            PrimaryButtonText = "Unlock",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        ConfigureDialog(dialog);
        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            var key = keyBox.Text;
            keyBox.Text = string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                await ViewModel.UnlockWithRecoveryKeyAsync(key);
            }
        }
    }
    private async void UseSavedPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanAttemptUnlock) return;
        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth) return;

        var found = await ViewModel.UnlockWithSavedPassphraseAsync();
        if (!found)
        {
            ViewModel.StatusMessage = "No saved password found for this drive.";
        }
    }
    private async void SavePasswordCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        // Security gate check before enabling saved passwords (cached for 2 min)
        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth)
        {
            SavePasswordCheckBox.IsChecked = false;
            return;
        }
        // Section 5.7: One-time notice
        if (ViewModel.NeedsSavedPasswordNotice)
        {
            var noticeDialog = new ContentDialog
            {
                Title = "BitLocker Saved Passwords Notice",
                Content = new TextBlock
                {
                    Text = "Saving a password allows anyone using this Windows account to unlock this drive with one click.\n\nThe password is encrypted using Windows DPAPI specifically for your user profile and stored on this computer only.",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "I understand",
                DefaultButton = ContentDialogButton.Close
            };
            ConfigureDialog(noticeDialog);
            await noticeDialog.ShowAsync();
            ViewModel.MarkSavedPasswordNoticeShown();
        }
    }
    private async void ManageSavedPasswordsButton_Click(object sender, RoutedEventArgs e)
    {
        // Section 6: Security gate with 2-minute cache
        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth) return;
        await ShowManageSavedPasswordsDialogAsync();
    }
    private async Task ShowManageSavedPasswordsDialogAsync()
    {
        var passwords = await ViewModel.GetAllSavedPasswordsAsync();
        if (passwords.Count == 0)
        {
            var emptyDialog = new ContentDialog
            {
                Title = "Saved Passwords",
                Content = new TextBlock { Text = "No saved BitLocker passwords found.", TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close"
            };
            ConfigureDialog(emptyDialog);
            await emptyDialog.ShowAsync();
            return;
        }
        ContentDialog? manageDialog = null;
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{passwords.Count} saved password(s). Passwords are encrypted with DPAPI (viewing is not permitted):",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        foreach (var pwd in passwords)
        {
            var card = new Border
            {
                Background = GetCardBackgroundBrush(),
                BorderBrush = GetCardBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var infoStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var title = string.IsNullOrWhiteSpace(pwd.Label) ? $"Drive {pwd.DriveLetter}" : $"Drive {pwd.DriveLetter} ({pwd.Label})";
            infoStack.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 });
            var dateStr = pwd.SavedAt.ToLocalTime().ToString("g");
            var idDisplay = string.IsNullOrEmpty(pwd.VolumeGuid) ? "No GUID" : pwd.VolumeGuid;
            var subText = $"Saved: {dateStr} • {idDisplay}";
            infoStack.Children.Add(new TextBlock
            {
                Text = subText,
                FontSize = 11,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!pwd.IsValid)
            {
                infoStack.Children.Add(new TextBlock
                {
                    Text = "⚠ Invalid: password changed or rejected",
                    FontSize = 11,
                    Foreground = GetCautionBrush()
                });
            }
            Grid.SetColumn(infoStack, 0);
            rowGrid.Children.Add(infoStack);
            var actionsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var updateBtn = new Button
            {
                Content = "Update",
                FontSize = 12
            };
            AutomationProperties.SetName(updateBtn, $"Update saved password for drive {pwd.DriveLetter}");
            var capturedPwd = pwd;
            updateBtn.Click += async (s, e) =>
            {
                manageDialog?.Hide();
                await ShowUpdatePasswordDialogAsync(capturedPwd);
                await ShowManageSavedPasswordsDialogAsync();
            };
            var deleteBtn = new Button
            {
                Content = "Delete",
                FontSize = 12
            };
            AutomationProperties.SetName(deleteBtn, $"Delete saved password for drive {pwd.DriveLetter}");
            deleteBtn.Click += async (s, e) =>
            {
                await ViewModel.RemoveSavedPasswordAsync(capturedPwd.VolumeGuid, capturedPwd.DriveLetter);
                manageDialog?.Hide();
                await ShowManageSavedPasswordsDialogAsync();
            };
            actionsStack.Children.Add(updateBtn);
            actionsStack.Children.Add(deleteBtn);
            Grid.SetColumn(actionsStack, 1);
            rowGrid.Children.Add(actionsStack);
            card.Child = rowGrid;
            panel.Children.Add(card);
        }
        manageDialog = new ContentDialog
        {
            Title = "Manage Saved Passwords",
            Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
            PrimaryButtonText = "Delete All",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        ConfigureDialog(manageDialog);
        var res = await manageDialog.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            var confirmDialog = new ContentDialog
            {
                Title = "Delete All Saved Passwords?",
                Content = new TextBlock
                {
                    Text = "Are you sure you want to delete all saved BitLocker passwords? This action cannot be undone.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Delete All",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            ConfigureDialog(confirmDialog);
            var confirmRes = await confirmDialog.ShowAsync();
            if (confirmRes == ContentDialogResult.Primary)
            {
                await ViewModel.ClearAllSavedPasswordsAsync();
            }
        }
    }
    private async Task ShowUpdatePasswordDialogAsync(AppLock.Models.BitLockerSavedPassword pwd)
    {
        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth) return;
        var pwdBox = new PasswordBox
        {
            PlaceholderText = "Enter new BitLocker password",
            Width = 320
        };
        AutomationProperties.SetName(pwdBox, $"New password for Drive {pwd.DriveLetter}");
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = $"Enter the new password for Drive {pwd.DriveLetter}:",
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(pwdBox);
        var updateDialog = new ContentDialog
        {
            Title = $"Update Password for Drive {pwd.DriveLetter}",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        ConfigureDialog(updateDialog);
        var res = await updateDialog.ShowAsync();
        if (res == ContentDialogResult.Primary && !string.IsNullOrEmpty(pwdBox.Password))
        {
            await ViewModel.UpdateSavedPasswordAsync(pwd.VolumeGuid, pwd.DriveLetter, pwdBox.Password);
            ViewModel.StatusMessage = $"Saved password for Drive {pwd.DriveLetter} updated.";
            ViewModel.StatusSeverity = BitLockerStatusSeverity.Success;
        }
    }
    private async void TurnOnButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsElevated)
        {
            ViewModel.StatusMessage = "Administrator rights are required to turn on BitLocker.";
            ViewModel.StatusSeverity = BitLockerStatusSeverity.Warning;
            return;
        }

        var drives = ViewModel.Drives.Select(d => d.Model).ToList();
        var selectedLetter = ViewModel.SelectedDrive?.DriveLetter;

        var dialog = new EnableBitLockerDialog(drives, selectedLetter);
        ConfigureDialog(dialog);
        await dialog.ShowAsync();

        if (dialog.IsConfirmed && dialog.ResultRequest != null)
        {
            await ViewModel.TurnOnBitLockerAsync(dialog.ResultRequest);
        }
    }
    private async void TurnOffButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null || !ViewModel.CanTurnOffSelected) return;

        if (!ViewModel.IsElevated)
        {
            ViewModel.StatusMessage = "Administrator rights are required to turn off BitLocker.";
            ViewModel.StatusSeverity = BitLockerStatusSeverity.Warning;
            return;
        }

        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth) return;

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        var warningText = drive.IsOsDrive
            ? $"WARNING: Turning off BitLocker on the Operating System drive ({drive.DisplayName}) will permanently decrypt all system files and reduce device security. Decryption may take several hours."
            : $"Turning off BitLocker will decrypt all data on drive {drive.DisplayName}. The drive will no longer be protected.";

        panel.Children.Add(new TextBlock
        {
            Text = warningText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetCautionBrush()
        });

        var expectedLetter = drive.DriveLetter.TrimEnd(':').ToUpperInvariant();
        panel.Children.Add(new TextBlock
        {
            Text = $"To confirm, type the drive letter \"{expectedLetter}\" below:",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });

        var confirmBox = new TextBox
        {
            PlaceholderText = expectedLetter,
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(confirmBox, "Type drive letter to confirm decryption");
        panel.Children.Add(confirmBox);

        var dialog = new ContentDialog
        {
            Title = $"Turn off BitLocker for {drive.DisplayName}?",
            Content = panel,
            PrimaryButtonText = "Turn off BitLocker (Decrypt)",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        ConfigureDialog(dialog);

        confirmBox.TextChanged += (s, args) =>
        {
            var text = confirmBox.Text.Trim().TrimEnd(':').ToUpperInvariant();
            dialog.IsPrimaryButtonEnabled = string.Equals(text, expectedLetter, StringComparison.OrdinalIgnoreCase);
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Double confirmation for OS drive per spec
        if (drive.IsOsDrive)
        {
            var secondDialog = new ContentDialog
            {
                Title = "Final Confirmation - OS Drive Decryption",
                Content = new TextBlock
                {
                    Text = $"Are you absolutely certain you want to turn off BitLocker on the operating system drive {drive.DriveLetter}? Your computer will not be protected by BitLocker until you re-encrypt it.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Yes, decrypt OS drive",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            ConfigureDialog(secondDialog);
            var secondResult = await secondDialog.ShowAsync();
            if (secondResult != ContentDialogResult.Primary) return;
        }

        await ViewModel.TurnOffBitLockerAsync(drive.DriveLetter);
    }

    private async void SuspendButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null || !ViewModel.CanSuspendOrResumeSelected) return;

        if (drive.ProtectionStatus == ProtectionStatus.Suspended)
        {
            await ViewModel.ResumeProtectionAsync();
            return;
        }

        if (drive.IsOsDrive)
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(new TextBlock
            {
                Text = "Suspending BitLocker protection keeps data encrypted but allows the computer to start without prompting for PIN or USB keys. Ideal for firmware or BIOS updates.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Opacity = 0.75
            });

            panel.Children.Add(new TextBlock { Text = "Automatically resume protection after:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0
            };
            AutomationProperties.SetName(combo, "Select number of reboots to suspend BitLocker");
            combo.Items.Add(new ComboBoxItem { Content = "1 reboot (Recommended)", Tag = 1 });
            combo.Items.Add(new ComboBoxItem { Content = "2 reboots", Tag = 2 });
            combo.Items.Add(new ComboBoxItem { Content = "3 reboots", Tag = 3 });
            combo.Items.Add(new ComboBoxItem { Content = "Until manually resumed", Tag = 0 });
            panel.Children.Add(combo);

            var dialog = new ContentDialog
            {
                Title = $"Suspend BitLocker - {drive.DisplayName}",
                Content = panel,
                PrimaryButtonText = "Suspend",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            ConfigureDialog(dialog);

            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                var selectedTag = (combo.SelectedItem as ComboBoxItem)?.Tag;
                int count = selectedTag is int i ? i : 1;
                await ViewModel.SuspendProtectionAsync(count);
            }
        }
        else
        {
            var dialog = new ContentDialog
            {
                Title = $"Suspend BitLocker - {drive.DisplayName}",
                Content = new TextBlock
                {
                    Text = $"Are you sure you want to suspend BitLocker protection on {drive.DisplayName}? Key protectors will be temporarily disabled until manually resumed.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Suspend protection",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            ConfigureDialog(dialog);

            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                await ViewModel.SuspendProtectionAsync(0);
            }
        }
    }

    private async void BackupKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null) return;

        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth) return;

        await ShowRecoveryKeyBackupDialogAsync(drive);
    }

    private async Task ShowRecoveryKeyBackupDialogAsync(DriveItemViewModel drive)
    {
        var protectors = await ViewModel.GetKeyProtectorsAsync();
        var recoveryProtectors = protectors.Where(p => p.TypeCode == 3).ToList();

        if (recoveryProtectors.Count == 0)
        {
            var noKeyDialog = new ContentDialog
            {
                Title = $"No Recovery Key - {drive.DisplayName}",
                Content = new TextBlock
                {
                    Text = $"Drive {drive.DisplayName} does not currently have a BitLocker numerical recovery password protector.\n\nWithout a recovery key, your data cannot be recovered if you forget your password or the TPM resets. Would you like to add one now?",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Add recovery key",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            ConfigureDialog(noKeyDialog);

            var res = await noKeyDialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                var newKey = await ViewModel.GenerateRecoveryPasswordAsync(drive.DriveLetter);
                if (!string.IsNullOrEmpty(newKey))
                {
                    await ViewModel.AddRecoveryPasswordProtectorAsync(newKey);
                    await ShowRecoveryKeyBackupDialogAsync(drive);
                }
            }
            return;
        }

        var panel = new StackPanel { Spacing = 14, MinWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = $"BitLocker numerical recovery key for {drive.DisplayName}. Store this key in a secure location separate from this computer:",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });

        foreach (var p in recoveryProtectors)
        {
            var key = await ViewModel.GetRecoveryKeyAsync(p.Id) ?? "Key retrieval unavailable";

            var card = new Border
            {
                Background = GetCardBackgroundBrush(),
                BorderBrush = GetCardBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12)
            };

            var cardStack = new StackPanel { Spacing = 8 };
            cardStack.Children.Add(new TextBlock
            {
                Text = $"Protector ID: {p.Id}",
                FontSize = 11,
                Opacity = 0.6
            });

            var keyBox = new TextBox
            {
                Text = key,
                IsReadOnly = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };
            AutomationProperties.SetName(keyBox, "BitLocker recovery key");
            cardStack.Children.Add(keyBox);

            var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var copyBtn = new Button
            {
                Content = "Copy key",
                FontSize = 12
            };
            AutomationProperties.SetName(copyBtn, "Copy recovery key to clipboard");
            var saveFileBtn = new Button
            {
                Content = "Save to file (.txt)",
                FontSize = 12
            };
            AutomationProperties.SetName(saveFileBtn, "Save recovery key to text file");
            var feedbackText = new TextBlock
            {
                Text = "",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GetSuccessBrush()
            };

            var capturedKey = key;
            var capturedId = p.Id;
            copyBtn.Click += (s, e) =>
            {
                CopyRecoveryKeyWithAutoClear(capturedKey, ViewModel.AutoClearClipboard);
                feedbackText.Text = ViewModel.AutoClearClipboard ? "Copied! (Auto-clears in 60s)" : "Copied to clipboard!";
            };

            saveFileBtn.Click += async (s, e) =>
            {
                var filePath = await SaveRecoveryKeyToFileAsync(drive.DriveLetter, drive.VolumeLabel, capturedId, capturedKey);
                if (!string.IsNullOrEmpty(filePath))
                {
                    feedbackText.Text = $"Saved to Desktop: {Path.GetFileName(filePath)}";
                }
            };

            actionsPanel.Children.Add(copyBtn);
            actionsPanel.Children.Add(saveFileBtn);
            actionsPanel.Children.Add(feedbackText);
            cardStack.Children.Add(actionsPanel);

            card.Child = cardStack;
            panel.Children.Add(card);
        }

        var backupDialog = new ContentDialog
        {
            Title = $"BitLocker Recovery Key - {drive.DisplayName}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        ConfigureDialog(backupDialog);

        await backupDialog.ShowAsync();
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null) return;

        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth) return;

        await ShowChangePasswordDialogAsync(drive);
    }

    private async Task ShowChangePasswordDialogAsync(DriveItemViewModel drive)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };

        panel.Children.Add(new TextBlock
        {
            Text = $"Set a new BitLocker password for {drive.DisplayName}:",
            FontSize = 13,
            Opacity = 0.75
        });

        var oldPwdBox = new PasswordBox
        {
            Header = "Current password (optional if drive is unlocked)",
            PlaceholderText = "Current password"
        };
        AutomationProperties.SetName(oldPwdBox, "Current BitLocker password");
        panel.Children.Add(oldPwdBox);

        var newPwdBox = new PasswordBox
        {
            Header = "New password",
            PlaceholderText = "At least 8 characters"
        };
        AutomationProperties.SetName(newPwdBox, "New BitLocker password");
        panel.Children.Add(newPwdBox);

        var confirmPwdBox = new PasswordBox
        {
            Header = "Confirm new password",
            PlaceholderText = "Re-enter new password"
        };
        AutomationProperties.SetName(confirmPwdBox, "Confirm new BitLocker password");
        panel.Children.Add(confirmPwdBox);

        var strengthBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 4,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetName(strengthBar, "Password strength");
        var strengthLabel = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Opacity = 0.6
        };
        var matchLabel = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = GetCautionBrush()
        };
        panel.Children.Add(strengthBar);
        panel.Children.Add(strengthLabel);
        panel.Children.Add(matchLabel);

        ContentDialog dialog = new ContentDialog
        {
            Title = $"Change Password - {drive.DisplayName}",
            Content = panel,
            PrimaryButtonText = "Change password",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        ConfigureDialog(dialog);

        void ValidateInput()
        {
            var p1 = newPwdBox.Password;
            var p2 = confirmPwdBox.Password;

            int score = 0;
            if (p1.Length >= 8) score += 25;
            if (p1.Length >= 12) score += 15;
            if (p1.Any(char.IsUpper)) score += 20;
            if (p1.Any(char.IsDigit)) score += 20;
            if (p1.Any(c => !char.IsLetterOrDigit(c))) score += 20;

            strengthBar.Value = score;
            if (string.IsNullOrEmpty(p1))
            {
                strengthLabel.Text = "";
            }
            else if (score < 40)
            {
                strengthLabel.Text = "Weak password";
            }
            else if (score < 75)
            {
                strengthLabel.Text = "Moderate password";
            }
            else
            {
                strengthLabel.Text = "Strong password";
            }

            if (!string.IsNullOrEmpty(p2) && p1 != p2)
            {
                matchLabel.Text = "Passwords do not match.";
            }
            else
            {
                matchLabel.Text = "";
            }

            dialog.IsPrimaryButtonEnabled = p1.Length >= 8 && p1 == p2;
        }

        newPwdBox.PasswordChanged += (s, e) => ValidateInput();
        confirmPwdBox.PasswordChanged += (s, e) => ValidateInput();

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            var oldPwd = oldPwdBox.Password;
            var newPwd = newPwdBox.Password;
            await ViewModel.ChangePasswordAsync(oldPwd, newPwd);
        }
    }

    private async void ManageProtectorsButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null) return;

        var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
        if (!auth) return;

        await ShowManageProtectorsDialogAsync(drive);
    }

    private async Task ShowManageProtectorsDialogAsync(DriveItemViewModel drive)
    {
        var protectors = await ViewModel.GetKeyProtectorsAsync();

        ContentDialog? manageDialog = null;
        var panel = new StackPanel { Spacing = 14, MinWidth = 440 };

        panel.Children.Add(new TextBlock
        {
            Text = $"Key protectors configured for {drive.DisplayName}. At least one key protector must remain on the volume at all times:",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });

        // Add protector buttons toolbar
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addPasswordBtn = new Button
        {
            Content = "+ Add password",
            FontSize = 12
        };
        AutomationProperties.SetName(addPasswordBtn, "Add password protector");
        var addRecoveryKeyBtn = new Button
        {
            Content = "+ Add recovery key",
            FontSize = 12
        };
        AutomationProperties.SetName(addRecoveryKeyBtn, "Add recovery key protector");
        topActions.Children.Add(addPasswordBtn);
        topActions.Children.Add(addRecoveryKeyBtn);
        panel.Children.Add(topActions);

        var listPanel = new StackPanel { Spacing = 8 };
        bool canDelete = protectors.Count > 1;

        foreach (var p in protectors)
        {
            var card = new Border
            {
                Background = GetCardBackgroundBrush(),
                BorderBrush = GetCardBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = p.IconGlyph,
                FontSize = 20,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GetAccentBrush()
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = p.TypeName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13
            });
            info.Children.Add(new TextBlock
            {
                Text = $"ID: {p.Id}",
                FontSize = 11,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var delBtn = new Button
            {
                Content = "Delete",
                FontSize = 12,
                IsEnabled = canDelete,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(delBtn, $"Remove protector {p.TypeName}");
            if (!canDelete)
            {
                ToolTipService.SetToolTip(delBtn, "Cannot delete the last key protector.");
            }

            var capturedProtector = p;
            delBtn.Click += async (s, e) =>
            {
                var confirmDialog = new ContentDialog
                {
                    Title = $"Delete {capturedProtector.TypeName} Protector?",
                    Content = new TextBlock
                    {
                        Text = $"Are you sure you want to remove this {capturedProtector.TypeName} protector from drive {drive.DisplayName}?",
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                ConfigureDialog(confirmDialog);
                var confirmRes = await confirmDialog.ShowAsync();
                if (confirmRes == ContentDialogResult.Primary)
                {
                    manageDialog?.Hide();
                    await ViewModel.DeleteKeyProtectorAsync(capturedProtector.Id);
                    await ShowManageProtectorsDialogAsync(drive);
                }
            };
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(delBtn);

            card.Child = grid;
            listPanel.Children.Add(card);
        }

        panel.Children.Add(new ScrollViewer { Content = listPanel, MaxHeight = 300 });

        manageDialog = new ContentDialog
        {
            Title = $"Manage Key Protectors - {drive.DisplayName}",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        ConfigureDialog(manageDialog);

        addPasswordBtn.Click += async (s, e) =>
        {
            manageDialog?.Hide();
            await ShowAddPasswordProtectorDialogAsync(drive);
            await ShowManageProtectorsDialogAsync(drive);
        };

        addRecoveryKeyBtn.Click += async (s, e) =>
        {
            manageDialog?.Hide();
            await ShowAddRecoveryKeyProtectorDialogAsync(drive);
            await ShowManageProtectorsDialogAsync(drive);
        };

        await manageDialog.ShowAsync();
    }

    private async Task ShowAddPasswordProtectorDialogAsync(DriveItemViewModel drive)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        var pwdBox = new PasswordBox
        {
            Header = "Enter new password:",
            PlaceholderText = "At least 8 characters"
        };
        AutomationProperties.SetName(pwdBox, "Enter new password");
        var confirmBox = new PasswordBox
        {
            Header = "Confirm password:",
            PlaceholderText = "Re-enter password"
        };
        AutomationProperties.SetName(confirmBox, "Confirm password");
        var msgLabel = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = GetCautionBrush()
        };
        panel.Children.Add(pwdBox);
        panel.Children.Add(confirmBox);
        panel.Children.Add(msgLabel);

        var dialog = new ContentDialog
        {
            Title = $"Add Password Protector - {drive.DisplayName}",
            Content = panel,
            PrimaryButtonText = "Add protector",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        ConfigureDialog(dialog);

        void Check()
        {
            var p1 = pwdBox.Password;
            var p2 = confirmBox.Password;
            if (p1.Length < 8)
            {
                msgLabel.Text = "Password must be at least 8 characters.";
                dialog.IsPrimaryButtonEnabled = false;
            }
            else if (p1 != p2)
            {
                msgLabel.Text = "Passwords do not match.";
                dialog.IsPrimaryButtonEnabled = false;
            }
            else
            {
                msgLabel.Text = "";
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        pwdBox.PasswordChanged += (s, e) => Check();
        confirmBox.PasswordChanged += (s, e) => Check();

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary && pwdBox.Password.Length >= 8)
        {
            await ViewModel.AddPasswordProtectorAsync(pwdBox.Password);
        }
    }

    private async Task ShowAddRecoveryKeyProtectorDialogAsync(DriveItemViewModel drive)
    {
        var key = await ViewModel.GenerateRecoveryPasswordAsync(drive.DriveLetter) ?? "";
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };

        panel.Children.Add(new TextBlock
        {
            Text = "A new 48-digit numerical recovery key will be added to this drive. Make sure to back it up before adding:",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });

        var keyBox = new TextBox
        {
            Text = key,
            IsReadOnly = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(keyBox, "Generated recovery key");
        panel.Children.Add(keyBox);

        var copyBtn = new Button
        {
            Content = "Copy to clipboard",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(copyBtn, "Copy generated recovery key to clipboard");
        copyBtn.Click += (s, e) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(key);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            copyBtn.Content = "Copied!";
        };
        panel.Children.Add(copyBtn);

        var dialog = new ContentDialog
        {
            Title = $"Add Recovery Key Protector - {drive.DisplayName}",
            Content = panel,
            PrimaryButtonText = "Add protector",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        ConfigureDialog(dialog);

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary && !string.IsNullOrEmpty(key))
        {
            await ViewModel.AddRecoveryPasswordProtectorAsync(key);
        }
    }
    private async void CheckUsageButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = ViewModel.SelectedDrive;
        if (drive == null) return;

        await ShowUsageDialogAsync(drive);
    }

    private async Task ShowUsageDialogAsync(DriveItemViewModel drive)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 380 };

        if (drive.IsLocked)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Drive {drive.DisplayName} is currently locked.\n\nFile system details and storage usage are only available once the drive has been unlocked.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Opacity = 0.75
            });

            var lockedCard = new Border
            {
                Background = GetCardBackgroundBrush(),
                BorderBrush = GetCardBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12)
            };

            var lockedStack = new StackPanel { Spacing = 6 };
            lockedStack.Children.Add(new TextBlock { Text = $"Total Volume Capacity: {drive.CapacityDisplay}", FontSize = 13 });
            lockedStack.Children.Add(new TextBlock { Text = $"BitLocker Status: {drive.ProtectionStatusDisplay} ({drive.ConversionStatus})", FontSize = 12 });
            lockedStack.Children.Add(new TextBlock { Text = $"Encryption Method: {drive.EncryptionMethod}", FontSize = 12 });
            lockedCard.Child = lockedStack;
            panel.Children.Add(lockedCard);
        }
        else
        {
            long total = drive.TotalBytes;
            long free = drive.FreeBytes;
            long used = total - free;
            double usedPct = drive.CapacityUsedPercent;
            string fs = "Unknown";

            try
            {
                var di = new DriveInfo(drive.DriveLetter);
                if (di.IsReady)
                {
                    total = di.TotalSize;
                    free = di.AvailableFreeSpace;
                    used = total - free;
                    usedPct = total > 0 ? ((double)used / total) * 100.0 : 0;
                    fs = di.DriveFormat;
                }
            }
            catch { }

            panel.Children.Add(new TextBlock
            {
                Text = $"Storage usage breakdown for {drive.DisplayName}:",
                FontSize = 13,
                Opacity = 0.75
            });

            var barStack = new StackPanel { Spacing = 6 };
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var usedLabel = new TextBlock
            {
                Text = $"{FormatBytes(used)} used of {FormatBytes(total)}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13
            };
            var pctLabel = new TextBlock
            {
                Text = $"{usedPct:F1}%",
                FontSize = 13,
                Foreground = GetAccentBrush()
            };
            Grid.SetColumn(usedLabel, 0); Grid.SetColumn(pctLabel, 1);
            barGrid.Children.Add(usedLabel); barGrid.Children.Add(pctLabel);
            barStack.Children.Add(barGrid);

            var progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = usedPct,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Foreground = GetAccentBrush()
            };
            AutomationProperties.SetName(progressBar, "Drive storage capacity used");
            barStack.Children.Add(progressBar);
            panel.Children.Add(barStack);

            var card = new Border
            {
                Background = GetCardBackgroundBrush(),
                BorderBrush = GetCardBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12)
            };

            var grid = new Grid { RowSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void AddRow(int row, string title, string val)
            {
                var t = new TextBlock { Text = title, FontSize = 12, Margin = new Thickness(0,0,16,0), Opacity = 0.75 };
                var v = new TextBlock { Text = val, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                Grid.SetRow(t, row); Grid.SetColumn(t, 0);
                Grid.SetRow(v, row); Grid.SetColumn(v, 1);
                grid.Children.Add(t); grid.Children.Add(v);
            }

            AddRow(0, "Free space:", FormatBytes(free));
            AddRow(1, "File system:", fs);
            AddRow(2, "Protection status:", drive.ProtectionStatusDisplay);
            AddRow(3, "BitLocker encryption:", $"{drive.ConversionStatus} ({drive.EncryptionPercent}%)");
            AddRow(4, "Encryption algorithm:", drive.EncryptionMethod);

            card.Child = grid;
            panel.Children.Add(card);

            panel.Children.Add(CreateActiveTasksPanel(drive.DriveLetter));

            var openBtn = new Button
            {
                Content = "Open in File Explorer",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(openBtn, "Open drive in File Explorer");
            openBtn.Click += (s, e) => BitLockerViewModel.TryOpenDriveInExplorer(drive.DriveLetter);
            panel.Children.Add(openBtn);
        }

        var usageDialog = new ContentDialog
        {
            Title = $"Drive Usage - {drive.DisplayName}",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        ConfigureDialog(usageDialog);

        await usageDialog.ShowAsync();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
    private void IncludeInAutoUnlockCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetSelectedDriveAutoUnlock(IncludeInAutoUnlockCheckBox.IsChecked == true);
    }

    private async void AutoUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowStartupTaskDialogAsync();
    }

    private async Task ShowStartupTaskDialogAsync()
    {
        var taskInfo = await ViewModel.GetStartupTaskStatusAsync();

        var panel = new StackPanel { Spacing = 14, MinWidth = 420 };

        panel.Children.Add(new TextBlock
        {
            Text = "Configure a Windows Scheduled Task to automatically unlock your saved BitLocker drives when you log on, without displaying any windows or UAC prompts.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Opacity = 0.75
        });

        // Status Card
        var statusCard = new Border
        {
            Background = GetCardBackgroundBrush(),
            BorderBrush = GetCardBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12)
        };

        var statusGrid = new Grid { RowSpacing = 6 };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        statusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        statusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: Task State
        var stateTitle = new TextBlock { Text = "Task State:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0,0,12,0) };
        var stateVal = new TextBlock
        {
            Text = taskInfo.Exists ? taskInfo.State : "Not created",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = taskInfo.Exists ? 1.0 : 0.6
        };
        if (taskInfo.Exists)
        {
            stateVal.Foreground = GetSuccessBrush();
        }
        Grid.SetRow(stateTitle, 0); Grid.SetColumn(stateTitle, 0);
        Grid.SetRow(stateVal, 0); Grid.SetColumn(stateVal, 1);
        statusGrid.Children.Add(stateTitle); statusGrid.Children.Add(stateVal);

        // Row 1: Last Run Time
        var runTitle = new TextBlock { Text = "Last Run Time:", FontSize = 12, Margin = new Thickness(0,0,12,0) };
        var runVal = new TextBlock { Text = taskInfo.LastRunTime, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(runTitle, 1); Grid.SetColumn(runTitle, 0);
        Grid.SetRow(runVal, 1); Grid.SetColumn(runVal, 1);
        statusGrid.Children.Add(runTitle); statusGrid.Children.Add(runVal);

        // Row 2: Last Result
        var resTitle = new TextBlock { Text = "Last Result:", FontSize = 12, Margin = new Thickness(0,0,12,0) };
        var formattedResult = FormatTaskResult(taskInfo.LastResult);
        var resVal = new TextBlock
        {
            Text = formattedResult,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (taskInfo.LastResult == "0" || taskInfo.LastResult == "0x0" || formattedResult.Contains("Successful"))
        {
            resVal.Foreground = GetSuccessBrush();
        }
        else if (formattedResult == "Never run")
        {
            resVal.Opacity = 0.6;
        }
        else
        {
            resVal.Foreground = GetCautionBrush();
        }
        Grid.SetRow(resTitle, 2); Grid.SetColumn(resTitle, 0);
        Grid.SetRow(resVal, 2); Grid.SetColumn(resVal, 1);
        statusGrid.Children.Add(resTitle); statusGrid.Children.Add(resVal);

        statusCard.Child = statusGrid;
        panel.Children.Add(statusCard);

        if (taskInfo.NeedsRepair)
        {
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                Severity = InfoBarSeverity.Warning,
                IsClosable = false,
                Title = "Executable Path Changed",
                Message = "The task points to a previous executable location. Click Create/Update below to repair."
            });
        }

        // Action Buttons Grid
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

        var runNowBtn = new Button { Content = "Run now", IsEnabled = taskInfo.Exists };
        AutomationProperties.SetName(runNowBtn, "Run auto-unlock startup task now");
        var createBtn = new Button
        {
            Content = taskInfo.Exists ? "Update task" : "Create task",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        AutomationProperties.SetName(createBtn, "Create or update auto-unlock startup task");
        var removeBtn = new Button { Content = "Remove task", IsEnabled = taskInfo.Exists };
        AutomationProperties.SetName(removeBtn, "Remove auto-unlock startup task");

        buttonPanel.Children.Add(runNowBtn);
        buttonPanel.Children.Add(createBtn);
        buttonPanel.Children.Add(removeBtn);
        panel.Children.Add(buttonPanel);

        ContentDialog dialog = new ContentDialog
        {
            Title = "BitLocker Auto-Unlock at Logon",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        ConfigureDialog(dialog);

        runNowBtn.Click += async (s, e) =>
        {
            dialog.Hide();

            // Check if there are any locked drives
            var hasLocked = ViewModel.Drives.Any(d => d.IsLocked);
            if (!hasLocked)
            {
                ViewModel.StatusMessage = "All BitLocker drives are already unlocked.";
                ViewModel.StatusSeverity = BitLockerStatusSeverity.Informational;
                return;
            }

            if (ServiceContainer.Settings.BitLockerRequireHelloOnAppStart)
            {
                var auth = await BitLockerSecurityGate.EnsureAuthenticatedAsync(this.XamlRoot);
                if (!auth)
                {
                    ViewModel.StatusMessage = "Auto-unlock cancelled (Authentication required).";
                    ViewModel.StatusSeverity = BitLockerStatusSeverity.Warning;
                    return;
                }
            }

            var unlockedCount = await ServiceContainer.AutoUnlockService.UnlockEligibleDrivesAsync(showNotifications: true);
            await ViewModel.RefreshDrivesAsync();
            ViewModel.StatusMessage = unlockedCount > 0
                ? $"Successfully auto-unlocked {unlockedCount} drive{(unlockedCount == 1 ? "" : "s")}."
                : "No saved password matched the locked drive(s).";
            ViewModel.StatusSeverity = unlockedCount > 0 ? BitLockerStatusSeverity.Success : BitLockerStatusSeverity.Warning;
        };

        createBtn.Click += async (s, e) =>
        {
            createBtn.IsEnabled = false;
            var ok = await ViewModel.CreateStartupTaskAsync();
            ViewModel.StatusMessage = ok ? "Auto-unlock startup task created successfully." : "Failed to create scheduled task.";
            ViewModel.StatusSeverity = ok ? BitLockerStatusSeverity.Success : BitLockerStatusSeverity.Error;
            dialog.Hide();
        };

        removeBtn.Click += async (s, e) =>
        {
            removeBtn.IsEnabled = false;
            var ok = await ViewModel.DeleteStartupTaskAsync();
            ViewModel.StatusMessage = ok ? "Auto-unlock startup task removed." : "Failed to delete scheduled task.";
            ViewModel.StatusSeverity = ok ? BitLockerStatusSeverity.Informational : BitLockerStatusSeverity.Error;
            dialog.Hide();
        };

        await dialog.ShowAsync();
    }
    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowDiagnosticsDialogAsync();
    }

    private async Task ShowDiagnosticsDialogAsync()
    {
        var report = await ViewModel.GenerateDiagnosticsReportAsync();

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = "BitLocker environment, drive states, and recent action logs (all passwords and keys are strictly redacted):",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });

        var reportBox = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 11,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 320
        };
        AutomationProperties.SetName(reportBox, "BitLocker diagnostic report");
        panel.Children.Add(reportBox);

        var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copyBtn = new Button { Content = "Copy report", FontSize = 12 };
        AutomationProperties.SetName(copyBtn, "Copy diagnostic report to clipboard");
        var openLogsBtn = new Button { Content = "Open logs folder", FontSize = 12 };
        AutomationProperties.SetName(openLogsBtn, "Open logs folder in File Explorer");
        var feedbackText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetSuccessBrush()
        };

        copyBtn.Click += (s, e) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            feedbackText.Text = "Report copied to clipboard!";
        };

        openLogsBtn.Click += (s, e) =>
        {
            try
            {
                var dir = BitLockerLogger.LogsDirectory;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        };

        actionsPanel.Children.Add(copyBtn);
        actionsPanel.Children.Add(openLogsBtn);
        actionsPanel.Children.Add(feedbackText);
        panel.Children.Add(actionsPanel);

        var diagDialog = new ContentDialog
        {
            Title = "BitLocker Diagnostics",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        ConfigureDialog(diagDialog);

        await diagDialog.ShowAsync();
    }

    private async void BitLockerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowBitLockerSettingsDialogAsync();
    }

    private async Task ShowBitLockerSettingsDialogAsync()
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 380 };

        panel.Children.Add(new TextBlock
        {
            Text = "Configure preferences for BitLocker drive management, auto-unlock, and security:",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });

        var card = new Border
        {
            Background = GetCardBackgroundBrush(),
            BorderBrush = GetCardBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12)
        };

        var stack = new StackPanel { Spacing = 12 };

        var openExplorerToggle = new ToggleSwitch
        {
            Header = "Open drive in File Explorer after unlock",
            IsOn = ViewModel.OpenDriveAfterUnlock
        };
        AutomationProperties.SetName(openExplorerToggle, "Open drive in File Explorer after unlock");

        var autoUnlockToggle = new ToggleSwitch
        {
            Header = "Auto-unlock saved drives when AppLock opens",
            IsOn = ViewModel.AutoUnlockOnAppOpen
        };
        AutomationProperties.SetName(autoUnlockToggle, "Auto-unlock saved drives when AppLock opens");

        var requireHelloToggle = new ToggleSwitch
        {
            Header = "Require Windows Hello prompt before auto-unlocking on app start",
            IsOn = ViewModel.RequireHelloOnAppStart
        };
        AutomationProperties.SetName(requireHelloToggle, "Require Windows Hello prompt before auto-unlocking on app start");

        var toastToggle = new ToggleSwitch
        {
            Header = "Show toast notifications for BitLocker events",
            IsOn = ViewModel.ToastNotifications
        };
        AutomationProperties.SetName(toastToggle, "Show toast notifications for BitLocker events");

        var autoClearToggle = new ToggleSwitch
        {
            Header = "Auto-clear clipboard 60s after copying a recovery key",
            IsOn = ViewModel.AutoClearClipboard
        };
        AutomationProperties.SetName(autoClearToggle, "Auto-clear clipboard 60s after copying a recovery key");

        var intervalStack = new StackPanel { Spacing = 4 };
        intervalStack.Children.Add(new TextBlock { Text = "Drive list refresh interval:", FontSize = 12 });
        var intervalCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(intervalCombo, "Drive list refresh interval");
        intervalCombo.Items.Add(new ComboBoxItem { Content = "5 seconds (Default)", Tag = 5 });
        intervalCombo.Items.Add(new ComboBoxItem { Content = "10 seconds", Tag = 10 });
        intervalCombo.Items.Add(new ComboBoxItem { Content = "30 seconds", Tag = 30 });

        intervalCombo.SelectedIndex = ViewModel.RefreshIntervalSeconds switch
        {
            10 => 1,
            30 => 2,
            _ => 0
        };

        intervalStack.Children.Add(intervalCombo);

        stack.Children.Add(openExplorerToggle);
        stack.Children.Add(autoUnlockToggle);
        stack.Children.Add(requireHelloToggle);
        stack.Children.Add(toastToggle);
        stack.Children.Add(autoClearToggle);
        stack.Children.Add(intervalStack);

        card.Child = stack;
        panel.Children.Add(card);

        var dialog = new ContentDialog
        {
            Title = "BitLocker Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        ConfigureDialog(dialog);

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            ViewModel.OpenDriveAfterUnlock = openExplorerToggle.IsOn;
            ViewModel.AutoUnlockOnAppOpen = autoUnlockToggle.IsOn;
            ViewModel.RequireHelloOnAppStart = requireHelloToggle.IsOn;
            ViewModel.ToastNotifications = toastToggle.IsOn;
            ViewModel.AutoClearClipboard = autoClearToggle.IsOn;

            var selectedTag = (intervalCombo.SelectedItem as ComboBoxItem)?.Tag;
            int seconds = selectedTag is int s ? s : 5;
            ViewModel.RefreshIntervalSeconds = seconds;
            _refreshTimer.Interval = TimeSpan.FromSeconds(seconds);

            ViewModel.StatusMessage = "BitLocker settings saved.";
            ViewModel.StatusSeverity = BitLockerStatusSeverity.Success;
        }
    }

    private static void CopyRecoveryKeyWithAutoClear(string key, bool autoClear)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(key);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

        if (autoClear)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                try
                {
                    var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (content != null && content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        var text = await content.GetTextAsync();
                        if (string.Equals(text, key, StringComparison.Ordinal))
                        {
                            Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
                        }
                    }
                }
                catch { }
            });
        }
    }

    private static async Task<string?> SaveRecoveryKeyToFileAsync(string driveLetter, string volumeLabel, string protectorId, string key)
    {
        try
        {
            var cleanLetter = driveLetter.TrimEnd(':');
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var filename = $"BitLocker_RecoveryKey_{cleanLetter}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var fullPath = Path.Combine(desktopPath, filename);

            var content = $@"================================================================================
BitLocker Drive Encryption Recovery Key
================================================================================
Drive:            {driveLetter} {(string.IsNullOrEmpty(volumeLabel) ? "" : $"({volumeLabel})")}
Created:          {DateTime.Now:yyyy-MM-dd HH:mm:ss}
Key Protector ID: {protectorId}

Recovery Key:
{key}

To unlock this drive in Windows recovery or recovery prompt:
1. When prompted for BitLocker recovery, enter the 48-digit numerical key above.
2. Store this file in a secure location separate from this computer.
================================================================================
";
            await File.WriteAllTextAsync(fullPath, content);
            return fullPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save recovery key file: {ex.Message}");
            return null;
        }
    }
    private async void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Already elevated — nothing to do
            if (ServiceContainer.BitLockerSupportService.IsElevated) return;
            
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe == null) return;
            
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            
            // Signal authorized exit to prevent watchdog from restarting
            ProcessProtectionService.SignalAuthorizedExit();
            ProcessProtectionService.UnprotectCurrentProcess();
            
            // Start elevated process
            var process = System.Diagnostics.Process.Start(psi);
            
            if (process != null)
            {
                // Wait a moment for elevated process to start, then close this instance
                await Task.Delay(300);
                
                // Hide window before exiting for cleaner UX
                if (App.MainWindowInstance != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        App.MainWindowInstance.Close();
                    });
                }
                else
                {
                    // Terminate this non-elevated process
                    ServiceContainer.Reset();
                    Environment.Exit(0);
                }
            }
        }
        catch (OperationCanceledException) { /* user cancelled UAC prompt */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restart as admin: {ex.Message}");
        }
    }
}
