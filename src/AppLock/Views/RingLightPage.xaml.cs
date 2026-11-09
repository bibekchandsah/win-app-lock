using System;
using AppLock.Services;
using AppLock.Services.RingLight;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class RingLightPage : Page
{
    private bool _suppressToggle;

    public RingLightPage()
    {
        this.InitializeComponent();
        this.Loaded   += OnLoaded;
        this.Unloaded += OnUnloaded;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.RingLightService.EnabledChanged += OnEnabledChanged;
        RefreshUI(ServiceContainer.RingLightService.IsOn);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.RingLightService.EnabledChanged -= OnEnabledChanged;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnEnabledChanged(object? sender, bool enabled)
    {
        // Marshal to UI thread if needed
        if (DispatcherQueue != null)
            DispatcherQueue.TryEnqueue(() => RefreshUI(enabled));
        else
            RefreshUI(enabled);
    }

    private void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        ServiceContainer.SetRingLightEnabled(MasterToggle.IsOn);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshUI(bool enabled)
    {
        _suppressToggle = true;
        MasterToggle.IsOn = enabled;
        _suppressToggle = false;

        var s = ServiceContainer.Settings.RingLight;
        StatusText.Text = enabled
            ? $"On · {s.Kelvin}K · {s.BrightnessPercent}%"
            : "Off";
    }
}
