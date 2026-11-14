using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using AppLock.Services;
using AppLock.Views.Dialogs;
using AppLock.Services.SpeedMeter;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace AppLock.Views;

/// <summary>
/// Expandable network details flyout shown next to the speed widget: live graph, adapter and Wi-Fi
/// information, connection quality and the recent outage log. All of it is local API state; only the
/// opt-in features from later milestones would ever send traffic.
/// </summary>
public sealed partial class NetworkFlyoutWindow : Window
{
    private const int MaxGraphSamples = 300; // 5 minutes at the default 1 s interval
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMNCRP_DISABLED = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
        public MARGINS(int all)
        {
            cxLeftWidth = all;
            cxRightWidth = all;
            cyTopHeight = all;
            cyBottomHeight = all;
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);

    private readonly Queue<(double Down, double Up)> _samples = new();
    private readonly Polyline _downLine;
    private readonly Polyline _upLine;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _detailsTimer;

    private NetworkDetails _details = new();
    private string _localIp = string.Empty;
    private string? _publicIp;

    /// <summary>Raised when the user pauses or resumes tracking from the flyout.</summary>
    public event EventHandler<bool>? PauseToggled;

    public NetworkFlyoutWindow()
    {
        InitializeComponent();

        Title = "Network details";
        RootGrid.RequestedTheme = ElementTheme.Default;

        _downLine = CreateLine(Color.FromArgb(255, 74, 222, 128));
        _upLine = CreateLine(Color.FromArgb(255, 96, 165, 250));
        GraphCanvas.Children.Add(_downLine);
        GraphCanvas.Children.Add(_upLine);

        SetupWindow();

        Card.Loaded += (_, _) => MeasureAndResize();

        // Adapter/IP details change rarely; refreshing them on a slow timer keeps the flyout cheap.
        _detailsTimer = DispatcherQueue.CreateTimer();
        _detailsTimer.Interval = TimeSpan.FromSeconds(5);
        _detailsTimer.IsRepeating = true;
        _detailsTimer.Tick += (_, _) => RefreshDetails();
        _detailsTimer.Start();

        Closed += (_, _) => _detailsTimer?.Stop();
    }

    private static Polyline CreateLine(Color color) => new()
    {
        Stroke = new SolidColorBrush(color),
        StrokeThickness = 1.6,
        StrokeLineJoin = PenLineJoin.Round
    };

    private void SetupWindow()
    {
        try
        {
            if (AppWindow is { } appWindow)
            {
                appWindow.IsShownInSwitchers = false;

                var presenter = OverlappedPresenter.Create();
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                appWindow.SetPresenter(presenter);
            }

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            uint ncPolicy = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(uint));

            var margins = new MARGINS(-1);
            DwmExtendFrameIntoClientArea(hWnd, ref margins);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeedMeter] Flyout setup error: {ex.Message}");
        }
    }

    private void MeasureAndResize()
    {
        try
        {
            Card.Measure(new Size(380, double.PositiveInfinity));

            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var width = Math.Max(1, (int)Math.Ceiling(Card.DesiredSize.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(Card.DesiredSize.Height * scale));

            AppWindow?.Resize(new SizeInt32(width, height));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeedMeter] Flyout resize error: {ex.Message}");
        }
    }

    /// <summary>Puts the flyout beside the widget, flipping sides when it would leave the monitor.</summary>
    public void PlaceNear(PointInt32 widgetPosition, SizeInt32 widgetSize)
    {
        try
        {
            if (AppWindow is not { } appWindow) return;

            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var size = appWindow.Size;

            var left = widgetPosition.X - size.Width - 12;
            var top = widgetPosition.Y;

            if (left < work.X) left = widgetPosition.X + widgetSize.Width + 12;
            if (left + size.Width > work.X + work.Width) left = work.X + work.Width - size.Width - 8;

            if (top + size.Height > work.Y + work.Height) top = work.Y + work.Height - size.Height - 8;
            if (top < work.Y) top = work.Y + 8;

            left = Math.Max(work.X + 8, left);

            appWindow.Move(new PointInt32(left, top));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeedMeter] Flyout position error: {ex.Message}");
        }
    }

    /// <summary>One live reading from the speed service.</summary>
    public void Update(NetworkSpeedSample sample)
    {
        var settings = ServiceContainer.Settings.SpeedMeter;

        DownText.Text = $"↓ {SpeedFormatter.Format(sample.BytesDownPerSec, settings.Units, settings.Decimals)}";
        UpText.Text = $"↑ {SpeedFormatter.Format(sample.BytesUpPerSec, settings.Units, settings.Decimals)}";

        StatusText.Text = sample.HasAdapter ? sample.AdapterName : "No active adapter";

        _samples.Enqueue((sample.BytesDownPerSec, sample.BytesUpPerSec));
        while (_samples.Count > MaxGraphSamples) _samples.Dequeue();

        DrawGraph();
    }

    /// <summary>Session totals accumulated by the service since it started.</summary>
    public void SetSessionTotals(long downBytes, long upBytes)
    {
        SessionDownText.Text = $"Session ↓ {FormatBytes(downBytes)}";
        SessionUpText.Text = $"Session ↑ {FormatBytes(upBytes)}";
    }

    /// <summary>Today's totals from the usage store.</summary>
    public void SetTodayTotals(long downBytes, long upBytes)
    {
        TodayText.Text = $"Today ↓ {FormatBytes(downBytes)} · ↑ {FormatBytes(upBytes)}";
    }

    public void SetQuality(QualityStats stats)
    {
        QualityText.Text = stats.HasData
            ? $"Ping {stats.LatencyMs:F0} ms · Jitter {stats.JitterMs:F0} ms · Loss {stats.LossPercent:F0}% · {stats.Grade}"
            : "Ping — · Jitter — · Loss —";
    }

    public void SetPaused(bool paused)
    {
        PauseButton.Content = paused ? "Resume tracking" : "Pause tracking";
    }

    /// <summary>Rebuilds the info rows and the outage list.</summary>
    public void RefreshDetails()
    {
        _details = NetworkDetailsProvider.Read();
        _localIp = _details.LocalIp;

        AdapterText.Text = _details.HasAdapter ? _details.AdapterName : "—";
        ToolTipService.SetToolTip(AdapterText, _details.AdapterDescription);

        WifiText.Text = _details.IsWireless
            ? (_details.Ssid.Length > 0
                ? $"{_details.Ssid} · signal {_details.SignalBars}/5"
                : "Wi-Fi")
            : (_details.IsVpn ? "VPN adapter" : "Wired / virtual");

        LinkSpeedText.Text = _details.LinkSpeedBitsPerSec > 0
            ? $"{_details.LinkSpeedBitsPerSec / 1_000_000.0:F0} Mbps"
            : "—";

        LocalIpText.Text = _details.LocalIp.Length > 0 ? _details.LocalIp : "—";
        GatewayText.Text = _details.Gateway.Length > 0 ? _details.Gateway : "—";
        DnsText.Text = _details.Dns.Length > 0 ? _details.Dns : "—";

        CopyIpButton.IsEnabled = _details.LocalIp.Length > 0;

        UpdateOutages();
    }

    private void UpdateOutages()
    {
        var outages = ServiceContainer.SpeedMeterService?.Connectivity.RecentOutages?.ToList()
                      ?? new List<OutageRecord>();

        // An outage still in progress has no end yet; the monitor exposes only finished ones.
        OutagePanel.Children.Clear();
        NoOutageText.Visibility = outages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var outage in outages.Take(3))
        {
            var row = new TextBlock
            {
                FontSize = 12,
                Foreground = QualityText.Foreground,
                Text = $"{outage.Start:HH:mm} · offline for {FormatDuration(outage.Duration)}"
            };

            if (outage.AdapterName.Length > 0) row.Text += $" · {outage.AdapterName}";

            OutagePanel.Children.Add(row);
        }
    }

    private void DrawGraph()
    {
        var width = GraphCanvas.ActualWidth > 0 ? GraphCanvas.ActualWidth : 328;
        var height = GraphCanvas.ActualHeight > 0 ? GraphCanvas.ActualHeight : 72;

        var values = _samples.ToList();
        _downLine.Points = BuildPoints(values.Select(v => v.Down).ToList(), width, height);
        _upLine.Points = BuildPoints(values.Select(v => v.Up).ToList(), width, height);
    }

    private PointCollection BuildPoints(IReadOnlyList<double> values, double width, double height)
    {
        var points = new PointCollection();
        if (values.Count == 0) return points;

        var max = values.Max();
        if (max <= 0) max = 1;

        var step = values.Count > 1 ? width / (values.Count - 1) : width;

        for (var i = 0; i < values.Count; i++)
        {
            var y = height - Math.Clamp(values[i] / max, 0, 1) * (height - 2) - 1;
            points.Add(new Point(i * step, y));
        }

        return points;
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (_localIp.Length == 0) return;

        try
        {
            var package = new DataPackage();
            package.SetText(_localIp);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeedMeter] Copy IP error: {ex.Message}");
        }
    }

    /// <summary>Opt-in: one request to api.ipify.org, only when this button is pressed.</summary>
    private async void PublicIp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_publicIp is not null) CopyToClipboard(_publicIp);
            else
            {
                PublicIpButton.IsEnabled = false;
                PublicIpText.Visibility = Visibility.Visible;
                PublicIpText.Text = "Contacting api.ipify.org…";

                _publicIp = await SpeedTestService.GetPublicIpAsync(CancellationToken.None);

                PublicIpText.Text = $"Public IP: {_publicIp}";
                PublicIpButton.Content = "Copy public IP";
            }
        }
        catch (Exception ex)
        {
            PublicIpText.Visibility = Visibility.Visible;
            PublicIpText.Text = $"Public IP unavailable: {ex.Message}";
        }
        finally
        {
            PublicIpButton.IsEnabled = true;
        }
    }

    private async void SpeedTest_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SpeedTestDialog { XamlRoot = Card.XamlRoot };
        await dialog.ShowAsync();
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeedMeter] Clipboard error: {ex.Message}");
        }
    }

    private void OpenNetworkSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeedMeter] Open network settings error: {ex.Message}");
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        var paused = PauseButton.Content as string != "Resume tracking";
        PauseToggled?.Invoke(this, paused);
        SetPaused(paused);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value.ToString(index == 0 ? "F0" : "F1", CultureInfo.CurrentCulture)} {units[index]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} h {duration.Minutes} min";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes} min {duration.Seconds} s";
        return $"{(int)duration.TotalSeconds} s";
    }
}
