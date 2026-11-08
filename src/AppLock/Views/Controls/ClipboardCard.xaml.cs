using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AppLock.Services;

namespace AppLock.Views.Controls;

public sealed partial class ClipboardCard : UserControl
{
    public ClipboardCard()
    {
        this.InitializeComponent();
        this.Loaded += ClipboardCard_Loaded;
    }

    private void ClipboardCard_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshUI();
    }

    public void RefreshUI()
    {
        var settings = ServiceContainer.Settings.Clipboard;
        EnableToggle.IsOn = settings.Enabled;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var settings = ServiceContainer.Settings.Clipboard;
        if (!settings.Enabled)
        {
            StatusText.Text = "Disabled";
            StatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        }
        else if (ServiceContainer.ClipboardMonitor.IsPaused)
        {
            var until = ServiceContainer.ClipboardMonitor.PausedUntil;
            StatusText.Text = until.HasValue ? $"Paused until {until.Value:HH:mm}" : "Paused";
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorAttentionBrush"];
        }
        else
        {
            StatusText.Text = "Capturing";
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var settings = ServiceContainer.Settings.Clipboard;
        if (settings.Enabled != EnableToggle.IsOn)
        {
            settings.Enabled = EnableToggle.IsOn;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            if (settings.Enabled)
            {
                ServiceContainer.ClipboardMonitor.Start();
            }
            else
            {
                ServiceContainer.ClipboardMonitor.Stop();
            }
            UpdateStatus();
        }
    }
}
