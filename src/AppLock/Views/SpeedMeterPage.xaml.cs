using System;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.SpeedMeter;
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

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ServiceContainer.SpeedMeterService is { } service)
        {
            service.SampleUpdated -= OnSampleUpdated;
            service.SampleUpdated += OnSampleUpdated;
        }

        UpdateStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ServiceContainer.SpeedMeterService is { } service)
        {
            service.SampleUpdated -= OnSampleUpdated;
        }
    }

    private void OnSampleUpdated(object? sender, NetworkSpeedSample sample) => UpdateStatus(sample);

    private void LoadFromSettings()
    {
        _loading = true;
        MasterToggle.IsOn = Settings.Enabled;
        StartWithAppLockToggle.IsOn = Settings.StartWithAppLock;
        _loading = false;

        UpdateStatus();
    }

    private void UpdateStatus(NetworkSpeedSample? sample = null)
    {
        if (!Settings.Enabled)
        {
            StatusText.Text = "Off · turn this on to show the widget on your desktop.";
            return;
        }

        if (sample is null)
        {
            StatusText.Text = "On · waiting for the first reading…";
            return;
        }

        var units = Settings.Units;
        var decimals = Settings.Decimals;
        var down = SpeedFormatter.Format(sample.BytesDownPerSec, units, decimals);
        var up = SpeedFormatter.Format(sample.BytesUpPerSec, units, decimals);

        var adapter = sample.HasAdapter ? sample.AdapterName : "No active adapter";
        var link = sample.LinkSpeedBitsPerSec > 0
            ? $" · link {SpeedFormatter.Format(sample.LinkSpeedBitsPerSec / 8.0, "Bits", 0)}"
            : string.Empty;

        StatusText.Text = $"On · {adapter}{link} · ↓ {down} ↑ {up}";
    }

    private void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        ServiceContainer.SetSpeedMeterEnabled(MasterToggle.IsOn);
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
