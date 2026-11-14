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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

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
        ApplyNoActivate();
        AnimateIn();
        StartDismissTimer();
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Toast layout error: {ex.Message}");
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
