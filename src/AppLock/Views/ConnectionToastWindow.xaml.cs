using System;
using System.Runtime.InteropServices;
using AppLock.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace AppLock.Views;

/// <summary>
/// AppLock's own connection toast: themed, rounded, topmost, never activated, and independent of
/// the Windows notification settings.
/// </summary>
public sealed partial class ConnectionToastWindow : Window
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);

    private readonly ConnectionAlertSettings _alerts;
    private readonly int _stackIndex;
    private DispatcherQueueTimer? _dismissTimer;

    public ConnectionToastWindow(ConnectionAlertSettings alerts, bool lost, string title, string subtitle, int stackIndex)
    {
        InitializeComponent();

        _alerts = alerts;
        _stackIndex = stackIndex;

        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        StatusIcon.Glyph = lost ? "\uE783" : "\uE73E";
        StatusIcon.Foreground = new SolidColorBrush(lost
            ? Windows.UI.Color.FromArgb(255, 0xE8, 0x11, 0x23)
            : Windows.UI.Color.FromArgb(255, 0x4A, 0xDE, 0x80));

        RootGrid.Opacity = Math.Clamp(alerts.ToastOpacityPercent, 0, 100) / 100.0;

        ApplyTheme();
        SetupWindow();

        // Build the window's first frame before it is shown: an empty black rectangle used to
        // flash every time a toast appeared (for example while a colour picker was open).
        ApplyNoActivate();
        ApplyWindowChrome();
        PrimeFirstFrame();

        Card.Loaded += OnLoaded;
        Card.PointerEntered += (_, _) => _dismissTimer?.Stop();
        Card.PointerExited += (_, _) => StartDismissTimer();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _alerts.ToastTheme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    private void SetupWindow()
    {
        try
        {
            if (AppWindow is not { } appWindow) return;

            appWindow.IsShownInSwitchers = false;

            var presenter = OverlappedPresenter.Create();
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            appWindow.SetPresenter(presenter);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Toast setup error: {ex.Message}");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MeasureAndPosition();
        AnimateIn();
        StartDismissTimer();
    }

    /// <summary>Sizes and places the toast from an estimate, before the card has been measured.</summary>
    private void PrimeFirstFrame()
    {
        try
        {
            const int estimatedWidth = 380;
            const int estimatedHeight = 84;

            if (AppWindow is not { } appWindow) return;

            appWindow.Resize(new SizeInt32(estimatedWidth, estimatedHeight));
            MoveToAnchor(estimatedWidth, estimatedHeight);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Toast first frame error: {ex.Message}");
        }
    }

    private void MoveToAnchor(int width, int height)
    {
        if (AppWindow is not { } appWindow) return;

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var margin = Math.Clamp(_alerts.MarginPx, 0, 200);

        // Stack successive toasts away from their anchor edge.
        var offset = _stackIndex * (height + 8);

        int x = _alerts.Anchor switch
        {
            "TopLeft" or "MiddleLeft" or "BottomLeft" => work.X + margin,
            "TopRight" or "MiddleRight" or "BottomRight" => work.X + work.Width - width - margin,
            _ => work.X + (work.Width - width) / 2
        };

        int y = _alerts.Anchor switch
        {
            "TopLeft" or "TopCenter" or "TopRight" => work.Y + margin + offset,
            "BottomLeft" or "BottomCenter" or "BottomRight" => work.Y + work.Height - height - margin - offset,
            _ => work.Y + (work.Height - height) / 2 + offset
        };

        appWindow.Move(new PointInt32(x, y));
    }

    private void MeasureAndPosition()
    {
        try
        {
            Card.Measure(new Windows.Foundation.Size(360, double.PositiveInfinity));
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;

            var width = Math.Max(1, (int)Math.Ceiling(Card.DesiredSize.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(Card.DesiredSize.Height * scale));

            if (AppWindow is not { } appWindow) return;

            appWindow.Resize(new SizeInt32(width, height));
            MoveToAnchor(width, height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Toast layout error: {ex.Message}");
        }
    }

    /// <summary>
    /// Per-pixel transparency, so the rounded card's corners are not black rectangles, and no
    /// activation, so showing a toast never steals focus from an open flyout or dialog.
    /// </summary>
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
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Toast chrome error: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Toast chrome error: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows the toast without activating it, so open flyouts, color pickers, and other windows
    /// never lose focus when a notification appears.
    /// </summary>
    public void ShowWithoutActivating()
    {
        try
        {
            ApplyNoActivate();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            const int SW_SHOWNA = 8;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOACTIVATE = 0x0010;
            const uint SWP_SHOWWINDOW = 0x0040;

            ShowWindow(hWnd, SW_SHOWNA);
            SetWindowPos(hWnd, (nint)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Toast show error: {ex.Message}");
        }
    }

    /// <summary>Fade or slide-fade the card in; "None" keeps it static.</summary>
    private void AnimateIn()
    {
        var mode = _alerts.Animation;
        if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase)) return;

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = RootGrid.Opacity,
            Duration = TimeSpan.FromMilliseconds(180)
        };

        Storyboard.SetTarget(fade, Card);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        if (string.Equals(mode, "SlideTop", StringComparison.OrdinalIgnoreCase))
        {
            var transform = new TranslateTransform { Y = -16 };
            Card.RenderTransform = transform;

            var slide = new DoubleAnimation
            {
                From = -16,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "Y");
            storyboard.Children.Add(slide);
        }

        storyboard.Begin();
    }

    private void StartDismissTimer()
    {
        _dismissTimer ??= DispatcherQueue.CreateTimer();

        _dismissTimer.Stop();
        _dismissTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_alerts.DurationSeconds, 2, 15));
        _dismissTimer.Tick -= OnDismissTick;
        _dismissTimer.Tick += OnDismissTick;
        _dismissTimer.Start();
    }

    private void OnDismissTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _dismissTimer?.Stop();
        Close();
    }
}
