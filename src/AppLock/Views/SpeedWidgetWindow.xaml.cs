using System;
using System.Linq;
using System.Runtime.InteropServices;
using AppLock.Services;
using AppLock.Services.SpeedMeter;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace AppLock.Views;

/// <summary>
/// The floating speed widget: borderless, rounded, topmost, never activates, draggable,
/// snappable, optionally click-through and excluded from screen capture.
/// </summary>
public sealed partial class SpeedWidgetWindow : Window
{
    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMNCRP_DISABLED = 1;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private const int VK_MENU = 0x12;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _altTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _tapTimer;

    private bool _dragging;
    private bool _clickThroughActive;
    private bool _captureExcluded;
    private bool _hiddenForFullscreen;
    private POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;

    public SpeedWidgetWindow()
    {
        InitializeComponent();
        Title = "Speed Meter";

        SetupWindow();

        Widget.Loaded += OnWidgetLoaded;

        Widget.PointerPressed += OnPointerPressed;
        Widget.PointerMoved += OnPointerMoved;
        Widget.PointerReleased += OnPointerReleased;
        Widget.PointerCaptureLost += OnPointerCaptureLost;

        // A single click opens the details flyout; the short delay lets a double-click win instead.
        _tapTimer = DispatcherQueue.CreateTimer();
        _tapTimer.Interval = TimeSpan.FromMilliseconds(250);
        _tapTimer.IsRepeating = false;
        _tapTimer.Tick += (_, _) => ServiceContainer.SpeedMeterService?.ToggleFlyout();

        Widget.Tapped += OnWidgetTapped;
        Widget.DoubleTapped += OnWidgetDoubleTapped;

        // While click-through is on, holding Alt temporarily makes the widget draggable again.
        _altTimer = DispatcherQueue.CreateTimer();
        _altTimer.Interval = TimeSpan.FromMilliseconds(150);
        _altTimer.IsRepeating = true;
        _altTimer.Tick += (_, _) => UpdateClickThroughForAlt();

        Closed += (_, _) =>
        {
            _altTimer?.Stop();
            _tapTimer?.Stop();
        };
    }

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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Window setup error: {ex.Message}");
        }
    }

    private void OnWidgetLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowChrome();
        ResizeToContent();
        PositionOnScreen();

        Update(new NetworkSpeedSample());
        ApplyClickThrough();
    }

    /// <summary>
    /// After the first frame exists, make the window per-pixel transparent (so the rounded card
    /// shows the desktop through its corners) and keep it out of Alt+Tab and focus.
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

            // IsShownInSwitchers=false already keeps it out of Alt+Tab and the taskbar; the volume
            // overlay had WS_EX_TOOLWINDOW make the window invisible here, so it is not applied.
            var exStyle = (int)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            exStyle |= WS_EX_NOACTIVATE;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle);

            ApplyCaptureAffinity();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Chrome error: {ex.Message}");
        }
    }

    /// <summary>Keeps the widget out of screenshots and screen sharing when the setting is on.</summary>
    private void ApplyCaptureAffinity()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            var exclude = ServiceContainer.Settings.SpeedMeter.HideFromCapture;
            if (exclude == _captureExcluded) return;

            _captureExcluded = SetWindowDisplayAffinity(hWnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Capture affinity error: {ex.Message}");
        }
    }

    private void ResizeToContent()
    {
        try
        {
            Widget.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = Widget.DesiredSize;

            // AppWindow sizes are physical pixels, DesiredSize is DIPs.
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;

            var width = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));

            AppWindow?.Resize(new SizeInt32(width, height));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Resize error: {ex.Message}");
        }
    }

    /// <summary>Finds the monitor the widget belongs on: the saved one if it still exists.</summary>
    private DisplayArea ResolveArea()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;
        var areas = DisplayArea.FindAll();

        if (!string.IsNullOrEmpty(settings.MonitorId))
        {
            var match = areas.FirstOrDefault(a => DescribeArea(a.WorkArea) == settings.MonitorId);
            if (match is not null) return match;
        }

        return AppWindow is { } appWindow
            ? DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
            : areas.First();
    }

    private static string DescribeArea(RectInt32 work) => $"{work.X},{work.Y},{work.Width},{work.Height}";

    /// <summary>Places the widget at the stored percentage of its monitor work area.</summary>
    private void PositionOnScreen()
    {
        try
        {
            if (AppWindow is not { } appWindow) return;

            var settings = ServiceContainer.Settings.SpeedMeter;
            var area = ResolveArea();
            var size = appWindow.Size;

            var position = WidgetPositioner.FromPercent(settings, area.WorkArea, size);
            position = WidgetPositioner.Clamp(position, size, area.WorkArea);

            appWindow.Move(position);

            WidgetPositioner.SavePercent(settings, position, size, area.WorkArea);
            settings.MonitorId = DescribeArea(area.WorkArea);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Position error: {ex.Message}");
        }
    }

    /// <summary>Where the widget currently sits, so the details flyout can open beside it.</summary>
    public (PointInt32 Position, SizeInt32 Size)? GetBounds()
        => AppWindow is { } appWindow ? (appWindow.Position, appWindow.Size) : null;

    /// <summary>Moves the widget back onto the primary monitor and stores that position.</summary>
    public void ResetPosition()
    {
        var settings = ServiceContainer.Settings.SpeedMeter;
        settings.PositionPercentX = 98;
        settings.PositionPercentY = 90;
        settings.MonitorId = null;

        PositionOnScreen();
    }

    /// <summary>Re-applies appearance, chrome flags and geometry after a settings change.</summary>
    public void ApplySettings()
    {
        Widget.ApplyAppearance(ServiceContainer.Settings.SpeedMeter);
        ResizeToContent();
        PositionOnScreen();
        ApplyCaptureAffinity();
        ApplyClickThrough();
    }

    /// <summary>Writes one reading into the widget with the configured units and decimals.</summary>
    public void Update(NetworkSpeedSample sample)
    {
        var settings = ServiceContainer.Settings.SpeedMeter;
        var units = settings.Units;
        var decimals = settings.Decimals;

        Widget.SetValues(
            SpeedFormatter.Format(sample.BytesDownPerSec, units, decimals),
            SpeedFormatter.Format(sample.BytesUpPerSec, units, decimals));

        Widget.AddSparkSample(sample.BytesDownPerSec, sample.BytesUpPerSec);
    }

    /// <summary>Updates the optional CPU / RAM / disk / GPU / battery rows.</summary>
    public void SetStats(SystemStats stats) => Widget.SetStats(stats);

    /// <summary>Today's totals row and the metered badge.</summary>
    public void SetUsage(long todayDown, long todayUp, bool metered)
        => Widget.SetUsage(todayDown, todayUp, metered);

    /// <summary>Hides for full-screen apps / unhides afterwards without closing the window.</summary>
    public void SetFullscreenHidden(bool hidden)
    {
        if (_hiddenForFullscreen == hidden) return;

        try
        {
            if (hidden)
            {
                AppWindow?.Hide();
            }
            else
            {
                AppWindow?.Show();
                KeepOnTop();
            }

            _hiddenForFullscreen = hidden;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Visibility error: {ex.Message}");
        }
    }

    private void KeepOnTop()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOACTIVATE = 0x0010;

            SetWindowPos(hWnd, (nint)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Topmost error: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ── Dragging ─────────────────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var settings = ServiceContainer.Settings.SpeedMeter;
        if (!settings.Draggable || settings.LockPosition || _clickThroughActive) return;

        var point = e.GetCurrentPoint(Widget);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (!GetCursorPos(out _dragStartCursor)) return;
        if (AppWindow is null) return;

        _dragStartWindow = AppWindow.Position;
        _dragging = Widget.CapturePointer(e.Pointer);

        // Not marked handled: a press-and-release without movement should still raise Tapped.
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || AppWindow is null) return;
        if (!GetCursorPos(out var cursor)) return;

        var target = new PointInt32(
            _dragStartWindow.X + (cursor.x - _dragStartCursor.x),
            _dragStartWindow.Y + (cursor.y - _dragStartCursor.y));

        AppWindow.Move(target);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        Widget.ReleasePointerCapture(e.Pointer);
        FinishDrag();
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        FinishDrag();
    }

    /// <summary>Snaps to a nearby edge, then stores the position as percentages.</summary>
    private void FinishDrag()
    {
        try
        {
            if (AppWindow is not { } appWindow) return;

            var settings = ServiceContainer.Settings.SpeedMeter;
            var area = ResolveArea();
            var size = appWindow.Size;
            var position = appWindow.Position;

            if (settings.SnapToEdges)
            {
                position = WidgetPositioner.Snap(position, size, area.WorkArea);
            }

            position = WidgetPositioner.Clamp(position, size, area.WorkArea);
            appWindow.Move(position);

            WidgetPositioner.SavePercent(settings, position, size, area.WorkArea);
            settings.MonitorId = DescribeArea(area.WorkArea);
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Drop error: {ex.Message}");
        }
    }

    private void OnWidgetTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_dragging) return;
        _tapTimer?.Start();
    }

    private void OnWidgetDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Cancel the pending single-click action so a double-click never opens the flyout twice.
        _tapTimer?.Stop();

        var settings = ServiceContainer.Settings.SpeedMeter;
        switch (settings.DoubleClickAction)
        {
            case "Compact":
                settings.Compact = !settings.Compact;
                ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                ApplySettings();
                break;

            case "None":
                break;

            default:
                ServiceContainer.SpeedMeterService?.ToggleFlyout();
                break;
        }

        e.Handled = true;
    }

    // ── Click-through ────────────────────────────────────────────────────────

    /// <summary>Applies WS_EX_TRANSPARENT so clicks pass through, unless Alt is held.</summary>
    private void ApplyClickThrough(bool forceOff = false)
    {
        var want = !forceOff && ServiceContainer.Settings.SpeedMeter.ClickThrough;

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            var exStyle = (int)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            var updated = want
                ? exStyle | WS_EX_TRANSPARENT
                : exStyle & ~WS_EX_TRANSPARENT;

            if (updated != exStyle)
            {
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, updated);
            }

            _clickThroughActive = want;

            if (want) _altTimer?.Start();
            else _altTimer?.Stop();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Click-through error: {ex.Message}");
        }
    }

    private void UpdateClickThroughForAlt()
    {
        if (!ServiceContainer.Settings.SpeedMeter.ClickThrough)
        {
            _altTimer?.Stop();
            return;
        }

        var altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        ApplyClickThrough(forceOff: altDown);
    }
}
