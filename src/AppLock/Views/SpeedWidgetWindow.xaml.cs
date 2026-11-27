using System;
using System.Linq;
using System.Runtime.InteropServices;
using AppLock.Services;
using AppLock.Services.SpeedMeter;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private bool _idleHidden;
    private bool _disconnectedHidden;
    private bool _currentlyHidden;

    public SpeedWidgetWindow()
    {
        InitializeComponent();
        Title = "Speed Meter";

        SetupWindow();

        // Build the first frame before the window is ever shown. Windows would otherwise display a
        // default-sized, unpainted (black) rectangle until Loaded ran, which is the large black
        // box that appeared when the app started.
        Update(new NetworkSpeedSample());
        ApplyBackdrop();
        ApplyWindowChrome();
        ResizeToContent();
        PositionOnScreen();

        Widget.Loaded += OnWidgetLoaded;

        // Rows and top-app lines change at runtime; the window has to follow the content.
        Widget.SizeChanged += (_, _) => ResizeToContent();

        Widget.ManipulationMode = ManipulationModes.None;
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
        ApplyBackdrop();
        ApplyWindowChrome();

        // Rebuild once more from the saved settings: the constructor runs before every service has
        // finished coming up, and this is what guarantees all enabled rows exist on first show.
        Widget.ApplyAppearance(ServiceContainer.Settings.SpeedMeter);
        ResizeToContent();
        PositionOnScreen();

        Update(new NetworkSpeedSample());
        SetStats(SystemStats.Empty);
        ApplyClickThrough();
    }

    /// <summary>
    /// Glass uses the acrylic system backdrop and Follow Windows uses Mica; the widget draws its
    /// own rounded card on top. Without a backdrop those presets only showed the window's black
    /// default background, which is why opacity seemed to do nothing. Transparent draws no card fill
    /// at all, so it needs the transparent backdrop for the same reason: otherwise the window's own
    /// opaque surface is what shows behind the readings.
    /// </summary>
    private ConfigurableAcrylicBackdrop? _acrylicBackdrop;

    private void ApplyBackdrop()
    {
        try
        {
            var settings = ServiceContainer.Settings.SpeedMeter;
            var preset = settings.ThemePreset ?? "Glass";

            // Follow Windows means Mica; Glass uses a configurable Acrylic blur + tint whose
            // opacity slider drives the frost/blur intensity so background content shows through.
            var mica = MicaController.IsSupported();
            var acrylic = DesktopAcrylicController.IsSupported();

            bool useCustomBlur = string.Equals(preset, "Custom", StringComparison.OrdinalIgnoreCase)
                && settings.BlurAmountPercent > 0;

            if ((preset == "Glass" || useCustomBlur) && acrylic)
            {
                _acrylicBackdrop ??= new ConfigurableAcrylicBackdrop();
                var tint = Controls.SpeedWidgetControl.ParseColor(
                    settings.BackgroundColor,
                    Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20));
                float opacity = Math.Clamp(settings.BackgroundOpacityPercent, 0, 100) / 100f;
                float blur = Math.Clamp(settings.BlurAmountPercent, 0, 100) / 100f;
                float tintOpacity = preset == "Glass" ? opacity * 0.65f : opacity;
                float luminosity = preset == "Glass" ? opacity * 0.85f : blur * 0.9f;
                _acrylicBackdrop.Update(tint, tintOpacity, luminosity);

                if (!ReferenceEquals(SystemBackdrop, _acrylicBackdrop))
                {
                    SystemBackdrop = _acrylicBackdrop;
                }
            }
            else
            {
                _acrylicBackdrop = null;
                SystemBackdrop = preset switch
                {
                    "FollowWindows" => mica ? new AlwaysActiveMicaBackdrop() : new TransparentBackdrop(),
                    _ => new TransparentBackdrop()
                };
            }

            Widget.SetBackdropAvailable(preset switch
            {
                "Glass" => acrylic,
                "Custom" => useCustomBlur && acrylic,
                "FollowWindows" => mica,
                _ => false
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Backdrop error: {ex.Message}");
        }
    }

    private abstract class AlwaysActiveBackdropBase : SystemBackdrop
    {
        private ISystemBackdropControllerWithTargets? _controller;
        private SystemBackdropConfiguration? _config;

        protected abstract ISystemBackdropControllerWithTargets CreateController();
        protected virtual void ConfigureController(ISystemBackdropControllerWithTargets controller) { }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            _controller = CreateController();
            _config = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = ResolveBackdropTheme(xamlRoot)
            };
            ConfigureController(_controller);
            _controller.AddSystemBackdropTarget(connectedTarget);
            _controller.SetSystemBackdropConfiguration(_config);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller?.Dispose();
            _controller = null;
            _config = null;
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
            if (_controller != null && _config != null)
            {
                _config.IsInputActive = true;
                _config.Theme = ResolveBackdropTheme(xamlRoot);
                _controller.SetSystemBackdropConfiguration(_config);
            }
        }

        private static SystemBackdropTheme ResolveBackdropTheme(XamlRoot xamlRoot)
        {
            var theme = (xamlRoot?.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
            return theme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Light
                    ? SystemBackdropTheme.Light
                    : SystemBackdropTheme.Dark
            };
        }

        protected ISystemBackdropControllerWithTargets? ActiveController => _controller;
    }

    private sealed class AlwaysActiveMicaBackdrop : AlwaysActiveBackdropBase
    {
        protected override ISystemBackdropControllerWithTargets CreateController() => new MicaController();
    }

    private sealed class ConfigurableAcrylicBackdrop : AlwaysActiveBackdropBase
    {
        private Windows.UI.Color _tintColor = Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20);
        private float _tintOpacity = 0.45f;
        private float _luminosityOpacity = 0.6f;

        public void Update(Windows.UI.Color tintColor, float tintOpacity, float luminosityOpacity)
        {
            _tintColor = tintColor;
            _tintOpacity = Math.Clamp(tintOpacity, 0f, 1f);
            _luminosityOpacity = Math.Clamp(luminosityOpacity, 0f, 1f);
            if (ActiveController is DesktopAcrylicController acrylic)
            {
                ApplyToController(acrylic);
            }
        }

        protected override ISystemBackdropControllerWithTargets CreateController() => new DesktopAcrylicController();

        protected override void ConfigureController(ISystemBackdropControllerWithTargets controller)
        {
            if (controller is DesktopAcrylicController acrylic)
            {
                ApplyToController(acrylic);
            }
        }

        private void ApplyToController(DesktopAcrylicController controller)
        {
            controller.TintColor = _tintColor;
            controller.FallbackColor = _tintColor;
            controller.TintOpacity = _tintOpacity;
            controller.LuminosityOpacity = _luminosityOpacity;
        }
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
            // The control is already in the live tree, so its desired size is cached for the
            // window's current constraints; invalidating gives the intrinsic (unconstrained) size.
            Widget.InvalidateMeasure();
            Widget.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = Widget.DesiredSize;

            // The window's current size constrains the control, so the intrinsic size wins: that
            // is what keeps a newly enabled top-app line or a longer process name from clipping.
            size = new Windows.Foundation.Size(
                Math.Max(size.Width, Widget.ActualWidth),
                Math.Max(size.Height, Widget.ActualHeight));

            // AppWindow sizes are physical pixels, DesiredSize is DIPs.
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;

            // Before the first layout pass the content can measure to nothing; a small fallback
            // keeps the window from appearing at its huge default size.
            if (size.Width < 20 || size.Height < 20)
            {
                size = new Windows.Foundation.Size(320, 80);
            }

            var width = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));

            // "CPU · process 12%" needs more room than the plain speed row; without this the
            // top-app lines were cut at the window edge.
            if (ServiceContainer.Settings.SpeedMeter.ShowTopApps)
            {
                width = Math.Max(width, (int)Math.Ceiling(210 * scale));
            }

            if (AppWindow is not { } appWindow) return;
            if (appWindow.Size.Width == width && appWindow.Size.Height == height) return;

            appWindow.Resize(new SizeInt32(width, height));

            // Growing downwards must not push the widget off the bottom of the screen.
            var area = ResolveArea();
            var clamped = WidgetPositioner.Clamp(appWindow.Position, new SizeInt32(width, height), area.WorkArea);

            if (clamped.X != appWindow.Position.X || clamped.Y != appWindow.Position.Y)
            {
                appWindow.Move(clamped);
            }
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

        // Indexed access on purpose: enumerating DisplayArea.FindAll() throws InvalidCastException
        // in this WinRT projection, which aborted positioning and left the widget at the default
        // cascade position (and never saved it).
        var areas = DisplayArea.FindAll();

        if (!string.IsNullOrEmpty(settings.MonitorId))
        {
            for (var i = 0; i < areas.Count; i++)
            {
                if (DescribeArea(areas[i].WorkArea) == settings.MonitorId) return areas[i];
            }
        }

        if (AppWindow is { } appWindow)
        {
            var fromWindow = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            if (fromWindow is not null) return fromWindow;
        }

        return areas.Count > 0 ? areas[0] : DisplayArea.Primary;
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

            // No SavePercent here: this also runs while the window is still being created, when
            // its size and monitor are not final yet, and it used to overwrite the remembered
            // spot with the default one. Only a drag or "Reset position" stores the position.
            appWindow.Move(position);
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

        if (AppWindow is { } appWindow)
        {
            var area = ResolveArea();
            WidgetPositioner.SavePercent(settings, appWindow.Position, appWindow.Size, area.WorkArea);
            settings.MonitorId = DescribeArea(area.WorkArea);
        }
    }

    /// <summary>Re-applies appearance, chrome flags and geometry after a settings change.</summary>
    public void ApplySettings()
    {
        Widget.ApplyAppearance(ServiceContainer.Settings.SpeedMeter);
        ApplyBackdrop();
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

        Widget.SetRates(
            SpeedFormatter.Format(sample.BytesDownPerSec, units, decimals),
            SpeedFormatter.Format(sample.BytesUpPerSec, units, decimals),
            sample.BytesDownPerSec,
            sample.BytesUpPerSec);

        Widget.AddSparkSample(sample.BytesDownPerSec, sample.BytesUpPerSec);

        // The widget is sized to its content; the content grows when a reading arrives.
        ResizeToContent();
    }

    /// <summary>Feeds the top CPU / RAM / disk / GPU process lines.</summary>
    public void SetTopApps(TopAppsStats stats)
    {
        Widget.SetTopApps(stats);
        ResizeToContent();
    }

    /// <summary>Updates the optional CPU / RAM / disk / GPU / battery rows.</summary>
    public void SetStats(SystemStats stats)
    {
        Widget.SetStats(stats);
        ResizeToContent();
    }

    /// <summary>Today's totals row plus the metered and VPN badges.</summary>
    public void SetUsage(long todayDown, long todayUp, bool metered, bool vpn)
    {
        Widget.SetUsage(todayDown, todayUp, metered, vpn);
        ResizeToContent();
    }

    /// <summary>Hides for full-screen apps / unhides afterwards without closing the window.</summary>
    public void SetFullscreenHidden(bool hidden)
    {
        _hiddenForFullscreen = hidden;
        ApplyVisibility();
    }

    /// <summary>Hidden because traffic stayed below the idle threshold.</summary>
    public void SetIdleHidden(bool hidden)
    {
        _idleHidden = hidden;
        ApplyVisibility();
    }

    /// <summary>Hidden because the connection is down and the user asked for that.</summary>
    public void SetDisconnectedHidden(bool hidden)
    {
        _disconnectedHidden = hidden;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        var hide = _hiddenForFullscreen || _idleHidden || _disconnectedHidden;
        if (hide == _currentlyHidden) return;

        try
        {
            if (hide)
            {
                AppWindow?.Hide();
            }
            else
            {
                AppWindow?.Show();
                KeepOnTop();
            }

            _currentlyHidden = hide;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Visibility error: {ex.Message}");
        }
    }

    /// <summary>True when the mouse is over the widget, used to "peek" while idle-hidden.</summary>
    public bool ContainsCursor()
    {
        try
        {
            if (AppWindow is not { } appWindow) return false;
            if (!GetCursorPos(out var cursor)) return false;

            var position = appWindow.Position;
            var size = appWindow.Size;

            return cursor.x >= position.X && cursor.x < position.X + size.Width
                && cursor.y >= position.Y && cursor.y < position.Y + size.Height;
        }
        catch (Exception)
        {
            return false;
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

    private Windows.Foundation.Point _dragStartPointDip;
    private POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private bool _dragIsTouch;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var settings = ServiceContainer.Settings.SpeedMeter;
        if (!settings.Draggable || settings.LockPosition || _clickThroughActive) return;

        var point = e.GetCurrentPoint(Widget);
        if (!point.Properties.IsLeftButtonPressed && !point.IsInContact) return;
        if (AppWindow is null) return;

        _dragIsTouch = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;
        if (_dragIsTouch)
        {
            _dragStartPointDip = point.Position;
        }
        else
        {
            if (!GetCursorPos(out _dragStartCursor)) return;
            _dragStartWindow = AppWindow.Position;
        }

        _dragging = Widget.CapturePointer(e.Pointer);

        // Not marked handled: a press-and-release without movement should still raise Tapped.
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || AppWindow is null) return;

        if (!_dragIsTouch)
        {
            if (!GetCursorPos(out var cursor)) return;
            var target = new PointInt32(
                _dragStartWindow.X + (cursor.x - _dragStartCursor.x),
                _dragStartWindow.Y + (cursor.y - _dragStartCursor.y));
            AppWindow.Move(target);
            return;
        }

        var point = e.GetCurrentPoint(Widget);
        double scale = Widget.XamlRoot?.RasterizationScale ?? 1.0;
        int dx = (int)Math.Round((point.Position.X - _dragStartPointDip.X) * scale);
        int dy = (int)Math.Round((point.Position.Y - _dragStartPointDip.Y) * scale);
        if (dx == 0 && dy == 0) return;

        var pos = AppWindow.Position;
        AppWindow.Move(new PointInt32(pos.X + dx, pos.Y + dy));
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
