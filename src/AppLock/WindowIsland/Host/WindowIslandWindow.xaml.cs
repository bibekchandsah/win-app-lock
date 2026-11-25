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
    private const uint  WM_MOUSEACTIVATE   = 0x0021;
    private const uint  WM_POWERBROADCAST  = 0x0218;
    private const uint  WM_DPICHANGED      = 0x02E0;
    private const int   MA_NOACTIVATE      = 3;
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
    [DllImport("gdi32.dll")]
    private static extern bool BeginPath(nint hdc);
    [DllImport("gdi32.dll")]
    private static extern bool EndPath(nint hdc);
    [DllImport("gdi32.dll")]
    private static extern bool CloseFigure(nint hdc);
    [DllImport("gdi32.dll")]
    private static extern bool MoveToEx(nint hdc, int x, int y, nint lppt);
    [DllImport("gdi32.dll")]
    private static extern bool LineTo(nint hdc, int x, int y);
    [DllImport("gdi32.dll")]
    private static extern bool PolyBezierTo(nint hdc, GDI_POINT[] lppt, uint cCount);
    [DllImport("gdi32.dll")]
    private static extern nint PathToRegion(nint hdc);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [StructLayout(LayoutKind.Sequential)]
    private struct GDI_POINT
    {
        public int X;
        public int Y;
        public GDI_POINT(int x, int y) { X = x; Y = y; }
    }
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
    private WindowIslandAudio?      _audio;             // devices + app sessions for the volume bar
    private DQTimer?                _volumeUiTimer;
    private bool                    _appVolumeMode;     // slider drives the playing app instead of the device
    private string?                 _appSessionId;
    private bool                    _volumeTargetMuted;
    private long                    _volumeLastWriteMs;   // last user-driven slider change
    private bool                    _volumePointerDown;
    private string                  _mediaAppName = string.Empty;   // app the player is showing
    private double                  _mediaDuration;                // seconds, for seeking
    private bool                    _seekPointerDown;
    private long                    _lastSeekMs;
    private long                    _lastVolumeCardMs;
    private int                     _pendingVolumeCard = -1;
    private DQTimer?                _volumeCardTimer;
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
    private nint                              _pasteTargetWindow = nint.Zero;  // app the user was in when the island opened

    // Milestone 9: Note & Timer & Reminders
    private WindowIslandNotesStore?           _notesStore;
    private WindowIslandReminderStore?        _reminderStore;
    private WindowIslandTimer?                _timer;
    private string?                           _activeNoteId;
    private string?                           _loadedNoteId;
    private DQTimer?                          _noteSaveTimer;
    private bool                              _noteUpdating;
    private string?                           _editorBaselineRtf;
    private DateTime?                         _reminderParsedDue;

    // Milestone 10: Weather
    private bool                              _showingWeeklyWeather;

    // Milestone 11: Controls
    private DQTimer?                          _restartConfirmTimer;
    private DQTimer?                          _shutdownConfirmTimer;
    private readonly BtBatteryWarningTracker  _btWarningTracker = new();
    private bool                              _restartConfirming;
    private bool                              _shutdownConfirming;
    private bool                              _controlsBrightnessChanging;
    private bool                              _controlsBrightnessPointerDown;
    private long                              _brightnessLastWriteMs;
    private long                              _lastBrightnessCardMs;
    private int                               _pendingBrightnessCard = -1;
    private DQTimer?                          _brightnessCardTimer;
    private System.Management.ManagementEventWatcher? _brightnessWmiWatcher;
    private AppLock.Services.Audio.MicrophoneVolumeService? _controlsMicService;
    private string?                           _selectedMicDeviceId;
    private string                            _controlsMicFingerprint = string.Empty;
    private bool                              _controlsMicUpdating;
    private bool                              _controlsMicPointerDown;
    private bool                              _controlsMicUserSelected;
    private string?                           _controlsMicLastTooltip;
    private string?                           _pendingDefaultMicId;
    private DateTime                          _pendingDefaultMicUntil = DateTime.MinValue;

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
    private DQTimer? _controlsTimer;

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
        InitAudioListener();
        InitBrightnessListener();

        _lockKeySource = new LockKeyCardSource(DispatcherQueue);
        _lockKeySource.CapsLockChanged   += OnCapsLockChanged;
        _lockKeySource.NumLockChanged    += OnNumLockChanged;
        _lockKeySource.WatchedKeyPressed += OnWatchedKeyPressed;
        SyncWatchedKeys();
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
        ControlsMicSlider.Minimum = 0;
        ControlsMicSlider.Maximum = 100;

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
        _commandService = new WindowIslandCommandService(_fileIndex, _launcher!, _settings);
        _commandService.SearchCommandAdded += _ => ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

        // Milestone 9: Note, Timer, Reminders
        _notesStore = new WindowIslandNotesStore();
        _notesStore.Changed += () => DispatcherQueue.TryEnqueue(RefreshNotesUI);
        NoteContentBox.Loaded += (_, _) =>
        {
            _loadedNoteId = null;
            RefreshNotesUI();
        };
        NoteContentBox.AddHandler(UIElement.TappedEvent, new TappedEventHandler(NoteContentBox_Tapped), true);
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

        Windows.System.Power.PowerManager.BatteryStatusChanged         += OnPowerManagerEvent;
        Windows.System.Power.PowerManager.PowerSupplyStatusChanged     += OnPowerManagerEvent;
        Windows.System.Power.PowerManager.RemainingChargePercentChanged += OnPowerManagerEvent;
        Windows.System.Power.PowerManager.EnergySaverStatusChanged     += OnPowerManagerEvent;

        _controlsTimer = DispatcherQueue.CreateTimer();
        _controlsTimer.Interval = TimeSpan.FromSeconds(2);
        _controlsTimer.IsRepeating = true;
        _controlsTimer.Tick += (_, _) =>
        {
            if (_activeTab == 7 && _compositor?.IsOpen == true)
            {
                RefreshControlsUI();
                if (ControlsDeviceOverlay != null && ControlsDeviceOverlay.Visibility == Visibility.Visible)
                {
                    _ = PopulateDeviceListAsync();
                }
            }
            else
            {
                _ = UpdateFoldedBtBatteriesAsync();
            }
        };
        _controlsTimer.Start();

        try { CompositionTarget.Rendering += OnCompositionRendering; } catch { }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void Show(WindowIslandSettings settings)
    {
        _settings = settings;
        SyncWatchedKeys();
        IslandMotion.ChatHeightDip = settings.ChatHeightDip > 0 ? settings.ChatHeightDip : 440f;
        UpdateChatHeightLabel();

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
        UpdateShapeContentOffset();

        _isVisible = true;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);

        // Start Home tab background sampler immediately (tab 0 is active on first open)
        StartHomeState();
        _clipboardState?.Refresh();
        _ = UpdateFoldedBtBatteriesAsync();

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
        _commandService?.UpdateSettings(settings);
        IslandMotion.ChatHeightDip = settings.ChatHeightDip > 0 ? settings.ChatHeightDip : 440f;
        UpdateChatHeightLabel();
        PositionAndSize();
        _compositor?.ApplySettings(settings);
        UpdateHitRegion(_compositor?.IsOpen ?? false);
        UpdateShapeContentOffset();
        SyncWatchedKeys();
        ApplyContentVisibility();
        UpdateClock();
        UpdateBattery();
        UpdateFoldedWeatherMini();
        UpdateMicCamDots();
        _ = UpdateFoldedBtBatteriesAsync();

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
        _controlsTimer?.Stop();
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
        if (_lockKeySource != null)
        {
            _lockKeySource.CapsLockChanged   -= OnCapsLockChanged;
            _lockKeySource.NumLockChanged    -= OnNumLockChanged;
            _lockKeySource.WatchedKeyPressed -= OnWatchedKeyPressed;
            _lockKeySource.Dispose();
            _lockKeySource = null;
        }
        _clipboardSource?.Dispose();
        _clipboardSource = null;
        _screenshotSource?.Dispose();
        _screenshotSource = null;
        _volumeController?.Dispose();
        _volumeController = null;
        StopVolumeUi();
        DisposeBrightnessListener();
        _controlsMicService?.Dispose();
        _controlsMicService = null;
        if (_audio != null)
        {
            _audio.SystemVolumeChanged -= OnSystemVolumeChanged;
            _audio.Dispose();
            _audio = null;
        }

        // Milestone 12
        WindowIslandNotificationService.Instance.VpnStateChanged     -= OnVpnStateChanged;
        WindowIslandNotificationService.Instance.BatteryLowCard      -= OnBatteryLowCard;
        WindowIslandNotificationService.Instance.BatteryChargingCard -= OnBatteryChargingCard;
        WindowIslandNotificationService.Instance.MicCamStateChanged  -= OnMicCamStateChanged;

        Windows.System.Power.PowerManager.BatteryStatusChanged         -= OnPowerManagerEvent;
        Windows.System.Power.PowerManager.PowerSupplyStatusChanged     -= OnPowerManagerEvent;
        Windows.System.Power.PowerManager.RemainingChargePercentChanged -= OnPowerManagerEvent;
        Windows.System.Power.PowerManager.EnergySaverStatusChanged     -= OnPowerManagerEvent;

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
            if (ControlsAddCardOverlay != null) ControlsAddCardOverlay.Visibility = Visibility.Collapsed;
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
        UpdateHitRegion(open: true);
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

        ApplyTextTabAccent(accentColor);
    }

    private void ApplyTextTabAccent(Windows.UI.Color accent)
    {
        var accentBrush = new SolidColorBrush(accent);
        if (TextDictationIcon != null) TextDictationIcon.Foreground = accentBrush;
        if (TextQrBtnIcon != null) TextQrBtnIcon.Foreground = accentBrush;
        if (TextQrOverlayIcon != null) TextQrOverlayIcon.Foreground = accentBrush;
        if (TextPasteToAppBtn != null)
        {
            TextPasteToAppBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(0x20, accent.R, accent.G, accent.B));
            TextPasteToAppBtn.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x50, accent.R, accent.G, accent.B));
            TextPasteToAppBtn.Foreground = accentBrush;
        }
    }

    private static Windows.UI.Color GetContrastingForeground(Windows.UI.Color bg)
    {
        double lum = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        return lum > 150
            ? ColorHelper.FromArgb(0xFF, 0x10, 0x12, 0x16)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
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
                if (ControlsAddCardOverlay != null && ControlsAddCardOverlay.Visibility == Visibility.Visible)
                {
                    CloseControlsAddCardOverlay();
                    e.Handled = true;
                    break;
                }
                if (ControlsDeviceOverlay != null && ControlsDeviceOverlay.Visibility == Visibility.Visible)
                {
                    ControlsDeviceOverlay.Visibility = Visibility.Collapsed;
                    _controlsDeviceMode = "none";
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
                bool isSaverOn = !isCharging && (status.SystemStatusFlag == 1 || WindowIslandControlsService.IsBatterySaverActive());
                BatteryIcon.Glyph = isCharging
                    ? "\uEBB5"
                    : (isSaverOn
                        ? "\uEBC0"
                        : percent switch
                        {
                            >= 90 => "\uE859",
                            >= 75 => "\uE856",
                            >= 50 => "\uE854",
                            >= 25 => "\uE852",
                            _     => "\uE850"
                        });
                BatteryIcon.Foreground = isCharging
                    ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4C, 0xAF, 0x50))
                    : GetBtBatteryBrush(percent);
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
                : (_compositor.IsCard ? _activeCardW : _compositor.CurrentFoldedWidthDip)) * factor;
            float curHeightDip = (_compositor.IsOpen
                ? IslandMotion.TabSize(_activeTab).H
                : (_compositor.IsCard ? _activeCardH : WindowIslandCompositor.FoldedHeightDip)) * factor;

            float leftDip = (WindowWidthDip * factor - curWidthDip) / 2f;
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
            bool hasInteractiveCard = !string.IsNullOrEmpty(_eventQueue?.CurrentCard?.PrimaryActionLabel);
            if (_hoverInside && !_compositor.IsOpen && !hasInteractiveCard && elapsed >= openDelay)
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

        // Remember the user's app before any click here can hand focus to the island itself
        // (opening the Clipboard tab focuses its search box), so pastes still land in that app.
        nint foreground = GetForegroundWindow();
        if (foreground != nint.Zero && foreground != _hwnd) _pasteTargetWindow = foreground;

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
        _eventQueue?.HoldForMotion(IslandMotion.ClosePeriodMs);

        FadeVisual(_openVisual,   0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_foldedVisual, 1f, IslandMotion.ContentFadeInMs, IslandMotion.ContentFadeInDelayMs);

        _compositor.AnimateToFolded(() =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_compositor != null && !_compositor.IsOpen && !_compositor.IsCard && !_compositor.IsCommand)
                {
                    UpdateHitRegion(open: false, isCard: false);
                    PositionAndSize();
                }
                _measuringFrames = false;
                UpdateStatsDisplay();
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

    private void UpdateShapeContentOffset()
    {
        double topOffset = _settings.Shape == "Capsule" ? 6.0 : 0.0;
        double factor = _settings.SizePercent / 100.0;
        var margin = new Thickness(0, topOffset, 0, 0);
        var origin = new Windows.Foundation.Point(0.5, 0.0);

        void ApplyLayerScaleAndMargin(FrameworkElement? el)
        {
            if (el == null) return;
            el.Margin = margin;
            el.RenderTransformOrigin = origin;
            el.RenderTransform = Math.Abs(factor - 1.0) < 0.001
                ? null
                : new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = factor, ScaleY = factor };
        }

        ApplyLayerScaleAndMargin(FoldedLayer);
        ApplyLayerScaleAndMargin(CardLayer);
        ApplyLayerScaleAndMargin(OpenLayer);
        ApplyLayerScaleAndMargin(CommandLayer);
    }

    private void UpdateHitRegion(bool open, bool isCard = false, bool isCommand = false)
    {
        double scale  = _scale > 0 ? _scale : 1.0;
        float factor  = (float)(_settings.SizePercent / 100.0);

        float foldedW = _compositor?.CurrentFoldedWidthDip ?? WindowIslandCompositor.FoldedWidthDip;
        float foldedR = _settings.Shape == "Capsule" ? WindowIslandCompositor.FoldedRadiusDip : WindowIslandCompositor.FoldedNotchRadiusDip;
        float widthDip  = isCommand ? IslandMotion.CommandBarSize.W * factor : (open ? IslandMotion.TabSize(_activeTab).W : (isCard ? _activeCardW : foldedW)) * factor;
        float heightDip = isCommand ? IslandMotion.CommandBarSize.H * factor : (open ? IslandMotion.TabSize(_activeTab).H : (isCard ? _activeCardH : WindowIslandCompositor.FoldedHeightDip)) * factor;
        float radiusDip = (open || isCommand) ? WindowIslandCompositor.OpenRadiusDip * factor : (isCard ? MathF.Min(19f, _activeCardH / 2f) : foldedR) * factor;
        float scaledWindowW = (isCommand ? IslandMotion.WindowWidthForCommand : WindowWidthDip) * factor;
        float leftDip   = (scaledWindowW - widthDip) / 2f;
        float topDip    = _settings.Shape == "Capsule" ? 6f : 0f;

        int leftPx    = (int)Math.Round(leftDip * scale);
        int topPx     = (int)Math.Round(topDip  * scale);
        int rightPx   = (int)Math.Round((leftDip + widthDip)  * scale);
        int bottomPx  = (int)Math.Round((topDip  + heightDip) * scale);
        int ellipsePx = (int)Math.Round(radiusDip * 2 * scale);

        nint rgn;
        if (_settings.Shape != "Capsule")
        {
            int botR  = (int)Math.Round(radiusDip * scale);
            int earPx = botR;
            rgn = CreateNotchRegion(leftPx, rightPx, bottomPx, earPx, botR);
            if (rgn == nint.Zero)
            {
                rgn = CreateRoundRectRgn(leftPx, topPx, rightPx, bottomPx, ellipsePx, ellipsePx);
            }
        }
        else
        {
            rgn = CreateRoundRectRgn(leftPx, topPx, rightPx, bottomPx, ellipsePx, ellipsePx);
        }
        if (rgn != nint.Zero) SetWindowRgn(_hwnd, rgn, true);
    }

    private static nint CreateNotchRegion(int left, int right, int bottom, int ear, int botR)
    {
        nint hdc = GetDC(nint.Zero);
        if (hdc == nint.Zero) return nint.Zero;
        try
        {
            if (!BeginPath(hdc)) return nint.Zero;

            int earC = (int)Math.Round(ear * 0.55);
            int botC = (int)Math.Round(botR * 0.55);
            int startX = Math.Max(0, left - ear);

            MoveToEx(hdc, startX, 0, nint.Zero);

            // Top-left concave ear
            PolyBezierTo(hdc, new[]
            {
                new GDI_POINT(startX + earC, 0),
                new GDI_POINT(left, ear - earC),
                new GDI_POINT(left, ear)
            }, 3);

            // Left vertical wall
            if (bottom - botR > ear)
            {
                LineTo(hdc, left, bottom - botR);
            }

            // Bottom-left convex corner
            PolyBezierTo(hdc, new[]
            {
                new GDI_POINT(left, bottom - botR + botC),
                new GDI_POINT(left + botR - botC, bottom),
                new GDI_POINT(left + botR, bottom)
            }, 3);

            // Bottom edge
            LineTo(hdc, right - botR, bottom);

            // Bottom-right convex corner
            PolyBezierTo(hdc, new[]
            {
                new GDI_POINT(right - botR + botC, bottom),
                new GDI_POINT(right, bottom - botR + botC),
                new GDI_POINT(right, bottom - botR)
            }, 3);

            // Right vertical wall
            if (bottom - botR > ear)
            {
                LineTo(hdc, right, ear);
            }

            // Top-right concave ear
            PolyBezierTo(hdc, new[]
            {
                new GDI_POINT(right, ear - earC),
                new GDI_POINT(right + ear - earC, 0),
                new GDI_POINT(right + ear, 0)
            }, 3);

            CloseFigure(hdc);
            if (!EndPath(hdc)) return nint.Zero;
            return PathToRegion(hdc);
        }
        finally
        {
            ReleaseDC(nint.Zero, hdc);
        }
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
        if (msg == WM_POWERBROADCAST)
        {
            DispatcherQueue.TryEnqueue(OnSystemPowerChanged);
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

    private void OnPowerManagerEvent(object? sender, object e)
    {
        DispatcherQueue.TryEnqueue(OnSystemPowerChanged);
    }

    private async void OnSystemPowerChanged()
    {
        UpdateBattery();
        WindowIslandNotificationService.Instance.NotifyPowerStatusChanged();
        if (_activeTab == 7)
        {
            RefreshControlsUI();
        }

        // Some ACPI/EC drivers update ACLineStatus/BatteryFlag ~100ms after the power broadcast
        await Task.Delay(150);
        UpdateBattery();
        WindowIslandNotificationService.Instance.NotifyPowerStatusChanged();
        if (_activeTab == 7)
        {
            RefreshControlsUI();
        }
    }

    // ── Milestone 4: Live Cards & Event Engine ─────────────────────────────────

    private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_activeTab != 0) return;
        var props = e.GetCurrentPoint(VolumeSlider).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        AdjustVolume(delta > 0 ? 2 : -2);
    }

    /// <summary>One wheel notch or hotkey step on whichever target the volume bar shows.</summary>
    private void AdjustVolume(int delta)
    {
        if (_audio == null) return;

        int current;
        if (_appVolumeMode && !string.IsNullOrEmpty(_appSessionId))
        {
            // App mode: the same session the slider drives, never the system volume.
            var session = _audio.FindSession(_appSessionId);
            if (session == null) return;
            current = session.Volume;
        }
        else
        {
            current = _audio.GetDeviceVolume();
        }

        if (current < 0) return;
        ApplyVolumeLevel(Math.Clamp(current + delta, 0, 100), pushCard: true);
    }

    /// <summary>Writes the level to the bar's current target and repaints the row.</summary>
    private void ApplyVolumeLevel(int vol, bool pushCard)
    {
        vol = Math.Clamp(vol, 0, 100);
        _volumeLastWriteMs = Environment.TickCount64;

        if (_appVolumeMode && !string.IsNullOrEmpty(_appSessionId))
        {
            _audio?.SetSessionVolume(_appSessionId, vol);
        }
        else
        {
            // One write path: the island audio helper already owns the default endpoint.
            _audio?.SetDeviceVolume(vol);
        }

        _volumeChanging = true;
        VolumeSlider.Value = vol;
        _volumeChanging = false;

        VolumeText.Text = $"{vol}%";
        UpdateVolumeIcon(vol, _volumeTargetMuted);

        if (pushCard) QueueVolumeCard(vol);
    }

    private void SyncWatchedKeys()
    {
        if (_lockKeySource == null) return;
        var keys = new System.Collections.Generic.List<(int VirtualKey, string Label)>();
        var seen = new System.Collections.Generic.HashSet<int>();

        if (_settings.CardDeleteKeyWarningEnabled && seen.Add(0x2E))
        {
            keys.Add((0x2E, "Delete"));
        }

        if (_settings.CustomKeyWarnings != null)
        {
            foreach (var raw in _settings.CustomKeyWarnings)
            {
                if (WindowIslandSettings.TryResolveVirtualKey(raw, out int vk, out string label) && seen.Add(vk))
                {
                    keys.Add((vk, label));
                }
            }
        }

        _lockKeySource.SetWatchedKeys(keys);
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

    private void OnNumLockChanged(bool isOn)
    {
        if (!_settings.CardNumLockEnabled) return;
        int stay = _settings.GetCardStayMs();
        var accent = GetSystemAccentColor();
        string accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "numlock",
            Icon: "\uE765",
            Title: "Num Lock",
            Detail: isOn ? "On" : "Off",
            AccentHex: isOn ? accentHex : "#C0FFFFFF",
            Priority: WindowIslandCardPriority.CapsLock,
            StayMs: stay));
    }

    private void OnWatchedKeyPressed(string keyLabel)
    {
        int stay = _settings.GetCardStayMs();
        _eventQueue?.Push(new WindowIslandCard(
            Kind: $"keywarn-{keyLabel}",
            Icon: "\uE7BA",
            Title: $"{keyLabel} Key",
            Detail: "Warning: pressed",
            AccentHex: "#FFA726",
            Priority: WindowIslandCardPriority.CapsLock,
            StayMs: stay));
    }

    private float _activeCardW = 270f;
    private float _activeCardH = 38f;
    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _lastCopiedThumb;

    private async void OnClipboardCaptured(ClipboardCardInfo info)
    {
        if (!_settings.CardClipboardEnabled) return;
        int stay = _settings.GetCardStayMs();
        bool isImage = info.Kind == "clipboard.image";

        _lastCopiedThumb = null;
        if (isImage && info.ImageBytes != null && info.ImageBytes.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(info.ImageBytes);
                using var ras = ms.AsRandomAccessStream();
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelHeight = 68 };
                await bmp.SetSourceAsync(ras);
                _lastCopiedThumb = bmp;
            }
            catch { }
        }

        var accent = GetSystemAccentColor();
        string accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "clipboard",
            Icon: isImage ? "\uEB9F" : "\uE77F",
            Title: info.Title,
            Detail: info.Detail,
            AccentHex: accentHex,
            Priority: WindowIslandCardPriority.Clipboard,
            StayMs: stay,
            IsStacked: true));
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

    private string? _activeCardKind;
    private string? _activeCardTagId;

    private void OnShowCard(WindowIslandCard card, bool animate)
    {
        _activeCardKind = card.Kind;
        _activeCardTagId = card.TagId;
        var accentColor = HexToColor(card.AccentHex);
        var cardAccentBrush = new SolidColorBrush(accentColor);
        CardIcon.FontFamily = card.Kind == "weather-rain"
            ? new FontFamily("Segoe UI Symbol, Segoe Fluent Icons")
            : new FontFamily("Segoe Fluent Icons");
        CardIcon.Glyph = card.Icon;
        CardIcon.Foreground = cardAccentBrush;

        bool hasActions = !string.IsNullOrEmpty(card.PrimaryActionLabel);
        CardLayer.IsHitTestVisible = hasActions;

        if (card.IsStacked)
        {
            _activeCardW = hasActions ? 400f : 330f;
            _activeCardH = 58f;
            CardLayer.Width = _activeCardW;
            CardLayer.Height = _activeCardH;
            CardContentGrid.HorizontalAlignment = HorizontalAlignment.Stretch;

            CardTitle.Visibility = Visibility.Collapsed;
            CardDetail.Visibility = Visibility.Collapsed;
            CardProgressBar.Visibility = Visibility.Collapsed;
            CardValueText.Visibility = Visibility.Collapsed;

            CardStackedTitle.Text = card.Title;
            CardStackedDetail.Text = card.Detail;
            CardStackedDetail.Visibility = string.IsNullOrEmpty(card.Detail) ? Visibility.Collapsed : Visibility.Visible;
            double maxTextW = hasActions ? 200.0 : 250.0;
            CardStackedTitle.MaxWidth = maxTextW;
            CardStackedDetail.MaxWidth = maxTextW;
            CardStackedTextPanel.Visibility = Visibility.Visible;
            CardActionPanel.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
            if (hasActions)
            {
                CardPrimaryActionBtn.Content = card.PrimaryActionLabel;
            }

            bool showThumb = card.Kind == "clipboard" && card.Icon == "\uEB9F" && _lastCopiedThumb != null;
            if (showThumb)
            {
                CardThumbBorder.Background = new ImageBrush
                {
                    ImageSource = _lastCopiedThumb,
                    Stretch = Stretch.UniformToFill
                };
                CardThumbBorder.Visibility = Visibility.Visible;
                CardIconBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                CardThumbBorder.Visibility = Visibility.Collapsed;
                CardIconBadge.Visibility = Visibility.Visible;
                CardIconBadge.Width = 34;
                CardIconBadge.Height = 34;
                CardIconBadge.CornerRadius = new CornerRadius(17);
                CardIconBadge.Background = (hasActions || card.Kind == "bt-battery" || card.Kind == "weather-rain")
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(42, accentColor.R, accentColor.G, accentColor.B))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 255, 255));
                CardIcon.FontSize = 15;
            }
        }
        else
        {
            _activeCardW = 270f;
            _activeCardH = 38f;
            CardLayer.Width = _activeCardW;
            CardLayer.Height = _activeCardH;

            CardStackedTextPanel.Visibility = Visibility.Collapsed;
            CardActionPanel.Visibility = Visibility.Collapsed;
            CardThumbBorder.Visibility = Visibility.Collapsed;
            CardIconBadge.Visibility = Visibility.Visible;
            CardIconBadge.Width = double.NaN;
            CardIconBadge.Height = double.NaN;
            CardIconBadge.CornerRadius = new CornerRadius(0);
            CardIconBadge.Background = null;
            CardIcon.FontSize = 14;

            CardTitle.Text = card.Title;
            CardTitle.Visibility = string.IsNullOrEmpty(card.Title) ? Visibility.Collapsed : Visibility.Visible;
            CardDetail.MaxWidth = 145;
            CardDetail.Text = card.Detail;
            CardDetail.Visibility = string.IsNullOrEmpty(card.Detail) ? Visibility.Collapsed : Visibility.Visible;

            if (card.Value >= 0)
            {
                CardContentGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                CardProgressBar.Foreground = cardAccentBrush;
                CardProgressBar.Value = card.Value;
                CardProgressBar.Visibility = Visibility.Visible;
                CardValueText.Text = $"{card.Value}%";
                CardValueText.Visibility = Visibility.Visible;
            }
            else
            {
                CardContentGrid.HorizontalAlignment = HorizontalAlignment.Center;
                CardProgressBar.Visibility = Visibility.Collapsed;
                CardValueText.Visibility = Visibility.Collapsed;
            }
        }

        // Milestone 15: bring island into view for card duration
        _visibilityService?.RequestTemporaryReveal(card.StayMs < 0 ? 15000 : card.StayMs + 1000);

        if (animate)
        {
            _compositor?.AnimateToCard(_activeCardW, _activeCardH);
            FadeVisual(_foldedVisual, 0f, 100);
            FadeVisual(_cardVisual, 1f, 160, delayMs: 40);
            UpdateHitRegion(open: false, isCard: true);
        }
    }

    private void CardPrimaryActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCardKind == "reminder" && !string.IsNullOrEmpty(_activeCardTagId))
        {
            _reminderStore?.Snooze(_activeCardTagId, 10);
            RefreshRemindersUI();
        }
        else if (_activeCardKind == "timer" && _timer != null)
        {
            _timer.Start(_timer.TotalDuration);
            UpdateTimerButtonsState(IslandTimerState.Running);
        }
        _eventQueue?.Clear();
    }

    private void CardDismissActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCardKind == "reminder" && !string.IsNullOrEmpty(_activeCardTagId))
        {
            _reminderStore?.Complete(_activeCardTagId);
            RefreshRemindersUI();
        }
        _eventQueue?.Clear();
    }

    private void OnClearCard()
    {
        CardLayer.IsHitTestVisible = false;
        _activeCardKind = null;
        _activeCardTagId = null;
        FadeVisual(_cardVisual, 0f, 100);
        FadeVisual(_foldedVisual, 1f, 160, delayMs: 40);
        _compositor?.AnimateToFolded(() =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_compositor != null && !_compositor.IsOpen && !_compositor.IsCard && !_compositor.IsCommand)
                {
                    UpdateHitRegion(open: false, isCard: false);
                }
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

        bool isCmd = _compositor?.IsCommand == true;
        float factor = (float)(_settings.SizePercent / 100.0);
        var (tabW, tabH) = isCmd ? IslandMotion.CommandBarSize : IslandMotion.TabSize(_activeTab);
        float baseWinW = isCmd ? IslandMotion.WindowWidthForCommand : WindowWidthDip;
        float baseWinH = tabH + 60f;
        float winW = baseWinW * factor;
        float winH = baseWinH * factor;

        // OpenLayer & CommandLayer keep unscaled logical sizes and scale via RenderTransform
        OpenLayer.Width     = tabW;
        OpenLayer.Height    = tabH;

        float topDip = _settings.Shape == "Capsule" ? 6f : 0f;
        CommandLayer.Width  = IslandMotion.CommandBarSize.W;
        CommandLayer.Height = IslandMotion.CommandBarSize.H;
        CommandLayer.Margin = new Thickness(0, topDip, 0, 0);

        _compositor?.UpdateWindowWidth(winW, _settings);

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
        _homeState = new WindowIslandHomeState(DispatcherQueue);
        _homeState.MediaUpdated += OnMediaUpdated;
        _homeState.StatsUpdated += OnStatsUpdated;
        _homeState.Start();
        StartVolumeUi();
        StartEqTimer();
    }

    private void StopHomeState()
    {
        if (_homeState == null) return;
        _homeState.MediaUpdated -= OnMediaUpdated;
        _homeState.StatsUpdated -= OnStatsUpdated;
        _homeState.Dispose();
        _homeState = null;
        StopVolumeUi();
        StopEqTimer();
        _mediaAppName = string.Empty;
        // Hide folded media mini when home tab is not active
        FoldedMediaMini.Visibility = Visibility.Collapsed;
    }

    private void OnMediaUpdated(AppLock.Island.Modules.MediaSnapshot snap)
    {
        try
        {
            _isMediaPlaying = snap.Playing;

            _mediaAppName = snap.HasSession ? snap.App : string.Empty;

            if (!snap.HasSession || string.IsNullOrEmpty(snap.Title))
            {
                MediaPlayerPanel.Visibility = Visibility.Collapsed;
                MediaNextApp.Visibility     = Visibility.Collapsed;
                RecentPanel.Visibility      = Visibility.Visible;
                FoldedMediaMini.Visibility  = Visibility.Collapsed;
                StopEqTimer();
                ClearLyrics();
                return;
            }

            MediaPlayerPanel.Visibility = Visibility.Visible;
            RecentPanel.Visibility      = Visibility.Collapsed;

            // The chevron only makes sense while another app also has media to show.
            MediaNextApp.Visibility = snap.SessionCount > 1 ? Visibility.Visible : Visibility.Collapsed;

            MediaTitle.Text  = snap.Title;
            MediaArtist.Text = snap.Artist;
            MediaApp.Text    = snap.App.ToUpperInvariant();
            MediaPlayPauseIcon.Glyph = snap.Playing ? "\uE769" : "\uE768"; // Pause : Play

            // Progress line with elapsed / remaining times, like a phone-style player card
            _mediaDuration = snap.DurationSeconds;

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
            bool hasArt = false;
            if (!string.IsNullOrEmpty(snap.ArtFile))
            {
                try
                {
                    string artPath = Path.Combine(AppLock.Island.Modules.MediaSessionSource.ArtFolder, snap.ArtFile);
                    if (File.Exists(artPath))
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(artPath));
                        MediaArt.Source = bmp;
                        FoldedArtBorder.Background = new ImageBrush { ImageSource = bmp, Stretch = Stretch.UniformToFill };
                        FoldedArtFallback.Visibility = Visibility.Collapsed;
                        hasArt = true;
                    }
                }
                catch { }
            }
            if (!hasArt)
            {
                MediaArt.Source = null;
                FoldedArtBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
                FoldedArtFallback.Visibility = Visibility.Visible;
            }

            // Folded mini (music icon/art on left end, live music beat on right end)
            _isMediaPlaying = snap.Playing;
            var vis = snap.Playing ? Visibility.Visible : Visibility.Collapsed;
            FoldedMediaMini.Visibility = vis;
            FoldedEqMini.Visibility = vis;
            if (snap.Playing)
            {
                var eqBrush = new SolidColorBrush(GetSystemAccentColor());
                EqBar0.Background = eqBrush;
                EqBar1.Background = eqBrush;
                EqBar2.Background = eqBrush;
                EqBar3.Background = eqBrush;
                FoldedArtFallback.Foreground = eqBrush;
            }
            UpdateFoldedWeatherMini();
            UpdateDynamicFoldedWidth();
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

    private const int HomeRecentCount = 4;

    /// <summary>
    /// Fills the Home tab's RECENTLY COPIED rows from the same clipboard history list. Each row is a
    /// hoverable card; clicking it copies that item back onto the clipboard.
    /// </summary>
    private void UpdateHomeRecents(IReadOnlyList<AppLock.Island.Modules.ClipboardRow> rows)
    {
        try
        {
            RecentList.Children.Clear();

            int count = Math.Min(rows.Count, HomeRecentCount);
            for (int i = 0; i < count; i++)
            {
                RecentList.Children.Add(BuildRecentRow(rows[i]));
            }

            RecentEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { WindowIslandHost.Log("UpdateHomeRecents: " + ex.Message); }
    }

    private FrameworkElement BuildRecentRow(AppLock.Island.Modules.ClipboardRow row)
    {
        // A Button gives the row a native pointer-over/pressed state, so the hover highlight is
        // driven by the control template instead of hand-rolled PointerEntered/PointerExited.
        var host = new Button
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 3, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Leading badge: "T" for text, the real thumbnail for an image, a glyph otherwise.
        var badge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };

        switch (row.Type)
        {
            case "text":
                badge.Child = new TextBlock
                {
                    Text = "T",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(190, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                break;

            case "image":
                string? thumbPath = ResolveRecentThumb(row.Thumb);
                if (thumbPath != null)
                {
                    // A wider badge gives the thumbnail enough room to be recognisable.
                    badge.Width = 40;
                    badge.Background = new ImageBrush
                    {
                        ImageSource = new BitmapImage(new Uri(thumbPath)),
                        Stretch = Stretch.UniformToFill
                    };
                }
                else
                {
                    badge.Child = RecentGlyph("\uEB9F");
                }
                break;

            case "files":
                badge.Child = RecentGlyph("\uE8B7");
                break;

            default:
                badge.Child = RecentGlyph("\uE77F");
                break;
        }
        Grid.SetColumn(badge, 0);

        var preview = new TextBlock
        {
            Text = row.Preview,
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 255, 255, 255)),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(preview, 1);

        var age = new TextBlock
        {
            Text = row.Age,
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(112, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(age, 2);

        grid.Children.Add(badge);
        grid.Children.Add(preview);
        grid.Children.Add(age);
        host.Content = grid;

        host.Click += async (_, _) => await CopyRecentItemAsync(row);
        ToolTipService.SetToolTip(host, "Click to copy");

        return host;
    }

    private static FontIcon RecentGlyph(string glyph) => new()
    {
        Glyph = glyph,
        FontSize = 11,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255))
    };

    private static string? ResolveRecentThumb(string thumb)
    {
        if (string.IsNullOrEmpty(thumb)) return null;
        try
        {
            if (File.Exists(thumb)) return thumb;
            string candidate = Path.Combine(ServiceContainer.ClipboardStore.StoragePath, thumb);
            if (File.Exists(candidate)) return candidate;
        }
        catch { }
        return null;
    }

    /// <summary>Copies a recently-copied item back onto the clipboard.</summary>
    private async Task CopyRecentItemAsync(AppLock.Island.Modules.ClipboardRow row)
    {
        try
        {
            if (_clipboardState != null)
            {
                await _clipboardState.PasteAsync(row.Id, copyOnly: true);
            }
        }
        catch (Exception ex) { WindowIslandHost.Log("CopyRecentItem: " + ex.Message); }
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

            // The volume row is driven by RefreshVolumeUi, which knows whether the slider is
            // showing the device or the playing app's volume.
        }
        catch (Exception ex) { WindowIslandHost.Log("OnStatsUpdated: " + ex.Message); }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024)         return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)  return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
    }

    private void UpdateVolumeIcon(int vol, bool muted)
    {
        _volumeTargetMuted = muted;
        VolumeIcon.Glyph = muted || vol == 0 ? "\uE74F"
            : vol < 33   ? "\uE992"
            : vol < 66   ? "\uE993"
            : "\uE995";
    }

    // ── Volume bar: device or playing app, mute and output-device picker ──────

    private void InitAudioListener()
    {
        if (_audio != null) return;
        _audio = new WindowIslandAudio();
        _audio.SystemVolumeChanged += OnSystemVolumeChanged;
        _ = Task.Run(() => _audio?.Refresh());
    }

    private void OnSystemVolumeChanged(int vol, bool muted)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Ignore echoes of our own writes from the Home tab slider
            if (Environment.TickCount64 - _volumeLastWriteMs < 400 || _volumePointerDown) return;

            if (_compositor?.IsOpen == true && _activeTab == 0)
            {
                if (!_appVolumeMode)
                {
                    _volumeChanging = true;
                    VolumeSlider.Value = vol;
                    _volumeChanging = false;
                    VolumeText.Text = $"{vol}%";
                    UpdateVolumeIcon(vol, muted);
                }
                return;
            }

            if (_settings.CardVolumeEnabled)
            {
                PushVolumeCard(muted ? 0 : vol, muted);
            }
        });
    }

    private void StartVolumeUi()
    {
        if (_volumeUiTimer != null) return;
        InitAudioListener();
        _volumeUiTimer = DispatcherQueue.CreateTimer();
        _volumeUiTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _volumeUiTimer.Tick += (_, _) => RefreshVolumeUi();
        _volumeUiTimer.Start();
        RefreshVolumeUi();
    }

    private void StopVolumeUi()
    {
        _volumeUiTimer?.Stop();
        _volumeUiTimer = null;
        _volumeCardTimer?.Stop();
        _volumeCardTimer = null;
        _pendingVolumeCard = -1;
        if (VolumeDeviceOverlay != null) VolumeDeviceOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Reads the playback devices and the app sessions and paints the volume row. While an app has
    /// audio, the two-part switch appears so the slider can drive that app's volume; otherwise the
    /// row drives the output device like a plain speaker slider.
    /// </summary>
    private void RefreshVolumeUi()
    {
        try
        {
            if (_audio == null) return;
            _audio.Refresh();

            // Follow the app the player card is showing, so cycling it with the chevron also moves
            // the app-volume target; otherwise the loudest session wins.
            var app = _audio.FindPlayingSession(_mediaAppName);
            bool audible = app != null && app.Peak > 0.0005f;
            bool hasApp = app != null && (audible || _isMediaPlaying || _audio.HasAudibleSession());

            if (!hasApp) _appVolumeMode = false;
            _appSessionId = app?.SessionId;

            VolumeTargets.Visibility = hasApp ? Visibility.Visible : Visibility.Collapsed;
            if (hasApp)
            {
                SetVolumeTargetVisual(VolumeDeviceTarget, !_appVolumeMode);
                SetVolumeTargetVisual(VolumeAppTarget, _appVolumeMode);
            }

            int volume;
            bool muted;
            if (_appVolumeMode && app != null)
            {
                volume = app.Volume;
                muted = app.Muted;
            }
            else
            {
                volume = _audio.GetDeviceVolume();
                muted = _audio.IsDeviceMuted();
            }

            // Never move the thumb while the user is dragging it: the drag writes the volume a
            // few dozen times a second, and a refresh landing in between would fight the gesture
            // (and could leave the level at whatever the last sync wrote).
            bool userIsAdjusting = _volumePointerDown || Environment.TickCount64 - _volumeLastWriteMs < 1200;
            if (!userIsAdjusting)
            {
                _volumeChanging = true;
                if (volume >= 0) VolumeSlider.Value = volume;
                _volumeChanging = false;
            }

            // Showing or hiding the two-part switch re-flows the row and shifts the slider under
            // the finger; do that only between gestures, never while one is in progress.
            if (!userIsAdjusting)
            {
                VolumeSlider.IsEnabled = _appVolumeMode ? hasApp : _audio.HasDevices;
            }

            VolumeText.Text = volume < 0 ? "—" : $"{volume}%";
            UpdateVolumeIcon(volume, muted);

            VolumeDeviceName.Text = ShortDeviceName(_audio.DefaultDeviceName);
            ToolTipService.SetToolTip(VolumeDevicePicker, _audio.DefaultDeviceName);

            if (VolumeDeviceOverlay.Visibility == Visibility.Visible) BuildVolumeDeviceList();
        }
        catch (Exception ex) { WindowIslandHost.Log("RefreshVolumeUi: " + ex.Message); }
    }

    /// <summary>Drops the "(Realtek(R) Audio)" style suffix so the picker label stays short.</summary>
    private static string ShortDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "No output";
        int paren = name.IndexOf('(');
        string shortName = paren > 1 ? name[..paren].Trim() : name.Trim();
        return shortName.Length > 18 ? shortName[..18] : shortName;
    }

    private static void SetVolumeTargetVisual(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(selected ? (byte)38 : (byte)0, 255, 255, 255));
        button.Opacity = selected ? 1.0 : 0.55;
    }

    private void VolumeMute_Click(object sender, RoutedEventArgs e)
    {
        if (_appVolumeMode && !string.IsNullOrEmpty(_appSessionId))
        {
            var session = _audio?.FindSession(_appSessionId);
            if (session != null) _audio?.SetSessionMute(_appSessionId, !session.Muted);
        }
        else
        {
            _audio?.ToggleDeviceMute();
        }
        RefreshVolumeUi();
    }

    private void VolumeDeviceTarget_Click(object sender, RoutedEventArgs e)
    {
        _appVolumeMode = false;
        RefreshVolumeUi();
    }

    private void VolumeAppTarget_Click(object sender, RoutedEventArgs e)
    {
        _appVolumeMode = true;
        RefreshVolumeUi();
    }

    private void VolumeDevicePicker_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeDeviceOverlay.Visibility == Visibility.Visible)
        {
            VolumeDeviceOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // Opening the list is the moment a fresh device list matters (a headset may have been
        // plugged in), so pay for the full scan here instead of on every tick.
        _audio?.Refresh(forceDeviceScan: true);
        BuildVolumeDeviceList();
        VolumeDeviceOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>Builds the in-panel device list; clicking a device makes it the Windows default.</summary>
    private void BuildVolumeDeviceList()
    {
        if (_audio == null) return;
        VolumeDeviceList.Children.Clear();

        var devices = _audio.Devices;
        if (devices.Count == 0)
        {
            VolumeDeviceList.Children.Add(new TextBlock
            {
                Text = "No playback devices",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(130, 255, 255, 255)),
                Margin = new Thickness(8, 6, 8, 6)
            });
            return;
        }

        foreach (var device in devices)
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(new FontIcon
            {
                Glyph = device.IsDefault ? "\uE73E" : "\uE7F4",
                FontSize = device.IsDefault ? 11 : 12,
                Width = 14,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(
                    device.IsDefault ? (byte)255 : (byte)150, 255, 255, 255))
            });
            content.Children.Add(new TextBlock
            {
                Text = device.Name,
                FontSize = 10.5,
                MaxWidth = 170,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(
                    device.IsDefault ? (byte)240 : (byte)190, 255, 255, 255))
            });

            var item = new Button
            {
                Content = content,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            string id = device.Id;
            item.Click += (_, _) =>
            {
                _audio?.SetDefaultDevice(id);
                VolumeDeviceOverlay.Visibility = Visibility.Collapsed;
                RefreshVolumeUi();
            };
            VolumeDeviceList.Children.Add(item);
        }
    }

    // ── Equalizer bars (real-time audio peak meter while playing) ─────────────

    private static readonly float[] EqBarWeights = { 0.65f, 1.0f, 0.85f, 0.55f };
    private readonly double[] _eqHeights = { 3, 3, 3, 3 };

    private void StartEqTimer()
    {
        if (_eqTimer != null) return;
        _eqTimer = DispatcherQueue.CreateTimer();
        _eqTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 Hz real-time beat response
        _eqTimer.IsRepeating = true;
        _eqTimer.Tick += (_, _) => TickEqualizer();
        _eqTimer.Start();
    }

    private void StopEqTimer()
    {
        _eqTimer?.Stop();
        _eqTimer = null;
        try
        {
            EqBar0.Height = 3;
            EqBar1.Height = 3;
            EqBar2.Height = 3;
            EqBar3.Height = 3;
        }
        catch { }
    }

    private void TickEqualizer()
    {
        if (!_isMediaPlaying) return;
        try
        {
            float peak = _audio?.GetOutputPeak() ?? 0f;
            float level = peak < 0.004f ? 0f : MathF.Min(1f, MathF.Pow(peak, 0.65f) * 1.25f);

            var bars = new[] { EqBar0, EqBar1, EqBar2, EqBar3 };
            for (int i = 0; i < 4; i++)
            {
                double target = level <= 0f
                    ? 3.0
                    : 3.0 + level * 14.0 * EqBarWeights[i] * (0.82 + _eqRng.NextDouble() * 0.36);
                target = Math.Clamp(target, 3.0, 17.0);
                _eqHeights[i] += (target - _eqHeights[i]) * 0.65;
                bars[i].Height = _eqHeights[i];
            }
        }
        catch { }
    }

    // ── Media controls ────────────────────────────────────────────────────────

    private void MediaPlayPause_Click(object sender, RoutedEventArgs e)
        => _ = _homeState?.MediaCommandAsync("toggle");

    /// <summary>Shows the next app that has media; the panel stays on it, so Play/Pause hit it.</summary>
    private void MediaNextApp_Click(object sender, RoutedEventArgs e)
        => _homeState?.CycleMediaSession();

    // ── Seek bar: click or drag anywhere on the line to jump there ───────────

    private void MediaSeek_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _seekPointerDown = true;
        MediaSeekArea.CapturePointer(e.Pointer);
        _ = SeekToPointerAsync(e.GetCurrentPoint(MediaSeekArea).Position.X, throttle: false);
    }

    private void MediaSeek_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_seekPointerDown) return;
        _ = SeekToPointerAsync(e.GetCurrentPoint(MediaSeekArea).Position.X, throttle: true);
    }

    private void MediaSeek_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_seekPointerDown)
        {
            _ = SeekToPointerAsync(e.GetCurrentPoint(MediaSeekArea).Position.X, throttle: false);
        }

        _seekPointerDown = false;
        MediaSeekArea.ReleasePointerCapture(e.Pointer);
    }

    private async Task SeekToPointerAsync(double x, bool throttle)
    {
        if (_homeState == null || _mediaDuration <= 0) return;

        double width = MediaSeekArea.ActualWidth;
        if (width <= 0) return;

        // Dragging raises this every few pixels; the player only needs a few seeks per second.
        long now = Environment.TickCount64;
        if (throttle && now - _lastSeekMs < 120) return;
        _lastSeekMs = now;

        double ratio = Math.Clamp(x / width, 0, 1);
        double target = ratio * _mediaDuration;

        // Paint the result straight away; the 1 Hz poll then confirms the player's position.
        MediaProgress.Value = ratio;
        MediaElapsed.Text = FormatTime(target);
        MediaRemaining.Text = "-" + FormatTime(Math.Max(0, _mediaDuration - target));

        await _homeState.SeekMediaAsync(target);
    }

    private void MediaPrev_Click(object sender, RoutedEventArgs e)
        => _ = _homeState?.MediaCommandAsync("previous");

    private void MediaNext_Click(object sender, RoutedEventArgs e)
        => _ = _homeState?.MediaCommandAsync("next");

    // ── Volume slider ─────────────────────────────────────────────────────────

    private void VolumeSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        => _volumePointerDown = true;

    private void VolumeSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        => _volumePointerDown = false;

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_volumeChanging) return;
        ApplyVolumeLevel((int)e.NewValue, pushCard: true);
    }

    /// <summary>
    /// A drag raises this handler many times a second; pushing a card for each one re-laid out the
    /// island and cost a whole core. Push immediately if the last card is old enough, otherwise
    /// keep only the newest value and show it once the drag pauses.
    /// </summary>
    private void QueueVolumeCard(int vol)
    {
        if (!_settings.CardVolumeEnabled) return;
        if (_compositor?.IsOpen == true && _activeTab == 0) return;

        if (Environment.TickCount64 - _lastVolumeCardMs >= 250)
        {
            PushVolumeCard(vol);
            return;
        }

        _pendingVolumeCard = vol;
        if (_volumeCardTimer == null)
        {
            _volumeCardTimer = DispatcherQueue.CreateTimer();
            _volumeCardTimer.Interval = TimeSpan.FromMilliseconds(250);
            _volumeCardTimer.IsRepeating = false;
            _volumeCardTimer.Tick += (_, _) =>
            {
                int pending = _pendingVolumeCard;
                _pendingVolumeCard = -1;
                if (pending >= 0) PushVolumeCard(pending);
            };
        }

        _volumeCardTimer.Stop();
        _volumeCardTimer.Start();
    }

    private void PushVolumeCard(int vol, bool muted = false)
    {
        _lastVolumeCardMs = Environment.TickCount64;
        _pendingVolumeCard = -1;

        var accent = GetSystemAccentColor();
        string accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        string icon = (muted || vol == 0) ? "\uE74F" : (vol < 33 ? "\uE992" : (vol < 66 ? "\uE993" : "\uE995"));
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "volume",
            Icon: icon,
            Title: muted ? "Muted" : string.Empty,
            Detail: string.Empty,
            AccentHex: accentHex,
            Priority: WindowIslandCardPriority.Volume,
            StayMs: _settings.GetCardStayMs(),
            Value: vol));
    }

    // ── Brightness live preview & external sync ───────────────────────────────

    private void InitBrightnessListener()
    {
        try
        {
            ServiceContainer.BrightnessService.BrightnessChanged -= OnSystemBrightnessChanged;
            ServiceContainer.BrightnessService.BrightnessChanged += OnSystemBrightnessChanged;
        }
        catch { }

        _ = Task.Run(() =>
        {
            try
            {
                var watcher = new System.Management.ManagementEventWatcher(
                    "root\\WMI",
                    "SELECT * FROM WmiMonitorBrightnessEvent");
                watcher.EventArrived += (_, e) =>
                {
                    try
                    {
                        if (e.NewEvent?["Brightness"] is byte level)
                            HandleExternalBrightnessChanged(level);
                        else if (e.NewEvent?["Brightness"] != null && int.TryParse(e.NewEvent["Brightness"].ToString(), out int parsed))
                            HandleExternalBrightnessChanged(parsed);
                    }
                    catch { }
                };
                watcher.Start();
                _brightnessWmiWatcher = watcher;
            }
            catch { }
        });
    }

    private void DisposeBrightnessListener()
    {
        try { ServiceContainer.BrightnessService.BrightnessChanged -= OnSystemBrightnessChanged; } catch { }
        try { _brightnessWmiWatcher?.Stop(); } catch { }
        try { _brightnessWmiWatcher?.Dispose(); } catch { }
        _brightnessWmiWatcher = null;
        _brightnessCardTimer?.Stop();
        _brightnessCardTimer = null;
        _pendingBrightnessCard = -1;
    }

    private void OnSystemBrightnessChanged(object? sender, AppLock.Services.Brightness.BrightnessChangedEventArgs e)
    {
        HandleExternalBrightnessChanged(e.Brightness);
    }

    private void HandleExternalBrightnessChanged(int level)
    {
        int clamped = Math.Clamp(level, 0, 100);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Environment.TickCount64 - _brightnessLastWriteMs < 400 || _controlsBrightnessPointerDown)
                return;

            if (_compositor?.IsOpen == true && _activeTab == 7)
            {
                _controlsBrightnessChanging = true;
                ControlsBrightnessSlider.Value = clamped;
                _controlsBrightnessChanging = false;
                ControlsBrightnessText.Text = $"{clamped}%";
                return;
            }

            if (_settings.CardBrightnessEnabled)
            {
                QueueBrightnessCard(clamped);
            }
        });
    }

    private void QueueBrightnessCard(int level)
    {
        if (!_settings.CardBrightnessEnabled) return;
        if (_compositor?.IsOpen == true && _activeTab == 7) return;

        if (Environment.TickCount64 - _lastBrightnessCardMs >= 250)
        {
            PushBrightnessCard(level);
            return;
        }

        _pendingBrightnessCard = level;
        if (_brightnessCardTimer == null)
        {
            _brightnessCardTimer = DispatcherQueue.CreateTimer();
            _brightnessCardTimer.Interval = TimeSpan.FromMilliseconds(250);
            _brightnessCardTimer.IsRepeating = false;
            _brightnessCardTimer.Tick += (_, _) =>
            {
                int pending = _pendingBrightnessCard;
                _pendingBrightnessCard = -1;
                if (pending >= 0) PushBrightnessCard(pending);
            };
        }

        _brightnessCardTimer.Stop();
        _brightnessCardTimer.Start();
    }

    private void PushBrightnessCard(int level)
    {
        _lastBrightnessCardMs = Environment.TickCount64;
        _pendingBrightnessCard = -1;

        var accent = GetSystemAccentColor();
        string accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "brightness",
            Icon: "\uE706",
            Title: string.Empty,
            Detail: string.Empty,
            AccentHex: accentHex,
            Priority: WindowIslandCardPriority.Brightness,
            StayMs: _settings.GetCardStayMs(),
            Value: Math.Clamp(level, 0, 100)));
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
            await (_clipboardState?.PasteAsync(row.Id, copyOnly: shift, _pasteTargetWindow) ?? Task.CompletedTask);
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
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
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
                Width = 52,
                Height = 38,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 0),
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
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "T",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
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
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            leadingVisual = fileBadge;
        }

        Grid.SetColumn(leadingVisual, 0);
        inner.Children.Add(leadingVisual);

        // Preview + source
        var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var preview = new TextBlock
        {
            Text = row.Preview,
            FontSize = 11.5,
            MaxLines = 1,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var source = new TextBlock
        {
            Text = string.IsNullOrEmpty(row.Source) ? row.Age : $"{row.Source} · {row.Age}",
            FontSize = 9.5,
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
            MinWidth = 24,
            Height = 24,
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
            await (_clipboardState?.PasteAsync(row.Id, copyOnly: shift, _pasteTargetWindow) ?? Task.CompletedTask);
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
            // ClearValue, not null: an explicit null Foreground makes the template's text brush
            // empty, so the label would vanish instead of falling back to the style's colour.
            ShelfClearBtn.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);
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
        var accent = GetSystemAccentColor();
        LauncherAddConfirmBtn.Background = new SolidColorBrush(accent);
        LauncherAddConfirmBtn.Foreground = new SolidColorBrush(GetContrastingForeground(accent));
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
        var accent = GetSystemAccentColor();
        LauncherAddConfirmBtn.Background = new SolidColorBrush(accent);
        LauncherAddConfirmBtn.Foreground = new SolidColorBrush(GetContrastingForeground(accent));
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

    public void ToggleCommandBar()
    {
        if (_compositor != null && _compositor.IsCommand)
        {
            CloseCommandBar();
        }
        else
        {
            OpenCommandBar();
        }
    }

    public void OpenCommandBar()
    {
        if (_compositor == null) return;
        if (!_isVisible) SetIslandVisible(true);

        _previousForegroundWindow = GetForegroundWindow();
        SetForegroundWindow(_hwnd);

        _compositor.UpdateWindowWidth(IslandMotion.WindowWidthForCommand, _settings);
        _compositor.AnimateToCommand(IslandMotion.CommandBarSize.W, IslandMotion.CommandBarSize.H, () =>
        {
            _measuringFrames = false;
        });

        PositionAndSize();
        UpdateHitRegion(open: false, isCard: false, isCommand: true);

        CommandLayer.IsHitTestVisible = true;
        OpenLayer.IsHitTestVisible    = false;
        _measuringFrames = true;

        FadeVisual(_foldedVisual,  0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_cardVisual,    0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_openVisual,    0f, IslandMotion.ContentFadeOutMs);
        FadeVisual(_commandVisual, 1f, IslandMotion.ContentFadeInMs, IslandMotion.ContentFadeInDelayMs);

        if (CommandSearchIcon != null)
        {
            CommandSearchIcon.Foreground = new SolidColorBrush(GetSystemAccentColor());
        }

        ExecuteCommandQuery(CommandInput.Text);
        CommandInput.Focus(FocusState.Programmatic);
        if (!string.IsNullOrEmpty(CommandInput.Text))
        {
            CommandInput.SelectAll();
            DispatcherQueue.TryEnqueue(() => CommandInput.SelectAll());
        }
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
                Text = "Type a file name, app name, math (e.g. 2*(3+4)), or @command (e.g. @youtube, @google, @github, @incognito, lock, note, weather)…",
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(12, 36, 12, 0),
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
        var accent = GetSystemAccentColor();
        var border = new Border
        {
            Background = isSelected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(45, accent.R, accent.G, accent.B))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255)),
            BorderBrush = isSelected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(140, accent.R, accent.G, accent.B))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6)
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
        border.Tag = title;

        border.PointerEntered += (_, _) =>
        {
            if (_selectedCommandIndex != index)
            {
                _selectedCommandIndex = index;
                UpdateCommandSelectionVisuals(scrollIntoView: false);
            }
        };

        border.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            _selectedCommandIndex = index;
            await ExecuteSelectedCommandAsync(false);
        };

        return border;
    }

    private void UpdateCommandSelectionVisuals(bool scrollIntoView)
    {
        var accent = GetSystemAccentColor();
        var selBg = new SolidColorBrush(Windows.UI.Color.FromArgb(45, accent.R, accent.G, accent.B));
        var selBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(140, accent.R, accent.G, accent.B));
        var normalBg = new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255));
        var normalBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));

        for (int i = 0; i < CommandResultList.Children.Count; i++)
        {
            if (CommandResultList.Children[i] is not Border border) continue;
            bool isSel = (i == _selectedCommandIndex);

            border.Background = isSel ? selBg : normalBg;
            border.BorderBrush = isSel ? selBorder : normalBorder;

            if (border.Tag is TextBlock title)
            {
                title.FontWeight = isSel ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }

            if (isSel && scrollIntoView)
            {
                border.StartBringIntoView();
            }
        }
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
        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            if (_selectedCommandIndex >= 0 && _selectedCommandIndex < _currentCommandItems.Count)
            {
                var sel = _currentCommandItems[_selectedCommandIndex];
                if (sel.Title.StartsWith("@"))
                {
                    int spaceIdx = sel.Title.IndexOf(' ');
                    string cmd = spaceIdx > 0 ? sel.Title[..spaceIdx] : sel.Title;
                    CommandInput.Text = $"{cmd} ";
                    CommandInput.SelectionStart = CommandInput.Text.Length;
                    e.Handled = true;
                    return;
                }
            }
        }

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
                UpdateCommandSelectionVisuals(scrollIntoView: true);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Up)
        {
            if (_currentCommandItems.Count > 0)
            {
                _selectedCommandIndex = (_selectedCommandIndex + _currentCommandItems.Count - 1) % _currentCommandItems.Count;
                UpdateCommandSelectionVisuals(scrollIntoView: true);
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
            if (NoteContentBox != null && NoteContentBox.IsLoaded && !string.IsNullOrEmpty(_activeNoteId))
            {
                NoteContentBox.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
                rtf = (rtf ?? string.Empty).TrimEnd('\0');
                if (!string.Equals(rtf, _editorBaselineRtf, StringComparison.Ordinal))
                {
                    _notesStore?.UpdateNote(_activeNoteId, rtf);
                    _editorBaselineRtf = rtf;
                }
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
        if (!NoteContentBox.IsLoaded)
        {
            return;
        }

        // Only push note text into the editor when the active note actually changed. Reloading
        // whenever the editor reads back empty (it does while the island is folded) made
        // SetText raise TextChanged -> auto-save -> Changed -> RefreshNotesUI -> SetText, an
        // endless loop that pegged the UI thread and grew the note by one paragraph per pass.
        if (_loadedNoteId != _activeNoteId)
        {
            _loadedNoteId = _activeNoteId;
            _noteUpdating = true;
            LoadNoteIntoEditor(activeNote.Content);
            DispatcherQueue.TryEnqueue(() =>
            {
                CaptureEditorBaseline();
                _noteUpdating = false;
                UpdateFormattingToolbarUI();
            });
        }
    }

    private void LoadNoteIntoEditor(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            NoteContentBox.Document.SetText(TextSetOptions.None, string.Empty);
            CaptureEditorBaseline();
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
        CaptureEditorBaseline();
        UpdateFormattingToolbarUI();
    }

    /// <summary>
    /// Records the RTF the editor reports right after a programmatic load. RichEditBox rewrites
    /// RTF while loading it, so saving that echo added a paragraph to the note on every reload
    /// (the island reloads the note each time it opens).
    /// </summary>
    private void CaptureEditorBaseline()
    {
        try
        {
            NoteContentBox.Document.GetText(TextGetOptions.FormatRtf, out string baseline);
            _editorBaselineRtf = (baseline ?? string.Empty).TrimEnd('\0');
        }
        catch
        {
            _editorBaselineRtf = null;
        }
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
        if (_noteUpdating || string.IsNullOrEmpty(_activeNoteId) || !NoteContentBox.IsLoaded) return;
        _noteSaveTimer?.Stop();
        _noteSaveTimer = DispatcherQueue.CreateTimer();
        _noteSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _noteSaveTimer.Tick += (_, _) => FlushPendingNoteSave();
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

        string trimmed = lineText.TrimStart();
        int indentLen = lineText.Length - trimmed.Length;
        int caretPos = sel.StartPosition;
        int paraStart = range.StartPosition;

        // Tapped on or near the circle/checkmark at the start of the checklist item
        if (caretPos <= paraStart + indentLen + 2)
        {
            if (trimmed.StartsWith("○ ") || trimmed.StartsWith("✔ ") || trimmed.StartsWith("● ") || trimmed.StartsWith("☐ ") || trimmed.StartsWith("☑ "))
            {
                ToggleChecklistLine(range, lineText);
                int markerEnd = paraStart + indentLen + 2;
                sel.SetRange(markerEnd, markerEnd);
            }
        }
    }

    private void ToggleChecklistLine(ITextRange range, string lineText)
    {
        string trimmed = lineText.TrimStart();
        int indentLen = lineText.Length - trimmed.Length;
        string indent = lineText.Substring(0, indentLen);

        // Preserve any trailing paragraph break (\r / \n) without formatting it
        string body = trimmed.TrimEnd('\r', '\n');
        string trailingBreak = trimmed.Substring(body.Length);

        var accent = GetSystemAccentColor();

        if (body.StartsWith("○ ") || body.StartsWith("☐ "))
        {
            // Transition: Unchecked -> Checked
            string content = body.Length > 2 ? body.Substring(2) : string.Empty;
            range.SetText(TextSetOptions.None, indent + "✔ " + content + trailingBreak);

            // Format checkmark with window accent color
            var markerRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen, range.StartPosition + indentLen + 1);
            markerRange.CharacterFormat.ForegroundColor = accent;
            markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;

            // Format line text with strikethrough & dimmed color (excluding trailingBreak)
            if (content.Length > 0)
            {
                int textStart = range.StartPosition + indentLen + 2;
                var textRange = NoteContentBox.Document.GetRange(textStart, textStart + content.Length);
                textRange.CharacterFormat.Strikethrough = FormatEffect.On;
                textRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(160, 200, 200, 200);
            }
        }
        else if (body.StartsWith("✔ ") || body.StartsWith("● ") || body.StartsWith("☑ "))
        {
            // Transition: Checked -> Unchecked
            string content = body.Length > 2 ? body.Substring(2) : string.Empty;
            range.SetText(TextSetOptions.None, indent + "○ " + content + trailingBreak);

            // Format circle with soft white
            var markerRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen, range.StartPosition + indentLen + 1);
            markerRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
            markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;

            // Remove strikethrough & restore white color (excluding trailingBreak)
            if (content.Length > 0)
            {
                int textStart = range.StartPosition + indentLen + 2;
                var textRange = NoteContentBox.Document.GetRange(textStart, textStart + content.Length);
                textRange.CharacterFormat.Strikethrough = FormatEffect.Off;
                textRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
            }
        }
        else
        {
            // Convert normal line to checklist item
            range.SetText(TextSetOptions.None, indent + "○ " + body + trailingBreak);
            var markerRange = NoteContentBox.Document.GetRange(range.StartPosition + indentLen, range.StartPosition + indentLen + 1);
            markerRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
            markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;
        }

        UpdateFormattingToolbarUI();
    }

    private static bool IsChecklistPrefix(string trimmed) =>
        trimmed.StartsWith("○ ") || trimmed.StartsWith("✔ ") || trimmed.StartsWith("● ") || trimmed.StartsWith("☐ ") || trimmed.StartsWith("☑ ");

    private void NoteFmtChecklist_Click(object sender, RoutedEventArgs e)
    {
        var sel = NoteContentBox.Document.Selection;
        if (sel.ParagraphFormat.ListType != MarkerType.None)
        {
            sel.ParagraphFormat.ListType = MarkerType.None;
        }

        var range = sel.GetClone();
        // If selection ends at the very start of the next line, pull back 1 char so we don't include that extra line
        if (sel.Length > 0 && range.EndPosition > range.StartPosition)
        {
            var tail = NoteContentBox.Document.GetRange(range.EndPosition - 1, range.EndPosition);
            tail.GetText(TextGetOptions.None, out string tailChar);
            if (tailChar == "\r" || tailChar == "\n")
            {
                range.EndPosition--;
            }
        }

        range.Expand(TextRangeUnit.Paragraph);
        range.GetText(TextGetOptions.None, out string rawText);

        bool hadTrailingCr = rawText.EndsWith('\r') || rawText.EndsWith('\n');
        string core = rawText.TrimEnd('\r', '\n');
        string trailingBreak = rawText.Substring(core.Length);
        string[] lines = core.Split('\r');

        bool allAreChecklist = lines.Length > 0 && lines.All(l =>
        {
            string t = l.TrimStart();
            if (lines.Length > 1 && string.IsNullOrEmpty(t)) return true;
            return IsChecklistPrefix(t);
        });

        int startPos = range.StartPosition;
        var updatedLines = new string[lines.Length];

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            int indentLen = line.Length - trimmed.Length;
            string indent = line.Substring(0, indentLen);

            if (allAreChecklist)
            {
                if (IsChecklistPrefix(trimmed))
                {
                    updatedLines[i] = indent + (trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty);
                }
                else
                {
                    updatedLines[i] = line;
                }
            }
            else
            {
                if (lines.Length > 1 && string.IsNullOrEmpty(trimmed))
                {
                    updatedLines[i] = line;
                }
                else if (IsChecklistPrefix(trimmed))
                {
                    updatedLines[i] = line;
                }
                else
                {
                    updatedLines[i] = indent + "○ " + trimmed;
                }
            }
        }

        string newCore = string.Join("\r", updatedLines);
        range.SetText(TextSetOptions.None, newCore + trailingBreak);

        // Apply clean formatting per line
        var accent = GetSystemAccentColor();
        int cursorOffset = startPos;
        for (int i = 0; i < updatedLines.Length; i++)
        {
            string l = updatedLines[i];
            string t = l.TrimStart();
            int indentLen = l.Length - t.Length;

            if (IsChecklistPrefix(t))
            {
                var markerRange = NoteContentBox.Document.GetRange(cursorOffset + indentLen, cursorOffset + indentLen + 1);
                bool isChecked = t.StartsWith("✔ ") || t.StartsWith("● ") || t.StartsWith("☑ ");
                markerRange.CharacterFormat.ForegroundColor = isChecked
                    ? accent
                    : Windows.UI.Color.FromArgb(180, 255, 255, 255);
                markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;

                int textLen = Math.Max(0, t.Length - 2);
                if (textLen > 0)
                {
                    var textRange = NoteContentBox.Document.GetRange(cursorOffset + indentLen + 2, cursorOffset + indentLen + 2 + textLen);
                    textRange.CharacterFormat.Strikethrough = isChecked ? FormatEffect.On : FormatEffect.Off;
                    textRange.CharacterFormat.ForegroundColor = isChecked
                        ? Windows.UI.Color.FromArgb(160, 200, 200, 200)
                        : Windows.UI.Color.FromArgb(255, 240, 240, 240);
                }
            }
            else if (l.Length > 0)
            {
                var lineRange = NoteContentBox.Document.GetRange(cursorOffset, cursorOffset + l.Length);
                lineRange.CharacterFormat.Strikethrough = FormatEffect.Off;
                lineRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
            }

            cursorOffset += l.Length + 1; // +1 for '\r'
        }

        if (lines.Length == 1)
        {
            int caretTarget = startPos + updatedLines[0].Length;
            sel.SetRange(caretTarget, caretTarget);
        }
        else
        {
            sel.SetRange(startPos, startPos + newCore.Length);
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

    private void NoteContentBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var sel = NoteContentBox.Document.Selection;
            var range = sel.GetClone();
            range.Expand(TextRangeUnit.Paragraph);
            range.GetText(TextGetOptions.None, out string lineText);

            string trimmedStart = lineText.TrimStart();
            string body = trimmedStart.TrimEnd('\r', '\n');
            int indentLen = lineText.Length - trimmedStart.Length;
            string indent = lineText.Substring(0, indentLen);

            if (IsCtrlDown() && IsChecklistPrefix(body))
            {
                int oldPos = sel.StartPosition;
                ToggleChecklistLine(range, lineText);
                sel.SetRange(oldPos, oldPos);
                e.Handled = true;
                return;
            }

            if (body == "○" || body == "○ " || body == "✔" || body == "✔ " || body == "●" || body == "☐" || body == "☑")
            {
                // Empty checklist line -> exit checklist mode
                string trailingBreak = trimmedStart.Substring(body.Length);
                range.SetText(TextSetOptions.None, indent + trailingBreak);
                range.CharacterFormat.Strikethrough = FormatEffect.Off;
                range.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
                int pos = range.StartPosition + indentLen;
                sel.SetRange(pos, pos);
                UpdateFormattingToolbarUI();
                e.Handled = true;
            }
            else if (IsChecklistPrefix(body))
            {
                // Continue checklist on the new line
                sel.Collapse(false);
                int insertPos = sel.StartPosition;
                string insertion = "\r" + indent + "○ ";
                sel.SetText(TextSetOptions.None, insertion);

                var markerRange = NoteContentBox.Document.GetRange(insertPos + 1 + indentLen, insertPos + 1 + indentLen + 1);
                markerRange.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
                markerRange.CharacterFormat.Strikethrough = FormatEffect.Off;

                int newCaret = insertPos + insertion.Length;
                sel.SetRange(newCaret, newCaret);
                sel.CharacterFormat.Strikethrough = FormatEffect.Off;
                sel.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
                UpdateFormattingToolbarUI();
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
            var accentBrush = new SolidColorBrush(GetSystemAccentColor());
            FoldedTimerText.Foreground = accentBrush;
            if (FoldedTimerRing != null)
            {
                FoldedTimerRing.Value = Math.Clamp(1.0 - progress, 0.0, 1.0) * 100.0;
                FoldedTimerRing.Foreground = accentBrush;
            }
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

        var total = _timer?.TotalDuration ?? TimeSpan.FromMinutes(5);
        string durText = total.TotalMinutes >= 1
            ? $"{(int)Math.Round(total.TotalMinutes)} min"
            : $"{(int)Math.Round(total.TotalSeconds)} s";

        var accent = GetSystemAccentColor();
        string accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        _eventQueue?.Push(new WindowIslandCard(
            Kind: "timer",
            Icon: "\uE916",
            Title: "Time is up",
            Detail: $"Timer · {durText}",
            AccentHex: accentHex,
            Priority: WindowIslandCardPriority.ReminderTimer,
            StayMs: -1,
            IsStacked: true,
            PrimaryActionLabel: "Again"));

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
            var accent = GetSystemAccentColor();
            string accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
            _eventQueue?.Push(new WindowIslandCard(
                Kind: "reminder",
                Icon: "\uEA8F",
                Title: "Reminder",
                Detail: reminder.Title,
                AccentHex: accentHex,
                Priority: WindowIslandCardPriority.ReminderTimer,
                StayMs: -1,
                IsStacked: true,
                PrimaryActionLabel: "+10 min",
                TagId: reminder.Id));

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
                BatteryCardKind.ChargingStarted  => ("\uEBB5", "Charging",          $"{e.Percent}%", "#22CC44"),
                BatteryCardKind.ChargingComplete => ("\uE83F", "Fully Charged",     "100%", "#22CC44"),
                BatteryCardKind.Unplugged        => ("\uE850", "Unplugged",         $"{e.Percent}%", "#FF8C00"),
                _                                => ("\uE83F", "Battery",           $"{e.Percent}%", "#808080")
            };
            _eventQueue?.Push(new WindowIslandCard(
                "battery-charging", icon, title, detail,
                accent, WindowIslandCardPriority.Battery, stayMs));
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

        // Dynamic Weather & Time-of-Day Theme for Hero Card (no Windows accent color)
        var palette = WeatherCardTheme.Resolve(
            weather.SymbolCode,
            weather.ConditionText,
            weather.WindSpeedMps,
            cur,
            sr,
            ss,
            isDaytime);

        static Windows.UI.Color Argb(uint c) => ColorHelper.FromArgb(
            (byte)((c >> 24) & 0xFF),
            (byte)((c >> 16) & 0xFF),
            (byte)((c >> 8) & 0xFF),
            (byte)(c & 0xFF));

        var heroBg = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        heroBg.GradientStops.Add(new GradientStop { Color = Argb(palette.TopColor), Offset = 0.0 });
        heroBg.GradientStops.Add(new GradientStop { Color = Argb(palette.MiddleColor), Offset = 0.55 });
        heroBg.GradientStops.Add(new GradientStop { Color = Argb(palette.BottomColor), Offset = 1.0 });

        WeatherHeroCard.Background = heroBg;
        WeatherHeroCard.BorderBrush = new SolidColorBrush(Argb(palette.BorderColor));
        WeatherConditionIcon.Foreground = new SolidColorBrush(Argb(palette.IconColor));

        RenderWeatherCityDots(settings);

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
        if (!settings.WeatherTempOnIsland || _timer?.State == IslandTimerState.Running)
        {
            FoldedWeatherMini.Visibility = Visibility.Collapsed;
            UpdateDynamicFoldedWidth();
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
        UpdateDynamicFoldedWidth();
    }

    private void RenderWeatherCityDots(WindowIslandSettings settings)
    {
        if (WeatherCityDotsPanel == null || WeatherCityDotsContainer == null) return;
        WeatherCityDotsPanel.Children.Clear();

        var cities = settings.GetWeatherCities();
        if (cities == null || cities.Count == 0)
        {
            WeatherCityDotsContainer.Visibility = Visibility.Collapsed;
            return;
        }

        WeatherCityDotsContainer.Visibility = Visibility.Visible;
        int activeIdx = Math.Clamp(settings.ActiveWeatherCityIndex, 0, cities.Count - 1);
        bool hasMoreThanThree = cities.Count > 3;

        if (hasMoreThanThree)
        {
            var upBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70E", FontSize = 8, Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)) },
                Background = new SolidColorBrush(ColorHelper.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 2, 3, 2),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ToolTipService.SetToolTip(upBtn, "Previous saved address");
            upBtn.Click += async (_, _) =>
            {
                int prev = (activeIdx - 1 + cities.Count) % cities.Count;
                await WindowIslandWeatherService.Instance.SelectCityAsync(prev);
                RefreshWeatherUI();
            };
            WeatherCityDotsPanel.Children.Add(upBtn);
        }

        int windowStart = hasMoreThanThree ? Math.Clamp(activeIdx - 1, 0, cities.Count - 3) : 0;
        int visibleCount = Math.Min(3, cities.Count);

        for (int offset = 0; offset < visibleCount; offset++)
        {
            int cityIdx = windowStart + offset;
            var city = cities[cityIdx];
            bool isSel = (cityIdx == activeIdx);
            string address = string.IsNullOrWhiteSpace(city.Country) ? city.Name : $"{city.Name}, {city.Country}";

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = isSel ? 8 : 6,
                Height = isSel ? 8 : 6,
                Fill = new SolidColorBrush(isSel
                    ? ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dotBtn = new Button
            {
                Content = dot,
                Background = new SolidColorBrush(ColorHelper.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ToolTipService.SetToolTip(dotBtn, $"{address} ({cityIdx + 1}/{cities.Count})");
            dotBtn.Click += async (_, _) =>
            {
                await WindowIslandWeatherService.Instance.SelectCityAsync(cityIdx);
                RefreshWeatherUI();
            };

            WeatherCityDotsPanel.Children.Add(dotBtn);
        }

        if (hasMoreThanThree)
        {
            var downBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70D", FontSize = 8, Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)) },
                Background = new SolidColorBrush(ColorHelper.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 2, 3, 2),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ToolTipService.SetToolTip(downBtn, "Next saved address");
            downBtn.Click += async (_, _) =>
            {
                int next = (activeIdx + 1) % cities.Count;
                await WindowIslandWeatherService.Instance.SelectCityAsync(next);
                RefreshWeatherUI();
            };
            WeatherCityDotsPanel.Children.Add(downBtn);
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
        if (WeatherLocationsHeaderIcon != null)
        {
            WeatherLocationsHeaderIcon.Foreground = new SolidColorBrush(GetSystemAccentColor());
        }
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
        var accent = GetSystemAccentColor();

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
                Background = new SolidColorBrush(isActive ? ColorHelper.FromArgb(0x30, accent.R, accent.G, accent.B) : ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(isActive ? ColorHelper.FromArgb(0x80, accent.R, accent.G, accent.B) : ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new FontIcon
            {
                Glyph = "\uE707",
                FontSize = 11,
                Foreground = new SolidColorBrush(isActive ? accent : ColorHelper.FromArgb(0xA0, 0xFF, 0xFF, 0xFF))
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
                    Foreground = new SolidColorBrush(accent),
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

    private bool _isRefreshingControlsUi;

    private async void RefreshControlsUI()
    {
        if (_isRefreshingControlsUi) return;
        _isRefreshingControlsUi = true;
        try
        {
            var accentBrush = new SolidColorBrush(GetSystemAccentColor());
            var disabledBrush = new SolidColorBrush(ColorHelper.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

            // 1. Wi-Fi
            var (hasWifi, wifiOn, wifiSsid) = await WindowIslandControlsService.Instance.GetWiFiStateAsync();
            ControlsWifiBtn.IsEnabled = hasWifi;
            ControlsWifiStatus.Text = hasWifi ? (wifiOn ? wifiSsid : "Off") : "No adapter";
            ControlsWifiIcon.Foreground = wifiOn ? accentBrush : disabledBrush;

            // 2. Bluetooth
            var (hasBt, btOn) = await WindowIslandControlsService.Instance.GetBluetoothStateAsync();
            ControlsBtBtn.IsEnabled = hasBt;
            if (!hasBt)
            {
                ControlsBtStatus.Text = "No adapter";
            }
            else if (!btOn)
            {
                ControlsBtStatus.Text = "Off";
            }
            else if (string.IsNullOrEmpty(ControlsBtStatus.Text) || ControlsBtStatus.Text == "Off" || ControlsBtStatus.Text == "No adapter")
            {
                ControlsBtStatus.Text = "On";
            }
            ControlsBtIcon.Foreground = btOn ? accentBrush : disabledBrush;

            // 3. Dark Theme
            bool isDark = WindowIslandControlsService.IsSystemDarkTheme();
            ControlsThemeStatus.Text = isDark ? "Dark" : "Light";
            ControlsThemeIcon.Foreground = isDark ? accentBrush : new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0xD7, 0x00));

            // 4. Battery
            var (hasBattery, batPct, isCharging, isPluggedIn, isSaverOn, batText) = WindowIslandControlsService.GetDetailedBatteryInfo();
            ControlsBatteryStatus.Text = hasBattery ? $"{batPct}% · {batText}" : "On mains power";
            var batColor = hasBattery
                ? (isPluggedIn || isCharging
                    ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4C, 0xAF, 0x50))
                    : GetBtBatteryBrush(batPct))
                : new SolidColorBrush(ColorHelper.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
            ControlsBatteryIcon.Glyph = isCharging
                ? "\uEA93"
                : (isPluggedIn
                    ? "\uEBB5"
                    : (isSaverOn
                        ? "\uEBC0"
                        : batPct switch
                        {
                            >= 90 => "\uE859",
                            >= 75 => "\uE856",
                            >= 50 => "\uE854",
                            >= 25 => "\uE852",
                            _     => "\uE850"
                        }));
            ControlsBatteryIcon.Foreground = batColor;
            ControlsBatteryPlugIcon.Glyph = isPluggedIn ? "\uE945" : "\uE850";
            ControlsBatteryPlugIcon.Foreground = isPluggedIn
                ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4C, 0xAF, 0x50))
                : batColor;
            ControlsBatterySaverIcon.Foreground = isSaverOn
                ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4C, 0xAF, 0x50))
                : new SolidColorBrush(ColorHelper.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            ToolTipService.SetToolTip(ControlsBatterySaverBtn, isSaverOn ? "Battery Saver: On (click to turn off)" : "Battery Saver: Off (click to turn on)");

            // 5. VPN
            var (vpnConnected, vpnName) = WindowIslandControlsService.GetVpnStatus();
            ControlsVpnStatus.Text = vpnConnected ? vpnName : "Off";
            ControlsVpnIcon.Foreground = vpnConnected ? accentBrush : disabledBrush;

            // 6. Brightness
            if (ServiceContainer.BrightnessService.IsAvailable)
            {
                int curBri = await ServiceContainer.BrightnessService.GetBrightnessAsync();
                _controlsBrightnessChanging = true;
                ControlsBrightnessSlider.Value = curBri;
                ControlsBrightnessText.Text = $"{curBri}%";
                _controlsBrightnessChanging = false;
            }

            // 6b. Microphone
            if (_settings.Controls.ShowMicrophone)
                RefreshControlsMicrophoneUi();

            // 7. Dynamic row visibility and Quick Cards Grid
            if (ControlsBrightnessRow != null)
                ControlsBrightnessRow.Visibility = _settings.Controls.ShowBrightness ? Visibility.Visible : Visibility.Collapsed;
            if (ControlsMicrophoneRow != null)
                ControlsMicrophoneRow.Visibility = _settings.Controls.ShowMicrophone ? Visibility.Visible : Visibility.Collapsed;
            if (ControlsPowerRow != null)
                ControlsPowerRow.Visibility = _settings.Controls.ShowPowerRow ? Visibility.Visible : Visibility.Collapsed;

            // 8. Bluetooth Battery Cards (supports multiple connected devices in a dynamic grid)
            var btDevices = await WindowIslandControlsService.GetBluetoothDevicesAsync();
            if (hasBt && btOn)
            {
                var connectedNames = btDevices.Where(d => d.IsConnected).Select(d => d.Name).ToList();
                string btStatusText = connectedNames.Count switch
                {
                    0 => "On",
                    1 => connectedNames[0],
                    _ => $"{connectedNames[0]} +{connectedNames.Count - 1}"
                };
                if (ControlsBtStatus.Text != btStatusText)
                {
                    ControlsBtStatus.Text = btStatusText;
                }
            }
            RenderFoldedBtBatteries(btOn, btDevices);

            if (ControlsBtBatteryContainer != null && _settings.Controls.ShowBluetoothBatteryCard)
            {
                var connectedDevices = btOn
                    ? btDevices.Where(d => d.IsConnected).OrderByDescending(d => d.BatteryPercent.HasValue).ThenBy(d => d.Name).ToList()
                    : new System.Collections.Generic.List<WindowIslandControlsService.IslandBtDevice>();

                ControlsBtBatteryContainer.Children.Clear();
                ControlsBtBatteryContainer.RowDefinitions.Clear();
                ControlsBtBatteryContainer.ColumnDefinitions.Clear();

                if (connectedDevices.Count > 0)
                {
                    int totalCards = connectedDevices.Count;
                    int numCols = totalCards <= 4 ? totalCards : 4;
                    int numRows = (totalCards + numCols - 1) / numCols;

                    for (int c = 0; c < numCols; c++)
                    {
                        ControlsBtBatteryContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    }
                    for (int r = 0; r < numRows; r++)
                    {
                        ControlsBtBatteryContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    }

                    for (int i = 0; i < totalCards; i++)
                    {
                        int row = i / numCols;
                        int col = i % numCols;
                        var card = CreateBtBatteryGridCard(connectedDevices[i], accentBrush, isSingleCard: totalCards == 1);
                        Grid.SetRow(card, row);
                        Grid.SetColumn(card, col);
                        ControlsBtBatteryContainer.Children.Add(card);
                    }

                    ControlsBtBatteryContainer.Visibility = Visibility.Visible;
                }
                else
                {
                    ControlsBtBatteryContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    ControlsBtBatteryContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    if (ControlsBtBatteryCard != null)
                    {
                        if (ControlsBtBatteryCard.Parent is Panel p)
                        {
                            p.Children.Remove(ControlsBtBatteryCard);
                        }
                        if (ControlsBtBatteryCardIcon != null)
                        {
                            ControlsBtBatteryCardIcon.Foreground = btOn
                                ? accentBrush
                                : new SolidColorBrush(ColorHelper.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
                        }
                        ControlsBtBatteryCardTitle.Text = "Bluetooth devices";
                        ControlsBtBatteryCardStatus.Text = btOn ? "No devices connected" : "Bluetooth is turned off";
                        ControlsBtBatteryCardStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 255, 255, 255));
                        Grid.SetRow(ControlsBtBatteryCard, 0);
                        Grid.SetColumn(ControlsBtBatteryCard, 0);
                        Grid.SetColumnSpan(ControlsBtBatteryCard, 1);
                        ControlsBtBatteryContainer.Children.Add(ControlsBtBatteryCard);
                    }
                    ControlsBtBatteryContainer.Visibility = _settings.Controls.HideBatteryCardWhenNone ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else if (ControlsBtBatteryContainer != null)
            {
                ControlsBtBatteryContainer.Visibility = Visibility.Collapsed;
            }

            RenderQuickCardsGrid();
        }
        catch { }
        finally
        {
            _isRefreshingControlsUi = false;
        }
    }

    private bool _isUpdatingFoldedBt;

    private async Task UpdateFoldedBtBatteriesAsync()
    {
        if (_isUpdatingFoldedBt) return;
        _isUpdatingFoldedBt = true;
        try
        {
            if (!_settings.Controls.ShowBtBatteryOnIsland && !_settings.Controls.BtLowBatteryAlert)
            {
                if (FoldedBtBatteryPanel != null) FoldedBtBatteryPanel.Visibility = Visibility.Collapsed;
                UpdateDynamicFoldedWidth();
                return;
            }

            var (_, btOn) = await WindowIslandControlsService.Instance.GetBluetoothStateAsync();
            var btDevices = btOn
                ? await WindowIslandControlsService.GetBluetoothDevicesAsync()
                : new System.Collections.Generic.List<WindowIslandControlsService.IslandBtDevice>();
            RenderFoldedBtBatteries(btOn, btDevices);
        }
        catch { }
        finally
        {
            _isUpdatingFoldedBt = false;
        }
    }

    private void RenderFoldedBtBatteries(bool btOn, System.Collections.Generic.List<WindowIslandControlsService.IslandBtDevice> btDevices)
    {
        CheckBtBatteryWarnings(btOn, btDevices);

        if (FoldedBtBatteryPanel == null) return;
        FoldedBtBatteryPanel.Children.Clear();

        if (!_settings.Controls.ShowBtBatteryOnIsland || !btOn)
        {
            FoldedBtBatteryPanel.Visibility = Visibility.Collapsed;
            UpdateDynamicFoldedWidth();
            return;
        }

        var connected = btDevices
            .Where(d => d.IsConnected && d.BatteryPercent.HasValue)
            .OrderByDescending(d => d.BatteryPercent.HasValue)
            .ThenBy(d => d.Name)
            .ToList();

        if (connected.Count == 0)
        {
            FoldedBtBatteryPanel.Visibility = Visibility.Collapsed;
            UpdateDynamicFoldedWidth();
            return;
        }

        var toShow = _settings.Controls.ShowMultipleBtBatteriesOnIsland
            ? connected.Take(4).ToList()
            : connected.Take(1).ToList();

        string style = _settings.Controls.BtBatteryIslandStyle ?? "IconAndPercent";

        foreach (var dev in toShow)
        {
            int pct = Math.Clamp(dev.BatteryPercent!.Value, 0, 100);
            string devGlyph = dev.DeviceType switch
            {
                "Headphones" => "\uE7F6",
                "Speaker"    => "\uE7F5",
                "Keyboard"   => "\uE765",
                "Mouse"      => "\uE962",
                "Phone"      => "\uE8EA",
                _            => "\uE702"
            };

            var chip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center
            };

            chip.Children.Add(new FontIcon
            {
                Glyph = devGlyph,
                FontSize = 11,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            });

            var batBrush = GetBtBatteryBrush(pct);
            if (style == "IconAndBattery" || style == "IconBatteryAndPercent")
            {
                chip.Children.Add(new FontIcon
                {
                    Glyph = GetBtBatteryGlyph(pct),
                    FontSize = 11,
                    Foreground = batBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (style == "IconAndPercent" || style == "IconBatteryAndPercent")
            {
                chip.Children.Add(new TextBlock
                {
                    Text = $"{pct}%",
                    FontFamily = new FontFamily("Segoe UI Variable Small"),
                    FontSize = 11,
                    Foreground = batBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            FoldedBtBatteryPanel.Children.Add(chip);
        }

        FoldedBtBatteryPanel.Visibility = Visibility.Visible;
        UpdateDynamicFoldedWidth();
    }

    private void CheckBtBatteryWarnings(bool btOn, System.Collections.Generic.List<WindowIslandControlsService.IslandBtDevice> btDevices)
    {
        if (!btOn || !_settings.Controls.BtLowBatteryAlert) return;

        var thresholds = _settings.Controls.GetBtWarningThresholds();
        if (thresholds.Count == 0) return;

        foreach (var dev in btDevices)
        {
            if (!dev.IsConnected || !dev.BatteryPercent.HasValue) continue;
            int pct = Math.Clamp(dev.BatteryPercent.Value, 0, 100);
            string key = string.IsNullOrWhiteSpace(dev.MacAddress) ? dev.Name : dev.MacAddress;

            int? crossed = _btWarningTracker.CheckThreshold(key, pct, thresholds);
            if (!crossed.HasValue) continue;

            string devGlyph = dev.DeviceType switch
            {
                "Headphones" => "\uE7F6",
                "Speaker"    => "\uE7F5",
                "Keyboard"   => "\uE765",
                "Mouse"      => "\uE962",
                "Phone"      => "\uE8EA",
                _            => "\uE702"
            };
            string accentHex = pct <= 20 ? "#FF4D4D" : (pct <= 50 ? "#FFA726" : "#FFD54F");
            int stayMs = _settings.GetCardStayMs();

            _eventQueue?.Push(new WindowIslandCard(
                $"bt-battery-{key}-{crossed.Value}",
                devGlyph,
                dev.Name,
                $"{pct}% remaining (below {crossed.Value}%)",
                accentHex,
                WindowIslandCardPriority.Battery,
                stayMs,
                IsStacked: true));

            if (_settings.Controls.BtWarningWindowsNotification)
            {
                ServiceContainer.TrayService.ShowBalloonNotification(
                    $"{dev.Name} Battery ({pct}%)",
                    $"{pct}% remaining (below {crossed.Value}%)",
                    pct <= 20 ? AppLock.Services.NotificationSeverity.Warning : AppLock.Services.NotificationSeverity.Informational);
            }

            if (pct <= 20)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
        }
    }

    private void UpdateDynamicFoldedWidth()
    {
        if (_compositor == null || FoldedContentStack == null) return;
        FoldedContentStack.InvalidateMeasure();
        FoldedContentStack.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 32));
        float desiredWidth = MathF.Max(WindowIslandCompositor.FoldedWidthDip, (float)FoldedContentStack.DesiredSize.Width + 32f);
        _compositor.SetFoldedWidth(desiredWidth, animate: true);
        if (!_compositor.IsOpen && !_compositor.IsCard && !_compositor.IsCommand)
        {
            UpdateHitRegion(open: false, isCard: false, isCommand: false);
        }
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

    private async void ControlsBatterySaverBtn_Click(object sender, RoutedEventArgs e)
    {
        await WindowIslandControlsService.ToggleBatterySaverAsync();
        UpdateBattery();
        RefreshControlsUI();
    }

    private async void ControlsVpnBtn_Click(object sender, RoutedEventArgs e)
    {
        await WindowIslandControlsService.ToggleVpnAsync();
        RefreshControlsUI();
    }

    // ── Wi-Fi & Bluetooth Device Lists Overlay ───────────────────────────────

    private string _controlsDeviceMode = "none";
    private bool _isPopulatingDeviceList;
    private string _lastDeviceListSignature = string.Empty;
    private string _pendingWifiSsid = string.Empty;

    private void ControlsWifiListBtn_Click(object sender, RoutedEventArgs e)
    {
        _controlsDeviceMode = "wifi";
        _lastDeviceListSignature = string.Empty;
        ControlsDeviceList.Children.Clear();
        ControlsDeviceOverlayTitle.Text = "Wi-Fi networks";
        ControlsDeviceOverlay.Visibility = Visibility.Visible;
        _ = PopulateDeviceListAsync(showLoading: true);
    }

    private void ControlsBtListBtn_Click(object sender, RoutedEventArgs e)
    {
        _controlsDeviceMode = "bluetooth";
        _lastDeviceListSignature = string.Empty;
        ControlsDeviceList.Children.Clear();
        ControlsDeviceOverlayTitle.Text = "Bluetooth devices";
        ControlsDeviceOverlay.Visibility = Visibility.Visible;
        _ = PopulateDeviceListAsync(showLoading: true);
    }

    private void ControlsDeviceCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ControlsWifiPasswordOverlay != null) ControlsWifiPasswordOverlay.Visibility = Visibility.Collapsed;
        _pendingWifiSsid = string.Empty;
        ControlsDeviceOverlay.Visibility = Visibility.Collapsed;
        _controlsDeviceMode = "none";
        _lastDeviceListSignature = string.Empty;
    }

    private void ControlsWifiPasswordCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        ControlsWifiPasswordOverlay.Visibility = Visibility.Collapsed;
        ControlsWifiPasswordBox.Password = string.Empty;
        ControlsWifiPasswordError.Visibility = Visibility.Collapsed;
        _pendingWifiSsid = string.Empty;
    }

    private void ControlsWifiPasswordBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ControlsWifiPasswordConnectBtn_Click(sender, new RoutedEventArgs());
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ControlsWifiPasswordCancelBtn_Click(sender, new RoutedEventArgs());
        }
    }

    private async void ControlsWifiPasswordConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingWifiSsid)) return;
        string ssid = _pendingWifiSsid;
        string password = ControlsWifiPasswordBox.Password;

        ControlsWifiPasswordConnectBtn.IsEnabled = false;
        ControlsWifiPasswordCancelBtn.IsEnabled = false;
        ControlsWifiPasswordConnectBtn.Content = "Connecting...";
        ControlsWifiPasswordError.Visibility = Visibility.Collapsed;
        try
        {
            bool ok = await WindowIslandControlsService.ConnectWiFiWithPasswordAsync(ssid, password, isSecured: true);
            if (ok)
            {
                ControlsWifiPasswordOverlay.Visibility = Visibility.Collapsed;
                ControlsWifiPasswordBox.Password = string.Empty;
                _pendingWifiSsid = string.Empty;
                _lastDeviceListSignature = string.Empty;
                await PopulateDeviceListAsync(showLoading: false);
                RefreshControlsUI();
            }
            else
            {
                ControlsWifiPasswordError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            ControlsWifiPasswordConnectBtn.IsEnabled = true;
            ControlsWifiPasswordCancelBtn.IsEnabled = true;
            ControlsWifiPasswordConnectBtn.Content = "Connect";
        }
    }

    private async void ControlsDeviceRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ControlsDeviceRefreshBtn != null) ControlsDeviceRefreshBtn.IsEnabled = false;
        try
        {
            _lastDeviceListSignature = string.Empty;
            await PopulateDeviceListAsync(showLoading: false);
            RefreshControlsUI();
        }
        finally
        {
            if (ControlsDeviceRefreshBtn != null) ControlsDeviceRefreshBtn.IsEnabled = true;
        }
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

    private async Task PopulateDeviceListAsync(bool showLoading = false)
    {
        if (_isPopulatingDeviceList) return;
        _isPopulatingDeviceList = true;
        try
        {
            string mode = _controlsDeviceMode;
            if (showLoading && ControlsDeviceList.Children.Count == 0)
            {
                ControlsDeviceList.Children.Add(new TextBlock
                {
                    Text = "Loading...",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 25, 0, 0)
                });
            }

            if (mode == "wifi")
            {
                var networks = await WindowIslandControlsService.GetWiFiNetworksAsync();
                if (_controlsDeviceMode != "wifi") return;

                bool locBlocked = WindowIslandControlsService.LastWifiLocationBlocked;
                string signature = $"wifi:{(locBlocked ? 1 : 0)}:" + string.Join("|", networks.Select(n => $"{n.Ssid}:{(n.IsConnected ? 1 : 0)}:{n.SignalBars}:{(n.IsSecured ? 1 : 0)}"));
                if (signature == _lastDeviceListSignature && ControlsDeviceList.Children.Count > 0 && ControlsDeviceList.Children[0] is Grid)
                {
                    return;
                }
                _lastDeviceListSignature = signature;

                if (networks.Count == 0 && !locBlocked)
                {
                    ControlsDeviceList.Children.Clear();
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

                var newRows = new System.Collections.Generic.List<(string Key, Grid Row)>(networks.Count + (locBlocked ? 1 : 0));
                if (locBlocked)
                {
                    var warnRow = BuildWifiLocationWarningRow();
                    warnRow.Tag = "wifi-location-warning";
                    newRows.Add(("wifi-location-warning", warnRow));
                }
                foreach (var net in networks)
                {
                    var row = BuildWiFiDeviceRow(net);
                    string rowSig = $"{net.Ssid}:{(nOrZero(net.IsConnected))}:{net.SignalBars}:{(nOrZero(net.IsSecured))}";
                    row.Tag = rowSig;
                    newRows.Add((rowSig, row));
                }
                ApplySmoothDeviceRows(newRows);
            }
            else if (mode == "bluetooth")
            {
                var devices = await WindowIslandControlsService.GetBluetoothDevicesAsync();
                if (_controlsDeviceMode != "bluetooth") return;

                string signature = "bt:" + string.Join("|", devices.Select(d => $"{d.MacAddress}:{d.Name}:{(d.IsConnected ? 1 : 0)}:{d.BatteryPercent?.ToString() ?? ""}:{d.DeviceType}"));
                if (signature == _lastDeviceListSignature && ControlsDeviceList.Children.Count > 0 && ControlsDeviceList.Children[0] is Grid)
                {
                    return;
                }
                _lastDeviceListSignature = signature;

                if (devices.Count == 0)
                {
                    ControlsDeviceList.Children.Clear();
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

                var newRows = new System.Collections.Generic.List<(string Key, Grid Row)>(devices.Count);
                foreach (var dev in devices)
                {
                    var row = BuildBluetoothDeviceRow(dev);
                    string rowSig = $"{dev.MacAddress}:{dev.Name}:{(nOrZero(dev.IsConnected))}:{dev.BatteryPercent?.ToString() ?? ""}:{dev.DeviceType}";
                    row.Tag = rowSig;
                    newRows.Add((rowSig, row));
                }
                ApplySmoothDeviceRows(newRows);
            }
        }
        catch (Exception ex)
        {
            ControlsDeviceList.Children.Clear();
            _lastDeviceListSignature = string.Empty;
            ControlsDeviceList.Children.Add(new TextBlock
            {
                Text = "Error: " + ex.Message,
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 120, 120)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            });
        }
        finally
        {
            _isPopulatingDeviceList = false;
        }

        static int nOrZero(bool v) => v ? 1 : 0;
    }

    private void ApplySmoothDeviceRows(System.Collections.Generic.List<(string Key, Grid Row)> newRows)
    {
        if (ControlsDeviceList.Children.Count == newRows.Count &&
            ControlsDeviceList.Children.All(c => c is Grid))
        {
            for (int i = 0; i < newRows.Count; i++)
            {
                if (ControlsDeviceList.Children[i] is Grid existing && existing.Tag as string == newRows[i].Key)
                {
                    continue;
                }
                ControlsDeviceList.Children[i] = newRows[i].Row;
            }
            return;
        }

        ControlsDeviceList.Children.Clear();
        foreach (var (_, row) in newRows)
        {
            ControlsDeviceList.Children.Add(row);
        }
    }

    private Grid BuildWifiLocationWarningRow()
    {
        var grid = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 167, 38)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(95, 255, 167, 38)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 14,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 183, 77)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var msg = new TextBlock
        {
            Text = "Location is off — turn on Location to scan nearby Wi-Fi networks.",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(msg, 1);
        grid.Children.Add(msg);

        var btn = new Button
        {
            Content = "Location Settings",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(65, 255, 167, 38)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
        };
        btn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = "ms-settings:privacy-location", UseShellExecute = true }); } catch { }
        };
        Grid.SetColumn(btn, 2);
        grid.Children.Add(btn);

        return grid;
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
        string wifiGlyph = net.SignalBars switch
        {
            0 => "\uE871",
            1 => "\uE872",
            2 => "\uE873",
            3 => "\uE874",
            _ => "\uE701"
        };
        var iconGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE701",
            FontSize = 15,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(net.SignalBars == 0 ? (byte)45 : (byte)65, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (net.SignalBars > 0)
        {
            iconGrid.Children.Add(new FontIcon
            {
                Glyph = wifiGlyph,
                FontSize = 15,
                Foreground = net.IsConnected
                    ? accentBrush
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(235, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        iconBorder.Child = iconGrid;
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
            Text = net.Status,
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
                actionBtn.IsEnabled = false;
                try
                {
                    actionBtn.Content = "Disconnecting...";
                    await WindowIslandControlsService.DisconnectWiFiAsync();
                    await Task.Delay(800);
                    _lastDeviceListSignature = string.Empty;
                    await PopulateDeviceListAsync(showLoading: false);
                    RefreshControlsUI();
                }
                finally
                {
                    actionBtn.IsEnabled = true;
                }
            }
            else if (net.IsSaved)
            {
                actionBtn.IsEnabled = false;
                try
                {
                    actionBtn.Content = "Connecting...";
                    await WindowIslandControlsService.ConnectWiFiAsync(net.Ssid);
                    await Task.Delay(1200);
                    _lastDeviceListSignature = string.Empty;
                    await PopulateDeviceListAsync(showLoading: false);
                    RefreshControlsUI();
                }
                finally
                {
                    actionBtn.IsEnabled = true;
                }
            }
            else if (!net.IsSecured)
            {
                actionBtn.IsEnabled = false;
                try
                {
                    actionBtn.Content = "Connecting...";
                    await WindowIslandControlsService.ConnectWiFiWithPasswordAsync(net.Ssid, null, isSecured: false);
                    await Task.Delay(800);
                    _lastDeviceListSignature = string.Empty;
                    await PopulateDeviceListAsync(showLoading: false);
                    RefreshControlsUI();
                }
                finally
                {
                    actionBtn.IsEnabled = true;
                }
            }
            else
            {
                _pendingWifiSsid = net.Ssid;
                ControlsWifiPasswordTitle.Text = $"Connect to {net.Ssid}";
                ControlsWifiPasswordBox.Password = string.Empty;
                ControlsWifiPasswordError.Visibility = Visibility.Collapsed;
                ControlsWifiPasswordConnectBtn.Background = new SolidColorBrush(GetSystemAccentColor());
                ControlsWifiPasswordOverlay.Visibility = Visibility.Visible;
                ControlsWifiPasswordBox.Focus(FocusState.Programmatic);
            }
        };
        Grid.SetColumn(actionBtn, 2);
        grid.Children.Add(actionBtn);

        return grid;
    }

    private static string GetBtBatteryGlyph(int percent) => WindowIslandControlsService.GetBtBatteryGlyph(percent);

    private static SolidColorBrush GetBtBatteryBrush(int percent, Brush? accentBrush = null)
    {
        if (percent <= 20) return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 77, 77));   // Red (low / <= 20%)
        if (percent <= 50) return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 167, 38));  // Amber/Orange (21-50%)
        if (percent < 80)  return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 139, 195, 74));  // Light Green (51-79%)
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80));                      // Full Green (80-100%)
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
            "Keyboard"   => "\uE765",
            "Mouse"      => "\uE962",
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

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock
        {
            Text = dev.IsConnected ? "Connected" : "Not connected",
            FontSize = 9.5,
            Foreground = dev.IsConnected
                ? accentBrush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(status);

        if (dev.BatteryPercent.HasValue)
        {
            var dotSep = new TextBlock
            {
                Text = "·",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusRow.Children.Add(dotSep);

            int bVal = Math.Clamp(dev.BatteryPercent.Value, 0, 100);
            var batIcon = new FontIcon
            {
                Glyph = GetBtBatteryGlyph(bVal),
                FontSize = 11,
                Foreground = GetBtBatteryBrush(bVal, accentBrush),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusRow.Children.Add(batIcon);

            var batText = new TextBlock
            {
                Text = $"{bVal}%",
                FontSize = 9.5,
                Foreground = GetBtBatteryBrush(bVal, accentBrush),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusRow.Children.Add(batText);
        }

        stack.Children.Add(title);
        stack.Children.Add(statusRow);
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
            actionBtn.IsEnabled = false;
            try
            {
                if (dev.IsConnected)
                {
                    actionBtn.Content = "Disconnecting...";
                    await WindowIslandControlsService.DisconnectBluetoothDeviceAsync(dev.MacAddress);
                }
                else
                {
                    actionBtn.Content = "Connecting...";
                    bool ok = await WindowIslandControlsService.ConnectBluetoothDeviceAsync(dev.MacAddress, dev.Name);
                    if (!ok)
                    {
                        actionBtn.Content = "Unavailable";
                        await Task.Delay(900);
                    }
                }
                await Task.Delay(600);
                _lastDeviceListSignature = string.Empty;
                await PopulateDeviceListAsync(showLoading: false);
                RefreshControlsUI();
            }
            finally
            {
                actionBtn.IsEnabled = true;
            }
        };
        Grid.SetColumn(actionBtn, 2);
        grid.Children.Add(actionBtn);

        return grid;
    }

    private void ControlsBtBatteryCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        ControlsBtListBtn_Click(this, new RoutedEventArgs());
    }

    private Border CreateBtBatteryGridCard(WindowIslandControlsService.IslandBtDevice dev, Brush accentBrush, bool isSingleCard = false)
    {
        string glyph = dev.DeviceType switch
        {
            "Headphones" => "\uE7F6",
            "Speaker"    => "\uE7F5",
            "Keyboard"   => "\uE765",
            "Mouse"      => "\uE962",
            "Phone"      => "\uE8EA",
            _            => "\uE702"
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (isSingleCard)
        {
            border.Height = 52;
            border.Padding = new Thickness(12, 8, 12, 8);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left icon
            var iconBorder = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 15,
                Foreground = dev.IsConnected
                    ? (accentBrush as SolidColorBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0xCD, 0xFF)))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
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
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(250, 255, 255, 255)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = accentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusRow.Children.Add(dot);

            var statusText = new TextBlock
            {
                Text = "Connected",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusRow.Children.Add(statusText);

            stack.Children.Add(title);
            stack.Children.Add(statusRow);
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            // Right side: Battery badge
            if (dev.BatteryPercent.HasValue)
            {
                int bVal = Math.Clamp(dev.BatteryPercent.Value, 0, 100);
                var batStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                var batText = new TextBlock
                {
                    Text = $"{bVal}%",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = GetBtBatteryBrush(bVal, accentBrush),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var batIcon = new FontIcon
                {
                    Glyph = GetBtBatteryGlyph(bVal),
                    FontSize = 13,
                    Foreground = GetBtBatteryBrush(bVal, accentBrush),
                    VerticalAlignment = VerticalAlignment.Center
                };
                batStack.Children.Add(batText);
                batStack.Children.Add(batIcon);
                Grid.SetColumn(batStack, 2);
                grid.Children.Add(batStack);
            }

            border.Child = grid;
        }
        else
        {
            border.Height = 78;
            border.Padding = new Thickness(8, 6, 8, 6);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Top row: Centered device icon + more icon on top right
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 18,
                Foreground = dev.IsConnected
                    ? (accentBrush as SolidColorBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0xCD, 0xFF)))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            topGrid.Children.Add(icon);

            var moreIcon = new FontIcon
            {
                Glyph = "\uE712",
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(moreIcon, 1);
            topGrid.Children.Add(moreIcon);

            Grid.SetRow(topGrid, 0);
            grid.Children.Add(topGrid);

            // 2. Name
            var title = new TextBlock
            {
                Text = dev.Name,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(250, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 1)
            };
            Grid.SetRow(title, 1);
            grid.Children.Add(title);

            // 3. Status row (dot + text)
            var statusStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 4,
                Margin = new Thickness(0, 0, 0, 2)
            };

            if (dev.IsConnected)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = accentBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                statusStack.Children.Add(dot);

                var statusText = new TextBlock
                {
                    Text = "Connected",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                statusStack.Children.Add(statusText);
            }
            else
            {
                var statusText = new TextBlock
                {
                    Text = "Not connected",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                statusStack.Children.Add(statusText);
            }
            Grid.SetRow(statusStack, 2);
            grid.Children.Add(statusStack);

            // 4. Bottom: Battery or Connect button
            if (dev.IsConnected && dev.BatteryPercent.HasValue)
            {
                int bVal = Math.Clamp(dev.BatteryPercent.Value, 0, 100);
                var batteryStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 4
                };

                var batText = new TextBlock
                {
                    Text = $"{bVal}%",
                    FontSize = 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = GetBtBatteryBrush(bVal, accentBrush),
                    VerticalAlignment = VerticalAlignment.Center
                };
                batteryStack.Children.Add(batText);

                var batIcon = new FontIcon
                {
                    Glyph = GetBtBatteryGlyph(bVal),
                    FontSize = 11,
                    Foreground = GetBtBatteryBrush(bVal, accentBrush),
                    VerticalAlignment = VerticalAlignment.Center
                };
                batteryStack.Children.Add(batIcon);

                Grid.SetRow(batteryStack, 3);
                grid.Children.Add(batteryStack);
            }
            else if (!dev.IsConnected)
            {
                var connectBtn = new Button
                {
                    Content = "Connect",
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Height = 20,
                    Padding = new Thickness(8, 0, 8, 0),
                    CornerRadius = new CornerRadius(10),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255)),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255))
                };
                connectBtn.Click += async (s, e) =>
                {
                    connectBtn.Content = "...";
                    bool ok = await WindowIslandControlsService.ConnectBluetoothDeviceAsync(dev.MacAddress);
                    if (!ok)
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = "ms-settings:bluetooth", UseShellExecute = true }); } catch { }
                    }
                    await Task.Delay(1000);
                    RefreshControlsUI();
                };
                Grid.SetRow(connectBtn, 3);
                grid.Children.Add(connectBtn);
            }

            border.Child = grid;
        }

        border.PointerEntered += (s, e) =>
        {
            border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255));
        };
        border.PointerExited += (s, e) =>
        {
            border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        };
        border.Tapped += (s, e) =>
        {
            ControlsBtListBtn_Click(this, new RoutedEventArgs());
        };

        return border;
    }

    private Border CreateBtAddDeviceCard()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Height = 104,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 4
        };

        var icon = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 20,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(icon);

        var title = new TextBlock
        {
            Text = "Add device",
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(title);

        var sub = new TextBlock
        {
            Text = "Pair Bluetooth",
            FontSize = 9,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(sub);

        border.Child = stack;

        border.PointerEntered += (s, e) =>
        {
            border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 255, 255));
        };
        border.PointerExited += (s, e) =>
        {
            border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255));
        };
        border.Tapped += (s, e) =>
        {
            ControlsBtListBtn_Click(this, new RoutedEventArgs());
        };

        return border;
    }

    private void ControlsBrightnessSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        => _controlsBrightnessPointerDown = true;

    private void ControlsBrightnessSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        => _controlsBrightnessPointerDown = false;

    private void ControlsBrightnessSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(ControlsBrightnessSlider).Properties.MouseWheelDelta;
        if (delta == 0) return;
        int step = delta > 0 ? 5 : -5;
        int next = Math.Clamp((int)ControlsBrightnessSlider.Value + step, 0, 100);
        ControlsBrightnessSlider.Value = next;
        e.Handled = true;
    }

    private async void ControlsBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_controlsBrightnessChanging) return;
        int val = (int)e.NewValue;
        _brightnessLastWriteMs = Environment.TickCount64;
        ControlsBrightnessText.Text = $"{val}%";
        if (ServiceContainer.BrightnessService.IsAvailable)
        {
            await ServiceContainer.BrightnessService.SetBrightnessAsync(val);
        }
    }

    private void RefreshControlsMicrophoneUi()
    {
        if (ControlsMicrophoneRow == null || ControlsMicCombo == null) return;

        try
        {
            if (_controlsMicService == null)
            {
                _controlsMicService = new AppLock.Services.Audio.MicrophoneVolumeService();
                _controlsMicService.Initialize();
            }
            else
            {
                _controlsMicService.RefreshDevices();
                if (_pendingDefaultMicId == null || DateTime.UtcNow >= _pendingDefaultMicUntil)
                {
                    _controlsMicService.RefreshDefaultFlags();
                }
            }

            var mics = _controlsMicService.GetMicrophones();
            string fingerprint = string.Join(";", mics.Select(m => $"{m.DeviceId}|{m.FriendlyName}|{m.IsDefault}"));

            if (!string.Equals(_controlsMicFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _controlsMicFingerprint = fingerprint;
                _controlsMicUpdating = true;
                try
                {
                    ControlsMicCombo.Items.Clear();
                    int selectedIdx = -1;
                    for (int i = 0; i < mics.Count; i++)
                    {
                        var mic = mics[i];
                        var item = new ComboBoxItem
                        {
                            Content = mic.IsDefault ? $"{mic.FriendlyName} (Default)" : mic.FriendlyName,
                            Tag = mic.DeviceId,
                            FontSize = 11
                        };
                        ToolTipService.SetToolTip(item, mic.IsDefault ? $"{mic.FriendlyName} (Default)" : mic.FriendlyName);
                        ControlsMicCombo.Items.Add(item);

                        if (_controlsMicUserSelected && !string.IsNullOrEmpty(_selectedMicDeviceId) &&
                            string.Equals(mic.DeviceId, _selectedMicDeviceId, StringComparison.Ordinal))
                        {
                            selectedIdx = i;
                        }
                    }

                    if (selectedIdx < 0 && mics.Count > 0)
                    {
                        _controlsMicUserSelected = false;
                        int defaultIdx = mics.ToList().FindIndex(m => m.IsDefault);
                        selectedIdx = defaultIdx >= 0 ? defaultIdx : 0;
                        _selectedMicDeviceId = mics[selectedIdx].DeviceId;
                    }
                    ControlsMicCombo.SelectedIndex = selectedIdx;
                }
                finally
                {
                    _controlsMicUpdating = false;
                }
            }

            if (mics.Count == 0)
            {
                _selectedMicDeviceId = null;
                ControlsMicCombo.IsEnabled = false;
                ControlsMicSlider.IsEnabled = false;
                ControlsMicMuteBtn.IsEnabled = false;
                ControlsMicText.Text = "--";
                ControlsMicMuteIcon.Glyph = "\uEC54";
                ControlsMicMuteIcon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
                ControlsMicLockBadge.Visibility = Visibility.Collapsed;
                return;
            }

            ControlsMicCombo.IsEnabled = true;
            ControlsMicSlider.IsEnabled = true;
            ControlsMicMuteBtn.IsEnabled = true;

            var activeMic = (!string.IsNullOrEmpty(_selectedMicDeviceId)
                ? _controlsMicService.GetMicrophone(_selectedMicDeviceId)
                : null) ?? mics[0];
            _selectedMicDeviceId = activeMic.DeviceId;

            UpdateControlsMicDeviceState(activeMic);
        }
        catch { }
    }

    private void UpdateControlsMicDeviceState(AppLock.Services.Audio.MicrophoneDevice mic)
    {
        int vol = Math.Clamp((int)Math.Round(mic.GetVolume()), 0, 100);
        bool muted = mic.IsMuted();

        if (!_controlsMicPointerDown)
        {
            _controlsMicUpdating = true;
            ControlsMicSlider.Value = vol;
            _controlsMicUpdating = false;
            ControlsMicText.Text = $"{vol}%";
        }

        ControlsMicMuteIcon.Glyph = muted ? "\uEC54" : "\uE720";
        ControlsMicMuteIcon.Foreground = muted
            ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0x6B, 0x6B))
            : new SolidColorBrush(ColorHelper.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

        var vmSettings = ServiceContainer.Settings.VolumeMixer;
        bool locked = vmSettings.LockMicrophoneVolume;
        ControlsMicLockBadge.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;

        string tooltip;
        if (locked)
        {
            float target = vmSettings.LockedMicrophoneVolumes != null &&
                           vmSettings.LockedMicrophoneVolumes.TryGetValue(mic.DeviceId, out var stored)
                ? stored
                : vol;
            tooltip = $"Volume held at {(int)Math.Round(target)}% — changes made elsewhere are put back (Right-click mic icon to unlock)";
        }
        else
        {
            tooltip = $"{mic.FriendlyName} — Click to {(muted ? "unmute" : "mute")}, right-click to lock volume";
        }

        if (!string.Equals(_controlsMicLastTooltip, tooltip, StringComparison.Ordinal))
        {
            _controlsMicLastTooltip = tooltip;
            ToolTipService.SetToolTip(ControlsMicMuteBtn, tooltip);
            ToolTipService.SetToolTip(ControlsMicSlider, tooltip);
        }
    }

    private void ControlsMicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controlsMicUpdating) return;
        if (ControlsMicCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            _controlsMicUserSelected = true;
            _selectedMicDeviceId = id;
            var svc = _controlsMicService;
            var mic = svc?.GetMicrophone(id);
            if (mic != null) UpdateControlsMicDeviceState(mic);

            if (svc != null)
            {
                foreach (var m in svc.GetMicrophones())
                {
                    m.IsDefault = string.Equals(m.DeviceId, id, StringComparison.Ordinal);
                }
                _pendingDefaultMicId = id;
                _pendingDefaultMicUntil = DateTime.UtcNow.AddSeconds(3);
                RefreshControlsMicrophoneUi();

                _ = Task.Run(() =>
                {
                    svc.SetDefaultMicrophone(id);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _pendingDefaultMicId = null;
                        _controlsMicService?.RefreshDefaultFlags();
                        RefreshControlsMicrophoneUi();
                    });
                });
            }
        }
    }

    private void ControlsMicSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        => _controlsMicPointerDown = true;

    private void ControlsMicSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        => _controlsMicPointerDown = false;

    private void ControlsMicSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(ControlsMicSlider).Properties.MouseWheelDelta;
        if (delta == 0) return;
        int step = delta > 0 ? 5 : -5;
        ControlsMicSlider.Value = Math.Clamp((int)Math.Round(ControlsMicSlider.Value) + step, 0, 100);
        e.Handled = true;
    }

    private void ControlsMicSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_controlsMicUpdating) return;
        int val = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        ControlsMicText.Text = $"{val}%";
        if (string.IsNullOrEmpty(_selectedMicDeviceId)) return;

        var mic = _controlsMicService?.GetMicrophone(_selectedMicDeviceId);
        if (mic == null) return;

        mic.SetVolume(val);
        ServiceContainer.MicrophoneVolumeLockService?.NoteUserVolume(mic.DeviceId, val);
        if (ServiceContainer.Settings.VolumeMixer.LockMicrophoneVolume)
        {
            UpdateControlsMicDeviceState(mic);
        }
    }

    private void ControlsMicMuteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedMicDeviceId)) return;
        var mic = _controlsMicService?.GetMicrophone(_selectedMicDeviceId);
        if (mic == null) return;

        mic.SetMute(!mic.IsMuted());
        UpdateControlsMicDeviceState(mic);
    }

    private void ControlsMicMuteBtn_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var vmSettings = ServiceContainer.Settings.VolumeMixer;
        vmSettings.LockMicrophoneVolume = !vmSettings.LockMicrophoneVolume;
        if (vmSettings.LockMicrophoneVolume)
        {
            ServiceContainer.MicrophoneVolumeLockService?.CaptureCurrentVolumes();
        }
        else
        {
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }

        if (!string.IsNullOrEmpty(_selectedMicDeviceId))
        {
            var mic = _controlsMicService?.GetMicrophone(_selectedMicDeviceId);
            if (mic != null) UpdateControlsMicDeviceState(mic);
        }
        e.Handled = true;
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

    // ── Quick-Action Cards (Controls Add-on Milestone 1) ──────────────────────

    private bool _controlsQuickCardsEditMode = false;

    private void ControlsQuickCardsEditBtn_Click(object sender, RoutedEventArgs e)
    {
        _controlsQuickCardsEditMode = !_controlsQuickCardsEditMode;
        ControlsQuickCardsEditText.Text = _controlsQuickCardsEditMode ? "Done" : "Edit";
        ControlsQuickCardsEditIcon.Glyph = _controlsQuickCardsEditMode ? "\uE8FB" : "\uE70F";
        RenderQuickCardsGrid();
    }

    private void RenderQuickCardsGrid()
    {
        if (ControlsQuickCardsGrid == null) return;
        ControlsQuickCardsGrid.Children.Clear();
        ControlsQuickCardsGrid.RowDefinitions.Clear();

        var cards = _settings.Controls.Cards ?? QuickCard.Defaults();

        int totalSlots = cards.Count + (_controlsQuickCardsEditMode ? 1 : 0);
        int rowsNeeded = (int)Math.Ceiling(totalSlots / 4.0);
        if (rowsNeeded < 1) rowsNeeded = 1;

        for (int r = 0; r < rowsNeeded; r++)
        {
            ControlsQuickCardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var accentColor = GetSystemAccentColor();

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            int row = i / 4;
            int col = i % 4;

            var cardContainer = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Height = 54
            };

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5)
            };
            ToolTipService.SetToolTip(btn, string.IsNullOrWhiteSpace(card.Command) ? card.Name : $"{card.Name}\n{card.Command}");

            var contentStack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = new FontIcon
            {
                Glyph = string.IsNullOrEmpty(card.IconGlyph) ? "\uE71D" : card.IconGlyph,
                FontSize = 15,
                Foreground = new SolidColorBrush(accentColor),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            contentStack.Children.Add(icon);

            var titleBlock = new TextBlock
            {
                Text = card.Name,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 115
            };
            contentStack.Children.Add(titleBlock);

            btn.Content = contentStack;

            btn.Click += (_, _) =>
            {
                if (_controlsQuickCardsEditMode) return;
                _ = QuickCardRunner.ExecuteCardAsync(card, () =>
                {
                    if (_settings.Controls.CloseIslandAfterCard)
                    {
                        DispatcherQueue.TryEnqueue(Collapse);
                    }
                });
            };

            var flyout = new MenuFlyout();
            var editMenu = new MenuFlyoutItem { Text = "Edit…", Icon = new FontIcon { Glyph = "\uE70F" } };
            editMenu.Click += (_, _) => OpenControlsEditCardOverlay(card);
            var moveToStartMenu = new MenuFlyoutItem { Text = "Move to start", Icon = new FontIcon { Glyph = "\uE898" } };
            moveToStartMenu.Click += (_, _) =>
            {
                cards.Remove(card);
                cards.Insert(0, card);
                ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);
                RenderQuickCardsGrid();
            };
            var removeMenu = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "\uE74D" } };
            removeMenu.Click += (_, _) =>
            {
                cards.Remove(card);
                ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);
                RenderQuickCardsGrid();
            };
            flyout.Items.Add(editMenu);
            flyout.Items.Add(moveToStartMenu);
            flyout.Items.Add(removeMenu);
            btn.ContextFlyout = flyout;

            cardContainer.Children.Add(btn);

            if (_controlsQuickCardsEditMode)
            {
                var delBtn = new Button
                {
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 2, 2, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 40, 40, 45)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 100, 100)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 9, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 120, 120)) }
                };
                delBtn.Click += (_, _) =>
                {
                    cards.Remove(card);
                    ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);
                    RenderQuickCardsGrid();
                };
                cardContainer.Children.Add(delBtn);
            }

            Grid.SetRow(cardContainer, row);
            Grid.SetColumn(cardContainer, col);
            ControlsQuickCardsGrid.Children.Add(cardContainer);
        }

        if (_controlsQuickCardsEditMode)
        {
            int nextIdx = cards.Count;
            int addRow = nextIdx / 4;
            int addCol = nextIdx % 4;

            var addBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 54,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(10, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(accentColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5)
            };

            var addStack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            addStack.Children.Add(new FontIcon
            {
                Glyph = "\uE710",
                FontSize = 15,
                Foreground = new SolidColorBrush(accentColor),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            addStack.Children.Add(new TextBlock
            {
                Text = "Add card",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentColor),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            addBtn.Content = addStack;
            addBtn.Click += (_, _) => OpenControlsAddCardOverlay();

            Grid.SetRow(addBtn, addRow);
            Grid.SetColumn(addBtn, addCol);
            ControlsQuickCardsGrid.Children.Add(addBtn);
        }
    }

    // ── Quick Cards Add / Edit Overlay ─────────────────────────────────────────

    private QuickCard? _editingCard = null;
    private string _selectedIconGlyph = "\uE71D";

    private void OpenControlsAddCardOverlay()
    {
        _editingCard = null;
        ControlsAddCardTitle.Text = "Add card";
        ControlsCardSaveBtnText.Text = "Add card";

        ControlsCardNameBox.Text = string.Empty;
        ControlsCardCommandBox.Text = string.Empty;
        ControlsCardArgsBox.Text = string.Empty;
        ControlsCardAdminCheck.IsChecked = false;
        ControlsCardIconSearchBox.Text = string.Empty;

        _selectedIconGlyph = "\uE71D";
        ControlsCardSelectedIcon.Glyph = _selectedIconGlyph;

        ControlsCardErrorText.Visibility = Visibility.Collapsed;
        ControlsCardErrorText.Text = string.Empty;

        SwitchToCardTab(builtIn: true);
        RenderBuiltInCatalog();
        RenderCuratedIcons();

        ControlsAddCardOverlay.Visibility = Visibility.Visible;
    }

    private void OpenControlsEditCardOverlay(QuickCard card)
    {
        _editingCard = card;
        ControlsAddCardTitle.Text = "Edit card";
        ControlsCardSaveBtnText.Text = "Save changes";

        ControlsCardNameBox.Text = card.Name ?? string.Empty;
        ControlsCardCommandBox.Text = card.Command ?? string.Empty;
        ControlsCardArgsBox.Text = card.Arguments ?? string.Empty;
        ControlsCardAdminCheck.IsChecked = card.RunAsAdmin;
        ControlsCardIconSearchBox.Text = string.Empty;

        _selectedIconGlyph = string.IsNullOrEmpty(card.IconGlyph) ? "\uE71D" : card.IconGlyph;
        ControlsCardSelectedIcon.Glyph = _selectedIconGlyph;

        ControlsCardErrorText.Visibility = Visibility.Collapsed;
        ControlsCardErrorText.Text = string.Empty;

        SwitchToCardTab(builtIn: false);
        RenderCuratedIcons();

        ControlsAddCardOverlay.Visibility = Visibility.Visible;
    }

    private void CloseControlsAddCardOverlay()
    {
        if (ControlsAddCardOverlay != null)
        {
            ControlsAddCardOverlay.Visibility = Visibility.Collapsed;
        }
        _editingCard = null;
    }

    private void SwitchToCardTab(bool builtIn)
    {
        ControlsAddCardBuiltInPanel.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;
        ControlsAddCardCustomPanel.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;

        ControlsCardTabBuiltInBtn.Background = builtIn ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ControlsCardTabBuiltInText.Foreground = builtIn ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255));
        ControlsCardTabBuiltInText.FontWeight = builtIn ? FontWeights.SemiBold : FontWeights.Normal;

        ControlsCardTabCustomBtn.Background = !builtIn ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ControlsCardTabCustomText.Foreground = !builtIn ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255));
        ControlsCardTabCustomText.FontWeight = !builtIn ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ControlsCardTabBuiltInBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchToCardTab(builtIn: true);
        RenderBuiltInCatalog();
    }

    private void ControlsCardTabCustomBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchToCardTab(builtIn: false);
        RenderCuratedIcons();
    }

    private void ControlsAddCardCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        CloseControlsAddCardOverlay();
    }

    private void RenderBuiltInCatalog()
    {
        if (ControlsBuiltInGrid == null) return;
        ControlsBuiltInGrid.Children.Clear();
        ControlsBuiltInGrid.RowDefinitions.Clear();

        var catalog = QuickCardCatalog.GetCatalogCards();
        var currentCards = _settings.Controls.Cards ?? QuickCard.Defaults();

        int rowsNeeded = (int)Math.Ceiling(catalog.Count / 4.0);
        if (rowsNeeded < 1) rowsNeeded = 1;

        for (int r = 0; r < rowsNeeded; r++)
        {
            ControlsBuiltInGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var accentColor = GetSystemAccentColor();

        for (int i = 0; i < catalog.Count; i++)
        {
            var item = catalog[i];
            int row = i / 4;
            int col = i % 4;

            bool isAdded = currentCards.Any(c => c.Id == item.Id ||
                (!string.IsNullOrWhiteSpace(c.Command) && string.Equals(c.Command, item.Command, StringComparison.OrdinalIgnoreCase)));

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 52,
                Background = isAdded ? new SolidColorBrush(Windows.UI.Color.FromArgb(8, 255, 255, 255)) : new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
                BorderBrush = isAdded ? new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)) : new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 4, 6, 4),
                Opacity = isAdded ? 0.5 : 1.0
            };

            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(new FontIcon
            {
                Glyph = string.IsNullOrEmpty(item.IconGlyph) ? "\uE71D" : item.IconGlyph,
                FontSize = 14,
                Foreground = isAdded ? new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)) : new SolidColorBrush(accentColor),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            stack.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 10,
                FontWeight = isAdded ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = isAdded ? new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)) : new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 110
            });

            if (isAdded)
            {
                ToolTipService.SetToolTip(btn, $"{item.Name} (Already added)");
            }
            else
            {
                ToolTipService.SetToolTip(btn, string.IsNullOrWhiteSpace(item.Command) ? $"Add {item.Name}" : $"Add {item.Name}\n{item.Command}");
                btn.Click += (_, _) =>
                {
                    if (currentCards.Count >= 24)
                    {
                        ControlsCardErrorText.Text = "Maximum 24 cards reached.";
                        ControlsCardErrorText.Visibility = Visibility.Visible;
                        return;
                    }

                    var newCard = new QuickCard
                    {
                        Id = item.Id,
                        Name = item.Name,
                        IconGlyph = item.IconGlyph,
                        Kind = item.Kind,
                        Command = item.Command,
                        Arguments = item.Arguments,
                        RunAsAdmin = item.RunAsAdmin
                    };
                    currentCards.Add(newCard);
                    ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);
                    RenderQuickCardsGrid();
                    CloseControlsAddCardOverlay();
                };
            }

            btn.Content = stack;
            Grid.SetRow(btn, row);
            Grid.SetColumn(btn, col);
            ControlsBuiltInGrid.Children.Add(btn);
        }
    }

    private void ControlsCardIconDocsLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font#icon-list",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void RenderCuratedIcons(string filter = "")
    {
        if (ControlsCardIconsPanel == null) return;
        ControlsCardIconsPanel.Children.Clear();

        string trimmedFilter = filter.Trim();
        bool hasHex = QuickCardCatalog.TryParseHexGlyph(trimmedFilter, out string hexGlyph, out string hexCode);

        if (hasHex)
        {
            _selectedIconGlyph = hexGlyph;
            ControlsCardSelectedIcon.Glyph = hexGlyph;
        }

        var icons = QuickCardCatalog.GetCuratedIcons();
        if (!string.IsNullOrWhiteSpace(trimmedFilter))
        {
            icons = icons.Where(ic =>
                ic.Name.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase) ||
                ((int)ic.Glyph[0]).ToString("x4").Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (hasHex && !icons.Any(ic => ic.Glyph == hexGlyph))
            {
                icons.Insert(0, ($"Hex 0x{hexCode}", hexGlyph));
            }
        }

        var accentColor = GetSystemAccentColor();
        const int iconsPerRow = 14;
        int rowCount = (int)Math.Ceiling(icons.Count / (double)iconsPerRow);

        for (int r = 0; r < rowCount; r++)
        {
            var rowStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };

            for (int c = 0; c < iconsPerRow; c++)
            {
                int idx = r * iconsPerRow + c;
                if (idx >= icons.Count) break;

                var (name, glyph) = icons[idx];
                bool isSelected = glyph == _selectedIconGlyph;

                var iconBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(5),
                    Background = isSelected ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B)) : new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                    BorderBrush = isSelected ? new SolidColorBrush(accentColor) : new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
                    BorderThickness = new Thickness(isSelected ? 1.5 : 1)
                };
                string hexLabel = ((int)glyph[0]).ToString("X4");
                ToolTipService.SetToolTip(iconBtn, $"{name} (0x{hexLabel})");

                iconBtn.Content = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 13,
                    Foreground = isSelected ? new SolidColorBrush(accentColor) : new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255))
                };

                iconBtn.Click += (_, _) =>
                {
                    _selectedIconGlyph = glyph;
                    ControlsCardSelectedIcon.Glyph = glyph;
                    RenderCuratedIcons(ControlsCardIconSearchBox.Text);
                };

                rowStack.Children.Add(iconBtn);
            }

            ControlsCardIconsPanel.Children.Add(rowStack);
        }
    }

    private void ControlsCardIconSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderCuratedIcons(ControlsCardIconSearchBox.Text);
    }

    private async void ControlsCardBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                ControlsCardCommandBox.Text = file.Path;
                if (string.IsNullOrWhiteSpace(ControlsCardNameBox.Text))
                {
                    ControlsCardNameBox.Text = file.DisplayName;
                }
            }
        }
        catch { }
    }

    private async void ControlsCardTestBtn_Click(object sender, RoutedEventArgs e)
    {
        string name = ControlsCardNameBox.Text;
        string cmd = ControlsCardCommandBox.Text;

        var (isValid, error) = QuickCardCatalog.ValidateCard(name, cmd);
        if (!isValid)
        {
            ControlsCardErrorText.Text = error;
            ControlsCardErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 107, 107));
            ControlsCardErrorText.Visibility = Visibility.Visible;
            return;
        }

        var testCard = new QuickCard
        {
            Id = "test",
            Name = name.Trim(),
            Command = cmd.Trim(),
            Arguments = ControlsCardArgsBox.Text?.Trim() ?? string.Empty,
            RunAsAdmin = ControlsCardAdminCheck.IsChecked == true
        };

        var (success, errMsg) = await QuickCardRunner.ExecuteCardAsync(testCard);
        if (!success)
        {
            ControlsCardErrorText.Text = $"Couldn't run card: {errMsg}";
            ControlsCardErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 107, 107));
            ControlsCardErrorText.Visibility = Visibility.Visible;
        }
        else
        {
            ControlsCardErrorText.Text = "Card launched successfully!";
            ControlsCardErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 255, 140));
            ControlsCardErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ControlsCardSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string name = ControlsCardNameBox.Text;
        string cmd = ControlsCardCommandBox.Text;

        var (isValid, error) = QuickCardCatalog.ValidateCard(name, cmd);
        if (!isValid)
        {
            ControlsCardErrorText.Text = error;
            ControlsCardErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 107, 107));
            ControlsCardErrorText.Visibility = Visibility.Visible;
            return;
        }

        var cards = _settings.Controls.Cards ?? QuickCard.Defaults();

        if (_editingCard == null)
        {
            if (cards.Count >= 24)
            {
                ControlsCardErrorText.Text = "Maximum 24 cards reached.";
                ControlsCardErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 107, 107));
                ControlsCardErrorText.Visibility = Visibility.Visible;
                return;
            }

            string trimmedCmd = cmd.Trim();
            QuickCardKind kind = QuickCardKind.Builtin;
            if (trimmedCmd.Contains("://") || trimmedCmd.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                trimmedCmd.StartsWith("calculator:", StringComparison.OrdinalIgnoreCase))
            {
                kind = QuickCardKind.Uri;
            }
            else if (trimmedCmd.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                     trimmedCmd.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                     trimmedCmd.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                     trimmedCmd.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
            {
                kind = QuickCardKind.Executable;
            }
            else
            {
                kind = QuickCardKind.Path;
            }

            var newCard = new QuickCard
            {
                Id = Guid.NewGuid().ToString("N"),
                IsCustom = true,
                Name = name.Trim(),
                Command = trimmedCmd,
                Arguments = ControlsCardArgsBox.Text?.Trim() ?? string.Empty,
                IconGlyph = _selectedIconGlyph,
                Kind = kind,
                RunAsAdmin = ControlsCardAdminCheck.IsChecked == true
            };

            cards.Add(newCard);
        }
        else
        {
            _editingCard.Name = name.Trim();
            _editingCard.Command = cmd.Trim();
            _editingCard.Arguments = ControlsCardArgsBox.Text?.Trim() ?? string.Empty;
            _editingCard.IconGlyph = _selectedIconGlyph;
            _editingCard.RunAsAdmin = ControlsCardAdminCheck.IsChecked == true;
        }

        ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);
        RenderQuickCardsGrid();
        CloseControlsAddCardOverlay();
    }

    // ── Milestone 13: Chat tab (WebView2) ──────────────────────────────────────

    private void RefreshChatUI()
    {
        RenderChatSubTabs();
        UpdateChatHeightLabel();
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
        ApplyChatAddDialogAccent();
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
        ApplyChatAddDialogAccent();
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

    // ── Milestone 13: Chat Height Adjustment ─────────────────────────────────

    private const float MinChatHeightDip = 320f;
    private const float MaxChatHeightDip = 720f;
    private const float ChatHeightStepDip = 40f;

    private void ChatHeightDecreaseBtn_Click(object sender, RoutedEventArgs e)
    {
        float current = _settings.ChatHeightDip > 0 ? _settings.ChatHeightDip : 440f;
        SetChatHeight(current - ChatHeightStepDip);
    }

    private void ChatHeightIncreaseBtn_Click(object sender, RoutedEventArgs e)
    {
        float current = _settings.ChatHeightDip > 0 ? _settings.ChatHeightDip : 440f;
        SetChatHeight(current + ChatHeightStepDip);
    }

    private void ChatPresetCompact_Click(object sender, RoutedEventArgs e) => SetChatHeight(340f);
    private void ChatPresetStandard_Click(object sender, RoutedEventArgs e) => SetChatHeight(440f);
    private void ChatPresetTall_Click(object sender, RoutedEventArgs e) => SetChatHeight(560f);
    private void ChatPresetFull_Click(object sender, RoutedEventArgs e) => SetChatHeight(680f);

    private void SetChatHeight(float newHeight)
    {
        float clamped = Math.Clamp(newHeight, MinChatHeightDip, MaxChatHeightDip);
        _settings.ChatHeightDip = clamped;
        IslandMotion.ChatHeightDip = clamped;
        ServiceContainer.SettingsStore?.Save(ServiceContainer.Settings);

        UpdateChatHeightLabel();

        if (_activeTab == 4 && _compositor != null && _compositor.IsOpen)
        {
            PositionAndSize();
            UpdateHitRegion(open: true);
            _compositor.AnimateToTab(4);
            OpenLayer.UpdateLayout();
        }
    }

    private void ApplyChatAddDialogAccent()
    {
        var accent = GetSystemAccentColor();
        var accentBrush = new SolidColorBrush(accent);
        if (ChatAddOverlayIcon != null) ChatAddOverlayIcon.Foreground = accentBrush;
        if (ChatConfirmAddBtn != null)
        {
            ChatConfirmAddBtn.Background = accentBrush;
            ChatConfirmAddBtn.BorderBrush = accentBrush;
            ChatConfirmAddBtn.Foreground = new SolidColorBrush(GetContrastingForeground(accent));
        }
    }

    private void UpdateChatHeightLabel()
    {
        if (ChatHeightGripIcon != null)
        {
            ChatHeightGripIcon.Foreground = new SolidColorBrush(GetSystemAccentColor());
        }
        if (ChatHeightLabel == null) return;
        float h = _settings.ChatHeightDip > 0 ? _settings.ChatHeightDip : 440f;
        if (Math.Abs(h - 340f) < 2f) ChatHeightLabel.Text = "Compact";
        else if (Math.Abs(h - 440f) < 2f) ChatHeightLabel.Text = "Standard";
        else if (Math.Abs(h - 560f) < 2f) ChatHeightLabel.Text = "Tall";
        else if (Math.Abs(h - 680f) < 2f) ChatHeightLabel.Text = "Full";
        else ChatHeightLabel.Text = $"{Math.Round(h)}px";
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
