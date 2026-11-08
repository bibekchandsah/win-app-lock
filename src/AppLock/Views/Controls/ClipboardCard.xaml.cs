using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AppLock.Services;

namespace AppLock.Views.Controls;

public sealed partial class ClipboardCard : UserControl
{
    private DateTime? _pausedUntil;

    public ClipboardCard()
    {
        this.InitializeComponent();
        this.Loaded += ClipboardCard_Loaded;
        this.Unloaded += ClipboardCard_Unloaded;
    }

    private void ClipboardCard_Loaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardStore.DataChanged += OnClipboardDataChanged;
        RefreshUI();
    }

    private void ClipboardCard_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardStore.DataChanged -= OnClipboardDataChanged;
    }

    private void OnClipboardDataChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = RefreshStatsAsync();
        });
    }

    public void RefreshUI()
    {
        var settings = ServiceContainer.Settings.Clipboard;
        EnableToggle.IsOn = settings.Enabled;
        _pausedUntil = ServiceContainer.ClipboardMonitor.PausedUntil;
        UpdateStatus();
        _ = RefreshStatsAsync();
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

    private async Task RefreshStatsAsync()
    {
        try
        {
            int count = await ServiceContainer.ClipboardStore.GetItemCountAsync();
            long bytes = await ServiceContainer.ClipboardStore.GetStorageSizeBytesAsync();
            int max = ServiceContainer.Settings.Clipboard.MaxItems;

            ItemCountText.Text = $"{count} / {max}";
            StorageText.Text = FormatBytes(bytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardCard] Error refreshing stats: {ex.Message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
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

    private void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var frame = FindParentFrame(this);
        frame?.Navigate(typeof(ClipboardHistoryPage));
    }

    private void Pause5_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromMinutes(5));
    private void Pause15_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromMinutes(15));
    private void Pause60_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromMinutes(60));
    private void PauseIndefinite_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromDays(365));

    private void SetPause(TimeSpan duration)
    {
        ServiceContainer.ClipboardMonitor.SetPause(duration);
        _pausedUntil = ServiceContainer.ClipboardMonitor.PausedUntil;
        UpdateStatus();
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardMonitor.Resume();
        _pausedUntil = null;
        UpdateStatus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var frame = FindParentFrame(this);
        frame?.Navigate(typeof(ClipboardSettingsPage));
    }

    private async void DebugAddItemsButton_Click(object sender, RoutedEventArgs e)
    {
        DebugAddItemsButton.IsEnabled = false;
        try
        {
            await ServiceContainer.ClipboardStore.AddDebugItemsAsync(5, ServiceContainer.Settings.Clipboard.MaxItems);
            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardCard] Error adding debug items: {ex.Message}");
        }
        finally
        {
            DebugAddItemsButton.IsEnabled = true;
        }
    }

    private static Frame? FindParentFrame(DependencyObject child)
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is Frame frame) return frame;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}

