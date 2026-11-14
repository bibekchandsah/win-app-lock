using System;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Services.SpeedMeter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views.Dialogs;

/// <summary>
/// Opt-in speed test. Nothing runs until the user presses Start; the result list lives in
/// <see cref="SpeedTestService"/> for the session.
/// </summary>
public sealed partial class SpeedTestDialog : ContentDialog
{
    private CancellationTokenSource? _cts;

    public SpeedTestDialog()
    {
        InitializeComponent();
        RefreshHistory();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.IsActive = true;

        _cts = new CancellationTokenSource();

        var progress = new Progress<string>(text => StatusText.Text = text);

        try
        {
            var result = await SpeedTestService.RunAsync(progress, _cts.Token);

            ResultText.Text = $"↓ {result.DownMbps:F1} Mbps · ↑ {result.UpMbps:F1} Mbps";
            StatusText.Text = $"Finished at {result.When:HH:mm}.";
            RefreshHistory();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Test failed: {ex.Message}";
        }
        finally
        {
            Progress.IsActive = false;
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;

            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not cancel: {ex.Message}";
        }
    }

    private void RefreshHistory()
    {
        HistoryPanel.Children.Clear();

        if (SpeedTestService.Results.Count == 0)
        {
            HistoryPanel.Children.Add(new TextBlock
            {
                Text = "None yet",
                FontSize = 12,
                Foreground = StatusText.Foreground
            });
            return;
        }

        for (var index = 0; index < SpeedTestService.Results.Count; index++)
        {
            var result = SpeedTestService.Results[index];
            var suffix = index == 0 ? string.Empty : $" · {index + 1}";

            HistoryPanel.Children.Add(new TextBlock
            {
                Text = $"{result.When:HH:mm} · ↓ {result.DownMbps:F1} Mbps · ↑ {result.UpMbps:F1} Mbps{suffix}",
                FontSize = 12,
                Foreground = StatusText.Foreground
            });
        }
    }
}
