using AppLock.Models;
using AppLock.Services.BitLocker;
using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
            Interval = TimeSpan.FromSeconds(5)
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
        ServiceContainer.VolumeWatcher.Stop();
    }
    private void RefreshTimer_Tick(object? sender, object e)
    {
        _ = ViewModel.RefreshDrivesAsync();
    }
    private void OnVolumeWatcherChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (ViewModel.AutoUnlockOnAppOpen)
            {
                await ServiceContainer.AutoUnlockService.UnlockEligibleDrivesAsync(showNotifications: true);
            }
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
        // Confirmation dialog
        var dialog = new ContentDialog
        {
            Title = $"Lock drive {drive.DriveLetter}?",
            Content = new TextBlock
            {
                Text = $"Are you sure you want to lock drive {drive.DisplayName}? Any open files or applications on this drive may be forced to close.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Lock (Force dismount)",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.LockSelectedDriveAsync(forceDismount: true);
        }
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
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(passwordBox);
        var dialog = new ContentDialog
        {
            Title = $"Unlock drive {drive.DisplayName}",
            Content = panel,
            PrimaryButtonText = "Unlock",
            SecondaryButtonText = "Use recovery key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var pwd = passwordBox.Password;
            passwordBox.Password = string.Empty;
            if (!string.IsNullOrEmpty(pwd))
            {
                await ViewModel.UnlockWithPassphraseAsync(pwd);
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
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
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
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
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
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot
            };
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
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        foreach (var pwd in passwords)
        {
            var card = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
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
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!pwd.IsValid)
            {
                infoStack.Children.Add(new TextBlock
                {
                    Text = "⚠ Invalid: password changed or rejected",
                    FontSize = 11,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
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
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
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
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
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
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
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

        var dialog = new EnableBitLockerDialog(drives, selectedLetter)
        {
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();

        if (dialog.IsConfirmed && dialog.ResultRequest != null)
        {
            await ViewModel.TurnOnBitLockerAsync(dialog.ResultRequest);
        }
    }
    private void TurnOffButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void SuspendButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void BackupKeyButton_Click(object sender, RoutedEventArgs e) { /* M8 */ }
    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void ManageProtectorsButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void CheckUsageButton_Click(object sender, RoutedEventArgs e) { /* M8 */ }
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
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        // Status Card
        var statusCard = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
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
            Foreground = taskInfo.Exists
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        };
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
        var resVal = new TextBlock { Text = taskInfo.LastResult, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
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
        var createBtn = new Button
        {
            Content = taskInfo.Exists ? "Update task" : "Create task",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var removeBtn = new Button { Content = "Remove task", IsEnabled = taskInfo.Exists };

        buttonPanel.Children.Add(runNowBtn);
        buttonPanel.Children.Add(createBtn);
        buttonPanel.Children.Add(removeBtn);
        panel.Children.Add(buttonPanel);

        ContentDialog dialog = new ContentDialog
        {
            Title = "BitLocker Auto-Unlock at Logon",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        runNowBtn.Click += async (s, e) =>
        {
            runNowBtn.IsEnabled = false;
            var ok = await ViewModel.RunStartupTaskNowAsync();
            ViewModel.StatusMessage = ok ? "Auto-unlock task started." : "Failed to run scheduled task.";
            ViewModel.StatusSeverity = ok ? BitLockerStatusSeverity.Success : BitLockerStatusSeverity.Error;
            dialog.Hide();
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
    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e) { /* M8 */ }
    private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
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
            System.Diagnostics.Process.Start(psi);
            // Terminate this non-elevated process forcefully.
            // Environment.Exit is synchronous, ensuring only the elevated window remains.
            ServiceContainer.Reset();
            Environment.Exit(0);
        }
        catch (OperationCanceledException) { /* user cancelled UAC prompt */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restart as admin: {ex.Message}");
        }
    }
}
