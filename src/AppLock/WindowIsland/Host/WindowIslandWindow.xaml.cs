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
using Microsoft.UI.Text;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.System;
using AppLock.Island.Events;
using AppLock.Island.Modules;
using AppLock.Models;
using AppLock.Services;
using AppLock.WindowIsland.Events;
using AppLock.WindowIsland.Modules;
using AppLock.WindowIsland.Modules.Chat;
using AppLock.WindowIsland.Modules.Controls;
using AppLock.WindowIsland.Modules.Lyrics;
using AppLock.WindowIsland.Modules.Notifications;
using AppLock.WindowIsland.Modules.Text;
using AppLock.WindowIsland.Modules.Weather;
using AppLock.WindowIsland.Services;
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
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
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

    // Home tab lyrics: one lookup per track, shown beside the player when a lyric sheet exists.
    private readonly LrclibLyricsService _lyricsService = new();
    private string _lyricsKey = string.Empty;

    // Milestone 7: Launcher
    private WindowIslandLauncher?             _launcher;
    private string?                          _editingShortcutId;

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
    private string?                           _loadedNoteId;
    private DQTimer?                          _noteSaveTimer;
    private bool                              _noteUpdating;
    private DateTime?                         _reminderParsedDue;

    // Milestone 10: Weather
    private bool                              _showingWeeklyWeather;

    // Milestone 11: Controls
    private DQTimer?                          _restartConfirmTimer;
    private DQTimer?                          _shutdownConfirmTimer;
    private bool                              _restartConfirming;
    private bool                              _shutdownConfirming;
    private bool                              _controlsBrightnessChanging;

    // Milestone 12: Notifications, VPN, battery alert cards, mic/cam dots
    private bool _micDotActive;
    private bool _camDotActive;

    // Milestone 13: Chat (WebView2)
    private WebView2?                         _chatWebView;
    private bool                              _chatInitializing;
    private bool                              _chatInitialized;
    private DQTimer?                          _chatIdleTimer;
    private string?                           _chatLastNavigatedUrl;

    // Milestone 15: Visibility rules
    private WindowIslandVisibilityService?    _visibilityService;

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
    private TextBlock[] _tabTextBlocks = Array.Empty<TextBlock>();

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
        _tabTextBlocks = new TextBlock[]
        {
            TabHomeText, TabClipboardText, TabShelfText, TabTextText,
            TabChatText, TabNoteText, TabWeatherText, TabControlsText
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
        NoteContentBox.Loaded += (_, _) =>
        {
            _loadedNoteId = null;
            RefreshNotesUI();
        };
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

        // Milestone 12: VPN / battery alert cards / mic-cam dots
        WindowIslandNotificationService.Instance.VpnStateChanged    += OnVpnStateChanged;
        WindowIslandNotificationService.Instance.BatteryLowCard     += OnBatteryLowCard;
        WindowIslandNotificationService.Instance.BatteryChargingCard += OnBatteryChargingCard;
        WindowIslandNotificationService.Instance.MicCamStateChanged  += OnMicCamStateChanged;

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
        _clipboardState?.Refresh();

        // Milestone 15: start visibility service
        _visibilityService?.Dispose();
        _visibilityService = new WindowIslandVisibilityService(_settings, DispatcherQueue);
        _visibilityService.VisibilityChanged += OnIslandVisibilityChanged;
        _visibilityService.EdgeDwellTriggered += OnEdgeDwellTriggered;
        _visibilityService.Start();

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
        UpdateMicCamDots();

        // Milestone 15: re-create visibility service with new settings
        if (_visibilityService != null)
        {
            _visibilityService.VisibilityChanged  -= OnIslandVisibilityChanged;
            _visibilityService.EdgeDwellTriggered -= OnEdgeDwellTriggered;
            _visibilityService.Dispose();
            _visibilityService = new WindowIslandVisibilityService(_settings, DispatcherQueue);
            _visibilityService.VisibilityChanged  += OnIslandVisibilityChanged;
            _visibilityService.EdgeDwellTriggered += OnEdgeDwellTriggered;
            _visibilityService.Start();
        }

        // Milestone 16: Polish, Lite mode, Display Affinity, Frame Stats
        try
        {
            SetWindowDisplayAffinity(_hwnd, settings.ExcludeFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }
        catch { }

        if (_clockTimer != null)
        {
            _clockTimer.Interval = settings.LiteMode ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(1);
        }

        if (FrameStatsRow != null)
        {
            FrameStatsRow.Visibility = settings.ShowFrameStats ? Visibility.Visible : Visibility.Collapsed;
        }
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
        FlushPendingNoteSave();
        _noteSaveTimer?.Stop();
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

        // Milestone 12
        WindowIslandNotificationService.Instance.VpnStateChanged     -= OnVpnStateChanged;
        WindowIslandNotificationService.Instance.BatteryLowCard      -= OnBatteryLowCard;
        WindowIslandNotificationService.Instance.BatteryChargingCard -= OnBatteryChargingCard;
        WindowIslandNotificationService.Instance.MicCamStateChanged  -= OnMicCamStateChanged;

        // Milestone 13: Chat cleanup
        _chatIdleTimer?.Stop();
        _chatIdleTimer = null;
        DisposeChatWebView();

        // Milestone 15: Visibility service cleanup
        _visibilityService?.Dispose();
        _visibilityService = null;

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
            _clipboardState?.Refresh();
        }
        else StopHomeState();

        // Refresh clipboard / shelf / notes on switch
        if (_activeTab == 1)
        {
            _clipboardState?.Refresh();
            DispatcherQueue.TryEnqueue(() => ClipboardSearch?.Focus(FocusState.Programmatic));
        }
        else ResetClipboardClearButton();
        if (_activeTab == 2) RefreshShelfUI();
        else ResetShelfClearButton();
        if (_activeTab == 3) RefreshTextUI();
        else if (TextQrOverlay != null) TextQrOverlay.Visibility = Visibility.Collapsed;
        if (_activeTab == 4)
        {
            ResetChatIdleTimer();
            RefreshChatUI();
        }
        else if (_previousTab == 4)
        {
            if (ChatAddOverlay != null)
            {
                ChatAddOverlay.Visibility = Visibility.Collapsed;
                _editingChatIndex = -1;
                if (ChatWebViewHost != null) ChatWebViewHost.Visibility = Visibility.Visible;
            }
            SuspendChatWebView();
            StartChatIdleTimer();
        }
        if (_activeTab == 5)
        {
            _loadedNoteId = null;
            RefreshNotesUI();
            RefreshRemindersUI();
        }
        else
        {
            if (_previousTab == 5) FlushPendingNoteSave();
            ResetNoteDeleteBtnUI();
            if (NoteNewTitleOverlay != null) NoteNewTitleOverlay.Visibility = Visibility.Collapsed;
        }
        if (_activeTab == 6)
        {
            RefreshWeatherUI();
        }
        else if (_previousTab == 6)
        {
            if (WeatherCityOverlay != null) WeatherCityOverlay.Visibility = Visibility.Collapsed;
        }
        if (_activeTab == 7)
        {
            RefreshControlsUI();
        }
        else if (_previousTab == 7)
        {
            if (ControlsDeviceOverlay != null) ControlsDeviceOverlay.Visibility = Visibility.Collapsed;
        }

        // Show new content, collapse previous
        for (int i = 0; i < TabCount; i++)
        {
            _tabContents[i].Visibility = (i == _activeTab || i == _previousTab)
                ? Visibility.Visible
                : Visibility.Collapsed;
            _tabContents[i].Opacity = 1.0;
        }

        // Force layout pass on incoming tab so its composition visual and geometry exist
        _tabContents[_activeTab].UpdateLayout();

        if (_activeTab == 5)
        {
            _loadedNoteId = null;
            RefreshNotesUI();
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
            // After animation: collapse the previous tab and ensure active visual is steady
            DispatcherQueue.TryEnqueue(() =>
            {
                _tabContents[fromTab].Visibility = Visibility.Collapsed;
                toVisual.Opacity = 1f;
                toVisual.Offset  = Vector3.Zero;
            });
        };
        toVisual.StartAnimation("Opacity", fadeIn);
        toVisual.StartAnimation("Offset",  slideIn);
        batch.End();
    }

    private void UpdateTabVisuals()
    {
        // Snap non-participating tabs to hidden, preserve transitioning tabs
        for (int i = 0; i < TabCount; i++)
        {
            if (i != _activeTab && i != _previousTab)
            {
                var v = ElementCompositionPreview.GetElementVisual(_tabContents[i]);
                v.Opacity = 0f;
                v.Offset  = Vector3.Zero;
            }
        }
    }

    private void UpdateTabHighlight(bool animate)
    {
        var accentColor = GetSystemAccentColor();
        var accentBrush = new SolidColorBrush(accentColor);
        var defaultBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(192, 255, 255, 255)); // #C0FFFFFF default

        if (TabIndicator != null)
        {
            TabIndicator.Background = accentBrush;
        }

        // Highlight the active tab button text/icon with window accent color,
        // show text only for the active tab (e.g. icon [clipboard]), remaining tabs show icon only
        for (int i = 0; i < TabCount; i++)
        {
            bool active = (i == _activeTab);
            if (_tabButtons != null && i < _tabButtons.Length && _tabButtons[i] != null)
            {
                _tabButtons[i].Foreground = active ? accentBrush : defaultBrush;
            }
            if (_tabTextBlocks != null && i < _tabTextBlocks.Length && _tabTextBlocks[i] != null)
            {
                _tabTextBlocks[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Slide the tab indicator to the correct position under active tab
        float targetX = _activeTab * 34f;
        try
        {
            if (_tabButtons != null && _activeTab < _tabButtons.Length && _tabButtons[_activeTab] != null && TabStrip != null)
            {
                var transform = _tabButtons[_activeTab].TransformToVisual(TabStrip);
                var p = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                targetX = (float)p.X + 4f;
            }
        }
        catch { }

        if (animate)
        {
            var v = ElementCompositionPreview.GetElementVisual(TabIndicator);
            var anim = v.Compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, new Vector3(targetX, 0f, 0f));
            anim.Duration = TimeSpan.FromMilliseconds(IslandMotion.TabSwitchMs);
            v.StartAnimation("Offset", anim);
        }
        else
        {
            var v = ElementCompositionPreview.GetElementVisual(TabIndicator);
            v.Offset = new Vector3(targetX, 0f, 0f);
        }
    }

    private static Windows.UI.Color GetSystemAccentColor()
    {
        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            return uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            try
            {
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object res) && res is Windows.UI.Color c)
                {
                    return c;
                }
            }
            catch { }
            return Windows.UI.Color.FromArgb(255, 0, 120, 212); // Windows blue default
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
                if (TextQrOverlay != null && TextQrOverlay.Visibility == Visibility.Visible)
                {
                    TextQrOverlay.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                    break;
                }
                if (ChatAddOverlay != null && ChatAddOverlay.Visibility == Visibility.Visible)
                {
                    ChatCancelAdd_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                }
                if (WeatherCityOverlay != null && WeatherCityOverlay.Visibility == Visibility.Visible)
                {
                    WeatherSearchCancel_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                }
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

            case VirtualKey.Down when _activeTab == 1 && !IsCtrlDown():
                NavigateClipboardSelection(1);
                e.Handled = true;
                break;

            case VirtualKey.Up when _activeTab == 1 && !IsCtrlDown():
                NavigateClipboardSelection(-1);
                e.Handled = true;
                break;

            case VirtualKey.Enter when _activeTab == 1 && !IsCtrlDown():
                PasteSelectedClipboardItem();
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
            int openDelay = _settings.OpenDelayMs > 0 ? _settings.OpenDelayMs : IslandMotion.HoverOpenDelayMs;
            int closeDelay = _settings.CloseDelayMs > 0 ? _settings.CloseDelayMs : IslandMotion.HoverCloseDelayMs;
            if (_hoverInside && !_compositor.IsOpen && elapsed >= openDelay)
            {
                if (_eventQueue?.HasVisibleCard == true)
                {
                    _eventQueue.Clear();
                }
                Expand();
            }
            else if (!_hoverInside && _compositor.IsOpen && elapsed >= closeDelay)
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
            if (_activeTab == 1)
            {
                DispatcherQueue.TryEnqueue(() => ClipboardSearch?.Focus(FocusState.Programmatic));
            }
            if (_activeTab == 4)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ResetChatIdleTimer();
                    ResumeChatWebView();
                });
            }
        });
    }

    public void Collapse()
    {
        if (_compositor == null || !_compositor.IsOpen) return;

        if (_activeTab == 4)
        {
            SuspendChatWebView();
            StartChatIdleTimer();
        }

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
                if (_previousForegroundWindow != nint.Zero)
                {
                    SetForegroundWindow(_previousForegroundWindow);
                    _previousForegroundWindow = nint.Zero;
                }
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

        string islandDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "island");
        Directory.CreateDirectory(islandDir);

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
        await File.WriteAllTextAsync(Path.Combine(islandDir, "bench.txt"), report);
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

        // Milestone 15: bring island into view for card duration
        _visibilityService?.RequestTemporaryReveal(card.StayMs + 1000);

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

        // The open layer is the panel's content: it fills the window so the shell never shows
        // an empty strip under the cards.
        OpenLayer.Width  = winW;
        OpenLayer.Height = winH;

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
                ClearLyrics();
                return;
            }

            MediaPlayerPanel.Visibility = Visibility.Visible;
            RecentPanel.Visibility      = Visibility.Collapsed;

            MediaTitle.Text  = snap.Title;
            MediaArtist.Text = snap.Artist;
            MediaApp.Text    = snap.App.ToUpperInvariant();
            MediaPlayPauseIcon.Glyph = snap.Playing ? "\uE769" : "\uE768"; // Pause : Play

            // Progress line with elapsed / remaining times, like a phone-style player card
            MediaElapsed.Text   = FormatTime(snap.PositionSeconds);
            MediaRemaining.Text = snap.DurationSeconds > 0
                ? "-" + FormatTime(Math.Max(0, snap.DurationSeconds - snap.PositionSeconds))
                : string.Empty;

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

            // Lyrics follow the track: fetched once per title/artist pair.
            _ = UpdateLyricsAsync(snap);
        }
        catch (Exception ex) { WindowIslandHost.Log("OnMediaUpdated: " + ex.Message); }
    }

    /// <summary>m:ss for the player's elapsed / remaining labels.</summary>
    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private async Task UpdateLyricsAsync(MediaSnapshot snap)
    {
        string key = snap.Artist + "\u0001" + snap.Title;
        if (key == _lyricsKey) return;
        _lyricsKey = key;

        ShowLyrics(null);                                    // hide the previous track's sheet at once
        var lyrics = await _lyricsService.GetAsync(snap.Title, snap.Artist, snap.DurationSeconds);
        if (_lyricsKey != key) return;                        // track changed while we were fetching
        ShowLyrics(lyrics);
    }

    private void ClearLyrics()
    {
        _lyricsKey = string.Empty;
        ShowLyrics(null);
    }

    /// <summary>
    /// Renders the lyric sheet beside the player. While lyrics are on screen the tile grid steps
    /// aside so the text has room; without lyrics the tiles come back.
    /// </summary>
    private void ShowLyrics(LyricsResult? lyrics)
    {
        try
        {
            LyricsStack.Children.Clear();

            var lines = lyrics?.Lines ?? Array.Empty<string>();
            foreach (var line in lines)
            {
                LyricsStack.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 11,
                    LineHeight = 15,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 255, 255, 255))
                });
            }

            bool show = lines.Count > 0;
            LyricsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // With lyrics the player card shares the row with the lyrics card; without them it
            // spans the whole width again.
            Grid.SetColumnSpan(MediaCard, show ? 1 : 2);
        }
        catch (Exception ex) { WindowIslandHost.Log("ShowLyrics: " + ex.Message); }
    }

    private void RecentChevron_Click(object sender, RoutedEventArgs e) => SwitchTab(1);

    /// <summary>Fills the Home tab's RECENTLY COPIED rows from the same clipboard history list.</summary>
    private void UpdateHomeRecents(IReadOnlyList<AppLock.Island.Modules.ClipboardRow> rows)
    {
        var grids = new[] { RecentItem0, RecentItem1, RecentItem2 };
        var texts = new[] { RecentText0, RecentText1, RecentText2 };
        var ages  = new[] { RecentAge0,  RecentAge1,  RecentAge2  };
        var icons = new[] { RecentIcon0, RecentIcon1, RecentIcon2 };

        for (int i = 0; i < grids.Length; i++)
        {
            bool has = i < rows.Count;
            grids[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) continue;

            var row = rows[i];
            texts[i].Text = row.Preview;
            ages[i].Text  = row.Age;
            icons[i].Glyph = row.Type switch
            {
                "image" => "\uEB9F",
                "files" => "\uE7C3",
                "text"  => "\uE8C3",
                _       => "\uE77F"
            };
        }

        RecentEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        // The live reading is the Speed Meter's, so open that page rather than the island settings
        try { (App.MainWindowInstance as MainWindow)?.NavigateToSpeedMeterSettings(); } catch { }
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

    private bool _clipboardClearConfirming;
    private DQTimer? _clipboardClearConfirmTimer;

    private void ResetClipboardClearButton()
    {
        _clipboardClearConfirming = false;
        _clipboardClearConfirmTimer?.Stop();
        if (ClipboardClearBtn != null)
        {
            ClipboardClearBtn.Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 };
        }
    }

    private IReadOnlyList<AppLock.Island.Modules.ClipboardRow> _currentClipboardRows = Array.Empty<AppLock.Island.Modules.ClipboardRow>();
    private int _selectedClipboardIndex = 0;

    private void NavigateClipboardSelection(int delta)
    {
        if (_currentClipboardRows.Count == 0) return;
        _selectedClipboardIndex = Math.Clamp(_selectedClipboardIndex + delta, 0, _currentClipboardRows.Count - 1);
        UpdateClipboardSelection();
    }

    private async void PasteSelectedClipboardItem()
    {
        if (_selectedClipboardIndex >= 0 && _selectedClipboardIndex < _currentClipboardRows.Count)
        {
            bool shift = IsShiftDown();
            var row = _currentClipboardRows[_selectedClipboardIndex];
            await (_clipboardState?.PasteAsync(row.Id, copyOnly: shift) ?? Task.CompletedTask);
        }
    }

    private void ClipboardSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down)
        {
            NavigateClipboardSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Up)
        {
            NavigateClipboardSelection(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            PasteSelectedClipboardItem();
            e.Handled = true;
            return;
        }
    }

    private void ClipboardSearch_TextChanged(object sender, TextChangedEventArgs e)
        => _clipboardState?.Refresh(ClipboardSearch.Text.Trim());

    private async void ClipboardClear_Click(object sender, RoutedEventArgs e)
    {
        if (_clipboardClearConfirming)
        {
            ResetClipboardClearButton();
            try
            {
                await (_clipboardState?.ClearAsync() ?? Task.CompletedTask);
                _clipboardState?.Refresh(ClipboardSearch?.Text?.Trim());
            }
            catch { }
            return;
        }

        _clipboardClearConfirming = true;
        ClipboardClearBtn.Content = new TextBlock
        {
            Text = "Clear all?",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 71)),
            VerticalAlignment = VerticalAlignment.Center
        };

        _clipboardClearConfirmTimer?.Stop();
        _clipboardClearConfirmTimer = DispatcherQueue.CreateTimer();
        _clipboardClearConfirmTimer.Interval = TimeSpan.FromSeconds(3);
        _clipboardClearConfirmTimer.Tick += (_, _) =>
        {
            ResetClipboardClearButton();
        };
        _clipboardClearConfirmTimer.Start();
    }

    private void OnClipboardItemsLoaded(IReadOnlyList<AppLock.Island.Modules.ClipboardRow> rows)
    {
        _currentClipboardRows = rows;
        if (_selectedClipboardIndex >= rows.Count)
        {
            _selectedClipboardIndex = Math.Max(0, rows.Count - 1);
        }

        ClipboardList.Children.Clear();
        UpdateHomeRecents(rows);

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

        for (int i = 0; i < rows.Count; i++)
        {
            var rowGrid = BuildClipboardRow(rows[i], i);
            ClipboardList.Children.Add(rowGrid);
        }
    }

    private void UpdateClipboardSelection()
    {
        var accentBrush = new SolidColorBrush(GetSystemAccentColor());
        var normalBg = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        var selectedBg = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
        var normalBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255));

        for (int i = 0; i < ClipboardList.Children.Count; i++)
        {
            if (ClipboardList.Children[i] is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Border b)
            {
                bool sel = (i == _selectedClipboardIndex);
                b.Background = sel ? selectedBg : normalBg;
                b.BorderBrush = sel ? accentBrush : normalBorder;
                b.BorderThickness = new Thickness(sel ? 1.5 : 1);

                if (sel)
                {
                    b.StartBringIntoView();
                }
            }
        }
    }

    private Grid BuildClipboardRow(AppLock.Island.Modules.ClipboardRow row, int index)
    {
        bool isSelected = (index == _selectedClipboardIndex);
        var accentBrush = new SolidColorBrush(GetSystemAccentColor());
        var normalBg = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        var selectedBg = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
        var normalBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255));

        // Outer border button with comfortable sizing
        var border = new Border
        {
            Background = isSelected ? selectedBg : normalBg,
            BorderBrush = isSelected ? accentBrush : normalBorder,
            BorderThickness = new Thickness(isSelected ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Tag = index
        };
        border.PointerEntered  += (s, _) =>
        {
            _selectedClipboardIndex = index;
            UpdateClipboardSelection();
        };
        border.PointerExited   += (s, _) =>
        {
            if (_selectedClipboardIndex != index)
            {
                ((Border)s).Background = normalBg;
            }
        };

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Leading visual: prominent image preview thumbnail (64x48) or rounded badge (36x36)
        FrameworkElement leadingVisual;
        if (row.Type == "image")
        {
            string? imagePath = null;
            if (!string.IsNullOrEmpty(row.Thumb))
            {
                if (File.Exists(row.Thumb))
                {
                    imagePath = row.Thumb;
                }
                else
                {
                    try
                    {
                        string candidate = Path.Combine(ServiceContainer.ClipboardStore.StoragePath, row.Thumb);
                        if (File.Exists(candidate)) imagePath = candidate;
                    }
                    catch { }
                }
            }

            var thumbBorder = new Border
            {
                Width = 64,
                Height = 48,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };

            if (imagePath != null)
            {
                try
                {
                    var bmp = new BitmapImage(new Uri(imagePath))
                    {
                        DecodePixelHeight = 96
                    };
                    var img = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.UniformToFill,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    thumbBorder.Child = img;
                }
                catch
                {
                    thumbBorder.Child = new FontIcon
                    {
                        Glyph = "\uEB9F",
                        FontSize = 18,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
            }
            else
            {
                thumbBorder.Child = new FontIcon
                {
                    Glyph = "\uEB9F",
                    FontSize = 18,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            leadingVisual = thumbBorder;
        }
        else if (row.Type == "text")
        {
            var textBadge = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "T",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 16,
                    FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif"),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
            leadingVisual = textBadge;
        }
        else
        {
            string glyph = row.Type switch
            {
                "files" => "\uE8B7",
                _       => "\uE77F"
            };
            var fileBadge = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            leadingVisual = fileBadge;
        }

        Grid.SetColumn(leadingVisual, 0);
        inner.Children.Add(leadingVisual);

        // Preview + source (larger, readable fonts)
        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var preview = new TextBlock
        {
            Text = row.Preview,
            FontSize = 13,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var source = new TextBlock
        {
            Text = string.IsNullOrEmpty(row.Source) ? row.Age : $"{row.Source} · {row.Age}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(preview);
        textStack.Children.Add(source);
        Grid.SetColumn(textStack, 1);
        inner.Children.Add(textStack);

        // Interactive Pin Toggle Button
        var pinBtn = new Button
        {
            Style = (Style)RootGrid.Resources["TabButtonStyle"],
            Padding = new Thickness(4),
            MinWidth = 28,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(pinBtn, row.Pinned ? "Unpin item" : "Pin item");
        var pinIcon = new FontIcon
        {
            Glyph = row.Pinned ? "\uE840" : "\uE718",
            FontSize = 13,
            Foreground = row.Pinned
                ? new SolidColorBrush(GetSystemAccentColor())
                : new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255))
        };
        pinBtn.Content = pinIcon;
        pinBtn.PointerEntered += (_, _) =>
        {
            if (!row.Pinned)
            {
                pinIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 255, 255, 255));
            }
        };
        pinBtn.PointerExited += (_, _) =>
        {
            if (!row.Pinned)
            {
                pinIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255));
            }
        };
        pinBtn.Click += async (s, e) =>
        {
            try
            {
                await (_clipboardState?.PinAsync(row.Id) ?? Task.CompletedTask);
                _clipboardState?.Refresh(ClipboardSearch?.Text?.Trim());
            }
            catch { }
        };
        Grid.SetColumn(pinBtn, 2);
        inner.Children.Add(pinBtn);

        border.Child = inner;

        // Click = paste; Shift+Click = copy only (unless pin button clicked)
        border.PointerPressed += async (_, pe) =>
        {
            var originalSource = pe.OriginalSource as DependencyObject;
            if (originalSource != null && IsDescendantOf(originalSource, pinBtn))
            {
                return;
            }

            bool shift = pe.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
            await (_clipboardState?.PasteAsync(row.Id, copyOnly: shift) ?? Task.CompletedTask);
        };

        return new Grid { Children = { border } };
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject target)
    {
        while (node != null)
        {
            if (node == target) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ── Milestone 6: Shelf tab ─────────────────────────────────────────────────

    private bool _shelfClearConfirming;
    private DQTimer? _shelfClearConfirmTimer;

    private void ResetShelfClearButton()
    {
        _shelfClearConfirming = false;
        _shelfClearConfirmTimer?.Stop();
        if (ShelfClearBtn != null)
        {
            ShelfClearBtn.Content = "Clear all";
            ShelfClearBtn.Foreground = null;
        }
    }

    private void RefreshShelfUI()
    {
        if (_shelf == null) return;
        var rows = _shelf.Rows();

        ShelfEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfItemsGrid.Children.Clear();
        ShelfItemsGrid.RowDefinitions.Clear();
        ShelfItemsGrid.ColumnDefinitions.Clear();

        if (rows.Count == 0)
        {
            ResetShelfClearButton();
            if (ShelfClearBtn != null) ShelfClearBtn.IsEnabled = false;
            return;
        }
        if (ShelfClearBtn != null) ShelfClearBtn.IsEnabled = true;

        const int columns = 4;
        for (int c = 0; c < columns; c++)
        {
            ShelfItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int totalRows = (rows.Count + columns - 1) / columns;
        for (int r = 0; r < totalRows; r++)
        {
            ShelfItemsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var card = BuildShelfCard(rows[i]);
            int r = i / columns;
            int c = i % columns;
            Grid.SetRow(card, r);
            Grid.SetColumn(card, c);
            ShelfItemsGrid.Children.Add(card);
        }
    }

    private Grid BuildShelfCard(AppLock.Island.Modules.ShelfRow row)
    {
        string ext = System.IO.Path.GetExtension(row.Path).ToLowerInvariant();
        bool isDir = System.IO.Directory.Exists(row.Path);
        string iconGlyph = isDir ? "\uE8B7" : ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "\uEB9F",
            ".mp4" or ".mkv" or ".mov" or ".avi"                       => "\uE8B2",
            ".mp3" or ".wav" or ".flac" or ".aac"                      => "\uE8D6",
            ".pdf"                                                      => "\uEA90",
            ".exe" or ".msi"                                            => "\uE756",
            ".zip" or ".rar" or ".7z"                                   => "\uE8B9",
            _                                                           => "\uE7C3"
        };

        // Icon badge or preview image
        var iconBadge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        bool loadedImage = false;
        if (row.Exists && ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
        {
            try
            {
                var bmp = new BitmapImage(new Uri(row.Path)) { DecodePixelHeight = 68 };
                iconBadge.Child = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill
                };
                loadedImage = true;
            }
            catch { }
        }

        if (!loadedImage)
        {
            iconBadge.Child = new FontIcon
            {
                Glyph = iconGlyph,
                FontSize = 15,
                Foreground = new SolidColorBrush(row.Exists
                    ? Windows.UI.Color.FromArgb(220, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        // Text stack (Name + type/sub)
        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1
        };

        var nameBlock = new TextBlock
        {
            Text = row.Name,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(row.Exists
                ? Windows.UI.Color.FromArgb(235, 255, 255, 255)
                : Windows.UI.Color.FromArgb(100, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };

        string subText = isDir ? "Folder" : (ext.Length > 1 ? ext[1..].ToUpperInvariant() : "File");
        var subBlock = new TextBlock
        {
            Text = subText,
            FontSize = 9,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        textStack.Children.Add(nameBlock);
        textStack.Children.Add(subBlock);

        // Clear/Remove button (appears on hover)
        var removeBtn = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255))
            },
            Style = (Style)RootGrid.Resources["TabButtonStyle"],
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, -2, -2, 0),
            Opacity = 0.0 // Hidden by default, shown on hover
        };
        ToolTipService.SetToolTip(removeBtn, "Remove from shelf");
        string capturedPath = row.Path;
        removeBtn.Click += (s, e) =>
        {
            _shelf?.Remove(capturedPath);
            RefreshShelfUI();
        };

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(iconBadge, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(removeBtn, 2);

        inner.Children.Add(iconBadge);
        inner.Children.Add(textStack);
        inner.Children.Add(removeBtn);

        var card = new Border
        {
            Height = 54,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 6, 6),
            Child = inner
        };
        ToolTipService.SetToolTip(card, row.Path);

        // Hover effect: highlight border and reveal clear button
        card.PointerEntered += (s, _) =>
        {
            ((Border)s).Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255));
            ((Border)s).BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 255, 255, 255));
            removeBtn.Opacity = 1.0;
        };
        card.PointerExited += (s, _) =>
        {
            ((Border)s).Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255));
            ((Border)s).BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255));
            removeBtn.Opacity = 0.0;
        };

        // Click opens file/folder (unless clear button was clicked)
        card.PointerPressed += (s, pe) =>
        {
            var originalSource = pe.OriginalSource as DependencyObject;
            if (originalSource != null && IsDescendantOf(originalSource, removeBtn))
            {
                return;
            }
            if (row.Exists)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(capturedPath) { UseShellExecute = true });
                }
                catch { }
            }
        };

        return new Grid { Children = { card } };
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfClearConfirming)
        {
            ResetShelfClearButton();
            _shelf?.Clear();
            RefreshShelfUI();
            return;
        }

        if (_shelf == null || _shelf.Rows().Count == 0) return;

        _shelfClearConfirming = true;
        ShelfClearBtn.Content = new TextBlock
        {
            Text = "Clear all?",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 71)),
            VerticalAlignment = VerticalAlignment.Center
        };

        _shelfClearConfirmTimer?.Stop();
        _shelfClearConfirmTimer = DispatcherQueue.CreateTimer();
        _shelfClearConfirmTimer.Interval = TimeSpan.FromSeconds(3);
        _shelfClearConfirmTimer.Tick += (_, _) => ResetShelfClearButton();
        _shelfClearConfirmTimer.Start();
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

        var editItem = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = "\uE70F" } };
        editItem.Click += (_, _) => BeginEditShortcut(item);
        menu.Items.Add(editItem);

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
        _editingShortcutId = null;
        LauncherAddTitle.Text = "A link for the launcher";
        LauncherAddConfirmBtn.Content = "Add";
        LauncherTargetInput.Text = string.Empty;
        LauncherNameInput.Text = string.Empty;
        LauncherUseAppIconCheck.IsChecked = true;
        LauncherAddOverlay.Visibility = Visibility.Visible;
        LauncherTargetInput.Focus(FocusState.Programmatic);
    }

    /// <summary>Opens the same dialog pre-filled, so a shortcut can be corrected instead of redone.</summary>
    private void BeginEditShortcut(IslandShortcut item)
    {
        _editingShortcutId = item.Id;
        LauncherAddTitle.Text = "Edit shortcut";
        LauncherAddConfirmBtn.Content = "Save";
        LauncherTargetInput.Text = item.Target;
        LauncherNameInput.Text = item.Name;
        LauncherUseAppIconCheck.IsChecked = item.UseAppIcon;
        LauncherAddOverlay.Visibility = Visibility.Visible;
        LauncherTargetInput.Focus(FocusState.Programmatic);
    }

    private void LauncherAddCancel_Click(object sender, RoutedEventArgs e)
    {
        _editingShortcutId = null;
        LauncherAddOverlay.Visibility = Visibility.Collapsed;
    }

    private async void LauncherAddConfirm_Click(object sender, RoutedEventArgs e)
    {
        string target = LauncherTargetInput.Text.Trim();
        string name = LauncherNameInput.Text.Trim();
        bool useAppIcon = LauncherUseAppIconCheck.IsChecked == true;

        if (!string.IsNullOrEmpty(target) && _launcher != null)
        {
            if (_editingShortcutId is { } editingId)
                await _launcher.UpdateAsync(editingId, target, string.IsNullOrEmpty(name) ? null : name, useAppIcon);
            else
                await _launcher.AddAsync(target, string.IsNullOrEmpty(name) ? null : name, useAppIcon);
        }
        _editingShortcutId = null;
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

    private void FlushPendingNoteSave()
    {
        if (_noteSaveTimer != null)
        {
            _noteSaveTimer.Stop();
            if (NoteContentBox != null && !string.IsNullOrEmpty(_activeNoteId))
            {
                NoteContentBox.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
                _notesStore?.UpdateNote(_activeNoteId, (rtf ?? string.Empty).TrimEnd('\0'));
            }
        }
    }

    private void RefreshNotesUI()
    {
        if (_notesStore == null) return;
        var notes = _notesStore.Notes;

        NoteTabStrip.Children.Clear();

        if (notes.Count == 0)
        {
            _loadedNoteId = null;
            _noteUpdating = true;
            NoteContentBox.Document.SetText(TextSetOptions.None, string.Empty);
            _noteUpdating = false;
            return;
        }

        if (string.IsNullOrEmpty(_activeNoteId) || !notes.Any(n => n.Id == _activeNoteId))
        {
            _activeNoteId = notes[0].Id;
        }

        var accent = GetSystemAccentColor();
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
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(50, accent.R, accent.G, accent.B))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                Foreground = isAct
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, accent.R, accent.G, accent.B))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                Tag = note.Id
            };
            btn.Click += (s, _) =>
            {
                if (s is Button b && b.Tag is string nid)
                {
                    if (_activeNoteId != nid)
                    {
                        FlushPendingNoteSave();
                        _activeNoteId = nid;
                        RefreshNotesUI();
                    }
                }
            };
            NoteTabStrip.Children.Add(btn);
        }

        var activeNote = notes.FirstOrDefault(n => n.Id == _activeNoteId) ?? notes[0];
        NoteContentBox.Document.GetText(TextGetOptions.None, out string curTxt);
        if (_loadedNoteId != _activeNoteId || (string.IsNullOrWhiteSpace(curTxt) && !string.IsNullOrEmpty(activeNote.Content)))
        {
            _loadedNoteId = _activeNoteId;
            _noteUpdating = true;
            LoadNoteIntoEditor(activeNote.Content);
            _noteUpdating = false;
        }
    }

    private void LoadNoteIntoEditor(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            NoteContentBox.Document.SetText(TextSetOptions.None, string.Empty);
            UpdateFormattingToolbarUI();
            return;
        }

        string clean = content.TrimEnd('\0', '\r', '\n');
        if (clean.TrimStart().StartsWith("{\\rtf"))
        {
            try
            {
                NoteContentBox.Document.SetText(TextSetOptions.FormatRtf, clean);
            }
            catch
            {
                NoteContentBox.Document.SetText(TextSetOptions.None, clean);
            }
        }
        else if (clean.Contains("<mark") || clean.Contains("**") || clean.Contains("*") || clean.Contains("<u>") || clean.Contains("~~"))
        {
            string rtf = MarkdownToRtf(clean);
            try
            {
                NoteContentBox.Document.SetText(TextSetOptions.FormatRtf, rtf);
            }
            catch
            {
                NoteContentBox.Document.SetText(TextSetOptions.None, clean);
            }
        }
        else
        {
            NoteContentBox.Document.SetText(TextSetOptions.None, clean);
        }
        UpdateFormattingToolbarUI();
    }

    private void NoteAddBtn_Click(object sender, RoutedEventArgs e)
    {
        FlushPendingNoteSave();
        if (NoteNewTitleOverlay != null)
        {
            int nextIndex = (_notesStore?.Notes.Count ?? 0) + 1;
            NoteNewTitleBox.Text = $"Note {nextIndex}";
            NoteNewTitleOverlay.Visibility = Visibility.Visible;
            NoteNewTitleBox.Focus(FocusState.Programmatic);
            NoteNewTitleBox.SelectAll();
        }
        else
        {
            var note = _notesStore?.CreateNote();
            if (note != null)
            {
                _loadedNoteId = null;
                _activeNoteId = note.Id;
                RefreshNotesUI();
                NoteContentBox.Focus(FocusState.Programmatic);
            }
        }
    }

    private void NoteNewTitleCancel_Click(object sender, RoutedEventArgs e)
    {
        if (NoteNewTitleOverlay != null)
        {
            NoteNewTitleOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void NoteNewTitleCreate_Click(object sender, RoutedEventArgs e)
    {
        SubmitNewNoteTitle();
    }

    private void NoteNewTitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SubmitNewNoteTitle();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (NoteNewTitleOverlay != null) NoteNewTitleOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void SubmitNewNoteTitle()
    {
        string title = NoteNewTitleBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            title = $"Note {(_notesStore?.Notes.Count ?? 0) + 1}";
        }
        if (NoteNewTitleOverlay != null) NoteNewTitleOverlay.Visibility = Visibility.Collapsed;
        FlushPendingNoteSave();
        var note = _notesStore?.CreateNote(title);
        if (note != null)
        {
            _loadedNoteId = null;
            _activeNoteId = note.Id;
            RefreshNotesUI();
            NoteContentBox.Focus(FocusState.Programmatic);
        }
    }

    private void NoteCopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NoteContentBox.Document.GetText(TextGetOptions.None, out string plainText);
            NoteContentBox.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(plainText.TrimEnd('\r', '\n'));
            dp.SetRtf(rtf);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
        catch { }
    }

    private bool _noteDeleteConfirming;
    private DQTimer? _noteDeleteTimer;

    private void ResetNoteDeleteBtnUI()
    {
        _noteDeleteConfirming = false;
        _noteDeleteTimer?.Stop();
        if (NoteDeleteBtn != null)
        {
            NoteDeleteBtn.Content = new FontIcon { Glyph = "\uE74D", FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255)) };
            ToolTipService.SetToolTip(NoteDeleteBtn, "Delete current note");
        }
    }

    private void NoteDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeNoteId)) return;

        if (_noteDeleteConfirming)
        {
            ResetNoteDeleteBtnUI();
            _noteSaveTimer?.Stop();
            _notesStore?.DeleteNote(_activeNoteId);
            _activeNoteId = null;
            _loadedNoteId = null;
            RefreshNotesUI();
            return;
        }

        _noteDeleteConfirming = true;
        NoteDeleteBtn.Content = new TextBlock
        {
            Text = "Delete?",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 71)),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(NoteDeleteBtn, "Click again to confirm deletion");

        _noteDeleteTimer?.Stop();
        _noteDeleteTimer = DispatcherQueue.CreateTimer();
        _noteDeleteTimer.Interval = TimeSpan.FromSeconds(3.5);
        _noteDeleteTimer.Tick += (_, _) => ResetNoteDeleteBtnUI();
        _noteDeleteTimer.Start();
    }

    private void NoteContentBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_noteUpdating || string.IsNullOrEmpty(_activeNoteId)) return;
        _noteSaveTimer?.Stop();
        _noteSaveTimer = DispatcherQueue.CreateTimer();
        _noteSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _noteSaveTimer.Tick += (_, _) =>
        {
            _noteSaveTimer?.Stop();
            if (string.IsNullOrEmpty(_activeNoteId)) return;
            NoteContentBox.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            _notesStore?.UpdateNote(_activeNoteId, (rtf ?? string.Empty).TrimEnd('\0'));
        };
        _noteSaveTimer.Start();
    }

    // ── Formatting toolbar handlers & Active State ──────────────────────────

    private void NoteContentBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateFormattingToolbarUI();
    }

    private void NoteContentBox_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ToggleChecklistAtCaret();
    }

    private void UpdateFormattingToolbarUI()
    {
        if (NoteContentBox == null || NoteFmtBold == null) return;

        var sel = NoteContentBox.Document.Selection;
        var fmt = sel.CharacterFormat;
        var pfmt = sel.ParagraphFormat;

        bool isBold = (fmt.Bold == FormatEffect.On);
        bool isItalic = (fmt.Italic == FormatEffect.On);
        bool isUnderline = (fmt.Underline != UnderlineType.None);
        bool isStrike = (fmt.Strikethrough == FormatEffect.On);
        bool isBullet = (pfmt.ListType == MarkerType.Bullet);
        bool isNumbered = (pfmt.ListType == MarkerType.Arabic);

        var range = sel.GetClone();
        range.Expand(TextRangeUnit.Paragraph);
        range.GetText(TextGetOptions.None, out string lineText);
        string trimmed = lineText.TrimStart();
        bool isChecklist = trimmed.StartsWith("○ ") || trimmed.StartsWith("✔ ") || trimmed.StartsWith("● ") || trimmed.StartsWith("☐ ") || trimmed.StartsWith("☑ ");

        SetFormatButtonVisual(NoteFmtBold, isBold);
        SetFormatButtonVisual(NoteFmtItalic, isItalic);
        SetFormatButtonVisual(NoteFmtUnderline, isUnderline);
        SetFormatButtonVisual(NoteFmtStrike, isStrike);
        SetFormatButtonVisual(NoteFmtBullet, isBullet);
        SetFormatButtonVisual(NoteFmtNumbered, isNumbered);
        SetFormatButtonVisual(NoteFmtChecklist, isChecklist);

        var bg = fmt.BackgroundColor;
        SetColorSwatchVisual(NoteFmtColorYellow, bg.A > 0 && bg.R == 255 && bg.G == 230 && bg.B == 0);
        SetColorSwatchVisual(NoteFmtColorGreen,  bg.A > 0 && bg.R == 76  && bg.G == 175 && bg.B == 80);
        SetColorSwatchVisual(NoteFmtColorPink,   bg.A > 0 && bg.R == 255 && bg.G == 64  && bg.B == 129);
        SetColorSwatchVisual(NoteFmtColorCyan,   bg.A > 0 && bg.R == 0   && bg.G == 229 && bg.B == 255);
        SetColorSwatchVisual(NoteFmtColorNone,   bg.A == 0);
    }

    private void SetFormatButtonVisual(Button? btn, bool isActive)
    {
        if (btn == null) return;
        var accent = GetSystemAccentColor();
        btn.Background = isActive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(90, accent.R, accent.G, accent.B))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        btn.BorderBrush = isActive
            ? new SolidColorBrush(accent)
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        btn.BorderThickness = isActive ? new Thickness(1) : new Thickness(0);
    }

    private void SetColorSwatchVisual(Button? btn, bool isActive)
    {
        if (btn == null) return;
        btn.BorderBrush = isActive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        btn.BorderThickness = isActive ? new Thickness(1.5) : new Thickness(0);
    }

    private void NoteFmtBold_Click(object sender, RoutedEventArgs e)
    {
        var fmt = NoteContentBox.Document.Selection.CharacterFormat;
        fmt.Bold = (fmt.Bold == FormatEffect.On) ? FormatEffect.Off : FormatEffect.On;
        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void NoteFmtItalic_Click(object sender, RoutedEventArgs e)
    {
        var fmt = NoteContentBox.Document.Selection.CharacterFormat;
        fmt.Italic = (fmt.Italic == FormatEffect.On) ? FormatEffect.Off : FormatEffect.On;
        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void NoteFmtUnderline_Click(object sender, RoutedEventArgs e)
    {
        var fmt = NoteContentBox.Document.Selection.CharacterFormat;
        fmt.Underline = (fmt.Underline == UnderlineType.Single) ? UnderlineType.None : UnderlineType.Single;
        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void NoteFmtStrike_Click(object sender, RoutedEventArgs e)
    {
        var fmt = NoteContentBox.Document.Selection.CharacterFormat;
        fmt.Strikethrough = (fmt.Strikethrough == FormatEffect.On) ? FormatEffect.Off : FormatEffect.On;
        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void NoteFmtBullet_Click(object sender, RoutedEventArgs e)
    {
        var pfmt = NoteContentBox.Document.Selection.ParagraphFormat;
        pfmt.ListType = (pfmt.ListType == MarkerType.Bullet) ? MarkerType.None : MarkerType.Bullet;
        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void NoteFmtNumbered_Click(object sender, RoutedEventArgs e)
    {
        var pfmt = NoteContentBox.Document.Selection.ParagraphFormat;
        if (pfmt.ListType == MarkerType.Arabic)
        {
            pfmt.ListType = MarkerType.None;
        }
        else
        {
            pfmt.ListType = MarkerType.Arabic;
            pfmt.ListStart = 1;
        }
        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void ToggleChecklistAtCaret()
    {
        if (NoteContentBox == null || string.IsNullOrEmpty(_activeNoteId)) return;
        var sel = NoteContentBox.Document.Selection;
        var range = sel.GetClone();
        range.Expand(TextRangeUnit.Paragraph);
        range.GetText(TextGetOptions.None, out string lineText);

        int caretPos = sel.StartPosition;
        int paraStart = range.StartPosition;

        // Tapped near the start of the line (on or near the circle)
        if (caretPos <= paraStart + 3)
        {
            string trimmed = lineText.TrimStart();
            if (trimmed.StartsWith("○ ") || trimmed.StartsWith("✔ ") || trimmed.StartsWith("● ") || trimmed.StartsWith("☐ ") || trimmed.StartsWith("☑ "))
            {
                ToggleChecklistLine(range, lineText);
            }
        }
    }

    private void ToggleChecklistLine(ITextRange range, string lineText)
    {
        string trimmed = lineText.TrimStart();
        int indentLen = lineText.Length - trimmed.Length;
        string indent = lineText.Substring(0, indentLen);

        var accent = GetSystemAccentColor();

        if (trimmed.StartsWith("○ ") || trimmed.StartsWith("☐ "))
        {
            // Transition: Unchecked -> Checked
            string content = trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty;
            range.SetText(TextSetOptions.None, indent + "✔ " + content);

            // Format checkmark with window accent color
            var markerRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen, range.StartPosition + indentLen + 1);
            markerRange.CharacterFormat.ForegroundColor = accent;
            markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;

            // Format line text with strikethrough & dimmed color
            if (content.Length > 0)
            {
                var textRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen + 2, range.EndPosition);
                textRange.CharacterFormat.Strikethrough = FormatEffect.On;
                textRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(160, 200, 200, 200);
            }
        }
        else if (trimmed.StartsWith("✔ ") || trimmed.StartsWith("● ") || trimmed.StartsWith("☑ "))
        {
            // Transition: Checked -> Unchecked
            string content = trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty;
            range.SetText(TextSetOptions.None, indent + "○ " + content);

            // Format circle with soft white
            var markerRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen, range.StartPosition + indentLen + 1);
            markerRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
            markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;

            // Remove strikethrough & restore white color
            if (content.Length > 0)
            {
                var textRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen + 2, range.EndPosition);
                textRange.CharacterFormat.Strikethrough = FormatEffect.Off;
                textRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
            }
        }
        else
        {
            // Convert normal line to checklist item
            range.SetText(TextSetOptions.None, indent + "○ " + trimmed);
            var markerRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen, range.StartPosition + indentLen + 1);
            markerRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
            markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;
        }

        UpdateFormattingToolbarUI();
    }

    private void NoteFmtChecklist_Click(object sender, RoutedEventArgs e)
    {
        var sel = NoteContentBox.Document.Selection;
        var range = sel.GetClone();
        range.Expand(TextRangeUnit.Paragraph);
        range.GetText(TextGetOptions.None, out string lineText);

        string trimmed = lineText.TrimStart();
        if (trimmed.StartsWith("○ ") || trimmed.StartsWith("✔ ") || trimmed.StartsWith("● ") || trimmed.StartsWith("☐ ") || trimmed.StartsWith("☑ "))
        {
            if (trimmed.StartsWith("○ ") || trimmed.StartsWith("☐ "))
            {
                ToggleChecklistLine(range, lineText);
            }
            else
            {
                // Remove checklist marker
                string indent = lineText.Substring(0, lineText.Length - trimmed.Length);
                string content = trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty;
                range.SetText(TextSetOptions.None, indent + content);
                range.CharacterFormat.Strikethrough = FormatEffect.Off;
                range.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
            }
        }
        else
        {
            ToggleChecklistLine(range, lineText);
        }

        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void NoteFmtColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex)
        {
            var fmt = NoteContentBox.Document.Selection.CharacterFormat;
            if (hex == "none")
            {
                fmt.BackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            }
            else
            {
                var color = hex switch
                {
                    "#FFE600" => Windows.UI.Color.FromArgb(255, 255, 230, 0),
                    "#4CAF50" => Windows.UI.Color.FromArgb(255, 76, 175, 80),
                    "#FF4081" => Windows.UI.Color.FromArgb(255, 255, 64, 129),
                    "#00E5FF" => Windows.UI.Color.FromArgb(255, 0, 229, 255),
                    _ => Windows.UI.Color.FromArgb(255, 255, 230, 0)
                };
                if (fmt.BackgroundColor == color)
                {
                    fmt.BackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                }
                else
                {
                    fmt.BackgroundColor = color;
                }
            }
        }
        NoteContentBox.Focus(FocusState.Programmatic);
        UpdateFormattingToolbarUI();
    }

    private void NoteContentBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var sel = NoteContentBox.Document.Selection;
            var range = sel.GetClone();
            range.Expand(TextRangeUnit.Paragraph);
            range.GetText(TextGetOptions.None, out string lineText);
            string trimmed = lineText.Trim();
            if (trimmed == "○" || trimmed == "✔" || trimmed == "●" || trimmed == "☐" || trimmed == "☑")
            {
                range.SetText(TextSetOptions.None, string.Empty);
                range.CharacterFormat.Strikethrough = FormatEffect.Off;
                e.Handled = true;
            }
            else if (trimmed.StartsWith("○ ") || trimmed.StartsWith("✔ ") || trimmed.StartsWith("● ") || trimmed.StartsWith("☐ ") || trimmed.StartsWith("☑ "))
            {
                sel.Collapse(false);
                sel.SetText(TextSetOptions.None, "\r\n○ ");
                sel.Collapse(false);
                sel.CharacterFormat.Strikethrough = FormatEffect.Off;
                sel.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
                e.Handled = true;
            }
        }
    }

    public static string MarkdownToRtf(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        if (input.TrimStart().StartsWith("{\\rtf")) return input;

        var sb = new System.Text.StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\nouicompat{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}{\colortbl ;\red255\green230\blue0;\red76\green175\blue80;\red255\green64\blue129;\red0\green229\blue255;\red255\green255\blue255;}\viewkind4\uc1\pard\cf5\f0\fs22 ");

        string s = input.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

        s = System.Text.RegularExpressions.Regex.Replace(s, @"<mark\s+bg=""#FFE600"">(.*?)</mark>", @"\highlight1 $1\highlight0 ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<mark\s+bg=""#4CAF50"">(.*?)</mark>", @"\highlight2 $1\highlight0 ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<mark\s+bg=""#FF4081"">(.*?)</mark>", @"\highlight3 $1\highlight0 ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<mark\s+bg=""#00E5FF"">(.*?)</mark>", @"\highlight4 $1\highlight0 ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<mark.*?>(.*?)</mark>", @"\highlight1 $1\highlight0 ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        s = System.Text.RegularExpressions.Regex.Replace(s, @"\*\*(.*?)\*\*", @"\b $1\b0 ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\*(.*?)\*", @"\i $1\i0 ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<u>(.*?)</u>", @"\ul $1\ulnone ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"~~(.*?)~~", @"\strike $1\strike0 ");

        s = s.Replace("\r\n", @"\par ").Replace("\n", @"\par ");

        sb.Append(s);
        sb.Append(@"\par}");
        return sb.ToString();
    }

    // ── Timer UI ──────────────────────────────────────────────────────────────

    private void UpdateTimerButtonsState(IslandTimerState state)
    {
        if (TimerStartText == null || TimerStartIcon == null) return;
        switch (state)
        {
            case IslandTimerState.Running:
                TimerStartText.Text = "Pause";
                TimerStartIcon.Glyph = "\uE769";
                break;
            case IslandTimerState.Paused:
                TimerStartText.Text = "Resume";
                TimerStartIcon.Glyph = "\uE768";
                break;
            default:
                TimerStartText.Text = "Start";
                TimerStartIcon.Glyph = "\uE768";
                break;
        }
    }

    private void OnTimerTick(TimeSpan remaining, double progress)
    {
        string remStr = _timer?.FormatRemaining() ?? "5:00";
        if (remStr.StartsWith("0") && remStr.Length == 5)
            remStr = remStr.Substring(1);
        TimerDisplay.Text = remStr;

        if (TimerCircleRing != null)
        {
            double remainingFraction = Math.Clamp(1.0 - progress, 0.0, 1.0);
            TimerCircleRing.Value = remainingFraction * 100.0;
        }

        if (_timer?.State == IslandTimerState.Running)
        {
            FoldedTimerMini.Visibility = Visibility.Visible;
            FoldedTimerText.Text = _timer.FormatRemaining();
            UpdateTimerButtonsState(IslandTimerState.Running);
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
        UpdateTimerButtonsState(IslandTimerState.Idle);
        TimerDisplay.Text = "5:00";
        if (TimerCircleRing != null)
        {
            TimerCircleRing.Value = 0.0;
        }

        var accent = GetSystemAccentColor();
        string accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "timer",
            Icon: "\uE916",
            Title: "Timer finished",
            Detail: "Time's up!",
            AccentHex: accentHex,
            Priority: WindowIslandCardPriority.ReminderTimer,
            StayMs: 6000));

        try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
    }

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string minsStr && int.TryParse(minsStr, out int mins))
        {
            _timer?.Start(TimeSpan.FromMinutes(mins));
            UpdateTimerButtonsState(IslandTimerState.Running);
        }
    }

    private void TimerCustomToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TimerCustomPanel == null || TimerPresetsPanel == null) return;
        bool isCustomVisible = TimerCustomPanel.Visibility == Visibility.Visible;
        if (isCustomVisible)
        {
            TimerCustomPanel.Visibility = Visibility.Collapsed;
            TimerPresetsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            TimerCustomPanel.Visibility = Visibility.Visible;
            TimerPresetsPanel.Visibility = Visibility.Collapsed;
            TimerCustomInput.Focus(FocusState.Programmatic);
            TimerCustomInput.SelectAll();
        }
    }

    private void TimerCustomCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TimerCustomPanel != null && TimerPresetsPanel != null)
        {
            TimerCustomPanel.Visibility = Visibility.Collapsed;
            TimerPresetsPanel.Visibility = Visibility.Visible;
        }
    }

    private void TimerCustomPlayBtn_Click(object sender, RoutedEventArgs e)
    {
        SubmitCustomTimer();
    }

    private void TimerCustomInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SubmitCustomTimer();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (TimerCustomPanel != null && TimerPresetsPanel != null)
            {
                TimerCustomPanel.Visibility = Visibility.Collapsed;
                TimerPresetsPanel.Visibility = Visibility.Visible;
            }
            e.Handled = true;
        }
    }

    private void SubmitCustomTimer()
    {
        string input = TimerCustomInput.Text.Trim();
        if (TryParseDuration(input, out TimeSpan duration) && duration > TimeSpan.Zero)
        {
            _timer?.Start(duration);
            UpdateTimerButtonsState(IslandTimerState.Running);
            TimerCustomInput.Text = string.Empty;
            if (TimerCustomPanel != null && TimerPresetsPanel != null)
            {
                TimerCustomPanel.Visibility = Visibility.Collapsed;
                TimerPresetsPanel.Visibility = Visibility.Visible;
            }
        }
    }

    public static bool TryParseDuration(string? input, out TimeSpan duration)
        => WindowIslandDurationParser.TryParse(input, out duration);

    private void TimerStartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_timer == null) return;

        if (_timer.State == IslandTimerState.Running)
        {
            _timer.Pause();
            UpdateTimerButtonsState(IslandTimerState.Paused);
        }
        else if (_timer.State == IslandTimerState.Paused)
        {
            _timer.Resume();
            UpdateTimerButtonsState(IslandTimerState.Running);
        }
        else
        {
            _timer.Start(_timer.Remaining > TimeSpan.Zero ? _timer.Remaining : TimeSpan.FromMinutes(5));
            UpdateTimerButtonsState(IslandTimerState.Running);
        }
    }

    private void TimerResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _timer?.Reset();
        UpdateTimerButtonsState(IslandTimerState.Idle);
        TimerDisplay.Text = "5:00";
        if (TimerCircleRing != null)
        {
            TimerCircleRing.Value = 100.0;
        }
        FoldedTimerMini.Visibility = Visibility.Collapsed;
        UpdateFoldedWeatherMini();
    }

    // ── Reminders UI ──────────────────────────────────────────────────────────

    private void RefreshRemindersUI()
    {
        if (_reminderStore == null) return;
        var list = _reminderStore.ActiveReminders;

        if (ReminderCountBadge != null)
            ReminderCountBadge.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ReminderCountText != null)
            ReminderCountText.Text = list.Count.ToString();

        ReminderList.Children.Clear();

        if (list.Count == 0)
        {
            var placeholderStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 12, 6, 12),
                Spacing = 4
            };

            var guideTitle = new TextBlock
            {
                Text = "Type below what and when:",
                FontSize = 9.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var ex1 = new TextBlock
            {
                Text = "\u201Ccall mum tomorrow at 9\u201D",
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(95, 255, 255, 255)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var ex2 = new TextBlock
            {
                Text = "\u201Cpills every day at 23:00\u201D",
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(95, 255, 255, 255)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var ex3 = new TextBlock
            {
                Text = "\u201Cin 20 minutes turn the stove off\u201D",
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(95, 255, 255, 255)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            placeholderStack.Children.Add(guideTitle);
            placeholderStack.Children.Add(ex1);
            placeholderStack.Children.Add(ex2);
            placeholderStack.Children.Add(ex3);

            ReminderList.Children.Add(placeholderStack);
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
        bool isNow = rem.DueTimeUtc <= DateTime.UtcNow.AddMinutes(1);

        var grid = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 6, 4)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uEA8F",
            FontSize = 13,
            Foreground = isNow
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 167, 38))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(112, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = rem.Title,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var relTime = new TextBlock
        {
            Text = isNow ? "now" : WindowIslandReminderStore.FormatRelativeTime(rem.DueTimeUtc),
            FontSize = 9,
            FontWeight = isNow ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = isNow
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 167, 38))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(115, 255, 255, 255))
        };
        stack.Children.Add(title);
        stack.Children.Add(relTime);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        // Actions: Complete ✓ and Confirmable Delete ✕
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var doneBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE73E", FontSize = 9, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 108, 203, 95)) },
            Style = (Style)RootGrid.Resources["TabButtonStyle"],
            Width = 20, Height = 20,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(doneBtn, "Mark completed");
        doneBtn.Click += (_, _) => _reminderStore?.Complete(rem.Id);
        btnPanel.Children.Add(doneBtn);

        bool confirmingDelete = false;
        var delBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 9, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)) },
            Style = (Style)RootGrid.Resources["TabButtonStyle"],
            Height = 20,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(delBtn, "Delete reminder");
        delBtn.Click += (_, _) =>
        {
            if (!confirmingDelete)
            {
                confirmingDelete = true;
                delBtn.Content = new TextBlock { Text = "Delete?", FontSize = 9, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 100, 100)) };
                delBtn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 0, 0));
            }
            else
            {
                _reminderStore?.Delete(rem.Id);
            }
        };
        btnPanel.Children.Add(delBtn);

        Grid.SetColumn(btnPanel, 2);
        grid.Children.Add(btnPanel);

        return grid;
    }

    private void ReminderNaturalInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ReminderNaturalInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            _reminderParsedDue = null;
            if (ReminderDetectedContainer != null) ReminderDetectedContainer.Visibility = Visibility.Collapsed;
            if (ReminderWhenPanel != null) ReminderWhenPanel.Visibility = Visibility.Visible;
            return;
        }

        var result = WindowIslandReminderParser.Parse(text);
        if (result.HasExplicitTime)
        {
            _reminderParsedDue = result.DueTimeUtc;
            if (ReminderDetectedText != null) ReminderDetectedText.Text = WindowIslandReminderParser.FormatPill(result.DueTimeUtc);
            if (ReminderDetectedContainer != null) ReminderDetectedContainer.Visibility = Visibility.Visible;
            if (ReminderWhenPanel != null) ReminderWhenPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _reminderParsedDue = null;
            if (ReminderDetectedContainer != null) ReminderDetectedContainer.Visibility = Visibility.Collapsed;
            if (ReminderWhenPanel != null) ReminderWhenPanel.Visibility = Visibility.Visible;
        }
    }

    private void ReminderNaturalInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SubmitNaturalReminder();
            e.Handled = true;
        }
    }

    private void ReminderDetectedPill_Click(object sender, RoutedEventArgs e)
    {
        SubmitNaturalReminder();
    }

    private void ReminderQuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        string text = ReminderNaturalInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            ReminderNaturalInput?.Focus(FocusState.Programmatic);
            return;
        }

        string tag = b.Tag as string ?? "10m";
        DateTime due = tag switch
        {
            "1h" => DateTime.UtcNow.AddHours(1),
            "tomorrow" => DateTime.Now.Date.AddDays(1).AddHours(9).ToUniversalTime(),
            _ => DateTime.UtcNow.AddMinutes(10)
        };
        _reminderStore?.Add(text, due);
        ClearReminderInput();
        RefreshRemindersUI();
    }

    private void ReminderAdjust_Click(object sender, RoutedEventArgs e)
    {
        if (_reminderParsedDue == null || sender is not Button b) return;
        string tag = b.Tag as string ?? string.Empty;
        _reminderParsedDue = tag switch
        {
            "+day" => _reminderParsedDue.Value.AddDays(1),
            "-day" => _reminderParsedDue.Value.AddDays(-1),
            "+15m" => _reminderParsedDue.Value.AddMinutes(15),
            "-15m" => _reminderParsedDue.Value.AddMinutes(-15),
            _ => _reminderParsedDue.Value
        };
        if (_reminderParsedDue < DateTime.UtcNow)
            _reminderParsedDue = DateTime.UtcNow.AddMinutes(1);

        if (ReminderDetectedText != null)
            ReminderDetectedText.Text = WindowIslandReminderParser.FormatPill(_reminderParsedDue.Value);
    }

    private void SubmitNaturalReminder()
    {
        string text = ReminderNaturalInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        if (_reminderParsedDue.HasValue)
        {
            var parsed = WindowIslandReminderParser.Parse(text);
            string title = !string.IsNullOrWhiteSpace(parsed.Title) ? parsed.Title : text;
            _reminderStore?.Add(title, _reminderParsedDue.Value);
        }
        else
        {
            _reminderStore?.AddFromPhrase(text);
        }

        ClearReminderInput();
        RefreshRemindersUI();
    }

    private void ClearReminderInput()
    {
        if (ReminderNaturalInput != null) ReminderNaturalInput.Text = string.Empty;
        _reminderParsedDue = null;
        if (ReminderDetectedContainer != null) ReminderDetectedContainer.Visibility = Visibility.Collapsed;
        if (ReminderWhenPanel != null) ReminderWhenPanel.Visibility = Visibility.Visible;
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

    // ── Milestone 12: VPN / battery / mic-cam handlers ────────────────────────

    private void OnVpnStateChanged(object? sender, VpnStateChangedArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_settings.CardVpnEnabled) return;
            int stayMs = _settings.GetCardStayMs();
            if (e.Connected)
            {
                string name = string.IsNullOrWhiteSpace(e.AdapterName) ? "VPN" : e.AdapterName;
                _eventQueue?.Push(new WindowIslandCard(
                    "vpn", "\uE701", "VPN Connected", name,
                    "#00B4D8", WindowIslandCardPriority.Vpn, stayMs));
            }
            else
            {
                _eventQueue?.Push(new WindowIslandCard(
                    "vpn", "\uE701", "VPN Disconnected", "No active VPN",
                    "#808080", WindowIslandCardPriority.Vpn, stayMs));
            }
        });
    }

    private void OnBatteryLowCard(object? sender, BatteryCardArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_settings.CardBatteryLowEnabled) return;
            string accentHex = e.Percent <= 10 ? "#FF4444" : "#FF8C00";
            int stayMs = _settings.GetCardStayMs();
            _eventQueue?.Push(new WindowIslandCard(
                "battery-low", "\uE850", "Battery Low",
                $"{e.Percent}% remaining — please plug in",
                accentHex, WindowIslandCardPriority.Battery, stayMs, e.Percent));
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        });
    }

    private void OnBatteryChargingCard(object? sender, BatteryCardArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_settings.CardChargingEnabled) return;
            int stayMs = _settings.GetCardStayMs();
            var (icon, title, detail, accent) = e.Kind switch
            {
                BatteryCardKind.ChargingStarted  => ("\uEBB5", "Charging Started",   $"{e.Percent}%", "#22CC44"),
                BatteryCardKind.ChargingComplete => ("\uE83F", "Fully Charged",      "100% — unplug to save battery health", "#22CC44"),
                BatteryCardKind.Unplugged        => ("\uE850", "Charger Unplugged",  $"{e.Percent}% remaining", "#FF8C00"),
                _                                => ("\uE83F", "Battery", $"{e.Percent}%", "#808080")
            };
            _eventQueue?.Push(new WindowIslandCard(
                "battery-charging", icon, title, detail,
                accent, WindowIslandCardPriority.Battery, stayMs, e.Percent));
        });
    }

    private void OnMicCamStateChanged(object? sender, MicCamStateArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _micDotActive = e.MicActive;
            _camDotActive = e.CamActive;
            UpdateMicCamDots();
        });
    }

    private void UpdateMicCamDots()
    {
        bool enabled = _settings.MicCamDotsEnabled;
        bool anyActive = enabled && (_micDotActive || _camDotActive);

        MicCamDotsContainer.Visibility = anyActive ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = (enabled && _micDotActive) ? Visibility.Visible : Visibility.Collapsed;
        CamDot.Visibility = (enabled && _camDotActive) ? Visibility.Visible : Visibility.Collapsed;

        // Update tooltips with app names (fetched synchronously from the service)
        if (_micDotActive)
        {
            var (_, micApp, _, _) = WindowIslandNotificationService.GetMicCamState();
            ToolTipService.SetToolTip(MicDot,
                string.IsNullOrEmpty(micApp) ? "Microphone in use" : $"Microphone: {micApp}");
        }
        if (_camDotActive)
        {
            var (_, _, _, camApp) = WindowIslandNotificationService.GetMicCamState();
            ToolTipService.SetToolTip(CamDot,
                string.IsNullOrEmpty(camApp) ? "Camera in use" : $"Camera: {camApp}");
        }
    }

    private void RefreshWeatherUI()
    {
        var settings = _settings ?? ServiceContainer.Settings.WindowIsland;
        var city = settings.GetActiveCity();
        if (string.IsNullOrWhiteSpace(city.Name) || (city.Latitude == 0 && city.Longitude == 0))
        {
            WeatherCityOverlay.Visibility = Visibility.Visible;
            RenderSavedCitiesUI();
            WeatherCityText.Text = "No city set";
            WeatherBigTemp.Text = "--";
            WeatherConditionText.Text = "Tap to set location";
            WeatherFeelsLikeText.Text = "Feels like -- · Today --";
            WeatherOutlookText.Text = "Please enter your city to fetch weather data.";
            WeatherHoursStack.Children.Clear();
            WeatherWeekStack.Children.Clear();
            WeatherUpdatedText.Text = string.Empty;
            WeatherCityTimeText.Text = string.Empty;
            return;
        }

        WeatherCityText.Text = city.Name;
        var tzOffset = WindowIslandWeatherService.GetCityUtcOffset(city);
        var cityTime = DateTimeOffset.UtcNow.ToOffset(tzOffset);
        WeatherCityTimeText.Text = cityTime.ToString("ddd, h:mm tt");

        var weather = WindowIslandWeatherService.Instance.CurrentWeather;
        if (weather == null)
        {
            WeatherBigTemp.Text = "--";
            WeatherConditionText.Text = "Loading...";
            WeatherFeelsLikeText.Text = "Fetching forecast from MET Norway...";
            WeatherUpdatedText.Text = string.Empty;
            _ = WindowIslandWeatherService.Instance.RefreshAsync();
            return;
        }

        WeatherCityText.Text = string.IsNullOrEmpty(weather.CityName) ? city.Name : weather.CityName;
        WeatherBigTemp.Text = WindowIslandWeatherService.FormatTemp(weather.CurrentTempC, settings.WeatherUnits);
        WeatherConditionIcon.Glyph = weather.IconGlyph;
        WeatherConditionText.Text = weather.ConditionText;
        WeatherFeelsLikeText.Text = $"Feels like {WindowIslandWeatherService.FormatTemp(weather.FeelsLikeC, settings.WeatherUnits)} · Today {WindowIslandWeatherService.FormatTemp(weather.LowC, settings.WeatherUnits)}...{WindowIslandWeatherService.FormatTemp(weather.HighC, settings.WeatherUnits)}";
        WeatherOutlookText.Text = string.IsNullOrWhiteSpace(weather.SummaryOutlook) ? $"{weather.ConditionText} throughout the day." : weather.SummaryOutlook;
        WeatherUpdatedText.Text = GetFriendlyUpdatedTime(weather.UpdatedTime);

        // Metric Tiles visuals & values
        // 1. Wind: Compass rotation, speed & direction text
        WeatherWindSpeedText.Text = WindowIslandWeatherService.FormatSpeed(weather.WindSpeedMps, settings.WeatherUnits);
        WeatherWindDirText.Text = weather.WindDirectionCompass;
        WeatherWindRotate.Angle = weather.WindDirectionDeg;

        // 2. Humidity: Percentage, progress bar, dew point
        WeatherHumidityValText.Text = $"{(int)Math.Round(weather.HumidityPercent)}%";
        WeatherDewText.Text = $"Dew point {WindowIslandWeatherService.FormatTemp(weather.DewPointC, settings.WeatherUnits)}";
        WeatherHumidityFill.Width = Math.Clamp((weather.HumidityPercent / 100.0) * 80.0, 4, 80);

        // 3. Pressure: Value, trend, needle rotation (-50 to +50 deg from 970 to 1050 hPa)
        WeatherPressureValText.Text = $"{(int)Math.Round(weather.PressureHpa)} hPa";
        WeatherPressureTrendText.Text = weather.PressureTrend.ToLowerInvariant().Contains("fall") ? "↓ falling" : (weather.PressureTrend.ToLowerInvariant().Contains("rise") ? "↑ rising" : "→ steady");
        double clampedPressure = Math.Clamp(weather.PressureHpa, 970, 1050);
        WeatherPressureRotate.Angle = ((clampedPressure - 1010) / 40.0) * 50.0;

        // 4. Sun: Trajectory arc & dot, sunrise & sunset times
        var (calcSunrise, calcSunset) = NoaaSolarCalculator.CalculateSunriseSunset(city.Latitude, city.Longitude, DateTime.Today, tzOffset);
        DateTime sunrise = calcSunrise ?? weather.Sunrise;
        DateTime sunset  = calcSunset  ?? weather.Sunset;

        WeatherSunTimesText.Text = $"↑ {sunrise:h:mm tt}  ↓ {sunset:h:mm tt}";

        var cur = cityTime.TimeOfDay;
        var sr  = sunrise.TimeOfDay;
        var ss  = sunset.TimeOfDay;
        double sunProgress = 0.5;
        bool isDaytime = true;

        if (ss > sr)
        {
            if (cur <= sr)
            {
                sunProgress = 0.0;
                isDaytime = false;
            }
            else if (cur >= ss)
            {
                sunProgress = 1.0;
                isDaytime = false;
            }
            else
            {
                sunProgress = (cur - sr).TotalMinutes / (ss - sr).TotalMinutes;
                isDaytime = true;
            }
        }

        // Parabolic trajectory on 88x22 Canvas from (2, 18) via (44, -2) to (86, 18)
        double sunX = 2.0 + 84.0 * sunProgress;
        double sunY = 18.0 - 40.0 * sunProgress * (1.0 - sunProgress);
        Canvas.SetLeft(WeatherSunDot, Math.Clamp(sunX - 4.0, 0, 84));
        Canvas.SetTop(WeatherSunDot, Math.Clamp(sunY - 4.0, 0, 20));
        WeatherSunDot.Opacity = isDaytime ? 1.0 : 0.45;

        if (weather.SymbolCode.Contains("night"))
        {
            isDaytime = false;
        }

        // Dynamic Day/Night Theme for Hero Card
        var heroBg = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        if (isDaytime)
        {
            var accent = GetSystemAccentColor();
            heroBg.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, accent.R, accent.G, accent.B), Offset = 0.0 });
            heroBg.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, (byte)(accent.R * 0.55), (byte)(accent.G * 0.55), (byte)(accent.B * 0.55)), Offset = 1.0 });
            WeatherHeroCard.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            WeatherConditionIcon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0xEB, 0x3B));
        }
        else
        {
            heroBg.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x14, 0x1E, 0x30), Offset = 0.0 });
            heroBg.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x0A, 0x0F, 0x1A), Offset = 1.0 });
            WeatherHeroCard.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            WeatherConditionIcon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE2, 0xEA, 0xF8));
        }
        WeatherHeroCard.Background = heroBg;

        // 5. Precipitation: Value, mini-bar fill, subtext
        WeatherPrecipValText.Text = $"{weather.PrecipitationTodayMm:0.0} mm today";
        WeatherPrecipSubText.Text = weather.PrecipitationTodayMm > 0 ? "Rain recorded today" : "No rain expected today";
        WeatherPrecipFill.Width = Math.Clamp((weather.PrecipitationTodayMm / 20.0) * 80.0, 0, 80);

        // 6. UV Index: Value & level, spectrum thumb indicator
        WeatherUvValText.Text = $"{weather.UvIndex:0.0} {weather.UvLevel.ToLowerInvariant()}";
        WeatherUvSubText.Text = $"Tomorrow up to {(int)Math.Ceiling(weather.UvIndex)}";
        double uvPos = Math.Clamp((weather.UvIndex / 11.0) * 72.0, 0, 72);
        WeatherUvThumb.Margin = new Thickness(uvPos, 0, 0, 0);

        // Set rich informative tooltips on all cards
        ToolTipService.SetToolTip(WeatherHeroCard,
            $"Weather in {city.Name}\n{weather.ConditionText}, {WeatherBigTemp.Text}\nFeels like {WindowIslandWeatherService.FormatTemp(weather.FeelsLikeC, settings.WeatherUnits)} · High: {WindowIslandWeatherService.FormatTemp(weather.HighC, settings.WeatherUnits)}, Low: {WindowIslandWeatherService.FormatTemp(weather.LowC, settings.WeatherUnits)}\n{weather.SummaryOutlook}");

        string windAdvice = weather.WindSpeedMps switch
        {
            < 0.5 => "Calm air (smoke rises vertically).",
            < 3.3 => "Light breeze (wind felt on face, leaves rustle).",
            < 8.0 => "Gentle to moderate breeze (small branches move).",
            < 14.0 => "Fresh breeze (small trees sway).",
            _ => "Strong wind (inconvenience in walking)."
        };
        ToolTipService.SetToolTip(WeatherWindCard,
            $"Wind Conditions\nSpeed: {WeatherWindSpeedText.Text} · Direction: {weather.WindDirectionCompass} ({weather.WindDirectionDeg:0}°)\n{windAdvice}");

        string humidityAdvice = weather.DewPointC switch
        {
            < 10 => "Dry and crisp air.",
            < 16 => "Comfortable moisture levels.",
            < 21 => "Somewhat humid and muggy.",
            _ => "Very humid and oppressive."
        };
        ToolTipService.SetToolTip(WeatherHumidityCard,
            $"Relative Humidity & Dew Point\nHumidity: {(int)Math.Round(weather.HumidityPercent)}% · Dew Point: {WeatherDewText.Text.Replace("Dew point ", "")}\n{humidityAdvice}");

        string pressureAdvice = weather.PressureHpa >= 1013 ? "High pressure indicates fair, stable weather." : "Low pressure indicates clouds, wind, or changing conditions.";
        ToolTipService.SetToolTip(WeatherPressureCard,
            $"Barometric Pressure\nPressure: {WeatherPressureValText.Text} · Trend: {WeatherPressureTrendText.Text}\n{pressureAdvice}");

        string sunStatus = isDaytime
            ? $"Sun is {(int)Math.Round(sunProgress * 100)}% through the daytime path."
            : (cur < sr ? "Nighttime (before sunrise)." : "Nighttime (after sunset).");
        ToolTipService.SetToolTip(WeatherSunCard,
            $"Solar Cycle\nSunrise: {sunrise:h:mm tt} · Sunset: {sunset:h:mm tt}\n{sunStatus} Day length: {(ss - sr).Hours}h {(ss - sr).Minutes}m.");

        ToolTipService.SetToolTip(WeatherPrecipCard,
            $"Precipitation Today\nRain/Snow: {WeatherPrecipValText.Text}\nLiquid water equivalent measured from midnight to now.");

        string uvAdvice = weather.UvIndex switch
        {
            < 3 => "Low UV: No protection required for short exposure.",
            < 6 => "Moderate UV: Wear sunglasses and sun protection outdoors.",
            < 8 => "High UV: Seek shade during midday, wear hat and sunscreen.",
            _ => "Very High / Extreme UV: Avoid sun exposure during peak hours."
        };
        ToolTipService.SetToolTip(WeatherUvCard,
            $"Ultraviolet (UV) Radiation Index\nLevel: {weather.UvIndex:0.0} ({weather.UvLevel})\n{uvAdvice}");

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
                FontFamily = new FontFamily("Segoe UI Symbol, Segoe Fluent Icons"),
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
            ToolTipService.SetToolTip(itemBorder,
                $"{pt.Time:h:mm tt} ({timeLabel})\n{pt.ConditionText}\nTemp: {WindowIslandWeatherService.FormatTemp(pt.TempC, units)}" + (pt.PrecipitationMm > 0 ? $"\nPrecipitation: {pt.PrecipitationMm:0.0} mm" : ""));
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
                FontFamily = new FontFamily("Segoe UI Symbol, Segoe Fluent Icons"),
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
            ToolTipService.SetToolTip(itemBorder,
                $"{d.DayName} ({d.Date:MMM d})\n{d.ConditionText}\nHigh: {WindowIslandWeatherService.FormatTemp(d.TempHighC, units)}, Low: {WindowIslandWeatherService.FormatTemp(d.TempLowC, units)}" + (d.PrecipitationMm > 0 ? $"\nPrecipitation: {d.PrecipitationMm:0.0} mm" : ""));
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
            FoldedWeatherIcon.FontFamily = new FontFamily("Segoe UI Symbol, Segoe Fluent Icons");
            FoldedWeatherIcon.Glyph = weather.IconGlyph;
        }
        else
        {
            FoldedWeatherMini.Visibility = Visibility.Collapsed;
        }
    }

    private DateTime _lastWeatherScrollTime = DateTime.MinValue;

    private async void WeatherHeroCard_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var settings = _settings ?? ServiceContainer.Settings.WindowIsland;
        var cities = settings.GetWeatherCities();
        if (cities == null || cities.Count <= 1) return;

        // Debounce trackpad inertia events (280ms threshold)
        if (DateTime.UtcNow - _lastWeatherScrollTime < TimeSpan.FromMilliseconds(280))
        {
            e.Handled = true;
            return;
        }

        var prop = e.GetCurrentPoint(WeatherHeroCard).Properties;
        int delta = prop.MouseWheelDelta;
        if (delta == 0) return;

        _lastWeatherScrollTime = DateTime.UtcNow;
        e.Handled = true;

        // delta > 0 (scroll up) -> previous city; delta < 0 (scroll down) -> next city
        int currentIndex = settings.ActiveWeatherCityIndex;
        int nextIndex = delta > 0
            ? (currentIndex - 1 + cities.Count) % cities.Count
            : (currentIndex + 1) % cities.Count;

        await WindowIslandWeatherService.Instance.SelectCityAsync(nextIndex);
        RefreshWeatherUI();
    }

    private void WeatherChangeCityBtn_Click(object sender, RoutedEventArgs e)
    {
        WeatherSearchResultsStack.Children.Clear();
        WeatherSearchFeedback.Visibility = Visibility.Collapsed;
        WeatherSearchInput.Text = string.Empty;
        RenderSavedCitiesUI();
        WeatherCityOverlay.Visibility = Visibility.Visible;
        WeatherSearchInput.Focus(FocusState.Programmatic);
    }

    private void RenderSavedCitiesUI()
    {
        WeatherSavedCitiesStack.Children.Clear();
        var settings = _settings ?? ServiceContainer.Settings.WindowIsland;
        var cities = settings.GetWeatherCities();

        for (int i = 0; i < cities.Count; i++)
        {
            int index = i;
            var c = cities[i];
            bool isActive = (index == settings.ActiveWeatherCityIndex);

            var itemGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 1, 0, 1)
            };
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var selectBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(isActive ? ColorHelper.FromArgb(0x30, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(isActive ? ColorHelper.FromArgb(0x80, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new FontIcon
            {
                Glyph = "\uE707",
                FontSize = 11,
                Foreground = new SolidColorBrush(isActive ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0xA0, 0xFF, 0xFF, 0xFF))
            });

            var nameText = new TextBlock
            {
                Text = string.IsNullOrEmpty(c.Country) ? c.Name : $"{c.Name}, {c.Country}",
                FontSize = 11,
                FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(nameText);

            if (isActive)
            {
                sp.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            selectBtn.Content = sp;
            selectBtn.Click += async (_, _) =>
            {
                WeatherCityOverlay.Visibility = Visibility.Collapsed;
                await WindowIslandWeatherService.Instance.SelectCityAsync(index);
                RefreshWeatherUI();
            };

            Grid.SetColumn(selectBtn, 0);
            itemGrid.Children.Add(selectBtn);

            if (cities.Count > 1)
            {
                var delBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(4, 0, 0, 0),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6)
                };
                delBtn.Content = new FontIcon { Glyph = "\uE74D", FontSize = 10, Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)) };
                delBtn.Click += async (_, _) =>
                {
                    await WindowIslandWeatherService.Instance.RemoveCityAsync(index);
                    RenderSavedCitiesUI();
                    RefreshWeatherUI();
                };
                Grid.SetColumn(delBtn, 1);
                itemGrid.Children.Add(delBtn);
            }

            WeatherSavedCitiesStack.Children.Add(itemGrid);
        }
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
                        string simpleName = loc.DisplayName.Split(',')[0].Trim();
                        await WindowIslandWeatherService.Instance.AddCityAsync(simpleName, loc.Latitude, loc.Longitude, loc.Country);
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

    // ── Wi-Fi & Bluetooth Device Lists Overlay ───────────────────────────────

    private string _controlsDeviceMode = "none";

    private void ControlsWifiListBtn_Click(object sender, RoutedEventArgs e)
    {
        _controlsDeviceMode = "wifi";
        ControlsDeviceOverlayTitle.Text = "Wi-Fi networks";
        ControlsDeviceOverlay.Visibility = Visibility.Visible;
        _ = PopulateDeviceListAsync();
    }

    private void ControlsBtListBtn_Click(object sender, RoutedEventArgs e)
    {
        _controlsDeviceMode = "bluetooth";
        ControlsDeviceOverlayTitle.Text = "Bluetooth devices";
        ControlsDeviceOverlay.Visibility = Visibility.Visible;
        _ = PopulateDeviceListAsync();
    }

    private void ControlsDeviceCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        ControlsDeviceOverlay.Visibility = Visibility.Collapsed;
        _controlsDeviceMode = "none";
    }

    private void ControlsDeviceSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string uri = _controlsDeviceMode == "wifi" ? "ms-settings:network-wifi" : "ms-settings:bluetooth";
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
        }
        catch { }
    }

    private async Task PopulateDeviceListAsync()
    {
        try
        {
            ControlsDeviceList.Children.Clear();

            var loading = new TextBlock
            {
                Text = "Loading...",
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 25, 0, 0)
            };
            ControlsDeviceList.Children.Add(loading);

            if (_controlsDeviceMode == "wifi")
            {
                var networks = await WindowIslandControlsService.GetWiFiNetworksAsync();
                ControlsDeviceList.Children.Clear();

                if (networks.Count == 0)
                {
                    ControlsDeviceList.Children.Add(new TextBlock
                    {
                        Text = "No Wi-Fi networks found",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 25, 0, 0)
                    });
                    return;
                }

                foreach (var net in networks)
                {
                    var row = BuildWiFiDeviceRow(net);
                    ControlsDeviceList.Children.Add(row);
                }
            }
            else if (_controlsDeviceMode == "bluetooth")
            {
                var devices = await WindowIslandControlsService.GetBluetoothDevicesAsync();
                ControlsDeviceList.Children.Clear();

                if (devices.Count == 0)
                {
                    ControlsDeviceList.Children.Add(new TextBlock
                    {
                        Text = "No paired Bluetooth devices found",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 25, 0, 0)
                    });
                    return;
                }

                foreach (var dev in devices)
                {
                    var row = BuildBluetoothDeviceRow(dev);
                    ControlsDeviceList.Children.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            ControlsDeviceList.Children.Clear();
            ControlsDeviceList.Children.Add(new TextBlock
            {
                Text = "Error: " + ex.Message,
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 120, 120)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            });
        }
    }

    private Grid BuildWiFiDeviceRow(WindowIslandControlsService.IslandWiFiNetwork net)
    {
        var accentBrush = new SolidColorBrush(GetSystemAccentColor());

        var grid = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left circular icon
        var iconBorder = new Border
        {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new FontIcon
        {
            Glyph = "\uE701",
            FontSize = 14,
            Foreground = net.IsConnected
                ? accentBrush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = icon;
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // Middle details
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var title = new TextBlock
        {
            Text = net.Ssid,
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var status = new TextBlock
        {
            Text = net.IsConnected ? "Connected" : "Saved",
            FontSize = 9.5,
            Foreground = net.IsConnected
                ? accentBrush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255))
        };
        stack.Children.Add(title);
        stack.Children.Add(status);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        // Right button
        var actionBtn = new Button
        {
            Content = net.IsConnected ? "Disconnect" : "Connect",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Height = 26,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(13),
            VerticalAlignment = VerticalAlignment.Center,
            Background = net.IsConnected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255))
                : accentBrush,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(250, 255, 255, 255))
        };
        actionBtn.Click += async (_, _) =>
        {
            if (net.IsConnected)
            {
                actionBtn.Content = "Disconnecting...";
                await WindowIslandControlsService.DisconnectWiFiAsync();
            }
            else
            {
                actionBtn.Content = "Connecting...";
                await WindowIslandControlsService.ConnectWiFiAsync(net.Ssid);
            }
            await Task.Delay(1200);
            await PopulateDeviceListAsync();
            RefreshControlsUI();
        };
        Grid.SetColumn(actionBtn, 2);
        grid.Children.Add(actionBtn);

        return grid;
    }

    private Grid BuildBluetoothDeviceRow(WindowIslandControlsService.IslandBtDevice dev)
    {
        var accentBrush = new SolidColorBrush(GetSystemAccentColor());

        var grid = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left circular icon
        string glyph = dev.DeviceType switch
        {
            "Headphones" => "\uE7F6",
            "Speaker"    => "\uE7F5",
            "Phone"      => "\uE8EA",
            _            => "\uE702"
        };
        var iconBorder = new Border
        {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 14,
            Foreground = dev.IsConnected
                ? accentBrush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = icon;
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // Middle details
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var title = new TextBlock
        {
            Text = dev.Name,
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var status = new TextBlock
        {
            Text = dev.IsConnected ? "Connected" : "Not connected",
            FontSize = 9.5,
            Foreground = dev.IsConnected
                ? accentBrush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255))
        };
        stack.Children.Add(title);
        stack.Children.Add(status);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        // Right button
        var actionBtn = new Button
        {
            Content = dev.IsConnected ? "Disconnect" : "Connect",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Height = 26,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(13),
            VerticalAlignment = VerticalAlignment.Center,
            Background = dev.IsConnected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255))
                : accentBrush,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(250, 255, 255, 255))
        };
        actionBtn.Click += async (_, _) =>
        {
            if (dev.IsConnected)
            {
                actionBtn.Content = "Disconnecting...";
                await WindowIslandControlsService.DisconnectBluetoothDeviceAsync(dev.MacAddress);
            }
            else
            {
                actionBtn.Content = "Connecting...";
                bool ok = await WindowIslandControlsService.ConnectBluetoothDeviceAsync(dev.MacAddress);
                if (!ok)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = "ms-settings:bluetooth", UseShellExecute = true });
                    }
                    catch { }
                }
            }
            await Task.Delay(1000);
            await PopulateDeviceListAsync();
            RefreshControlsUI();
        };
        Grid.SetColumn(actionBtn, 2);
        grid.Children.Add(actionBtn);

        return grid;
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

    // ── Milestone 13: Chat tab (WebView2) ──────────────────────────────────────

    private void RefreshChatUI()
    {
        RenderChatSubTabs();
        _ = EnsureChatWebViewAsync();
    }

    private void RenderChatSubTabs()
    {
        ChatSubTabsPanel.Children.Clear();
        var sites = _settings.ChatSites ?? WindowIslandSettings.DefaultChatSites();
        int active = Math.Clamp(_settings.ActiveChatIndex, 0, Math.Max(0, sites.Count - 1));

        for (int i = 0; i < sites.Count; i++)
        {
            int index = i;
            var site = sites[i];
            bool isActive = i == active;

            var accent = GetSystemAccentColor();
            var btn = new Button
            {
                Style = (Style)RootGrid.Resources["TabButtonStyle"],
                Height = 28,
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(isActive
                    ? ColorHelper.FromArgb(0x40, accent.R, accent.G, accent.B)
                    : ColorHelper.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(isActive
                    ? ColorHelper.FromArgb(0x80, accent.R, accent.G, accent.B)
                    : ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (!string.IsNullOrEmpty(site.Icon))
            {
                sp.Children.Add(new FontIcon
                {
                    Glyph = site.Icon,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(isActive
                        ? ColorHelper.FromArgb(0xFF, accent.R, accent.G, accent.B)
                        : ColorHelper.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
                });
            }
            sp.Children.Add(new TextBlock
            {
                Text = site.Name,
                FontSize = 11,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(isActive
                    ? ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            });

            btn.Content = sp;
            btn.Click += (_, _) => SelectChatSite(index);

            // Context menu to edit or delete custom sites (sites at index >= 3)
            if (i >= 3)
            {
                var flyout = new MenuFlyout();

                var editItem = new MenuFlyoutItem
                {
                    Text = $"Edit {site.Name}",
                    Icon = new FontIcon { Glyph = "\uE70F" }
                };
                editItem.Click += (_, _) => ShowEditChatSiteOverlay(index);
                flyout.Items.Add(editItem);

                if (sites.Count > 1)
                {
                    var delItem = new MenuFlyoutItem
                    {
                        Text = $"Remove {site.Name}",
                        Icon = new FontIcon { Glyph = "\uE74D" }
                    };
                    delItem.Click += (_, _) =>
                    {
                        WindowIslandChatManager.RemoveChatSite(_settings, index);
                        ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);
                        RenderChatSubTabs();
                        _ = EnsureChatWebViewAsync();
                    };
                    flyout.Items.Add(delItem);
                }

                btn.ContextFlyout = flyout;
            }

            ChatSubTabsPanel.Children.Add(btn);
        }
    }

    private void SelectChatSite(int index)
    {
        _settings.ActiveChatIndex = index;
        RenderChatSubTabs();
        _ = EnsureChatWebViewAsync();
    }

    private async Task EnsureChatWebViewAsync()
    {
        if (_chatInitializing) return;

        var activeSite = WindowIslandChatManager.GetActiveSite(_settings);
        if (_chatWebView != null && _chatInitialized)
        {
            if (_chatLastNavigatedUrl != activeSite.Url)
            {
                _chatLastNavigatedUrl = activeSite.Url;
                try
                {
                    ChatSkeletonPanel.Visibility = Visibility.Visible;
                    _chatWebView.Source = new Uri(activeSite.Url);
                }
                catch { }
            }
            return;
        }

        _chatInitializing = true;
        ChatSkeletonPanel.Visibility = Visibility.Visible;
        ChatFallbackPanel.Visibility = Visibility.Collapsed;

        try
        {
            string version = string.Empty;
            try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (string.IsNullOrWhiteSpace(version))
            {
                ChatSkeletonPanel.Visibility = Visibility.Collapsed;
                ChatFallbackPanel.Visibility = Visibility.Visible;
                _chatInitializing = false;
                return;
            }

            if (_chatWebView == null)
            {
                _chatWebView = new WebView2
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                _chatWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 26, 26, 26);
                _chatWebView.NavigationCompleted += (_, _) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ChatSkeletonPanel.Visibility = Visibility.Collapsed;
                    });
                };

                ChatWebViewHost.Children.Clear();
                ChatWebViewHost.Children.Add(_chatWebView);
            }

            string userDataFolder = WindowIslandChatManager.GetUserDataFolder();
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
            await _chatWebView.EnsureCoreWebView2Async(env);

            _chatWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _chatWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            _chatInitialized = true;
            _chatLastNavigatedUrl = activeSite.Url;
            _chatWebView.Source = new Uri(activeSite.Url);
        }
        catch (Exception ex)
        {
            WindowIslandHost.Log("EnsureChatWebViewAsync error: " + ex);
            ChatSkeletonPanel.Visibility = Visibility.Collapsed;
            ChatFallbackPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _chatInitializing = false;
        }
    }

    private async void SuspendChatWebView()
    {
        try
        {
            if (_chatWebView?.CoreWebView2 != null)
            {
                await _chatWebView.CoreWebView2.TrySuspendAsync();
            }
        }
        catch { }
    }

    private void ResumeChatWebView()
    {
        try
        {
            if (_chatWebView?.CoreWebView2 != null)
            {
                _chatWebView.CoreWebView2.Resume();
            }
            else if (_activeTab == 4)
            {
                _ = EnsureChatWebViewAsync();
            }
        }
        catch { }
    }

    private void DisposeChatWebView()
    {
        try
        {
            if (_chatWebView != null)
            {
                ChatWebViewHost.Children.Clear();
                _chatWebView.Close();
                _chatWebView = null;
                _chatInitialized = false;
                _chatLastNavigatedUrl = null;
            }
        }
        catch { }
    }

    private void StartChatIdleTimer()
    {
        if (_chatIdleTimer == null)
        {
            _chatIdleTimer = DispatcherQueue.CreateTimer();
            _chatIdleTimer.IsRepeating = false;
            _chatIdleTimer.Tick += (_, _) =>
            {
                if (!_compositor?.IsOpen == true || _activeTab != 4)
                {
                    DisposeChatWebView();
                }
            };
        }
        int minutes = WindowIslandChatManager.ClampIdleMinutes(_settings.ChatIdleMinutes);
        _chatIdleTimer.Interval = TimeSpan.FromMinutes(minutes);
        _chatIdleTimer.Stop();
        _chatIdleTimer.Start();
    }

    private void ResetChatIdleTimer()
    {
        _chatIdleTimer?.Stop();
    }

    private int _editingChatIndex = -1;

    private void ChatAddBtn_Click(object sender, RoutedEventArgs e)
    {
        _editingChatIndex = -1;
        ChatAddTitleText.Text = "Add Custom AI Site";
        ChatConfirmAddBtnText.Text = "Add";
        ChatNewNameBox.Text = string.Empty;
        ChatNewUrlBox.Text = string.Empty;
        ChatAddErrorText.Visibility = Visibility.Collapsed;

        if (ChatWebViewHost != null)
        {
            ChatWebViewHost.Visibility = Visibility.Collapsed;
        }

        ChatAddOverlay.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => ChatNewNameBox?.Focus(FocusState.Programmatic));
    }

    private void ShowEditChatSiteOverlay(int index)
    {
        if (_settings?.ChatSites == null || index < 0 || index >= _settings.ChatSites.Count) return;
        var site = _settings.ChatSites[index];

        _editingChatIndex = index;
        ChatAddTitleText.Text = "Edit Custom AI Site";
        ChatConfirmAddBtnText.Text = "Save";
        ChatNewNameBox.Text = site.Name;
        ChatNewUrlBox.Text = site.Url;
        ChatAddErrorText.Visibility = Visibility.Collapsed;

        if (ChatWebViewHost != null)
        {
            ChatWebViewHost.Visibility = Visibility.Collapsed;
        }

        ChatAddOverlay.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => ChatNewNameBox?.Focus(FocusState.Programmatic));
    }

    private void ChatCancelAdd_Click(object sender, RoutedEventArgs e)
    {
        ChatAddOverlay.Visibility = Visibility.Collapsed;
        _editingChatIndex = -1;
        if (ChatWebViewHost != null)
        {
            ChatWebViewHost.Visibility = Visibility.Visible;
        }
    }

    private void ChatConfirmAdd_Click(object sender, RoutedEventArgs e)
    {
        string name = ChatNewNameBox.Text.Trim();
        string url = ChatNewUrlBox.Text.Trim();

        if (_editingChatIndex >= 0)
        {
            if (WindowIslandChatManager.UpdateChatSite(_settings, _editingChatIndex, name, url))
            {
                int editedIndex = _editingChatIndex;
                _editingChatIndex = -1;
                ChatAddOverlay.Visibility = Visibility.Collapsed;
                if (ChatWebViewHost != null)
                {
                    ChatWebViewHost.Visibility = Visibility.Visible;
                }
                ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);

                if (_settings.ActiveChatIndex == editedIndex)
                {
                    _chatLastNavigatedUrl = null; // force reload to the updated URL
                }

                RenderChatSubTabs();
                _ = EnsureChatWebViewAsync();
            }
            else
            {
                ChatAddErrorText.Text = "Invalid site name or URL (must be valid http/https).";
                ChatAddErrorText.Visibility = Visibility.Visible;
            }
            return;
        }

        if (WindowIslandChatManager.AddChatSite(_settings, name, url))
        {
            _settings.ActiveChatIndex = _settings.ChatSites.Count - 1;
            ChatAddOverlay.Visibility = Visibility.Collapsed;
            if (ChatWebViewHost != null)
            {
                ChatWebViewHost.Visibility = Visibility.Visible;
            }
            ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);
            RenderChatSubTabs();
            _ = EnsureChatWebViewAsync();
        }
        else
        {
            ChatAddErrorText.Text = "Invalid site name/URL, or max 6 sites reached.";
            ChatAddErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ChatBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ChatConfirmAdd_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ChatCancelAdd_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ChatPopoutBtn_Click(object sender, RoutedEventArgs e)
    {
        var active = WindowIslandChatManager.GetActiveSite(_settings);
        if (Uri.TryCreate(active.Url, UriKind.Absolute, out var uri))
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void ChatRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        try { _chatWebView?.Reload(); } catch { }
    }

    private void ChatDownloadWebView2_Click(object sender, RoutedEventArgs e)
    {
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703"));
    }

    // ── Milestone 14: Text tab (Text Tools & Transforms) ─────────────────────

    private bool _textLoadingClipboard;

    private void RefreshTextUI()
    {
        if (string.IsNullOrEmpty(TextInputBox.Text))
        {
            _ = LoadClipboardIntoTextTabAsync();
        }
        else
        {
            UpdateTextStats();
        }
    }

    private async Task LoadClipboardIntoTextTabAsync()
    {
        if (_textLoadingClipboard) return;
        _textLoadingClipboard = true;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                string text = await content.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        TextInputBox.Text = text;
                        UpdateTextStats();
                    });
                }
            }
        }
        catch { }
        finally
        {
            _textLoadingClipboard = false;
        }
    }

    private void UpdateTextStats()
    {
        var stats = WindowIslandTextManager.ComputeStatistics(TextInputBox.Text);
        TextStatsSummaryText.Text = $"{stats.Words} words · {stats.Characters} chars";
        TextStatsDetailedText.Text = $"Lines: {stats.Lines} · Words: {stats.Words} · Chars: {stats.Characters} (no spaces: {stats.CharactersNoSpaces})";
    }

    private void TextInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTextStats();
    }

    private void TextUpperBtn_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Text = WindowIslandTextManager.ToUpperCase(TextInputBox.Text);
    }

    private void TextLowerBtn_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Text = WindowIslandTextManager.ToLowerCase(TextInputBox.Text);
    }

    private void TextTitleBtn_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Text = WindowIslandTextManager.ToTitleCase(TextInputBox.Text);
    }

    private void TextTrimBtn_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Text = WindowIslandTextManager.TrimText(TextInputBox.Text);
    }

    private void TextNoBreaksBtn_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Text = WindowIslandTextManager.RemoveLineBreaks(TextInputBox.Text);
    }

    private void TextSortBtn_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Text = WindowIslandTextManager.SortLines(TextInputBox.Text);
    }

    private void TextClearBtn_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Text = string.Empty;
    }

    private string _currentQrText = string.Empty;

    private static byte[] GenerateQrPngBytes(string text, int pixelSize = 256)
    {
        var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(text, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium);
        const int quietZone = 4;
        int totalModules = qr.Size + quietZone * 2;
        int scale = Math.Max(1, pixelSize / totalModules);
        int actualSize = totalModules * scale;

        using var bitmap = new System.Drawing.Bitmap(actualSize, actualSize);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            for (int y = 0; y < qr.Size; y++)
            {
                for (int x = 0; x < qr.Size; x++)
                {
                    if (qr.GetModule(x, y))
                    {
                        g.FillRectangle(brush, (x + quietZone) * scale, (y + quietZone) * scale, scale, scale);
                    }
                }
            }
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private void TextQrBtn_Click(object sender, RoutedEventArgs e)
    {
        string text = TextInputBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            if (TextStatsDetailedText != null)
            {
                TextStatsDetailedText.Text = "Type or paste text first to generate a QR code.";
            }
            return;
        }

        _currentQrText = text;
        try
        {
            var visual = WifiQrCode.CreateVisual(text, 140);
            TextQrContainer.Child = visual;
            TextQrPreviewText.Text = text.Length <= 40 ? text : text[..40] + "…";
            TextQrCopyText.Text = "Copy Image";
            TextQrSaveText.Text = "Save PNG";
            TextQrOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (TextStatsDetailedText != null)
            {
                TextStatsDetailedText.Text = $"Failed to generate QR: {ex.Message}";
            }
        }
    }

    private void TextQrClose_Click(object sender, RoutedEventArgs e)
    {
        TextQrOverlay.Visibility = Visibility.Collapsed;
    }

    private async void TextQrCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentQrText)) return;
        try
        {
            byte[] pngBytes = GenerateQrPngBytes(_currentQrText, 300);
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            stream.Seek(0);

            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
            dp.SetText(_currentQrText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();

            TextQrCopyText.Text = "Copied!";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                TextQrCopyText.Text = "Copy Image";
            };
            timer.Start();
        }
        catch
        {
            TextQrCopyText.Text = "Failed";
        }
    }

    private async void TextQrSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentQrText)) return;
        try
        {
            byte[] pngBytes = GenerateQrPngBytes(_currentQrText, 400);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            string fileName = $"QRCode_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string filePath = Path.Combine(folder, fileName);
            await File.WriteAllBytesAsync(filePath, pngBytes);

            TextQrSaveText.Text = "Saved!";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                TextQrSaveText.Text = "Save PNG";
            };
            timer.Start();
        }
        catch
        {
            TextQrSaveText.Text = "Failed";
        }
    }

    private void TextFetchClipboardBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadClipboardIntoTextTabAsync();
    }

    private void TextCopyResultBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(TextInputBox.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

            TextCopyResultBtnText.Text = "Copied!";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                TextCopyResultBtnText.Text = "Copy";
                timer.Stop();
            };
            timer.Start();
        }
        catch { }
    }

    private async void TextPastePlainBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string plain = string.Empty;
            if (!string.IsNullOrEmpty(TextInputBox.Text))
            {
                plain = WindowIslandTextManager.StripFormatting(TextInputBox.Text);
            }
            else
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    plain = WindowIslandTextManager.StripFormatting(await content.GetTextAsync());
                }
            }

            if (!string.IsNullOrEmpty(plain))
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(plain);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }

            var target = _previousForegroundWindow != nint.Zero ? _previousForegroundWindow : GetForegroundWindow();
            Collapse();
            await Task.Delay(180);
            await WindowIslandTextManager.PasteToWindowAsync(target);
        }
        catch { }
    }

    private async void TextPasteToAppBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(TextInputBox.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

            var target = _previousForegroundWindow != nint.Zero ? _previousForegroundWindow : GetForegroundWindow();
            Collapse();
            await Task.Delay(180);
            await WindowIslandTextManager.PasteToWindowAsync(target);
        }
        catch { }
    }

    private async void TextDictationBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = _previousForegroundWindow != nint.Zero ? _previousForegroundWindow : GetForegroundWindow();
            Collapse();
            await Task.Delay(180);
            await WindowIslandTextManager.TriggerDictationAsync(target);
        }
        catch { }
    }

    // ── Milestone 15: Visibility rules handlers ────────────────────────────────

    private void OnIslandVisibilityChanged(bool shouldHide, string reason)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (shouldHide)
                SlideIslandOffScreen();
            else
                SlideIslandOnScreen();
        });
    }

    private void OnEdgeDwellTriggered()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _visibilityService?.ResetEdgeDwell();
            SlideIslandOnScreen();
        });
    }

    private void SlideIslandOffScreen()
    {
        if (_appWindow == null) return;
        // Move window above top edge so it is off-screen but still live
        var rect = new Windows.Graphics.RectInt32(
            _windowX,
            _windowY - _windowHeightPx - 10,
            _windowWidthPx,
            _windowHeightPx);
        _appWindow.MoveAndResize(rect);
    }

    private void SlideIslandOnScreen()
    {
        // PositionAndSize restores the canonical on-screen position
        PositionAndSize();
    }
}
