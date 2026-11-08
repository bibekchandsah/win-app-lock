using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        }
    }
}
