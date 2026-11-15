using System;
using System.Runtime.InteropServices;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace AppLock.Views;

public sealed partial class PortWatchToastWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMNCRP_DISABLED = 1;

    [StructLayout(LayoutKind.Sequential)]
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);

    private readonly Action? _onBlock;
    private readonly Action? _onAllow;
    private readonly Action? _onDetails;
    private DispatcherQueueTimer? _dismissTimer;

    public PortWatchToastWindow(
        string title,
        string subtitle,
        Action? onBlock = null,
        Action? onAllow = null,
        Action? onDetails = null,
        bool isSummary = false)
    {
        InitializeComponent();

        _onBlock = onBlock;
        _onAllow = onAllow;
        _onDetails = onDetails;

        TitleText.Text = title;
        SubtitleText.Text = subtitle;

        if (isSummary)
        {
            BlockBtn.Visibility = Visibility.Collapsed;
            AllowBtn.Content = "Allow all";
            DetailsBtn.Content = "Review";
        }

        ApplyTheme();
        SetupWindow();
        ApplyNoActivate();
        ApplyWindowChrome();
        PrimeFirstFrame();

        Card.Loaded += OnLoaded;
        Card.PointerEntered += (_, _) => _dismissTimer?.Stop();
        Card.PointerExited += (_, _) => StartDismissTimer();
    }

    private void ApplyTheme()
    {
        try
        {
            var theme = ServiceContainer.Settings?.SpeedMeter?.Alerts?.ToastTheme ?? "FollowSystem";
            RootGrid.RequestedTheme = theme switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };
        }
        catch
        {
            RootGrid.RequestedTheme = ElementTheme.Default;
        }
    }

    private void SetupWindow()
    {
        try
        {
            if (AppWindow is not { } appWindow) return;

            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortWatch] Toast setup error: {ex.Message}");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MeasureAndPosition();
        AnimateIn();
        StartDismissTimer();
    }

    private void PrimeFirstFrame()
    {
        try
        {
            const int estimatedWidth = 400;
            const int estimatedHeight = 110;

            if (AppWindow is not { } appWindow) return;

            appWindow.Resize(new SizeInt32(estimatedWidth, estimatedHeight));
            MoveToAnchor(estimatedWidth, estimatedHeight);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortWatch] Toast first frame error: {ex.Message}");
        }
    }

    private void MoveToAnchor(int width, int height)
    {
        if (AppWindow is not { } appWindow) return;

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var margin = 16;

        int x = work.X + work.Width - width - margin;
        int y = work.Y + work.Height - height - margin;

        appWindow.Move(new PointInt32(x, y));
    }

    private void MeasureAndPosition()
    {
        try
        {
            Card.Measure(new Windows.Foundation.Size(380, double.PositiveInfinity));
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;

            var width = Math.Max(1, (int)Math.Ceiling(Card.DesiredSize.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(Card.DesiredSize.Height * scale));

            if (AppWindow is not { } appWindow) return;

            appWindow.Resize(new SizeInt32(width, height));
            MoveToAnchor(width, height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortWatch] Toast layout error: {ex.Message}");
        }
    }

    private void ApplyWindowChrome()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            uint ncPolicy = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(uint));

            var margins = new MARGINS(-1);
            DwmExtendFrameIntoClientArea(hWnd, ref margins);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortWatch] Toast chrome error: {ex.Message}");
        }
    }

    private void ApplyNoActivate()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            var exStyle = (int)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortWatch] Toast chrome error: {ex.Message}");
        }
    }

    private void AnimateIn()
    {
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200)
        };

        Storyboard.SetTarget(fade, Card);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void StartDismissTimer()
    {
        _dismissTimer?.Stop();
        _dismissTimer = DispatcherQueue.CreateTimer();
        _dismissTimer.Interval = TimeSpan.FromSeconds(15);
        _dismissTimer.Tick += (_, _) => Close();
        _dismissTimer.Start();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BlockBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onBlock?.Invoke();
    }

    private void AllowBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onAllow?.Invoke();
    }

    private void DetailsBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onDetails?.Invoke();
    }
}
