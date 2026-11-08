using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.Services;

namespace AppLock.Views.Controls;

public sealed partial class ClipboardCard : UserControl
{
    private bool _isUpdatingFromEvent;

    public ClipboardCard()
    {
        this.InitializeComponent();
        this.Loaded += ClipboardCard_Loaded;
        this.Unloaded += ClipboardCard_Unloaded;
    }

    private void ClipboardCard_Loaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;
        ServiceContainer.ClipboardEnabledChanged += OnClipboardEnabledChanged;

        ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged += OnClipboardPauseStateChanged;

        RefreshUI();
    }

    private void ClipboardCard_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
    }

    private void OnClipboardEnabledChanged(object? sender, bool enabled)
    {
        DispatcherQueue?.TryEnqueue(RefreshUI);
    }

    private void OnClipboardPauseStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(RefreshUI);
    }

    public void RefreshUI()
    {
        var settings = ServiceContainer.Settings.Clipboard;
        _isUpdatingFromEvent = true;
        try
        {
            if (EnableToggle.IsOn != settings.Enabled)
            {
                EnableToggle.IsOn = settings.Enabled;
            }

            bool isPaused = settings.Enabled && ServiceContainer.ClipboardMonitor.IsPaused;
            if (isPaused)
            {
                CardPauseBadge.Visibility = Visibility.Visible;
                var until = ServiceContainer.ClipboardMonitor.PausedUntil;
                if (until.HasValue && until.Value < DateTime.Now.AddYears(1))
                {
                    var rem = until.Value - DateTime.Now;
                    string remText = rem.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(rem.TotalMinutes)}m" : "<1m";
                    CardPauseText.Text = $"Paused ({remText})";
                }
                else
                {
                    CardPauseText.Text = "Paused";
                }
            }
            else
            {
                CardPauseBadge.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _isUpdatingFromEvent = false;
        }
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFromEvent) return;
        ServiceContainer.SetClipboardEnabled(EnableToggle.IsOn);
    }
}
