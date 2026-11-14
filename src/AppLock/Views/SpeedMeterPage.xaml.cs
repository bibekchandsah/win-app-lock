using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

/// <summary>
/// Settings page for the floating network-speed widget (Others → Speed Meter).
/// Milestone 1 covers the shell, the master toggle and the themed preview.
/// </summary>
public sealed partial class SpeedMeterPage : Page
{
    // Setting the switches from code fires Toggled; this keeps those writes out of the handlers.
    private bool _loading;

    private static SpeedMeterSettings Settings => ServiceContainer.Settings.SpeedMeter;

    public SpeedMeterPage()
    {
        InitializeComponent();
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        MasterToggle.IsOn = Settings.Enabled;
        StartWithAppLockToggle.IsOn = Settings.StartWithAppLock;
        _loading = false;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = Settings.Enabled
            ? "On · the floating widget arrives with the engine in the next milestone."
            : "Off · turn this on to show the widget on your desktop.";
    }

    private void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.Enabled = MasterToggle.IsOn;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        UpdateStatus();
    }

    private void StartWithAppLockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.StartWithAppLock = StartWithAppLockToggle.IsOn;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(OthersPage));
        }
    }
}
