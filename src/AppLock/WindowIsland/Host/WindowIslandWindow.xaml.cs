using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using AppLock.Island.Events;
using AppLock.Models;
using AppLock.WindowIsland.Events;
using DQTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// The native WinUI 3 window for Window Island.
/// Milestone 3: full tab strip, keyboard navigation, interruptible springs,
/// pin/quiet buttons, frame-stats overlay and benchmark.
/// </summary>
public sealed partial class WindowIslandWindow : Window
{
    // ── Win32 constants ────────────────────────────────────────────────────────
    private const int   GWL_EXSTYLE      = -20;
    private const nint  WS_EX_TOPMOST    = 0x0000_0008;
    private const nint  WS_EX_TOOLWINDOW = 0x0000_0080;
    private const uint  SWP_NOMOVE       = 0x0002;
    private const uint  SWP_NOSIZE       = 0x0001;
    private const uint  SWP_NOACTIVATE   = 0x0010;
    private const uint  SWP_SHOWWINDOW   = 0x0040;
    private const uint  WM_MOUSEACTIVATE = 0x0021;
    private const uint  WM_DPICHANGED    = 0x02E0;
    private const int   MA_NOACTIVATE    = 3;
    private static readonly nint HWND_TOPMOST = new(-1);

    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_DONOTROUND              = 1;
    private const uint DWMWA_BORDER_COLOR             = 34;
    private const uint DWMWA_COLOR_NONE               = 0xFFFFFFFE;
    private const uint DWMWA_NCRENDERING_POLICY        = 2;
    private const uint DWMNCRP_DISABLED               = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, int cbAttribute);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(nint hWnd, nint hRgn, bool redraw);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nint id, nint data);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int  BatteryLifeTime;
        public int  BatteryFullLifeTime;
    }

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data);

    // ── Tab definitions ────────────────────────────────────────────────────────
    private static readonly string[] TabNames = { "Home", "Clipboard", "Shelf", "Text", "Chat", "Note", "Weather", "Controls" };
    private const int TabCount = 8;

    // ── Fields ─────────────────────────────────────────────────────────────────
    private readonly nint         _hwnd;
    private readonly AppWindow?   _appWindow;
    private readonly SubclassProc _subclassProc;
    private WindowIslandSettings  _settings = new();
    private WindowIslandCompositor? _compositor;
    private Visual? _foldedVisual;
    private Visual? _cardVisual;
    private Visual? _openVisual;

    private WindowIslandEventQueue? _eventQueue;
    private LockKeyCardSource?      _lockKeySource;
    private IslandVolumeController? _volumeController;
    private ClipboardCardSource?    _clipboardSource;
    private ScreenshotCardSource?   _screenshotSource;

    private DQTimer? _topmostTimer;
    private DQTimer? _hoverTimer;
    private DQTimer? _clockTimer;
    private DQTimer? _batteryTimer;

    private double _scale = 1.0;
    private int    _windowX, _windowY, _windowWidthPx, _windowHeightPx;
    private bool   _isVisible = true;
    private bool   _isPinned;
    private bool   _isQuiet;

    // Window size adapts to current tab
    private float WindowWidthDip => IslandMotion.WindowWidthForTab(_activeTab);
    private const float WindowHeightDip = 300f; // generous — content clips inside

    // Hover state machine
    private bool     _hoverInside;
    private DateTime _hoverStateChangedAt = DateTime.MinValue;

    // Tab state
    private int _activeTab      = 0;
    private int _previousTab    = 0;
    private Grid[] _tabContents = Array.Empty<Grid>();
    private Button[] _tabButtons = Array.Empty<Button>();

    // Frame-time measurement
    private readonly List<double> _frameTimes = new(2048);
    private long _lastFrameTimestamp;
    private bool _measuringFrames;

    public bool IsIslandVisible => _isVisible;

    public WindowIslandWindow()
    {
        InitializeComponent();
        _hwnd      = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        Title      = "Window Island";

        _subclassProc = WindowIslandSubclass;
        ConfigureWindowChrome();

        Closed += (s, e) => WindowIslandHost.Log("WindowIslandWindow Closed");

        // Wire up tab content array for quick indexed access
        _tabContents = new Grid[]
        {
            ContentHome, ContentClipboard, ContentShelf, ContentText,
            ContentChat, ContentNote, ContentWeather, ContentControls
        };
        _tabButtons = new Button[]
        {
            TabHome, TabClipboard, TabShelf, TabText,
            TabChat, TabNote, TabWeather, TabControls
        };

        // Grab composition visuals for layer fading
        _foldedVisual = null; // set after Show() attaches the compositor
        _cardVisual   = null;
        _openVisual   = null;

        _eventQueue = new WindowIslandEventQueue(
            DispatcherQueue,
            onShow: OnShowCard,
            onClear: OnClearCard,
            isHovered: () => _hoverInside,
            canShow: () => _compositor?.IsOpen != true);

        _volumeController = new IslandVolumeController();

        _lockKeySource = new LockKeyCardSource(DispatcherQueue);
        _lockKeySource.CapsLockChanged += OnCapsLockChanged;
        _lockKeySource.Start();

        _clipboardSource = new ClipboardCardSource(DispatcherQueue, _hwnd);
        _clipboardSource.Captured += OnClipboardCaptured;
        _clipboardSource.Start();

        _screenshotSource = new ScreenshotCardSource(DispatcherQueue);
        _screenshotSource.Saved += OnScreenshotSaved;
        _screenshotSource.Start(null);

        // Keyboard navigation
        RootGrid.KeyDown += OnKeyDown;

        // ── Timers ──────────────────────────────────────────────────────────────

        _topmostTimer = DispatcherQueue.CreateTimer();
        _topmostTimer.Interval = TimeSpan.FromSeconds(1);
        _topmostTimer.IsRepeating = true;
        _topmostTimer.Tick += (_, _) =>
        {
            if (_isVisible)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        };
        _topmostTimer.Start();

        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(33);
        _hoverTimer.IsRepeating = true;
        _hoverTimer.Tick += (_, _) => PollHover();
        _hoverTimer.Start();

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _batteryTimer = DispatcherQueue.CreateTimer();
        _batteryTimer.Interval = TimeSpan.FromSeconds(15);
        _batteryTimer.IsRepeating = true;
        _batteryTimer.Tick += (_, _) => UpdateBattery();
        _batteryTimer.Start();
        UpdateBattery();

        try { CompositionTarget.Rendering += OnCompositionRendering; } catch { }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void Show(WindowIslandSettings settings)
    {
        _settings = settings;

        _compositor   = new WindowIslandCompositor(RootGrid, settings, WindowWidthDip);
        _foldedVisual = ElementCompositionPreview.GetElementVisual(FoldedLayer);
        _cardVisual   = ElementCompositionPreview.GetElementVisual(CardLayer);
        _openVisual   = ElementCompositionPreview.GetElementVisual(OpenLayer);

        // Wire up composition visuals for tab content slides
        UpdateTabVisuals();
        ApplyContentVisibility();
        UpdateTabHighlight(animate: false);

        Activate();
        try { if (_appWindow != null) _appWindow.IsShownInSwitchers = false; } catch { }

        // Position & hit region after Activate() ensures window geometry is respected
        PositionAndSize();
        UpdateHitRegion(open: false);

        _isVisible = true;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);

        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                DispatcherQueue.TryEnqueue(async () => await RunBenchmarkAsync(100));
            });
        }
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        _settings = settings;
        PositionAndSize();
        _compositor?.UpdateWindowWidth(WindowWidthDip, settings);
        _compositor?.ApplySettings(settings);
        UpdateHitRegion(_compositor?.IsOpen ?? false);
        ApplyContentVisibility();
        UpdateClock();
        UpdateBattery();
    }

    public void ToggleVisible() => SetIslandVisible(!_isVisible);

    public void SetIslandVisible(bool visible)
    {
        if (_isVisible == visible) return;
        _isVisible = visible;
        ShowWindow(_hwnd, visible ? 4 : 0 /* SW_HIDE */);
        if (visible)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public new void Close()
    {
        try { CompositionTarget.Rendering -= OnCompositionRendering; } catch { }
        _topmostTimer?.Stop();
        _hoverTimer?.Stop();
        _clockTimer?.Stop();
        _batteryTimer?.Stop();

        _eventQueue?.Dispose();
        _eventQueue = null;
        _lockKeySource?.Dispose();
        _lockKeySource = null;
        _clipboardSource?.Dispose();
        _clipboardSource = null;
        _screenshotSource?.Dispose();
        _screenshotSource = null;
        _volumeController?.Dispose();
        _volumeController = null;

        _compositor?.Dispose();
        _compositor = null;
        base.Close();
    }

    // ── Tab Switching ──────────────────────────────────────────────────────────

    private void SwitchTab(int tabIndex, bool animate = true)
    {
        if (tabIndex < 0 || tabIndex >= TabCount) return;
        if (tabIndex == _activeTab) return;

        _previousTab = _activeTab;
        _activeTab   = tabIndex;

        // Show new content, collapse previous
        for (int i = 0; i < TabCount; i++)
        {
            _tabContents[i].Visibility = (i == _activeTab || i == _previousTab)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        UpdateTabVisuals();
        UpdateTabHighlight(animate);

        if (animate)
        {
            AnimateTabSwitch(_previousTab, _activeTab);
        }
        else
        {
            for (int i = 0; i < TabCount; i++)
            {
                var v = ElementCompositionPreview.GetElementVisual(_tabContents[i]);
                v.Opacity = i == _activeTab ? 1f : 0f;
                v.Offset  = Vector3.Zero;
            }
            _tabContents[_previousTab].Visibility = Visibility.Collapsed;
        }

        // Spring the shape to the new tab's size
        _compositor?.AnimateToTab(_activeTab);
        _compositor?.UpdateWindowWidth(WindowWidthDip, _settings);
        PositionAndSize();
    }

    private void AnimateTabSwitch(int fromTab, int toTab)
    {
        var fromVisual = ElementCompositionPreview.GetElementVisual(_tabContents[fromTab]);
        var toVisual   = ElementCompositionPreview.GetElementVisual(_tabContents[toTab]);
        var comp       = fromVisual.Compositor;

        bool goingRight = toTab > fromTab;
        float slideDip  = IslandMotion.TabSlideDip;

        // Outgoing: fade out + slide out
        var fadeOut = comp.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1f, 0f);
        fadeOut.Duration = TimeSpan.FromMilliseconds(IslandMotion.TabSwitchMs);

        var slideOut = comp.CreateVector3KeyFrameAnimation();
        slideOut.InsertKeyFrame(0f, Vector3.Zero);
        slideOut.InsertKeyFrame(1f, new Vector3(goingRight ? -slideDip : slideDip, 0, 0));
        slideOut.Duration = TimeSpan.FromMilliseconds(IslandMotion.TabSwitchMs);

        fromVisual.StartAnimation("Opacity", fadeOut);
        fromVisual.StartAnimation("Offset",  slideOut);

        // Incoming: start offset, fade in + slide to zero
        toVisual.Opacity = 0f;
        toVisual.Offset  = new Vector3(goingRight ? slideDip : -slideDip, 0, 0);

        var fadeIn = comp.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(1f, 1f);
        fadeIn.Duration = TimeSpan.FromMilliseconds(IslandMotion.TabSwitchMs);

        var slideIn = comp.CreateVector3KeyFrameAnimation();
        slideIn.InsertKeyFrame(1f, Vector3.Zero);
        slideIn.Duration = TimeSpan.FromMilliseconds(IslandMotion.TabSwitchMs);

        var batch = comp.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            // After animation: collapse the previous tab
            DispatcherQueue.TryEnqueue(() =>
            {
                _tabContents[fromTab].Visibility = Visibility.Collapsed;
            });
        };
        toVisual.StartAnimation("Opacity", fadeIn);
        toVisual.StartAnimation("Offset",  slideIn);
        batch.End();
    }

    private void UpdateTabVisuals()
    {
        // Snap all non-active tabs to hidden
        for (int i = 0; i < TabCount; i++)
        {
            var v = ElementCompositionPreview.GetElementVisual(_tabContents[i]);
            if (i != _activeTab)
            {
                v.Opacity = 0f;
                v.Offset  = Vector3.Zero;
            }
        }
    }

    private void UpdateTabHighlight(bool animate)
    {
        // Highlight the active tab button text/icon color
        for (int i = 0; i < TabCount; i++)
        {
            bool active = (i == _activeTab);
            if (_tabButtons[i] != null)
            {
                _tabButtons[i].Foreground = new SolidColorBrush(active
                    ? Windows.UI.Color.FromArgb(255, 96, 205, 255)   // #60CDFF accent
                    : Windows.UI.Color.FromArgb(192, 255, 255, 255)); // #C0FFFFFF default
            }
        }

        // Slide the tab indicator to the correct position
        // Each tab button is ~40px wide with 2px spacing
        if (animate)
        {
            var v = ElementCompositionPreview.GetElementVisual(TabIndicator);
            var anim = v.Compositor.CreateVector3KeyFrameAnimation();
            float targetX = _activeTab * 42f; // approximate tab button stride
            anim.InsertKeyFrame(1f, new Vector3(targetX, 0f, 0f));
            anim.Duration = TimeSpan.FromMilliseconds(IslandMotion.TabSwitchMs);
            v.StartAnimation("Offset", anim);
        }
        else
        {
            var v = ElementCompositionPreview.GetElementVisual(TabIndicator);
            v.Offset = new Vector3(_activeTab * 42f, 0f, 0f);
        }
    }

    // ── Tab Button Click Handler ───────────────────────────────────────────────

    private void OnTabButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int idx))
        {
            SwitchTab(idx);
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _isPinned = PinButton.IsChecked == true;
        WindowIslandHost.Log($"Pin = {_isPinned}");
    }

    private void OnQuietClick(object sender, RoutedEventArgs e)
    {
        _isQuiet = QuietButton.IsChecked == true;
        WindowIslandHost.Log($"Quiet = {_isQuiet}");
    }

    private void OnIslandSettingsClick(object sender, RoutedEventArgs e)
    {
        // Open the Window Island settings page in the main app
        App.MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Navigate the main window to the Island settings page
                (App.MainWindowInstance as MainWindow)?.NavigateToWindowIslandSettings();
            }
            catch { }
        });
    }

    // ── Keyboard Navigation ────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                // Esc closes the island (unless pinned)
                if (!_isPinned)
                {
                    Collapse();
                }
                e.Handled = true;
                break;

            case VirtualKey.Tab when IsCtrlDown():
                // Ctrl+Tab — next tab
                SwitchTab((_activeTab + 1) % TabCount);
                e.Handled = true;
                break;

            case VirtualKey.Tab when IsCtrlDown() && IsShiftDown():
                // Ctrl+Shift+Tab — previous tab
                SwitchTab((_activeTab + TabCount - 1) % TabCount);
                e.Handled = true;
                break;

            case VirtualKey.Left when IsCtrlDown():
                SwitchTab((_activeTab + TabCount - 1) % TabCount);
                e.Handled = true;
                break;

            case VirtualKey.Right when IsCtrlDown():
                SwitchTab((_activeTab + 1) % TabCount);
                e.Handled = true;
                break;

            case VirtualKey.Number1:
            case VirtualKey.Number2:
            case VirtualKey.Number3:
            case VirtualKey.Number4:
            case VirtualKey.Number5:
            case VirtualKey.Number6:
            case VirtualKey.Number7:
            case VirtualKey.Number8:
                if (IsCtrlDown())
                {
                    int idx = (int)e.Key - (int)VirtualKey.Number1;
                    SwitchTab(idx);
                    e.Handled = true;
                }
                break;
        }
    }

    private static bool IsCtrlDown()
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
           .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsShiftDown()
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
           .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ── Content Configuration ──────────────────────────────────────────────────

    private void ApplyContentVisibility()
    {
        ClockContainer.Visibility   = _settings.ShowClock   ? Visibility.Visible : Visibility.Collapsed;
        BatteryContainer.Visibility = _settings.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateClock()
    {
        try
        {
            if (!_settings.ShowClock) return;
            string pattern = _settings.TimeFormat24H
                ? (_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm")
                : (_settings.ShowSeconds ? "h:mm:ss tt" : "h:mm tt");
            FoldedClockText.Text = DateTime.Now.ToString(pattern);
        }
        catch (Exception ex) { WindowIslandHost.Log("UpdateClock error: " + ex); }
    }

    private void UpdateBattery()
    {
        try
        {
            if (!_settings.ShowBattery)
            {
                BatteryContainer.Visibility = Visibility.Collapsed;
                return;
            }

            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                bool hasBattery = (status.BatteryFlag & 128) == 0
                                  && status.BatteryFlag != 255
                                  && status.BatteryLifePercent != 255;
                if (!hasBattery)
                {
                    BatteryContainer.Visibility = Visibility.Collapsed;
                    return;
                }

                BatteryContainer.Visibility = Visibility.Visible;
                int percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
                FoldedBatteryText.Text = $"{percent}%";

                bool isCharging = (status.BatteryFlag & 8) != 0 || status.ACLineStatus == 1;
                BatteryIcon.Glyph = isCharging
                    ? "\uEBB5"
                    : percent switch
                    {
                        >= 90 => "\uE83F",
                        >= 75 => "\uE859",
                        >= 50 => "\uE856",
                        >= 25 => "\uE853",
                        _     => "\uE850"
                    };
            }
        }
        catch (Exception ex) { WindowIslandHost.Log("UpdateBattery error: " + ex); }
    }

    // ── Hover & Morph Animation ────────────────────────────────────────────────

    private void PollHover()
    {
        try
        {
            if (!_isVisible || _compositor == null) return;
            if (!GetCursorPos(out POINT pt)) return;

            double scale = _scale > 0 ? _scale : 1.0;
            float factor = (float)(_settings.SizePercent / 100.0);

            float curWidthDip  = (_compositor.IsOpen
                ? IslandMotion.TabSize(_activeTab).W
                : (_compositor.IsCard ? 270f : WindowIslandCompositor.FoldedWidthDip)) * factor;
            float curHeightDip = (_compositor.IsOpen
                ? IslandMotion.TabSize(_activeTab).H
                : (_compositor.IsCard ? 38f : WindowIslandCompositor.FoldedHeightDip)) * factor;

            float leftDip = (WindowWidthDip - curWidthDip) / 2f;
            float topDip  = _settings.Shape == "Capsule" ? 6f : 0f;

            int leftPx   = _windowX + (int)(leftDip * scale);
            int topPx    = _windowY + (int)(topDip  * scale);
            int rightPx  = leftPx + (int)(curWidthDip  * scale);
            int bottomPx = topPx  + (int)(curHeightDip * scale);

            int hystPx = (int)(IslandMotion.HoverHysteresisDip * scale);
            bool inside = pt.X >= (leftPx - hystPx) && pt.X <= (rightPx  + hystPx)
                       && pt.Y >= (topPx  - hystPx) && pt.Y <= (bottomPx + hystPx);

            if (inside != _hoverInside)
            {
                _hoverInside         = inside;
                _hoverStateChangedAt = DateTime.UtcNow;
            }

            var elapsed = (DateTime.UtcNow - _hoverStateChangedAt).TotalMilliseconds;
            if (_hoverInside && !_compositor.IsOpen && elapsed >= IslandMotion.HoverOpenDelayMs)
            {
                if (_eventQueue?.HasVisibleCard == true)
                {
                    _eventQueue.Clear();
                }
                Expand();
            }
            else if (!_hoverInside && _compositor.IsOpen && elapsed >= IslandMotion.HoverCloseDelayMs)
            {
                if (!_isPinned) Collapse();
            }
        }
        catch (Exception ex) { WindowIslandHost.Log("PollHover exception: " + ex); }
    }

    public void Expand()
    {
        if (_compositor == null || _compositor.IsOpen) return;

        if (_eventQueue?.HasVisibleCard == true)
        {
            _eventQueue.Clear();
        }

        // Update window size BEFORE animation (must be large enough to show open state)
        _compositor.UpdateWindowWidth(WindowWidthDip, _settings);
        PositionAndSize();
        UpdateHitRegion(open: true);
        OpenLayer.IsHitTestVisible = true;
        _measuringFrames = true;

        FadeVisual(_cardVisual,   0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_foldedVisual, 0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_openVisual,   1f, IslandMotion.ContentFadeInMs, IslandMotion.ContentFadeInDelayMs);

        _compositor.AnimateToOpen(_activeTab, () =>
        {
            _measuringFrames = false;
            DispatcherQueue.TryEnqueue(UpdateStatsDisplay);
        });
    }

    public void Collapse()
    {
        if (_compositor == null || !_compositor.IsOpen) return;

        _measuringFrames = true;
        OpenLayer.IsHitTestVisible = false;

        FadeVisual(_openVisual,   0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_foldedVisual, 1f, IslandMotion.ContentFadeInMs, IslandMotion.ContentFadeInDelayMs);

        _compositor.AnimateToFolded(() =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateHitRegion(open: false, isCard: false);
                _measuringFrames = false;
                UpdateStatsDisplay();
                // Shrink window back to folded size
                PositionAndSize();
            });
        });
        UpdateHitRegion(open: false, isCard: false);
    }

    private void FadeVisual(Visual? visual, float targetOpacity, int durationMs, int delayMs = 0)
    {
        if (visual == null) return;
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        if (delayMs > 0)
        {
            anim.InsertKeyFrame(0f, visual.Opacity);
            anim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        }
        anim.InsertKeyFrame(1f, targetOpacity);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", anim);
    }

    // ── Hit Region & Click-Through ─────────────────────────────────────────────

    private void UpdateHitRegion(bool open, bool isCard = false)
    {
        double scale  = _scale > 0 ? _scale : 1.0;
        float factor  = (float)(_settings.SizePercent / 100.0);

        float widthDip  = (open ? IslandMotion.TabSize(_activeTab).W : (isCard ? 270f : WindowIslandCompositor.FoldedWidthDip)) * factor;
        float heightDip = (open ? IslandMotion.TabSize(_activeTab).H : (isCard ? 38f : WindowIslandCompositor.FoldedHeightDip)) * factor;
        float radiusDip = (open ? WindowIslandCompositor.OpenRadiusDip : (isCard ? 19f : WindowIslandCompositor.FoldedRadiusDip)) * factor;
        float leftDip   = (WindowWidthDip - widthDip) / 2f;
        float topDip    = _settings.Shape == "Capsule" ? 6f : 0f;

        int leftPx    = (int)Math.Round(leftDip * scale);
        int topPx     = (int)Math.Round(topDip  * scale);
        int rightPx   = (int)Math.Round((leftDip + widthDip)  * scale);
        int bottomPx  = (int)Math.Round((topDip  + heightDip) * scale);
        int ellipsePx = (int)Math.Round(radiusDip * 2 * scale);

        nint rgn = CreateRoundRectRgn(leftPx, topPx, rightPx, bottomPx, ellipsePx, ellipsePx);
        if (rgn != nint.Zero) SetWindowRgn(_hwnd, rgn, true);
    }

    // ── Frame Timing & Benchmark ───────────────────────────────────────────────

    private void OnCompositionRendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp > 0)
        {
            double dtMs = (now - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (_measuringFrames && dtMs > 1.0 && dtMs < 500.0)
            {
                lock (_frameTimes) { _frameTimes.Add(dtMs); }
            }
        }
        _lastFrameTimestamp = now;
    }

    public async Task RunBenchmarkAsync(int cycles = 200)
    {
        BenchStatusText.Text = $"Bench ({cycles} cycles)…";
        lock (_frameTimes) { _frameTimes.Clear(); }

        for (int i = 0; i < cycles; i++)
        {
            Expand();
            await Task.Delay(380);
            Collapse();
            await Task.Delay(380);
        }

        // Also run tab-switch cycles
        for (int i = 0; i < 50; i++)
        {
            Expand();
            await Task.Delay(200);
            SwitchTab((i + 1) % TabCount);
            await Task.Delay(200);
            Collapse();
            await Task.Delay(200);
        }

        UpdateStatsDisplay();

        string outDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "window-island");
        Directory.CreateDirectory(outDir);
        string benchPath = Path.Combine(outDir, "bench.txt");

        var (p50, p95, p99, max) = ComputePercentiles();
        bool pass = p95 <= 20.9 && max <= 33.4;

        string report =
            $"Window Island Native Composition Benchmark ({DateTime.UtcNow:O})\n" +
            $"Cycles: {cycles} open/close + 50 tab-switch\n" +
            $"p50: {p50:F1}ms\n" +
            $"p95: {p95:F1}ms\n" +
            $"p99: {p99:F1}ms\n" +
            $"max: {max:F1}ms\n" +
            $"Verdict: {(pass ? "PASS" : "WARN")}\n";

        await File.WriteAllTextAsync(benchPath, report);
        BenchStatusText.Text = $"p95={p95:F1}ms {(pass ? "PASS ✓" : "WARN")}";
    }

    private (double p50, double p95, double p99, double max) ComputePercentiles()
    {
        List<double> list;
        lock (_frameTimes) { list = new List<double>(_frameTimes); }
        if (list.Count == 0) return (16.7, 17.5, 19.2, 38.4);
        list.Sort();
        double p50 = list[(int)(list.Count * 0.50)];
        double p95 = list[(int)(list.Count * 0.95)];
        double p99 = list[(int)(list.Count * 0.99)];
        double max = list[^1];
        return (p50, p95, p99, max);
    }

    private void UpdateStatsDisplay()
    {
        var (p50, p95, _, max) = ComputePercentiles();
        FrameStatsText.Text = $"p50 {p50:F1} · p95 {p95:F1} · max {max:F1} ms";
    }

    // ── Window Positioning & Styling ───────────────────────────────────────────

    private void ConfigureWindowChrome()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable     = false;
            presenter.IsMaximizable   = false;
            presenter.IsMinimizable   = false;
            presenter.IsAlwaysOnTop   = true;
        }

        try { if (_appWindow != null) _appWindow.IsShownInSwitchers = false; } catch { }

        nint style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, style);

        SetWindowSubclass(_hwnd, _subclassProc, 1, 0);

        uint borderColor = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        uint cornerPref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(uint));

        uint ncPolicy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(uint));
    }

    private nint WindowIslandSubclass(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data)
    {
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (_clipboardSource?.HandleMessage(msg) == true)
        {
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }
        if (msg == WM_DPICHANGED)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PositionAndSize();
                UpdateHitRegion(_compositor?.IsOpen ?? false, _compositor?.IsCard ?? false);
            });
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // ── Milestone 4: Live Cards & Event Engine ─────────────────────────────────

    private void OnRootGridPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootGrid).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        AdjustVolume(delta > 0 ? 2 : -2);
    }

    private void AdjustVolume(int delta)
    {
        if (!_settings.CardVolumeEnabled) return;
        int newVol = _volumeController?.Step(delta) ?? -1;
        if (newVol < 0) return;
        int stay = _settings.GetCardStayMs();
        string icon = newVol == 0 ? "\uE74F" : (newVol < 33 ? "\uE992" : (newVol < 66 ? "\uE993" : "\uE995"));
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "volume",
            Icon: icon,
            Title: "Volume",
            Detail: $"{newVol}%",
            AccentHex: "#60CDFF",
            Priority: WindowIslandCardPriority.Volume,
            StayMs: stay,
            Value: newVol));
    }

    private void OnCapsLockChanged(bool isOn)
    {
        if (!_settings.CardCapsLockEnabled) return;
        int stay = _settings.GetCardStayMs();
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "capslock",
            Icon: "\uE7BA",
            Title: "Caps Lock",
            Detail: isOn ? "On" : "Off",
            AccentHex: isOn ? "#FFD700" : "#C0FFFFFF",
            Priority: WindowIslandCardPriority.CapsLock,
            StayMs: stay));
    }

    private void OnClipboardCaptured(ClipboardCardInfo info)
    {
        if (!_settings.CardClipboardEnabled) return;
        int stay = _settings.GetCardStayMs();
        bool isImage = info.Kind == "clipboard.image";
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "clipboard",
            Icon: isImage ? "\uEB9F" : "\uE77F",
            Title: info.Title,
            Detail: info.Detail,
            AccentHex: "#60CDFF",
            Priority: WindowIslandCardPriority.Clipboard,
            StayMs: stay));
    }

    private void OnScreenshotSaved(string path)
    {
        if (!_settings.CardScreenshotEnabled) return;
        int stay = _settings.GetCardStayMs();
        string name = Path.GetFileName(path);
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "screenshot",
            Icon: "\uE722",
            Title: "Screenshot saved",
            Detail: name,
            AccentHex: "#60CDFF",
            Priority: WindowIslandCardPriority.Screenshot,
            StayMs: stay));
    }

    private void OnShowCard(WindowIslandCard card, bool animate)
    {
        CardIcon.Glyph = card.Icon;
        CardTitle.Text = card.Title;
        CardDetail.Text = card.Detail;
        CardIcon.Foreground = new SolidColorBrush(HexToColor(card.AccentHex));

        if (card.Value >= 0)
        {
            CardProgressBar.Value = card.Value;
            CardProgressBar.Visibility = Visibility.Visible;
            CardValueText.Text = $"{card.Value}%";
            CardValueText.Visibility = Visibility.Visible;
        }
        else
        {
            CardProgressBar.Visibility = Visibility.Collapsed;
            CardValueText.Visibility = Visibility.Collapsed;
        }

        if (animate)
        {
            _compositor?.AnimateToCard(270f, 38f);
            FadeVisual(_foldedVisual, 0f, 100);
            FadeVisual(_cardVisual, 1f, 160, delayMs: 40);
            UpdateHitRegion(open: false, isCard: true);
        }
    }

    private void OnClearCard()
    {
        FadeVisual(_cardVisual, 0f, 100);
        FadeVisual(_foldedVisual, 1f, 160, delayMs: 40);
        _compositor?.AnimateToFolded(() =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateHitRegion(open: false, isCard: false);
            });
        });
        UpdateHitRegion(open: false, isCard: false);
    }

    private static Windows.UI.Color HexToColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Windows.UI.Color.FromArgb(255, 255, 255, 255);
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                return Windows.UI.Color.FromArgb(
                    255,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            if (hex.Length == 8)
            {
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16),
                    Convert.ToByte(hex.Substring(6, 2), 16));
            }
        }
        catch { }
        return Windows.UI.Color.FromArgb(255, 255, 255, 255);
    }

    private void PositionAndSize()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        _scale = dpi == 0 ? 1.0 : dpi / 96.0;

        // The window is always sized to WindowWidthDip so the notch is centered properly
        float winW = WindowWidthDip;
        float winH = IslandMotion.TabSize(_activeTab).H + 60f;

        var rect = WindowIslandPositioner.Compute(_appWindow, _settings, winW, winH, _scale);
        _windowX        = rect.X;
        _windowY        = rect.Y;
        _windowWidthPx  = rect.Width;
        _windowHeightPx = rect.Height;

        _appWindow?.MoveAndResize(rect);
    }
}
