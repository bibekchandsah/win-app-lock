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
            ServiceContainer.SavedPasswordStore);

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
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = ViewModel.RefreshDrivesAsync();
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

    private void SavePasswordCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        // Checkbox state is read when unlocking — this handler is a no-op placeholder.
    }

    private async void ManageSavedPasswordsButton_Click(object sender, RoutedEventArgs e)
    {
        // Gate with AppLock admin password before showing stored credentials
        var authDialog = new ContentDialog
        {
            Title = "Confirm Identity",
            Content = new TextBlock
            {
                Text = "Enter your AppLock admin password to manage saved BitLocker passwords.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var authResult = await authDialog.ShowAsync();
        if (authResult != ContentDialogResult.Primary) return;

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
                Content = new TextBlock { Text = "No saved passwords found.", TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot
            };
            await emptyDialog.ShowAsync();
            return;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{passwords.Count} saved password(s). Select entries to delete:",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 8)
        });

        var checkBoxes = new List<(CheckBox cb, string driveLetter)>();
        foreach (var pwd in passwords)
        {
            var label = string.IsNullOrWhiteSpace(pwd.Label) ? pwd.DriveLetter : $"{pwd.DriveLetter} – {pwd.Label}";
            var dateStr = pwd.SavedAt.ToLocalTime().ToString("g");
            var cb = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = $"{label}  (saved {dateStr})",
                    TextWrapping = TextWrapping.Wrap
                }
            };
            panel.Children.Add(cb);
            checkBoxes.Add((cb, pwd.DriveLetter));
        }

        var manageDialog = new ContentDialog
        {
            Title = "Manage Saved Passwords",
            Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
            PrimaryButtonText = "Delete selected",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var res = await manageDialog.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            foreach (var (cb, letter) in checkBoxes)
            {
                if (cb.IsChecked == true)
                {
                    await ViewModel.RemoveSavedPasswordAsync(letter);
                }
            }
        }
    }

    private void TurnOnButton_Click(object sender, RoutedEventArgs e) { /* M6 */ }
    private void TurnOffButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void SuspendButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void BackupKeyButton_Click(object sender, RoutedEventArgs e) { /* M8 */ }
    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void ManageProtectorsButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void CheckUsageButton_Click(object sender, RoutedEventArgs e) { /* M8 */ }
    private void AutoUnlockButton_Click(object sender, RoutedEventArgs e) { /* M5 */ }
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
