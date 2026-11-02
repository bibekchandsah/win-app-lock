using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class BitLockerPage : Page
{
    public BitLockerViewModel ViewModel { get; }
    private readonly DispatcherTimer _refreshTimer;

    public BitLockerPage()
    {
        ViewModel = new BitLockerViewModel(
            ServiceContainer.BitLockerSupportService,
            ServiceContainer.BitLockerService,
            ServiceContainer.VolumeWatcher);

        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
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
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = driveLetter + "\\",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open Explorer for drive {driveLetter}: {ex.Message}");
            }
        }
    }

    private void LockButton_Click(object sender, RoutedEventArgs e) { /* M3 */ }
    private void UnlockButton_Click(object sender, RoutedEventArgs e) { /* M3 */ }
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
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe == null) return;
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restart as admin: {ex.Message}");
        }
    }
}
