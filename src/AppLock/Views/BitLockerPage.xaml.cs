using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AppLock.Views;

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
            settings => ServiceContainer.SettingsStore.Save(settings));

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

        await ViewModel.UnlockWithPassphraseAsync(pwd);
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
