using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class BitLockerPage : Page
{
    public BitLockerViewModel ViewModel { get; }

    public BitLockerPage()
    {
        ViewModel = new BitLockerViewModel(ServiceContainer.BitLockerSupportService);
        InitializeComponent();
        ElevationInfoBar.IsOpen = !ViewModel.IsElevated;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) { /* M2 */ }
    private void LockButton_Click(object sender, RoutedEventArgs e) { /* M3 */ }
    private void UnlockButton_Click(object sender, RoutedEventArgs e) { /* M3 */ }
    private void TurnOnButton_Click(object sender, RoutedEventArgs e) { /* M6 */ }
    private void TurnOffButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void SuspendButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void BackupKeyButton_Click(object sender, RoutedEventArgs e) { /* M8 */ }
    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void ManageProtectorsButton_Click(object sender, RoutedEventArgs e) { /* M7 */ }
    private void CheckUsageButton_Click(object sender, RoutedEventArgs e) { /* M8 */ }
    private void OpenExplorerButton_Click(object sender, RoutedEventArgs e) { /* M2 */ }
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
