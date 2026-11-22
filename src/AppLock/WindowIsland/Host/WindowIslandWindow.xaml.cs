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
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using AppLock.Island.Events;
using AppLock.Island.Modules;
using AppLock.Models;
using AppLock.Services;
using AppLock.WindowIsland.Events;
using AppLock.WindowIsland.Modules;
using AppLock.WindowIsland.Modules.Controls;
using AppLock.WindowIsland.Modules.Weather;
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
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
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
    private Visual? _commandVisual;
    private Visual? _openVisual;

    private WindowIslandEventQueue? _eventQueue;
    private LockKeyCardSource?      _lockKeySource;
    private IslandVolumeController? _volumeController;
    private ClipboardCardSource?    _clipboardSource;
    private ScreenshotCardSource?   _screenshotSource;

    // Milestone 5: Home tab
    private WindowIslandHomeState?  _homeState;
    private IslandTileActions?      _tileActions;
    private bool                    _isMediaPlaying;
    private bool                    _volumeChanging;    // guard to avoid re-entrant slider updates
    private DQTimer?                _eqTimer;
    private readonly Random         _eqRng = new();

    // Milestone 6: Clipboard + Shelf
    private WindowIslandClipboardState?       _clipboardState;
    private AppLock.Island.Modules.IslandShelf? _shelf;

    // Milestone 7: Launcher
    private WindowIslandLauncher?             _launcher;

    // Milestone 8: Command bar & File index
    private WindowIslandFileIndex?            _fileIndex;
    private WindowIslandCommandService?       _commandService;
    private List<CommandBarItem>              _currentCommandItems = new();
    private int                               _selectedCommandIndex;
    private nint                              _previousForegroundWindow = nint.Zero;

    // Milestone 9: Note & Timer & Reminders
    private WindowIslandNotesStore?           _notesStore;
    private WindowIslandReminderStore?        _reminderStore;
    private WindowIslandTimer?                _timer;
    private string?                           _activeNoteId;
    private bool                              _noteUpdating;

    // Milestone 10: Weather
    private bool                              _showingWeeklyWeather;

    // Milestone 11: Controls
    private DQTimer?                          _restartConfirmTimer;
    private DQTimer?                          _shutdownConfirmTimer;
    private bool                              _restartConfirming;
    private bool                              _shutdownConfirming;
    private bool                              _controlsBrightnessChanging;

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

        // Set Slider min/max in code-behind (XAML RangeBase coercion avoidance)
        VolumeSlider.Minimum = 0;
        VolumeSlider.Maximum = 100;
        MediaProgress.Minimum = 0;
        MediaProgress.Maximum = 1;
        ControlsBrightnessSlider.Minimum = 0;
        ControlsBrightnessSlider.Maximum = 100;

        // Tile actions (hide/show helpers use SetIslandVisible)
        _tileActions = new IslandTileActions(
            hideIsland: () => { SetIslandVisible(false); return Task.CompletedTask; },
            showIsland: () => { SetIslandVisible(true);  return Task.CompletedTask; },
            screenshotsFolder: () => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"));

        // Milestone 6: Clipboard + Shelf
        _clipboardState = new WindowIslandClipboardState(DispatcherQueue);
        _clipboardState.ItemsLoaded += OnClipboardItemsLoaded;
        _shelf = new AppLock.Island.Modules.IslandShelf();
        _shelf.Changed += () => DispatcherQueue.TryEnqueue(RefreshShelfUI);

        // Milestone 7: Launcher
        _launcher = new WindowIslandLauncher();
        _launcher.Changed += () => DispatcherQueue.TryEnqueue(RefreshLauncherUI);
        RefreshLauncherUI();

        // Milestone 8: Command bar & File index
        _fileIndex = new WindowIslandFileIndex();
        _fileIndex.StartBackgroundIndexing();
        _commandService = new WindowIslandCommandService(_fileIndex, _launcher!);

        // Milestone 9: Note, Timer, Reminders
        _notesStore = new WindowIslandNotesStore();
        _notesStore.Changed += () => DispatcherQueue.TryEnqueue(RefreshNotesUI);
        _reminderStore = new WindowIslandReminderStore();
        _reminderStore.Changed += () => DispatcherQueue.TryEnqueue(RefreshRemindersUI);
        _reminderStore.ReminderTriggered += OnReminderTriggered;
        _timer = new WindowIslandTimer();
        _timer.Tick += (rem, prog) => DispatcherQueue.TryEnqueue(() => OnTimerTick(rem, prog));
        _timer.Finished += () => DispatcherQueue.TryEnqueue(OnTimerFinished);

        // Milestone 10: Weather
        WindowIslandWeatherService.Instance.WeatherUpdated += OnWeatherUpdated;
        WindowIslandWeatherService.Instance.RainWarningTriggered += OnRainWarningTriggered;
        UpdateFoldedWeatherMini();

        // Milestone 11: Controls
        WindowIslandControlsService.Instance.ControlsStateChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshControlsUI);

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
        _foldedVisual  = ElementCompositionPreview.GetElementVisual(FoldedLayer);
        _cardVisual    = ElementCompositionPreview.GetElementVisual(CardLayer);
        _commandVisual = ElementCompositionPreview.GetElementVisual(CommandLayer);
        _openVisual    = ElementCompositionPreview.GetElementVisual(OpenLayer);

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

        // Start Home tab background sampler immediately (tab 0 is active on first open)
        StartHomeState();

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
        UpdateFoldedWeatherMini();
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
        _eqTimer?.Stop();

        _homeState?.Dispose();
        _homeState = null;

        _clipboardState = null;
        _shelf = null;
        _launcher = null;
        _fileIndex?.Dispose();
        _fileIndex = null;
        _commandService = null;
        _reminderStore?.Dispose();
        _reminderStore = null;
        _timer?.Dispose();
        _timer = null;
        _notesStore = null;

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

        // Start/stop home background sampler based on tab
        if (_activeTab == 0)
        {
            StartHomeState();
            RefreshLauncherUI();
        }
        else StopHomeState();

        // Refresh clipboard / shelf / notes on switch
        if (_activeTab == 1) _clipboardState?.Refresh();
        if (_activeTab == 2) RefreshShelfUI();
        if (_activeTab == 5)
        {
            RefreshNotesUI();
            RefreshRemindersUI();
        }
        if (_activeTab == 6)
        {
            RefreshWeatherUI();
        }
        if (_activeTab == 7)
        {
            RefreshControlsUI();
        }

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
            if (!_isVisible || _compositor == null || _compositor.IsCommand) return;
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

    private void UpdateHitRegion(bool open, bool isCard = false, bool isCommand = false)
    {
        double scale  = _scale > 0 ? _scale : 1.0;
        float factor  = (float)(_settings.SizePercent / 100.0);

        float widthDip  = isCommand ? 520f : (open ? IslandMotion.TabSize(_activeTab).W : (isCard ? 270f : WindowIslandCompositor.FoldedWidthDip)) * factor;
        float heightDip = isCommand ? 270f : (open ? IslandMotion.TabSize(_activeTab).H : (isCard ? 38f : WindowIslandCompositor.FoldedHeightDip)) * factor;
        float radiusDip = (open || isCommand) ? WindowIslandCompositor.OpenRadiusDip : (isCard ? 19f : WindowIslandCompositor.FoldedRadiusDip) * factor;
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

    // ── Milestone 5: Home tab ──────────────────────────────────────────────────

    private void StartHomeState()
    {
        if (_homeState != null) return;
        if (_volumeController == null) return;
        _homeState = new WindowIslandHomeState(DispatcherQueue, _volumeController);
        _homeState.MediaUpdated += OnMediaUpdated;
        _homeState.StatsUpdated += OnStatsUpdated;
        _homeState.Start();
        StartEqTimer();
    }

    private void StopHomeState()
    {
        if (_homeState == null) return;
        _homeState.MediaUpdated -= OnMediaUpdated;
        _homeState.StatsUpdated -= OnStatsUpdated;
        _homeState.Dispose();
        _homeState = null;
        StopEqTimer();
        // Hide folded media mini when home tab is not active
        FoldedMediaMini.Visibility = Visibility.Collapsed;
    }

    private void OnMediaUpdated(AppLock.Island.Modules.MediaSnapshot snap)
    {
        try
        {
            _isMediaPlaying = snap.Playing;

            if (!snap.HasSession || string.IsNullOrEmpty(snap.Title))
            {
                MediaPlayerPanel.Visibility = Visibility.Collapsed;
                RecentPanel.Visibility      = Visibility.Visible;
                FoldedMediaMini.Visibility  = Visibility.Collapsed;
                StopEqTimer();
                return;
            }

            MediaPlayerPanel.Visibility = Visibility.Visible;
            RecentPanel.Visibility      = Visibility.Collapsed;

            MediaTitle.Text  = snap.Title;
            MediaArtist.Text = snap.Artist;
            MediaApp.Text    = snap.App.ToUpperInvariant();
            MediaPlayPauseIcon.Glyph = snap.Playing ? "\uE769" : "\uE768"; // Pause : Play

            // Progress bar
            if (snap.DurationSeconds > 0)
            {
                double ratio = Math.Clamp(snap.PositionSeconds / snap.DurationSeconds, 0, 1);
                MediaProgress.Value = ratio;
                MediaProgress.Visibility = Visibility.Visible;
            }
            else
            {
                MediaProgress.Visibility = Visibility.Collapsed;
            }

            // Album art
            if (!string.IsNullOrEmpty(snap.ArtFile))
            {
                try
                {
                    string artPath = Path.Combine(AppLock.Island.Modules.MediaSessionSource.ArtFolder, snap.ArtFile);
                    if (File.Exists(artPath))
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(artPath));
                        MediaArt.Source  = bmp;
                        FoldedArt.Source = bmp;
                    }
                }
                catch { }
            }
            else
            {
                MediaArt.Source  = null;
                FoldedArt.Source = null;
            }

            // Folded mini
            _isMediaPlaying = snap.Playing;
            FoldedMediaMini.Visibility = snap.Playing ? Visibility.Visible : Visibility.Collapsed;
            UpdateFoldedWeatherMini();
            if (snap.Playing) StartEqTimer(); else StopEqTimer();
        }
        catch (Exception ex) { WindowIslandHost.Log("OnMediaUpdated: " + ex.Message); }
    }

    private void OnStatsUpdated(HomeSnapshot snap)
    {
        try
        {
            // CPU & RAM tile
            TileCpuText.Text = $"CPU {snap.CpuPercent:F0}%";
            TileRamText.Text = $"RAM {snap.RamPercent:F0}%";

            // Network tile
            TileNetDown.Text = $"↓ {FormatSpeed(snap.DownBytesPerSec)}";
            TileNetUp.Text   = $"↑ {FormatSpeed(snap.UpBytesPerSec)}";

            // Volume row (avoid re-entrant ValueChanged)
            _volumeChanging = true;
            VolumeSlider.Value = snap.Volume;
            _volumeChanging = false;
            VolumeText.Text = $"{snap.Volume}%";
            UpdateVolumeIcon(snap.Volume);
        }
        catch (Exception ex) { WindowIslandHost.Log("OnStatsUpdated: " + ex.Message); }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024)         return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)  return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
    }

    private void UpdateVolumeIcon(int vol)
    {
        VolumeIcon.Glyph = vol == 0 ? "\uE74F"
            : vol < 33   ? "\uE992"
            : vol < 66   ? "\uE993"
            : "\uE995";
    }

    // ── Equalizer bars (30 Hz while playing) ──────────────────────────────────

    private static readonly int[] EqMaxH = { 14, 18, 10 }; // max heights per bar
    private static readonly int[] EqMinH = { 4,  5,  3  }; // min heights per bar

    private void StartEqTimer()
    {
        if (_eqTimer != null) return;
        _eqTimer = DispatcherQueue.CreateTimer();
        _eqTimer.Interval = TimeSpan.FromMilliseconds(100); // ~10 Hz (smooth enough, low CPU)
        _eqTimer.IsRepeating = true;
        _eqTimer.Tick += (_, _) => TickEqualizer();
        _eqTimer.Start();
    }

    private void StopEqTimer()
    {
        _eqTimer?.Stop();
        _eqTimer = null;
    }

    private void TickEqualizer()
    {
        if (!_isMediaPlaying) return;
        try
        {
            EqBar0.Height = _eqRng.Next(EqMinH[0], EqMaxH[0] + 1);
            EqBar1.Height = _eqRng.Next(EqMinH[1], EqMaxH[1] + 1);
            EqBar2.Height = _eqRng.Next(EqMinH[2], EqMaxH[2] + 1);
        }
        catch { }
    }

    // ── Media controls ────────────────────────────────────────────────────────

    private void MediaPlayPause_Click(object sender, RoutedEventArgs e)
        => _ = _homeState?.MediaCommandAsync("toggle");

    private void MediaPrev_Click(object sender, RoutedEventArgs e)
        => _ = _homeState?.MediaCommandAsync("previous");

    private void MediaNext_Click(object sender, RoutedEventArgs e)
        => _ = _homeState?.MediaCommandAsync("next");

    // ── Volume slider ─────────────────────────────────────────────────────────

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_volumeChanging) return;
        int vol = (int)e.NewValue;
        _volumeController?.Set(vol);
        VolumeText.Text = $"{vol}%";
        UpdateVolumeIcon(vol);
        // Push a volume card when the user adjusts the slider
        if (_settings.CardVolumeEnabled)
        {
            string icon = vol == 0 ? "\uE74F" : (vol < 33 ? "\uE992" : (vol < 66 ? "\uE993" : "\uE995"));
            _eventQueue?.Push(new WindowIslandCard(
                Kind: "volume",
                Icon: icon,
                Title: "Volume",
                Detail: $"{vol}%",
                AccentHex: "#60CDFF",
                Priority: WindowIslandCardPriority.Volume,
                StayMs: _settings.GetCardStayMs(),
                Value: vol));
        }
    }

    // ── Tile button handlers ──────────────────────────────────────────────────

    private void TileArea_Click(object sender, RoutedEventArgs e)
        => _ = RunTileAsync("area");

    private void TileFullScreen_Click(object sender, RoutedEventArgs e)
        => _ = RunTileAsync("fullscreen");

    private void TileScreenText_Click(object sender, RoutedEventArgs e)
        => _ = RunTileAsync("screentext");

    private void TileQR_Click(object sender, RoutedEventArgs e)
        => _ = RunTileAsync("qr");

    private void TileNetwork_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to main app Others page (Speed Meter) — placeholder until Milestone 8
        try { (App.MainWindowInstance as MainWindow)?.NavigateToWindowIslandSettings(); } catch { }
    }

    private void TileCpuRam_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }

    private void TileRecord_Click(object sender, RoutedEventArgs e)
        => _ = RunTileAsync("record");

    private void TileCommand_Click(object sender, RoutedEventArgs e)
    {
        OpenCommandBar();
    }

    private async Task RunTileAsync(string id)
    {
        try
        {
            if (_tileActions == null) return;
            var outcome = await _tileActions.RunAsync(id);
            if (outcome != null)
            {
                // Show result as a card
                _eventQueue?.Push(new WindowIslandCard(
                    Kind: "tile",
                    Icon: outcome.Icon,
                    Title: outcome.Title,
                    Detail: outcome.Detail,
                    AccentHex: outcome.Accent,
                    Priority: WindowIslandCardPriority.Screenshot,
                    StayMs: 3500));
            }
        }
        catch (Exception ex) { WindowIslandHost.Log("RunTileAsync: " + ex); }
    }

    // ── Milestone 6: Clipboard tab ─────────────────────────────────────────────

    private void ClipboardSearch_TextChanged(object sender, TextChangedEventArgs e)
        => _clipboardState?.Refresh(ClipboardSearch.Text.Trim());

    private async void ClipboardClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await (_clipboardState?.ClearAsync() ?? Task.CompletedTask);
            _clipboardState?.Refresh();
        }
        catch { }
    }

    private void OnClipboardItemsLoaded(IReadOnlyList<AppLock.Island.Modules.ClipboardRow> rows)
    {
        ClipboardList.Children.Clear();

        if (rows.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No clipboard history yet.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0)
            };
            ClipboardList.Children.Add(empty);
            return;
        }

        foreach (var row in rows)
        {
            var rowGrid = BuildClipboardRow(row);
            ClipboardList.Children.Add(rowGrid);
        }
    }

    private Grid BuildClipboardRow(AppLock.Island.Modules.ClipboardRow row)
    {
        // Outer border button
        var border = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
        };
        border.PointerEntered  += (s, _) => ((Border)s).Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255));
        border.PointerExited   += (s, _) => ((Border)s).Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Type icon
        string glyph = row.Type switch
        {
            "image" => "\uEB9F",
            "files" => "\uE7C3",
            "text"  => "\uE8C3",
            _       => "\uE77F"
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = 13, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(icon, 0);
        inner.Children.Add(icon);

        // Preview + source
        var textStack = new StackPanel { Spacing = 1 };
        var preview = new TextBlock
        {
            Text = row.Preview,
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var source = new TextBlock
        {
            Text = string.IsNullOrEmpty(row.Source) ? row.Age : $"{row.Source} · {row.Age}",
            FontSize = 9,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(preview);
        textStack.Children.Add(source);
        Grid.SetColumn(textStack, 1);
        inner.Children.Add(textStack);

        // Pin indicator
        if (row.Pinned)
        {
            var pin = new FontIcon { Glyph = "\uE840", FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(pin, 2);
            inner.Children.Add(pin);
        }

        border.Child = inner;

        // Click = paste; Shift+Click = copy only
        border.PointerPressed += async (_, pe) =>
        {
            bool shift = pe.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
            await (_clipboardState?.PasteAsync(row.Id, copyOnly: shift) ?? Task.CompletedTask);
        };

        return new Grid { Children = { border } };
    }

    // ── Milestone 6: Shelf tab ─────────────────────────────────────────────────

    private void RefreshShelfUI()
    {
        if (_shelf == null) return;
        var rows = _shelf.Rows();

        // Clear all except the empty-state TextBlock (first child)
        while (ShelfList.Children.Count > 1)
            ShelfList.Children.RemoveAt(ShelfList.Children.Count - 1);

        ShelfEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var row in rows)
        {
            var item = BuildShelfRow(row);
            ShelfList.Children.Add(item);
        }
    }

    private Grid BuildShelfRow(AppLock.Island.Modules.ShelfRow row)
    {
        var container = new Grid();
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon
        string iconGlyph = System.IO.Directory.Exists(row.Path) ? "\uE8B7" :
            System.IO.Path.GetExtension(row.Path).ToLowerInvariant() switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "\uEB9F",
                ".mp4" or ".mkv" or ".mov" or ".avi"                       => "\uE8B2",
                ".mp3" or ".wav" or ".flac" or ".aac"                      => "\uE8D6",
                ".pdf"                                                      => "\uEA90",
                ".exe" or ".msi"                                            => "\uE756",
                ".zip" or ".rar" or ".7z"                                   => "\uE8B9",
                _                                                           => "\uE7C3"
            };

        var icon = new FontIcon
        {
            Glyph = iconGlyph, FontSize = 14,
            Foreground = new SolidColorBrush(row.Exists
                ? Windows.UI.Color.FromArgb(180, 255, 255, 255)
                : Windows.UI.Color.FromArgb(80, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(icon, 0);
        container.Children.Add(icon);

        // Name + path
        var nameBlock = new TextBlock
        {
            Text = row.Name,
            FontSize = 11,
            Foreground = new SolidColorBrush(row.Exists
                ? Windows.UI.Color.FromArgb(230, 255, 255, 255)
                : Windows.UI.Color.FromArgb(100, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameBlock, 1);
        container.Children.Add(nameBlock);

        // Remove button
        var removeBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Style = (Style)RootGrid.Resources["TabButtonStyle"],
            Width = 24, Height = 24,
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        ToolTipService.SetToolTip(removeBtn, "Remove from shelf");
        string capturedPath = row.Path;
        removeBtn.Click += (_, _) =>
        {
            _shelf?.Remove(capturedPath);
            RefreshShelfUI();
        };
        Grid.SetColumn(removeBtn, 2);
        container.Children.Add(removeBtn);

        // Open on click
        var border = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Child = container
        };
        border.PointerEntered += (s, _) => ((Border)s).Background = new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255));
        border.PointerExited  += (s, _) => ((Border)s).Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255));
        if (row.Exists)
        {
            border.PointerPressed += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedPath) { UseShellExecute = true }); }
                catch { }
            };
        }

        return new Grid { Children = { border } };
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        _shelf?.Clear();
        RefreshShelfUI();
    }

    private void ShelfGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        ShelfDropHint.Visibility = Visibility.Visible;
    }

    private async void ShelfGrid_Drop(object sender, DragEventArgs e)
    {
        ShelfDropHint.Visibility = Visibility.Collapsed;

        try
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new List<string>();
                foreach (var item in items)
                    paths.Add(item.Path);

                _shelf?.Add(paths);
                RefreshShelfUI();
            }
        }
        catch (Exception ex) { WindowIslandHost.Log("ShelfDrop: " + ex.Message); }
    }

    // ── Milestone 7: Launcher ──────────────────────────────────────────────────

    private void RefreshLauncherUI()
    {
        if (_launcher == null) return;
        var items = _launcher.Items;

        // Keep LauncherEmptyText and LauncherAddBtn in LauncherList
        // Clear all dynamically added shortcut buttons
        while (LauncherList.Children.Count > 2)
            LauncherList.Children.RemoveAt(1);

        LauncherEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Insert items between LauncherEmptyText (index 0) and LauncherAddBtn (last)
        int insertIndex = 1;
        foreach (var item in items)
        {
            var btn = BuildShortcutTile(item);
            LauncherList.Children.Insert(insertIndex++, btn);
        }
    }

    private Button BuildShortcutTile(IslandShortcut item)
    {
        var btn = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Tag = item.Id
        };
        ToolTipService.SetToolTip(btn, string.IsNullOrEmpty(item.Name) ? item.Target : $"{item.Name}\n{item.Target}");

        // Content: image or fallback icon
        if (!string.IsNullOrEmpty(item.IconFile) && System.IO.File.Exists(item.IconFile))
        {
            try
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(item.IconFile));
                btn.Content = new Image
                {
                    Source = bmp,
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform
                };
            }
            catch
            {
                btn.Content = CreateFallbackIcon(item);
            }
        }
        else
        {
            btn.Content = CreateFallbackIcon(item);
        }

        // Left-click: Ctrl = OpenFolder, normal = Launch
        btn.Click += (_, _) =>
        {
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl)
            {
                _launcher?.OpenFolder(item.Id);
            }
            else
            {
                _launcher?.Launch(item.Id);
            }
        };

        // Right-click context menu
        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "\uE714" } };
        openItem.Click += (_, _) => _launcher?.Launch(item.Id);
        menu.Items.Add(openItem);

        var locItem = new MenuFlyoutItem { Text = "Open file location", Icon = new FontIcon { Glyph = "\uE8B7" } };
        locItem.Click += (_, _) => _launcher?.OpenFolder(item.Id);
        menu.Items.Add(locItem);

        string ext = System.IO.Path.GetExtension(item.Target).ToLowerInvariant();
        if (ext is ".exe" or ".bat" or ".cmd" or ".ps1")
        {
            var adminItem = new MenuFlyoutItem { Text = "Run as administrator", Icon = new FontIcon { Glyph = "\uE7EF" } };
            adminItem.Click += (_, _) => _launcher?.Launch(item.Id, runAsAdmin: true);
            menu.Items.Add(adminItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var removeItem = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "\uE74D" } };
        removeItem.Click += (_, _) => _launcher?.Remove(item.Id);
        menu.Items.Add(removeItem);

        btn.ContextFlyout = menu;

        return btn;
    }

    private static FrameworkElement CreateFallbackIcon(IslandShortcut item)
    {
        string target = item.Target;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            return new FontIcon { Glyph = "\uE774", FontSize = 13, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 96, 205, 255)) };
        }
        if (System.IO.Directory.Exists(target))
        {
            return new FontIcon { Glyph = "\uE8B7", FontSize = 13, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 215, 0)) };
        }
        return new FontIcon { Glyph = "\uE756", FontSize = 13, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255)) };
    }

    private void LauncherAddBtn_Click(object sender, RoutedEventArgs e)
    {
        LauncherTargetInput.Text = string.Empty;
        LauncherNameInput.Text = string.Empty;
        LauncherUseAppIconCheck.IsChecked = true;
        LauncherAddOverlay.Visibility = Visibility.Visible;
    }

    private void LauncherAddCancel_Click(object sender, RoutedEventArgs e)
    {
        LauncherAddOverlay.Visibility = Visibility.Collapsed;
    }

    private async void LauncherAddConfirm_Click(object sender, RoutedEventArgs e)
    {
        string target = LauncherTargetInput.Text.Trim();
        string name = LauncherNameInput.Text.Trim();
        bool useAppIcon = LauncherUseAppIconCheck.IsChecked == true;

        if (!string.IsNullOrEmpty(target) && _launcher != null)
        {
            await _launcher.AddAsync(target, string.IsNullOrEmpty(name) ? null : name, useAppIcon);
        }
        LauncherAddOverlay.Visibility = Visibility.Collapsed;
    }

    private void LauncherRow_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void LauncherRow_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (_launcher != null)
                        await _launcher.AddAsync(item.Path);
                }
            }
            else if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink))
            {
                var uri = await e.DataView.GetWebLinkAsync();
                if (uri != null && _launcher != null)
                    await _launcher.AddAsync(uri.ToString());
            }
            else if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text) && _launcher != null)
                    await _launcher.AddAsync(text.Trim());
            }
        }
        catch (Exception ex)
        {
            WindowIslandHost.Log("LauncherRow_Drop: " + ex.Message);
        }
    }

    // ── Milestone 8: Command Bar & Actions ─────────────────────────────────────

    public void OpenCommandBar()
    {
        if (_compositor == null) return;
        if (!_isVisible) SetIslandVisible(true);

        _previousForegroundWindow = GetForegroundWindow();
        SetForegroundWindow(_hwnd);

        _compositor.UpdateWindowWidth(WindowWidthDip, _settings);
        PositionAndSize();
        UpdateHitRegion(open: false, isCard: false, isCommand: true);

        CommandLayer.IsHitTestVisible = true;
        OpenLayer.IsHitTestVisible    = false;
        _measuringFrames = true;

        FadeVisual(_foldedVisual,  0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_cardVisual,    0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_openVisual,    0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_commandVisual, 1f, IslandMotion.ContentFadeInMs, IslandMotion.ContentFadeInDelayMs);

        _compositor.AnimateToCommand(520f, 270f, () =>
        {
            _measuringFrames = false;
        });

        CommandInput.Text = string.Empty;
        ExecuteCommandQuery(string.Empty);
        CommandInput.Focus(FocusState.Programmatic);
    }

    public void CloseCommandBar()
    {
        if (_compositor == null || !_compositor.IsCommand) return;

        CommandLayer.IsHitTestVisible = false;
        FadeVisual(_commandVisual, 0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_foldedVisual,  1f, IslandMotion.ContentFadeInMs, IslandMotion.ContentFadeInDelayMs);

        _compositor.AnimateToFolded(() =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateHitRegion(open: false, isCard: false, isCommand: false);
                _measuringFrames = false;
                PositionAndSize();

                if (_previousForegroundWindow != nint.Zero)
                {
                    SetForegroundWindow(_previousForegroundWindow);
                    _previousForegroundWindow = nint.Zero;
                }
            });
        });
        UpdateHitRegion(open: false, isCard: false, isCommand: false);
    }

    private void CommandInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ExecuteCommandQuery(CommandInput.Text);
    }

    private void CommandClose_Click(object sender, RoutedEventArgs e)
    {
        CloseCommandBar();
    }

    private void ExecuteCommandQuery(string query)
    {
        _currentCommandItems = _commandService?.Query(query) ?? new List<CommandBarItem>();
        _selectedCommandIndex = 0;
        RenderCommandResults();
    }

    private void RenderCommandResults()
    {
        CommandResultList.Children.Clear();

        if (_currentCommandItems.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "Type a file name, app name, math (e.g. 2*(3+4)), or command (lock, note, weather)…",
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            CommandResultList.Children.Add(hint);
            return;
        }

        for (int i = 0; i < _currentCommandItems.Count; i++)
        {
            var item = _currentCommandItems[i];
            bool isSel = (i == _selectedCommandIndex);
            var row = BuildCommandResultRow(item, isSel, i);
            CommandResultList.Children.Add(row);
        }
    }

    private Border BuildCommandResultRow(CommandBarItem item, bool isSelected, int index)
    {
        var border = new Border
        {
            Background = isSelected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 96, 205, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255)),
            BorderBrush = isSelected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(120, 96, 205, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = item.IconGlyph,
            FontSize = 13,
            Foreground = new SolidColorBrush(HexToColor(item.AccentHex)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0)
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var stack = new StackPanel { Spacing = 1 };
        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 11,
            FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var sub = new TextBlock
        {
            Text = item.Subtitle,
            FontSize = 9,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        stack.Children.Add(title);
        stack.Children.Add(sub);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        border.Child = grid;

        border.PointerEntered += (_, _) =>
        {
            _selectedCommandIndex = index;
            RenderCommandResults();
        };

        border.PointerPressed += async (_, _) =>
        {
            _selectedCommandIndex = index;
            await ExecuteSelectedCommandAsync(false);
        };

        return border;
    }

    private async Task ExecuteSelectedCommandAsync(bool showInFolder)
    {
        if (_selectedCommandIndex < 0 || _selectedCommandIndex >= _currentCommandItems.Count)
            return;

        var item = _currentCommandItems[_selectedCommandIndex];
        if (showInFolder)
        {
            _commandService?.ShowInFolder(item);
        }
        else
        {
            await (_commandService?.ExecuteAsync(item, idx => SwitchTab(idx), () => OnIslandSettingsClick(this, new RoutedEventArgs())) ?? Task.CompletedTask);
        }

        CloseCommandBar();
    }

    private async void CommandInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseCommandBar();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Down)
        {
            if (_currentCommandItems.Count > 0)
            {
                _selectedCommandIndex = (_selectedCommandIndex + 1) % _currentCommandItems.Count;
                RenderCommandResults();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Up)
        {
            if (_currentCommandItems.Count > 0)
            {
                _selectedCommandIndex = (_selectedCommandIndex + _currentCommandItems.Count - 1) % _currentCommandItems.Count;
                RenderCommandResults();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            bool ctrl = IsCtrlDown();
            bool alt  = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (alt && _selectedCommandIndex >= 0 && _selectedCommandIndex < _currentCommandItems.Count)
            {
                _commandService?.CopyPathOrData(_currentCommandItems[_selectedCommandIndex]);
                CloseCommandBar();
            }
            else
            {
                await ExecuteSelectedCommandAsync(ctrl);
            }
            e.Handled = true;
        }
    }

    // ── Milestone 9: Note & Timer & Reminders ─────────────────────────────────

    private void RefreshNotesUI()
    {
        if (_notesStore == null) return;
        var notes = _notesStore.Notes;

        NoteTabStrip.Children.Clear();

        if (notes.Count == 0) return;

        if (string.IsNullOrEmpty(_activeNoteId) || !notes.Any(n => n.Id == _activeNoteId))
        {
            _activeNoteId = notes[0].Id;
        }

        foreach (var note in notes)
        {
            bool isAct = (note.Id == _activeNoteId);
            var btn = new Button
            {
                Content = note.Title,
                FontSize = 10,
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(4),
                Background = isAct
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(50, 96, 205, 255))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                Foreground = isAct
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 205, 255))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                Tag = note.Id
            };
            btn.Click += (s, _) =>
            {
                if (s is Button b && b.Tag is string nid)
                {
                    _activeNoteId = nid;
                    RefreshNotesUI();
                }
            };
            NoteTabStrip.Children.Add(btn);
        }

        var activeNote = notes.FirstOrDefault(n => n.Id == _activeNoteId) ?? notes[0];
        _noteUpdating = true;
        NoteContentBox.Text = activeNote.Content;
        _noteUpdating = false;
    }

    private void NoteAddBtn_Click(object sender, RoutedEventArgs e)
    {
        var note = _notesStore?.CreateNote();
        if (note != null)
        {
            _activeNoteId = note.Id;
            RefreshNotesUI();
            NoteContentBox.Focus(FocusState.Programmatic);
        }
    }

    private void NoteCopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(NoteContentBox.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
        catch { }
    }

    private void NoteDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeNoteId)) return;
        _notesStore?.DeleteNote(_activeNoteId);
        _activeNoteId = null;
        RefreshNotesUI();
    }

    private void NoteContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_noteUpdating || string.IsNullOrEmpty(_activeNoteId)) return;
        _notesStore?.UpdateNote(_activeNoteId, NoteContentBox.Text);
    }

    // ── Timer UI ──────────────────────────────────────────────────────────────

    private void OnTimerTick(TimeSpan remaining, double progress)
    {
        TimerDisplay.Text = _timer?.FormatRemaining() ?? "00:00";

        if (_timer?.State == IslandTimerState.Running)
        {
            FoldedTimerMini.Visibility = Visibility.Visible;
            FoldedTimerText.Text = _timer.FormatRemaining();
        }
        else
        {
            FoldedTimerMini.Visibility = Visibility.Collapsed;
        }
        UpdateFoldedWeatherMini();
    }

    private void OnTimerFinished()
    {
        FoldedTimerMini.Visibility = Visibility.Collapsed;
        UpdateFoldedWeatherMini();
        TimerStartBtn.Content = "Start";

        _eventQueue?.Push(new WindowIslandCard(
            Kind: "timer",
            Icon: "\uE916",
            Title: "Timer finished",
            Detail: "Time's up!",
            AccentHex: "#60CDFF",
            Priority: WindowIslandCardPriority.ReminderTimer,
            StayMs: 6000));

        try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
    }

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string minsStr && int.TryParse(minsStr, out int mins))
        {
            _timer?.Start(TimeSpan.FromMinutes(mins));
            TimerStartBtn.Content = "Pause";
        }
    }

    private void TimerStartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_timer == null) return;

        if (_timer.State == IslandTimerState.Running)
        {
            _timer.Pause();
            TimerStartBtn.Content = "Resume";
        }
        else if (_timer.State == IslandTimerState.Paused)
        {
            _timer.Resume();
            TimerStartBtn.Content = "Pause";
        }
        else
        {
            _timer.Start(_timer.Remaining > TimeSpan.Zero ? _timer.Remaining : TimeSpan.FromMinutes(5));
            TimerStartBtn.Content = "Pause";
        }
    }

    private void TimerResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _timer?.Reset();
        TimerStartBtn.Content = "Start";
        FoldedTimerMini.Visibility = Visibility.Collapsed;
        UpdateFoldedWeatherMini();
    }

    // ── Reminders UI ──────────────────────────────────────────────────────────

    private void RefreshRemindersUI()
    {
        if (_reminderStore == null) return;
        var list = _reminderStore.ActiveReminders;

        ReminderList.Children.Clear();

        if (list.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No active reminders",
                FontSize = 10,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };
            ReminderList.Children.Add(empty);
            return;
        }

        foreach (var rem in list)
        {
            var row = BuildReminderRow(rem);
            ReminderList.Children.Add(row);
        }
    }

    private Grid BuildReminderRow(IslandReminder rem)
    {
        var grid = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 3, 4, 3)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uEA8F",
            FontSize = 10,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 140, 0)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = rem.Title,
            FontSize = 10,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var relTime = new TextBlock
        {
            Text = WindowIslandReminderStore.FormatRelativeTime(rem.DueTimeUtc),
            FontSize = 8,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255))
        };
        stack.Children.Add(title);
        stack.Children.Add(relTime);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        // Complete ✓ button
        var doneBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE73E", FontSize = 9, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 108, 203, 95)) },
            Style = (Style)RootGrid.Resources["TabButtonStyle"],
            Width = 20, Height = 20,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(doneBtn, "Mark completed");
        doneBtn.Click += (_, _) => _reminderStore?.Complete(rem.Id);
        Grid.SetColumn(doneBtn, 2);
        grid.Children.Add(doneBtn);

        return grid;
    }

    private void ReminderQuickInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            string text = ReminderQuickInput.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                _reminderStore?.AddFromPhrase(text);
                ReminderQuickInput.Text = string.Empty;
            }
            e.Handled = true;
        }
    }

    private void OnReminderTriggered(IslandReminder reminder)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _eventQueue?.Push(new WindowIslandCard(
                Kind: "reminder",
                Icon: "\uEA8F",
                Title: "Reminder",
                Detail: reminder.Title,
                AccentHex: "#FF8C00",
                Priority: WindowIslandCardPriority.ReminderTimer,
                StayMs: 0));

            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        });
    }

    // ── Milestone 10: Weather Tab & Indicators ─────────────────────────────────

    private void OnWeatherUpdated(object? sender, WeatherForecastData? data)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeTab == 6) RefreshWeatherUI();
            UpdateFoldedWeatherMini();
        });
    }

    private void OnRainWarningTriggered(object? sender, WindowIslandCard card)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _eventQueue?.Push(card);
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        });
    }

    private void RefreshWeatherUI()
    {
        var settings = _settings ?? ServiceContainer.Settings.WindowIsland;
        if (string.IsNullOrWhiteSpace(settings.WeatherCity))
        {
            WeatherCityOverlay.Visibility = Visibility.Visible;
            WeatherSearchCancel.Visibility = Visibility.Collapsed;
            WeatherCityText.Text = "No city set";
            WeatherBigTemp.Text = "--";
            WeatherConditionText.Text = "Tap to set location";
            WeatherFeelsLikeText.Text = "Feels like -- · H: -- L: --";
            WeatherOutlookText.Text = "Please enter your city to fetch weather data.";
            WeatherHoursStack.Children.Clear();
            WeatherWeekStack.Children.Clear();
            return;
        }

        WeatherSearchCancel.Visibility = Visibility.Visible;

        var weather = WindowIslandWeatherService.Instance.CurrentWeather;
        if (weather == null)
        {
            WeatherCityText.Text = settings.WeatherCity;
            WeatherBigTemp.Text = "--";
            WeatherConditionText.Text = "Loading...";
            WeatherFeelsLikeText.Text = "Fetching forecast from MET Norway...";
            _ = WindowIslandWeatherService.Instance.RefreshAsync();
            return;
        }

        WeatherCityText.Text = string.IsNullOrEmpty(weather.CityName) ? settings.WeatherCity : weather.CityName;
        WeatherBigTemp.Text = WindowIslandWeatherService.FormatTemp(weather.CurrentTempC, settings.WeatherUnits);
        WeatherConditionIcon.Glyph = weather.IconGlyph;
        WeatherConditionText.Text = weather.ConditionText;
        WeatherFeelsLikeText.Text = $"Feels like {WindowIslandWeatherService.FormatTemp(weather.FeelsLikeC, settings.WeatherUnits)} · H: {WindowIslandWeatherService.FormatTemp(weather.HighC, settings.WeatherUnits)} L: {WindowIslandWeatherService.FormatTemp(weather.LowC, settings.WeatherUnits)}";
        WeatherOutlookText.Text = weather.SummaryOutlook;
        WeatherUpdatedText.Text = GetFriendlyUpdatedTime(weather.UpdatedTime);

        // Metric Tiles
        WeatherWindText.Text = $"{WindowIslandWeatherService.FormatSpeed(weather.WindSpeedMps, settings.WeatherUnits)} {weather.WindDirectionCompass}";
        WeatherHumidityText.Text = $"{(int)Math.Round(weather.HumidityPercent)}% · Dew {WindowIslandWeatherService.FormatTemp(weather.DewPointC, settings.WeatherUnits)}";
        WeatherPressureText.Text = $"{(int)Math.Round(weather.PressureHpa)} hPa · {weather.PressureTrend}";
        WeatherSunText.Text = $"{(weather.Sunrise.Year > 2000 ? weather.Sunrise.ToString("HH:mm") : "--:--")} · {(weather.Sunset.Year > 2000 ? weather.Sunset.ToString("HH:mm") : "--:--")}";
        WeatherPrecipText.Text = $"{weather.PrecipitationTodayMm:0.0} mm";
        WeatherUvText.Text = $"{weather.UvIndex:0.0} ({weather.UvLevel})";

        // Build hourly & week forecast items
        BuildWeatherHourlyItems(weather.Hourly, settings.WeatherUnits);
        BuildWeatherDailyItems(weather.Daily, settings.WeatherUnits);

        WeatherHoursScroll.Visibility = _showingWeeklyWeather ? Visibility.Collapsed : Visibility.Visible;
        WeatherWeekScroll.Visibility  = _showingWeeklyWeather ? Visibility.Visible : Visibility.Collapsed;

        UpdateFoldedWeatherMini();
    }

    private void BuildWeatherHourlyItems(IReadOnlyList<WeatherHourlyPoint> hourly, string units)
    {
        WeatherHoursStack.Children.Clear();
        if (hourly == null || hourly.Count == 0) return;

        bool isFirst = true;
        foreach (var pt in hourly.Take(16))
        {
            var itemBorder = new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(isFirst ? (byte)0x28 : (byte)0x15, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(isFirst ? (byte)0x35 : (byte)0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Width = 44
            };

            var sp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };

            string timeLabel = isFirst ? "Now" : pt.Time.ToString("HH:mm");
            sp.Children.Add(new TextBlock
            {
                Text = timeLabel,
                FontSize = 9,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            sp.Children.Add(new FontIcon
            {
                Glyph = pt.IconGlyph,
                FontSize = 13,
                Foreground = new SolidColorBrush(pt.SymbolCode.Contains("rain") || pt.SymbolCode.Contains("snow") ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0xFF, 0xFF, 0xD7, 0x00)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            sp.Children.Add(new TextBlock
            {
                Text = WindowIslandWeatherService.FormatTemp(pt.TempC, units),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            if (pt.PrecipitationMm > 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = $"{pt.PrecipitationMm:0.0}",
                    FontSize = 8,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xD0, 0x60, 0xCD, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            itemBorder.Child = sp;
            WeatherHoursStack.Children.Add(itemBorder);
            isFirst = false;
        }
    }

    private void BuildWeatherDailyItems(IReadOnlyList<WeatherDailyPoint> daily, string units)
    {
        WeatherWeekStack.Children.Clear();
        if (daily == null || daily.Count == 0) return;

        foreach (var d in daily)
        {
            var itemBorder = new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Width = 54
            };

            var sp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };

            sp.Children.Add(new TextBlock
            {
                Text = d.DayName,
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            sp.Children.Add(new FontIcon
            {
                Glyph = d.IconGlyph,
                FontSize = 13,
                Foreground = new SolidColorBrush(d.SymbolCode.Contains("rain") || d.SymbolCode.Contains("snow") ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0xFF, 0xFF, 0xD7, 0x00)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            sp.Children.Add(new TextBlock
            {
                Text = $"{WindowIslandWeatherService.FormatTemp(d.TempHighC, units)}/{WindowIslandWeatherService.FormatTemp(d.TempLowC, units)}",
                FontSize = 9,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            itemBorder.Child = sp;
            WeatherWeekStack.Children.Add(itemBorder);
        }
    }

    private static string GetFriendlyUpdatedTime(DateTime updatedUtc)
    {
        var diff = DateTime.UtcNow - updatedUtc;
        if (diff.TotalSeconds < 60) return "Updated just now";
        if (diff.TotalMinutes < 60) return $"Updated {(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"Updated {(int)diff.TotalHours}h ago";
        return $"Updated {updatedUtc.ToLocalTime():g}";
    }

    private void UpdateFoldedWeatherMini()
    {
        var settings = _settings ?? ServiceContainer.Settings.WindowIsland;
        if (!settings.WeatherTempOnIsland || _isMediaPlaying || _timer?.State == IslandTimerState.Running)
        {
            FoldedWeatherMini.Visibility = Visibility.Collapsed;
            return;
        }

        var weather = WindowIslandWeatherService.Instance.CurrentWeather;
        if (weather != null && !string.IsNullOrWhiteSpace(settings.WeatherCity))
        {
            FoldedWeatherMini.Visibility = Visibility.Visible;
            FoldedWeatherTemp.Text = WindowIslandWeatherService.FormatTemp(weather.CurrentTempC, settings.WeatherUnits);
            FoldedWeatherIcon.Glyph = weather.IconGlyph;
        }
        else
        {
            FoldedWeatherMini.Visibility = Visibility.Collapsed;
        }
    }

    private void WeatherChangeCityBtn_Click(object sender, RoutedEventArgs e)
    {
        WeatherCityOverlay.Visibility = Visibility.Visible;
        WeatherSearchInput.Focus(FocusState.Programmatic);
    }

    private void WeatherSearchCancel_Click(object sender, RoutedEventArgs e)
    {
        WeatherCityOverlay.Visibility = Visibility.Collapsed;
    }

    private void WeatherSearchInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            PerformCitySearch();
            e.Handled = true;
        }
    }

    private void WeatherSearchSubmit_Click(object sender, RoutedEventArgs e)
    {
        PerformCitySearch();
    }

    private async void PerformCitySearch()
    {
        string query = WeatherSearchInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        WeatherSearchSubmit.IsEnabled = false;
        WeatherSearchFeedback.Text = "Searching locations...";
        WeatherSearchFeedback.Visibility = Visibility.Visible;
        WeatherSearchResultsStack.Children.Clear();

        try
        {
            var results = await WindowIslandWeatherService.Instance.SearchCitiesAsync(query);
            if (results != null && results.Count > 0)
            {
                WeatherSearchFeedback.Visibility = Visibility.Collapsed;
                foreach (var loc in results)
                {
                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 6, 8, 6),
                        Margin = new Thickness(0, 2, 0, 2)
                    };

                    var sp = new StackPanel { Spacing = 2 };
                    sp.Children.Add(new TextBlock
                    {
                        Text = loc.DisplayName,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"{loc.Latitude:F2}, {loc.Longitude:F2}",
                        FontSize = 9,
                        Foreground = new SolidColorBrush(ColorHelper.FromArgb(0x80, 0xFF, 0xFF, 0xFF))
                    });
                    btn.Content = sp;

                    btn.Click += async (_, _) =>
                    {
                        WeatherCityOverlay.Visibility = Visibility.Collapsed;
                        WeatherSearchFeedback.Visibility = Visibility.Collapsed;
                        await WindowIslandWeatherService.Instance.SetLocationAsync(loc.DisplayName, loc.Latitude, loc.Longitude);
                        RefreshWeatherUI();
                    };

                    WeatherSearchResultsStack.Children.Add(btn);
                }
            }
            else
            {
                WeatherSearchFeedback.Text = "No locations found. Please check spelling.";
                WeatherSearchFeedback.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            WeatherSearchFeedback.Text = $"Search failed: {ex.Message}";
            WeatherSearchFeedback.Visibility = Visibility.Visible;
        }
        finally
        {
            WeatherSearchSubmit.IsEnabled = true;
        }
    }

    private async void WeatherRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        WeatherRefreshBtn.IsEnabled = false;
        WeatherUpdatedText.Text = "Updating...";
        try
        {
            await WindowIslandWeatherService.Instance.RefreshAsync(force: true);
            RefreshWeatherUI();
        }
        catch { }
        finally
        {
            WeatherRefreshBtn.IsEnabled = true;
        }
    }

    private void WeatherBtnHours_Click(object sender, RoutedEventArgs e)
    {
        _showingWeeklyWeather = false;
        WeatherHoursScroll.Visibility = Visibility.Visible;
        WeatherWeekScroll.Visibility = Visibility.Collapsed;
        WeatherBtnHours.Background = new SolidColorBrush(ColorHelper.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
        WeatherBtnHours.Foreground = new SolidColorBrush(Colors.White);
        WeatherBtnWeek.Background = new SolidColorBrush(Colors.Transparent);
        WeatherBtnWeek.Foreground = new SolidColorBrush(ColorHelper.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    }

    private void WeatherBtnWeek_Click(object sender, RoutedEventArgs e)
    {
        _showingWeeklyWeather = true;
        WeatherHoursScroll.Visibility = Visibility.Collapsed;
        WeatherWeekScroll.Visibility = Visibility.Visible;
        WeatherBtnWeek.Background = new SolidColorBrush(ColorHelper.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
        WeatherBtnWeek.Foreground = new SolidColorBrush(Colors.White);
        WeatherBtnHours.Background = new SolidColorBrush(Colors.Transparent);
        WeatherBtnHours.Foreground = new SolidColorBrush(ColorHelper.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    }

    // ── Milestone 11: Controls Tab ─────────────────────────────────────────────

    private async void RefreshControlsUI()
    {
        try
        {
            // 1. Wi-Fi
            var (hasWifi, wifiOn, wifiSsid) = await WindowIslandControlsService.Instance.GetWiFiStateAsync();
            ControlsWifiBtn.IsEnabled = hasWifi;
            ControlsWifiStatus.Text = hasWifi ? (wifiOn ? wifiSsid : "Off") : "No adapter";
            ControlsWifiIcon.Foreground = new SolidColorBrush(wifiOn ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

            // 2. Bluetooth
            var (hasBt, btOn) = await WindowIslandControlsService.Instance.GetBluetoothStateAsync();
            ControlsBtBtn.IsEnabled = hasBt;
            ControlsBtStatus.Text = hasBt ? (btOn ? "On" : "Off") : "No adapter";
            ControlsBtIcon.Foreground = new SolidColorBrush(btOn ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

            // 3. Dark Theme
            bool isDark = WindowIslandControlsService.IsSystemDarkTheme();
            ControlsThemeStatus.Text = isDark ? "Dark" : "Light";
            ControlsThemeIcon.Foreground = new SolidColorBrush(isDark ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0xFF, 0xFF, 0xD7, 0x00));

            // 4. Battery
            var (hasBattery, batPct, isCharging, batText) = WindowIslandControlsService.GetBatteryInfo();
            ControlsBatteryStatus.Text = hasBattery ? $"{batPct}% · {batText}" : "On mains power";
            ControlsBatteryIcon.Glyph = isCharging ? "\uEA93" : "\uE83F";

            // 5. VPN
            var (vpnConnected, vpnName) = WindowIslandControlsService.GetVpnStatus();
            ControlsVpnStatus.Text = vpnConnected ? vpnName : "Off";
            ControlsVpnIcon.Foreground = new SolidColorBrush(vpnConnected ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

            // 6. Brightness
            if (ServiceContainer.BrightnessService.IsAvailable)
            {
                int curBri = await ServiceContainer.BrightnessService.GetBrightnessAsync();
                _controlsBrightnessChanging = true;
                ControlsBrightnessSlider.Value = curBri;
                ControlsBrightnessText.Text = $"{curBri}%";
                _controlsBrightnessChanging = false;
            }
        }
        catch { }
    }

    private async void ControlsWifiBtn_Click(object sender, RoutedEventArgs e)
    {
        await WindowIslandControlsService.Instance.ToggleWiFiAsync();
        RefreshControlsUI();
    }

    private async void ControlsBtBtn_Click(object sender, RoutedEventArgs e)
    {
        await WindowIslandControlsService.Instance.ToggleBluetoothAsync();
        RefreshControlsUI();
    }

    private void ControlsThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowIslandControlsService.ToggleSystemDarkTheme();
        RefreshControlsUI();
    }

    private void ControlsBatteryBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:powersleep",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void ControlsVpnBtn_Click(object sender, RoutedEventArgs e)
    {
        await WindowIslandControlsService.ToggleVpnAsync();
        RefreshControlsUI();
    }

    private async void ControlsBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_controlsBrightnessChanging) return;
        int val = (int)e.NewValue;
        ControlsBrightnessText.Text = $"{val}%";
        if (ServiceContainer.BrightnessService.IsAvailable)
        {
            await ServiceContainer.BrightnessService.SetBrightnessAsync(val);
        }
    }

    private void ControlsLockBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowIslandControlsService.LockComputer();
    }

    private void ControlsSleepBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowIslandControlsService.SleepComputer();
    }

    private void ControlsRestartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_restartConfirming)
        {
            _restartConfirmTimer?.Stop();
            _restartConfirming = false;
            ControlsRestartText.Text = "Restarting...";
            ControlsRestartBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            WindowIslandControlsService.RestartComputer();
        }
        else
        {
            _restartConfirming = true;
            ControlsRestartText.Text = "Tap again (3s)";
            ControlsRestartBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(0x40, 0x60, 0xCD, 0xFF));

            _restartConfirmTimer?.Stop();
            _restartConfirmTimer = DispatcherQueue.CreateTimer();
            _restartConfirmTimer.Interval = TimeSpan.FromSeconds(3);
            _restartConfirmTimer.Tick += (_, _) =>
            {
                _restartConfirmTimer.Stop();
                _restartConfirming = false;
                ControlsRestartText.Text = "Restart";
                ControlsRestartBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            };
            _restartConfirmTimer.Start();
        }
    }

    private void ControlsShutdownBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_shutdownConfirming)
        {
            _shutdownConfirmTimer?.Stop();
            _shutdownConfirming = false;
            ControlsShutdownText.Text = "Shutting down...";
            ControlsShutdownBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            WindowIslandControlsService.ShutdownComputer();
        }
        else
        {
            _shutdownConfirming = true;
            ControlsShutdownText.Text = "Tap again (3s)";
            ControlsShutdownBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(0x40, 0xFF, 0x6B, 0x6B));

            _shutdownConfirmTimer?.Stop();
            _shutdownConfirmTimer = DispatcherQueue.CreateTimer();
            _shutdownConfirmTimer.Interval = TimeSpan.FromSeconds(3);
            _shutdownConfirmTimer.Tick += (_, _) =>
            {
                _shutdownConfirmTimer.Stop();
                _shutdownConfirming = false;
                ControlsShutdownText.Text = "Shut down";
                ControlsShutdownBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            };
            _shutdownConfirmTimer.Start();
        }
    }
}
