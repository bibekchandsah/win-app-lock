using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using AppLock.Services;
using AppLock.Services.VolumeMixer;
using AppLock.Models;
using AppLock.ViewModels;
using Windows.Graphics;
using Windows.Media.Control;

namespace AppLock.Views;

public sealed partial class VolumeOverlayWindow : Window
{
    private enum OverlayState
    {
        Idle,
        Expanded,
        MixerOpen
    }

    private readonly AudioMixerService _audioService;
    private readonly AppIconResolver _iconResolver;
    private readonly Services.Camera.CameraBrightnessService _cameraService;
    private readonly Services.Audio.MicrophoneVolumeService _microphoneService;
    private readonly Services.Audio.SpeakerVolumeService _speakerService;
    private bool _isUpdatingCameraBrightness; // Flag to prevent feedback loops
    private DispatcherQueueTimer? _cameraSyncTimer; // Mirrors camera brightness set outside the overlay
    private bool _isUpdatingMicrophoneVolume; // Flag to prevent feedback loops
    private OverlayState _state = OverlayState.Idle;
    private DispatcherQueueTimer? _hoverDelayTimer;
    private DispatcherQueueTimer? _hideDelayTimer;
    private DispatcherQueueTimer? _pollingTimer;
    private DispatcherQueueTimer? _topmostTimer;  // Periodically re-enforce topmost z-order
    private bool _isPointerOverBar;
    private bool _isPointerOverPanel;
    private bool _isUpdatingSlider; // Flag to prevent feedback loops

    // Window-level translucency — idle opacity is applied as layered-window alpha so the desktop
    // behind the overlay is visible instead of the card fading against an opaque black surface.
    private DispatcherQueueTimer? _opacityTimer;
    private bool _layeredAlphaReady;
    private double _overlayOpacity = 1.0;

    // Smooth drag tracking using screen coordinates (identical to ClipboardPopupWindow)
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartWindowPos;
    private int _dragStartWidth;
    private int _dragStartHeight;
    private int _lastMoveX = int.MinValue;
    private int _lastMoveY = int.MinValue;

    // Session tracking — ViewModel, session IDs, the row Grid, and the play/pause FontIcon for status sync
    private readonly Dictionary<string, (MixerSessionViewModel ViewModel, List<string> SessionIds, Grid Row, FontIcon PlayIcon)> _appSessions = new();

    // Microphone tracking — deviceId → row controls
    private readonly Dictionary<string, MicrophoneRowUi> _microphoneRows = new();

    // Playback device rows (shown when more than one speaker is connected)
    private readonly Dictionary<string, MicrophoneRowUi> _speakerRows = new();
    private int _speakerRowCount;

    // While a "set default device" call is in flight the highlight is already optimistic, so the
    // poll must not overwrite it with the old Windows value and make it flicker.
    private string? _pendingDefaultDeviceId;
    private DateTime _pendingDefaultUntil = DateTime.MinValue;

    // ── Smoothness helpers ───────────────────────────────────────────────────
    // The overlay polls a few times a second; everything below keeps that poll cheap and keeps it
    // from fighting whatever the user is currently dragging.
    private static readonly bool VerboseLog = false;
    private const int SessionSnapshotIntervalMs = 400;   // full per-app audio snapshot cadence
    private const int DeviceVolumeWriteIntervalMs = 60;  // coalesced device volume write cadence
    private readonly Dictionary<int, string> _processNameCache = new();
    private readonly Dictionary<string, DateTime> _userVolumeChangeAt = new();
    private readonly Dictionary<string, float> _pendingDeviceVolume = new();
    private readonly Dictionary<string, float> _pendingSessionVolume = new();
    private float? _pendingMasterVolume;
    private DispatcherQueueTimer? _volumeWriteTimer;
    private DateTime _lastMasterSliderChange = DateTime.MinValue;
    private DateTime _lastActiveAppSliderChange = DateTime.MinValue;
    private DateTime _lastSessionSnapshot = DateTime.MinValue;
    private DateTime _lastBrightnessUserChange = DateTime.MinValue;
    private int _lastBrightnessSet = -1;
    private DateTime _lastSmtcSync = DateTime.MinValue;
    private int _lastActiveAppPercent = -1;
    private bool _lastActiveAppMuted;
    private string _lastActiveAppName = string.Empty;
    private bool _activeAppRowShown;
    private int _lastAppRowCount = -1;
    private string? _activeAppNameKey;
    private string _activeAppName = string.Empty;

    // Screen brightness row (monitor brightness, independent of the camera brightness row)
    private MicrophoneRowUi? _brightnessRow;
    private int _brightnessRowCount;
    private DateTime _lastBrightnessSync = DateTime.MinValue;
    private DateTime _lastDefaultFlagsCheck = DateTime.MinValue;
    private DateTime _lastDeviceValueSync = DateTime.MinValue;
    private bool _isSyncingBrightness;

    // Last time the capture device list was re-enumerated (default device / hot-plug sync)
    private DateTime _lastMicrophoneScan = DateTime.MinValue;
    private DateTime _lastSpeakerScan = DateTime.MinValue;
    // Device enumeration creates MMDevice wrappers on the UI thread, so it is deliberately slow;
    // hot-plug does not need to be detected within two seconds.
    private const int MicrophoneScanIntervalMs = 4000;

    /// <summary>
    /// Controls of one device row (speaker or microphone): the left button selects the device as
    /// the Windows default, the right-hand button mutes/unmutes it.
    /// </summary>
    private sealed class MicrophoneRowUi
    {
        public string DeviceId = null!;
        public bool IsSpeaker;
        public Button IconButton = null!;
        public Grid ControlsGrid = null!;
        public Slider Slider = null!;
        public TextBlock PercentText = null!;
        public Button MuteButton = null!;
    }

    // Win32 imports for window styling — use Ptr variants for 64-bit safety
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    // SetWindowPos for z-order enforcement
    private static readonly nint HWND_TOPMOST = new nint(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOREDRAW = 0x0008; // Prevents flicker during drag
    private const uint SWP_NOCOPYBITS = 0x0100; // Don't preserve content
    private const uint SWP_NOZORDER = 0x0004; // Don't change z-order
    private const uint SWP_ASYNCWINDOWPOS = 0x4000; // Non-blocking updates
    private const uint SWP_FRAMECHANGED = 0x0020; // Applies style changes to the frame

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // DWM window corner preference & transparency interop
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_DONOTROUND = 1;
    private const uint DWMWCP_ROUND = 2;

    // DWM non-client rendering policy — set to DISABLED (1) to suppress outer shadow/glow
    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMNCRP_DISABLED = 1;

    // DWM window attributes for shadow control
    private const uint DWMWA_ALLOW_NCPAINT = 4;

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
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fEnable;
        public nint hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fTransitionOnMaximized;
    }

    private const uint DWM_BB_ENABLE = 0x00000001;
    private const uint DWM_BB_BLURREGION = 0x00000002;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(nint hWnd, ref DWM_BLURBEHIND pBlurBehind);
    
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, IntPtr lpRect, bool bErase);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint hrgnDest, nint hrgnSrc1, nint hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // WS_EX_LAYERED + UpdateLayeredWindow for true per-pixel alpha (no DWM glow)
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint LWA_COLORKEY = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private const int RGN_OR = 2;

    // Smooth size animation state
    private DispatcherQueueTimer? _sizeAnimationTimer;
    private int _animStartW;
    private int _animTargetW;
    private int _animStartH;
    private int _animTargetH;
    private DateTime _animStartTime;
    private int _animDurationMs;
    private bool _isExpanding;
    private int? _customAnchorCenterX;
    private int? _customAnchorY;
    private bool _customAnchorRight;
    private const int EdgeSnapThreshold = 24; // px: drop distance that still snaps flush to an edge

    // "Show only active apps": session id → last time it was producing audio
    private readonly Dictionary<string, DateTime> _lastAudible = new();
    private const double ActiveAppGraceSeconds = 2.5;

    // Media key via WM_APPCOMMAND
    private const int WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    private const int FAPPCOMMAND_OEM = 0x1000;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(nint hWnd, int Msg, nint wParam, nint lParam);

    // Win32 foreground window tracking
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    // Active application session tracking
    private string? _activeAppProcessName;
    private string? _activeAppDisplayName;
    private readonly List<string> _activeAppSessionIds = new();
    private bool _isUpdatingActiveAppSlider;
    private int _lastForegroundProcessId;
    private nint _foregroundEventHook;
    private WinEventDelegate? _foregroundEventDelegate;
    private bool _hasActiveAppRow;
    private string? _loadedActiveAppIconKey;

    // Camera brightness state
    private bool _hasCameraRow;
    
    // Microphone volume state
    private int _microphoneRowCount = 0;

    // SMTC session cache: processName (no .exe) → SMTC session
    private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _smtcSessions = new();

    public VolumeOverlayWindow(AudioMixerService audioService)
    {
        try
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _iconResolver = new AppIconResolver();
            _cameraService = new Services.Camera.CameraBrightnessService();
            _microphoneService = new Services.Audio.MicrophoneVolumeService();
            _speakerService = new Services.Audio.SpeakerVolumeService();

            // Suppress slider ValueChanged from XAML default Value="50" writing to the audio device
            _isUpdatingSlider = true;
            _isUpdatingCameraBrightness = true;
            _isUpdatingMicrophoneVolume = true;
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling InitializeComponent...");
            this.InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] InitializeComponent complete");
            _isUpdatingSlider = false;
            _isUpdatingCameraBrightness = false;
            _isUpdatingMicrophoneVolume = false;
            
            // Setup clipping for rounded corners on mixer panels
            SetupMixerPanelClipping();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling SetupWindow...");
            SetupWindow();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling SetupAudioEventHandlers...");
            SetupAudioEventHandlers();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling ApplySettings...");
            ApplySettings();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling ApplyTheme...");
            ApplyTheme();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling PositionWindow...");
            PositionWindow();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling UpdateMasterVolumeFromAudio...");
            // Initialize with current audio state
            UpdateMasterVolumeFromAudio();

            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Starting topmost enforcement timer...");
            // Start topmost enforcement timer (every 2 seconds)
            _topmostTimer = DispatcherQueue.CreateTimer();
            _topmostTimer.Interval = TimeSpan.FromSeconds(2);
            _topmostTimer.IsRepeating = true;
            _topmostTimer.Tick += (s, e) => EnsureTopmost();
            _topmostTimer.Start();

            // Initialize SMTC session manager for accurate per-app play/pause
            _ = InitSmtcAsync();

            // Setup foreground window tracking
            _foregroundEventDelegate = OnForegroundEvent;
            _foregroundEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                nint.Zero, _foregroundEventDelegate,
                0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            CaptureForegroundWindow();
            
            // Initialize camera brightness service
            _ = InitializeCameraAsync();

            // Initialize microphone volume service (ApplySettings may already have built the rows;
            // this rebuilds them from a fresh enumeration so startup state is always current)
            _ = InitializeMicrophoneAsync();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Constructor complete");

            // Note: DisplayArea.Changed event not available in this SDK version
            // Multi-monitor repositioning would require polling or manual refresh
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Constructor error: {ex}");
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private void SetupMixerPanelClipping()
    {
        // In WinUI 3, Border.CornerRadius clips the background but not child content.
        // We apply a Clip geometry that tracks the panel size to prevent child overflow.
        void SetupClip(Border border)
        {
            border.SizeChanged += (s, e) =>
            {
                // RectangleGeometry without radius still clips content to bounds.
                // The visual rounding comes from the Border's own CornerRadius painting.
                border.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
                };
            };
        }

        SetupClip(MixerPanelAbove);
        SetupClip(MixerPanelBelow);
        SetupClip(CardBorder);
    }

    private void SetupWindow()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] SetupWindow: Getting AppWindow...");
            var appWindow = AppWindow;

            if (appWindow != null)
            {
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] SetupWindow: AppWindow found, configuring...");
                // Hide from taskbar and task switcher
                appWindow.IsShownInSwitchers = false;

                // Use OverlappedPresenter for borderless topmost window
                var presenter = OverlappedPresenter.Create();
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                appWindow.SetPresenter(presenter);
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] SetupWindow: Presenter configured");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] SetupWindow: WARNING - AppWindow is null!");
            }

            // Remove title bar
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] SetupWindow: Extending content into title bar...");
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);

            // NOTE: WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW are applied AFTER Activate() is called
            // (see ApplyNoActivateStyle). Applying them before the first Activate() prevents the
            // compositor from painting the initial frame, making the window invisible.

            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Window setup complete");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Window setup error: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Applies WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW after the first Activate() call so the
    /// compositor has already presented the first frame and the window is visible.
    /// Also enforces HWND_TOPMOST z-order.
    /// </summary>
    public void ApplyNoActivateStyle()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd != 0)
            {
                // Enable DWM full-frame alpha transparency (removes black backing/corners completely)
                EnableTransparency(hWnd);

                // Log current window state
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ApplyNoActivateStyle called. HWND: {hWnd}");
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Window Visible: {this.Visible}, Content: {this.Content != null}");
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] RootGrid Visibility: {RootGrid.Visibility}, Opacity: {RootGrid.Opacity}");
                
                nint exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Current ExStyle: 0x{exStyle:X}");
                
                // NOTE: Removed WS_EX_TOOLWINDOW as it can make the window invisible in some scenarios
                // We only need WS_EX_NOACTIVATE (prevents focus steal) and WS_EX_TOPMOST (always on top).
                exStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] New ExStyle: 0x{exStyle:X}");

                // Enable window-wide translucency (WS_EX_LAYERED + LWA_ALPHA) so the configured
                // idle opacity really blends with the desktop instead of darkening the card against
                // the opaque window surface. A layered window must have its alpha set before it is
                // shown, so this runs before the SWP_SHOWWINDOW below.
                _layeredAlphaReady = true;
                ApplyWindowAlpha(_overlayOpacity);

                // Force window to the top of the z-order without activating it
                bool result = SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, 
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] SetWindowPos result: {result}, LastError: {Marshal.GetLastWin32Error()}");
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] NoActivate style and topmost z-order applied");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] ApplyNoActivateStyle: HWND is null!");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ApplyNoActivateStyle error: {ex}");
        }
    }

    /// <summary>
    /// Enables true transparent window rendering: disables DWM NC shadow/glow, extends glass frame
    /// fully into client area, and uses per-pixel alpha so XAML transparent areas show the desktop
    /// without any black backing, outer glow, or colored border artifact.
    /// </summary>
    private void EnableTransparency(nint hWnd)
    {
        try
        {
            if (hWnd == 0) return;

            // 1. Disable DWM non-client rendering entirely — this removes the outer glow/shadow
            //    that DWM adds around borderless windows (the purple/pink glow seen in screenshots).
            uint ncPolicy = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(uint));

            // 2. Extend the DWM glass frame into the ENTIRE client area (-1 = sheet of glass).
            //    This makes DWM composite the XAML DirectComposition tree with per-pixel alpha
            //    directly against the desktop, so transparent XAML pixels are truly see-through.
            MARGINS margins = new MARGINS(-1);
            DwmExtendFrameIntoClientArea(hWnd, ref margins);

            // 3. Suppress blur-behind with an offscreen empty region so DWM doesn't add
            //    any blurred backdrop that would appear as a tinted rectangle.
            nint hRgn = CreateRectRgn(-2, -2, -1, -1);
            DWM_BLURBEHIND blurBehind = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
                fEnable = true,
                hRgnBlur = hRgn
            };
            DwmEnableBlurBehindWindow(hWnd, ref blurBehind);
            DeleteObject(hRgn);

            // 4. Prevent DWM from rounding corners — sharp geometry so our XAML CornerRadius
            //    is the only rounding applied (no double-rounding artifact).
            uint cornerPreference = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(uint));

            // 5. Clear any GDI window region so DirectComposition can render true per-pixel alpha.
            //    A set GDI region clips DComp and prevents transparency on transparent pixels.
            SetWindowRgn(hWnd, nint.Zero, true);

            // 6. WinUI 3 paints an opaque (theme-colored) surface for the window itself, so the
            //    transparent XAML corners still show a black square corner behind the rounded card.
            //    Clip the window to the card's rounded rectangle so the outer edge is rounded too.
            ApplyRoundedWindowRegion(hWnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] EnableTransparency error: {ex}");
        }
    }

    /// <summary>
    /// Clips the window (HWND) to the same rounded rectangle as the XAML card. Without this the
    /// opaque WinUI window background shows square black corners around the card. The radius is
    /// read from the live CardBorder.CornerRadius so it follows the idle circle, the expanded
    /// pill and the size animation.
    /// </summary>
    private void ApplyRoundedWindowRegion(nint hWnd)
    {
        try
        {
            if (hWnd == 0 || !GetWindowRect(hWnd, out RECT rect)) return;

            int width  = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            double cornerRadius = CardBorder?.CornerRadius.TopLeft ?? 22.0;
            uint dpi = GetDpiForWindow(hWnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            // CreateRoundRectRgn takes the corner ellipse width/height, i.e. the diameter.
            int diameter = (int)Math.Round(cornerRadius * 2 * scale);
            int maxDiameter = Math.Min(width, height);
            if (diameter > maxDiameter) diameter = maxDiameter;
            if (diameter < 2) diameter = 2;

            nint hRgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter + 1, diameter + 1);
            if (hRgn == nint.Zero) return;

            if (SetWindowRgn(hWnd, hRgn, true) == 0)
            {
                // On failure the caller still owns the region, so release it.
                DeleteObject(hRgn);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ApplyRoundedWindowRegion error: {ex.Message}");
        }
    }

    private void UpdateWindowRegion(int width, int height)
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == 0) return;
            EnableTransparency(hWnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] UpdateWindowRegion error: {ex}");
        }
    }

    // ── Camera Brightness ──────────────────────────────────────────────────────

    private async Task InitializeCameraAsync()
    {
        try
        {
            // Check if user wants to show camera brightness
            var settings = ServiceContainer.Settings.VolumeMixer;
            if (!settings.ShowCameraBrightness)
            {
                Debug.WriteLine("[VolumeMixer] Camera brightness disabled in settings");
                _hasCameraRow = false;
                CameraIconHost.Visibility = Visibility.Collapsed;
                CameraBrightnessSlider.Visibility = Visibility.Collapsed;
                CameraBrightnessPercentText.Visibility = Visibility.Collapsed;
                CameraBrightnessPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }

            Debug.WriteLine("[VolumeMixer] Initializing camera brightness control...");
            bool success = await _cameraService.InitializeAsync();
            
            Debug.WriteLine($"[VolumeMixer] Camera init result: {success}");
            Debug.WriteLine($"[VolumeMixer] Supports brightness: {_cameraService.SupportsBrightness}");
            
            if (success && _cameraService.SupportsBrightness)
            {
                Debug.WriteLine($"[VolumeMixer] Camera brightness control available");
                Debug.WriteLine($"[VolumeMixer] Range: {_cameraService.MinBrightness} - {_cameraService.MaxBrightness}");
                
                // Seed the slider from the camera's actual brightness (the value Windows Settings
                // shows), so the overlay starts in sync instead of at the XAML default.
                var currentBrightness = _cameraService.CurrentBrightness;
                if (!currentBrightness.HasValue)
                {
                    Debug.WriteLine("[VolumeMixer] CurrentBrightness returned null - retrying in manual mode");
                    _cameraService.SetAutoBrightness(false);
                    currentBrightness = _cameraService.CurrentBrightness;
                }

                ApplyCameraBrightnessToSlider(currentBrightness);
                Debug.WriteLine($"[VolumeMixer] Initial camera brightness: {currentBrightness}");

                // Subscribe to brightness changes
                _cameraService.BrightnessChanged += OnCameraBrightnessChanged;

                // Keep following changes made outside the overlay (Windows Settings > Cameras)
                StartCameraBrightnessSync();
                
                // Show camera controls
                CameraIconHost.Visibility = Visibility.Visible;
                CameraBrightnessSlider.Visibility = Visibility.Visible;
                CameraBrightnessPercentText.Visibility = Visibility.Visible;
                CameraBrightnessPlaceholder.Visibility = Visibility.Visible;
                
                // Mark that we have a camera row
                _hasCameraRow = true;
            }
            else
            {
                Debug.WriteLine("[VolumeMixer] Camera brightness control not available");
                // Hide camera controls if not supported
                CameraIconHost.Visibility = Visibility.Collapsed;
                CameraBrightnessSlider.Visibility = Visibility.Collapsed;
                CameraBrightnessPercentText.Visibility = Visibility.Collapsed;
                CameraBrightnessPlaceholder.Visibility = Visibility.Collapsed;
                
                // Mark that we don't have a camera row
                _hasCameraRow = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] Camera initialization error: {ex.Message}");
            Debug.WriteLine($"[VolumeMixer] Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Pushes a camera brightness value (0-100) into the overlay controls without writing it back
    /// to the camera.
    /// </summary>
    private void ApplyCameraBrightnessToSlider(double? brightness)
    {
        if (!brightness.HasValue || CameraBrightnessSlider == null)
            return;

        double normalizedValue = MapCameraBrightnessToSlider(brightness.Value);

        _isUpdatingCameraBrightness = true;
        try
        {
            CameraBrightnessSlider.Value = normalizedValue;
            if (CameraBrightnessPercentText != null)
                CameraBrightnessPercentText.Text = $"{(int)normalizedValue}%";
        }
        finally
        {
            _isUpdatingCameraBrightness = false;
        }
    }

    /// <summary>
    /// Re-reads the camera's brightness and mirrors it in the slider, so changes made in Windows
    /// Settings > Bluetooth &amp; devices > Cameras are reflected while the overlay is open.
    /// </summary>
    private void RefreshCameraBrightnessFromDevice()
    {
        if (_isUpdatingCameraBrightness || !_cameraService.IsAvailable)
            return;

        var current = _cameraService.CurrentBrightness;
        if (!current.HasValue)
            return;

        double normalizedValue = MapCameraBrightnessToSlider(current.Value);
        if (Math.Abs(normalizedValue - CameraBrightnessSlider.Value) < 0.5)
            return;

        ApplyCameraBrightnessToSlider(current.Value);
        Debug.WriteLine($"[VolumeMixer] Camera brightness synced from device: {(int)normalizedValue}%");
    }

    private void StartCameraBrightnessSync()
    {
        _cameraSyncTimer?.Stop();
        _cameraSyncTimer = DispatcherQueue.CreateTimer();
        _cameraSyncTimer.Interval = TimeSpan.FromSeconds(1);
        _cameraSyncTimer.IsRepeating = true;
        _cameraSyncTimer.Tick += (s, e) =>
        {
            // Only while the overlay is showing its controls
            if (_state == OverlayState.Idle)
                return;

            RefreshCameraBrightnessFromDevice();
        };
        _cameraSyncTimer.Start();
    }

    private void OnCameraBrightnessChanged(object? sender, double brightness)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_isUpdatingCameraBrightness)
                    return;

                _isUpdatingCameraBrightness = true;
                double normalizedValue = MapCameraBrightnessToSlider(brightness);
                CameraBrightnessSlider.Value = normalizedValue;
                CameraBrightnessPercentText.Text = $"{(int)normalizedValue}%";
                _isUpdatingCameraBrightness = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeMixer] OnCameraBrightnessChanged error: {ex.Message}");
                _isUpdatingCameraBrightness = false;
            }
        });
    }

    private double MapCameraBrightnessToSlider(double cameraValue)
    {
        // Camera now uses 0-100 range, same as slider
        return cameraValue;
    }

    private double MapSliderToCameraBrightness(double sliderValue)
    {
        // Camera now uses 0-100 range, same as slider
        return sliderValue;
    }

    private void CameraBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingCameraBrightness)
            return;

        try
        {
            double sliderValue = e.NewValue;
            CameraBrightnessPercentText.Text = $"{(int)sliderValue}%";
            
            // Map slider value to camera brightness range and apply
            double cameraValue = MapSliderToCameraBrightness(sliderValue);
            _cameraService.SetBrightness(cameraValue);
            
            Debug.WriteLine($"[VolumeMixer] Camera brightness set to {(int)sliderValue}% (raw: {cameraValue})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] CameraBrightnessSlider_ValueChanged error: {ex.Message}");
        }
    }

    private void CameraBrightnessSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
            var step = delta > 0 ? 5 : -5; // 5% increments
            var newValue = Math.Clamp(CameraBrightnessSlider.Value + step, 0, 100);
            CameraBrightnessSlider.Value = newValue;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] CameraBrightnessSlider_PointerWheelChanged error: {ex.Message}");
        }
    }

    // ── Microphone Volume ───────────────────────────────────────────────────────

    private async Task InitializeMicrophoneAsync()
    {
        try
        {
            // Check if user wants to show microphone volume
            _speakerService.Initialize();
            _lastSpeakerScan = DateTime.UtcNow;

            var settings = ServiceContainer.Settings.VolumeMixer;
            if (!settings.ShowMicrophoneVolume)
            {
                Debug.WriteLine("[VolumeMixer] Microphone volume disabled in settings");
                RebuildMicrophoneRows();
                return;
            }

            Debug.WriteLine("[VolumeMixer] Initializing microphone volume controls...");
            bool success = _microphoneService.Initialize();
            _lastMicrophoneScan = DateTime.UtcNow;

            Debug.WriteLine($"[VolumeMixer] Microphone init result: {success}");

            RebuildMicrophoneRows();
            Debug.WriteLine("[VolumeMixer] Device rows created: " +
                $"{_speakerRowCount} speaker(s), {_microphoneRowCount} microphone(s)");

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] Microphone initialization error: {ex.Message}");
            Debug.WriteLine($"[VolumeMixer] Stack trace: {ex.StackTrace}");
            ClearMicrophoneRows();
        }
    }

    private void CreateMicrophoneRow(Services.Audio.MicrophoneDevice mic)
    {
        try
        {
            // Create icon button for idle view (Column 0 only)
            var iconButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = mic.DeviceId
            };

            var icon = new FontIcon
            {
                // E720 = Microphone. This button selects the default input device, so it always
                // shows a plain mic; mute state lives on the right-hand button.
                Glyph = "\uE720",
                FontSize = 16,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };

            iconButton.Content = icon;
            iconButton.Click += (s, e) =>
            {
                var deviceId = (string)((Button)s).Tag;
                SetDefaultMicrophone(deviceId);
            };

            // Create controls grid for expanded view
            var controlsGrid = new Grid
            {
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed,
                Opacity = 0
            };

            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Slider
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Percent
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Mute button

            // Slider - will be sized in ApplySettings
            var settings = ServiceContainer.Settings.VolumeMixer;
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = mic.GetVolume(),
                Width = settings.SliderWidth,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = mic.DeviceId
            };
            ToolTipService.SetToolTip(slider, mic.FriendlyName + (mic.IsDefault ? " (Default)" : ""));

            slider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingMicrophoneVolume) return;
                var deviceId = (string)((Slider)s).Tag;
                UpdateMicrophonePercent(deviceId, (int)Math.Round(e.NewValue));
                QueueDeviceVolumeChange(deviceId, (float)e.NewValue);
            };

            slider.PointerWheelChanged += (s, e) =>
            {
                try
                {
                    var delta = e.GetCurrentPoint(s as UIElement).Properties.MouseWheelDelta;
                    var step = delta > 0 ? 5 : -5;
                    var newValue = Math.Clamp(((Slider)s).Value + step, 0, 100);
                    ((Slider)s).Value = newValue;
                    e.Handled = true;
                }
                catch { }
            };

            Grid.SetColumn(slider, 0);
            controlsGrid.Children.Add(slider);

            // Percent text
            var percentText = new TextBlock
            {
                Text = $"{(int)mic.GetVolume()}%",
                FontSize = 12,
                MinWidth = 38,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            Grid.SetColumn(percentText, 1);
            controlsGrid.Children.Add(percentText);

            // Mute button
            var muteButton = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                CornerRadius = new CornerRadius(14),
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Tag = mic.DeviceId
            };
            ToolTipService.SetToolTip(muteButton, "Mute " + mic.FriendlyName);

            var muteIcon = new FontIcon
            {
                // E720 = Microphone, EC54 = Microphone Off (crossed out mic)
                Glyph = mic.IsMuted() ? "\uEC54" : "\uE720",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };

            muteButton.Content = muteIcon;
            muteButton.Click += (s, e) =>
            {
                var deviceId = (string)((Button)s).Tag;
                var device = _microphoneService.GetMicrophone(deviceId);
                if (device != null)
                {
                    bool muted = !device.IsMuted();
                    SetMicrophoneGlyph(deviceId, muted); // instant feedback
                    device.SetMute(muted);
                }
            };

            Grid.SetColumn(muteButton, 2);
            controlsGrid.Children.Add(muteButton);

            // Add to separate containers
            MicrophoneIconsContainer.Children.Add(iconButton);
            MicrophoneControlsContainer.Children.Add(controlsGrid);
            
            // Store references so polling can update this row in place
            _microphoneRows[mic.DeviceId] = new MicrophoneRowUi
            {
                DeviceId = mic.DeviceId,
                IconButton = iconButton,
                ControlsGrid = controlsGrid,
                Slider = slider,
                PercentText = percentText,
                MuteButton = muteButton,
            };
            
            Debug.WriteLine($"[VolumeMixer] Created row for: {mic.FriendlyName} ({(mic.IsDefault ? "Default" : "Secondary")})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] CreateMicrophoneRow error: {ex.Message}");
        }
    }

    private void CreateSpeakerRow(Services.Audio.SpeakerDevice speaker)
    {
        try
        {
            var iconButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = speaker.DeviceId
            };

            var icon = new FontIcon
            {
                // E767 = Volume, E74F = Muted. Left button: first click selects this speaker as the
                // Windows default, further clicks mute / unmute the selected speaker.
                Glyph = speaker.IsMuted() ? "\uE74F" : "\uE767",
                FontSize = 16,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };

            iconButton.Content = icon;
            iconButton.Click += (s, e) =>
            {
                var deviceId = (string)((Button)s).Tag;
                var device = _speakerService.GetSpeaker(deviceId);
                if (device == null) return;

                if (!device.IsDefault)
                {
                    SetDefaultSpeaker(deviceId);
                }
                else
                {
                    bool muted = !device.IsMuted();
                    SetSpeakerGlyphs(deviceId, muted); // instant feedback
                    device.SetMute(muted);
                }
            };

            var controlsGrid = new Grid
            {
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed,
                Opacity = 0
            };
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Slider
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Percent
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Mute button

            var settings = ServiceContainer.Settings.VolumeMixer;
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = speaker.GetVolume(),
                Width = settings.SliderWidth,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = speaker.DeviceId
            };
            ToolTipService.SetToolTip(slider, speaker.FriendlyName + (speaker.IsDefault ? " (Default)" : ""));

            slider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingMicrophoneVolume) return;
                var deviceId = (string)((Slider)s).Tag;
                UpdateMicrophonePercent(deviceId, (int)Math.Round(e.NewValue));
                QueueDeviceVolumeChange(deviceId, (float)e.NewValue);
            };

            slider.PointerWheelChanged += (s, e) =>
            {
                try
                {
                    var delta = e.GetCurrentPoint(s as UIElement).Properties.MouseWheelDelta;
                    var step = delta > 0 ? 5 : -5;
                    ((Slider)s).Value = Math.Clamp(((Slider)s).Value + step, 0, 100);
                    e.Handled = true;
                }
                catch { }
            };

            Grid.SetColumn(slider, 0);
            controlsGrid.Children.Add(slider);

            var percentText = new TextBlock
            {
                Text = $"{(int)speaker.GetVolume()}%",
                FontSize = 12,
                MinWidth = 38,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            Grid.SetColumn(percentText, 1);
            controlsGrid.Children.Add(percentText);

            var muteButton = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                CornerRadius = new CornerRadius(14),
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Tag = speaker.DeviceId
            };
            ToolTipService.SetToolTip(muteButton, "Mute " + speaker.FriendlyName);

            var muteIcon = new FontIcon
            {
                Glyph = speaker.IsMuted() ? "\uE74F" : "\uE767",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };

            muteButton.Content = muteIcon;
            muteButton.Click += (s, e) =>
            {
                var deviceId = (string)((Button)s).Tag;
                var device = _speakerService.GetSpeaker(deviceId);
                if (device != null)
                {
                    bool muted = !device.IsMuted();
                    SetSpeakerGlyphs(deviceId, muted); // instant feedback
                    device.SetMute(muted);
                }
            };

            Grid.SetColumn(muteButton, 2);
            controlsGrid.Children.Add(muteButton);

            MicrophoneIconsContainer.Children.Add(iconButton);
            MicrophoneControlsContainer.Children.Add(controlsGrid);

            _speakerRows[speaker.DeviceId] = new MicrophoneRowUi
            {
                DeviceId = speaker.DeviceId,
                IsSpeaker = true,
                IconButton = iconButton,
                ControlsGrid = controlsGrid,
                Slider = slider,
                PercentText = percentText,
                MuteButton = muteButton,
            };

            Debug.WriteLine($"[VolumeMixer] Created speaker row: {speaker.FriendlyName} ({(speaker.IsDefault ? "Default" : "Secondary")})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] CreateSpeakerRow error: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds the monitor brightness slider (icon in the narrow column, slider + percent in the
    /// expanded column) when the setting is on and a hardware brightness provider exists.
    /// </summary>
    private void CreateScreenBrightnessRow()
    {
        try
        {
            var settings = ServiceContainer.Settings.VolumeMixer;
            if (!settings.ShowScreenBrightness) return;
            if (!ServiceContainer.BrightnessService.IsAvailable) return;

            var iconButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(iconButton, "Screen brightness");
            iconButton.Content = new FontIcon
            {
                Glyph = "\uE706", // Brightness
                FontSize = 16,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };

            var controlsGrid = new Grid
            {
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed,
                Opacity = 0
            };
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                Width = settings.SliderWidth,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            ToolTipService.SetToolTip(slider, "Screen brightness");

            var percentText = new TextBlock
            {
                Text = "100%",
                FontSize = 12,
                MinWidth = 38,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            slider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingSlider) return;
                int percent = (int)Math.Round(e.NewValue);
                SetTextIfChanged(percentText, $"{percent}%");
                _lastBrightnessUserChange = DateTime.UtcNow;

                // Skip repeated identical writes while the thumb is being dragged.
                if (percent == _lastBrightnessSet) return;
                _lastBrightnessSet = percent;
                _ = ServiceContainer.BrightnessService.SetBrightnessAsync(percent);
            };

            slider.PointerWheelChanged += (s, e) =>
            {
                try
                {
                    var delta = e.GetCurrentPoint(s as UIElement).Properties.MouseWheelDelta;
                    var step = delta > 0 ? 5 : -5;
                    ((Slider)s).Value = Math.Clamp(((Slider)s).Value + step, 0, 100);
                    e.Handled = true;
                }
                catch { }
            };

            Grid.SetColumn(slider, 0);
            controlsGrid.Children.Add(slider);
            Grid.SetColumn(percentText, 1);
            controlsGrid.Children.Add(percentText);

            MicrophoneIconsContainer.Children.Add(iconButton);
            MicrophoneControlsContainer.Children.Add(controlsGrid);

            _brightnessRow = new MicrophoneRowUi
            {
                DeviceId = "screen-brightness",
                IconButton = iconButton,
                ControlsGrid = controlsGrid,
                Slider = slider,
                PercentText = percentText,
            };
            _brightnessRowCount = 1;

            _ = SyncScreenBrightnessAsync(force: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] CreateScreenBrightnessRow error: {ex.Message}");
        }
    }

    private async Task SyncScreenBrightnessAsync(bool force = false)
    {
        if (_brightnessRow == null || _isSyncingBrightness) return;
        if (!force && (DateTime.UtcNow - _lastBrightnessSync).TotalMilliseconds < 4000) return;

        // Don't snap the thumb back to a stale reading while the user is dragging.
        if (!force && (DateTime.UtcNow - _lastBrightnessUserChange).TotalMilliseconds < 1500) return;

        _isSyncingBrightness = true;
        _lastBrightnessSync = DateTime.UtcNow;
        try
        {
            int value = await ServiceContainer.BrightnessService.GetBrightnessAsync();
            ApplyScreenBrightnessToSlider(value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] SyncScreenBrightness error: {ex.Message}");
        }
        finally
        {
            _isSyncingBrightness = false;
        }
    }

    private void ApplyScreenBrightnessToSlider(int value)
    {
        if (_brightnessRow == null) return;

        value = Math.Clamp(value, 0, 100);
        _isUpdatingSlider = true;
        try { _brightnessRow.Slider.Value = value; }
        finally { _isUpdatingSlider = false; }

        SetTextIfChanged(_brightnessRow.PercentText, $"{value}%");
    }

    private void ClearMicrophoneRows()
    {
        MicrophoneIconsContainer.Children.Clear();
        MicrophoneControlsContainer.Children.Clear();
        _microphoneRows.Clear();
        _speakerRows.Clear();
        _brightnessRow = null;
        _microphoneRowCount = 0;
        _speakerRowCount = 0;
        _brightnessRowCount = 0;
        MicrophoneIconsContainer.Visibility = Visibility.Collapsed;
        MicrophoneControlsContainer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Recreates the device rows from the current device lists: speakers first (only when more than
    /// one is connected — a single speaker is already the master volume row), then microphones.
    /// </summary>
    private void RebuildMicrophoneRows()
    {
        ClearMicrophoneRows();

        var speakers = _speakerService.GetSpeakers();
        if (speakers.Count > 1)
        {
            foreach (var speaker in speakers)
            {
                CreateSpeakerRow(speaker);
            }
            _speakerRowCount = speakers.Count;
        }

        var microphones = ServiceContainer.Settings.VolumeMixer.ShowMicrophoneVolume
            ? _microphoneService.GetMicrophones()
            : (IReadOnlyList<Services.Audio.MicrophoneDevice>)Array.Empty<Services.Audio.MicrophoneDevice>();

        foreach (var mic in microphones)
        {
            CreateMicrophoneRow(mic);
        }
        _microphoneRowCount = microphones.Count;

        // Screen brightness comes last so the device rows stay grouped together
        CreateScreenBrightnessRow();

        bool hasRows = _speakerRowCount + _microphoneRowCount + _brightnessRowCount > 0;
        MicrophoneIconsContainer.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneControlsContainer.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;

        UpdateDeviceSelection();

        // Rows are created collapsed (idle view shows icons only); reveal their sliders right away
        // when the bar is expanded, otherwise new rows would sit misplaced until the next hover.
        if (_state is OverlayState.Expanded or OverlayState.MixerOpen)
        {
            ShowMicrophoneControls();
        }

        if (hasRows && _state != OverlayState.Idle)
        {
            ResizeWindowToContent();
        }
    }

    /// <summary>
    /// Marks the default devices: the selected speaker/microphone gets the accent background on its
    /// left-hand button so the active device is visible at a glance.
    /// </summary>
    private void UpdateDeviceSelection()
    {
        var accentBackground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var onAccent = ResourceBrush("TextOnAccentFillColorPrimaryBrush", accentBackground);
        var primary = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        foreach (var row in _speakerRows.Values)
        {
            try
            {
                var speaker = _speakerService.GetSpeaker(row.DeviceId);
                if (speaker == null) continue;

                bool isDefault = speaker.IsDefault;
                ApplySelectionStyle(row.IconButton, isDefault, accentBackground, onAccent, primary);
                ToolTipService.SetToolTip(row.IconButton, isDefault
                    ? $"{speaker.FriendlyName} — selected speaker (default). Click to mute or unmute."
                    : $"Set {speaker.FriendlyName} as the default speaker");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeMixer] UpdateDeviceSelection (speaker) error: {ex.Message}");
            }
        }

        foreach (var row in _microphoneRows.Values)
        {
            try
            {
                var mic = _microphoneService.GetMicrophone(row.DeviceId);
                if (mic == null) continue;

                bool isDefault = mic.IsDefault;
                ApplySelectionStyle(row.IconButton, isDefault, accentBackground, onAccent, primary);
                ToolTipService.SetToolTip(row.IconButton, isDefault
                    ? $"{mic.FriendlyName} — selected microphone (default)"
                    : $"Set {mic.FriendlyName} as the default microphone");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeMixer] UpdateDeviceSelection (microphone) error: {ex.Message}");
            }
        }
    }

    private static void ApplySelectionStyle(Button button, bool isSelected, Brush accentBackground, Brush onAccent, Brush primary)
    {
        button.Background = isSelected
            ? accentBackground
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        if (button.Content is FontIcon icon)
        {
            icon.Foreground = isSelected ? onAccent : primary;
        }
    }

    private static Brush ResourceBrush(string key, Brush fallback)
    {
        try { return (Brush)Application.Current.Resources[key]; }
        catch { return fallback; }
    }

    private void SetDefaultSpeaker(string deviceId)
    {
        try
        {
            // Optimistic: move the highlight right away so the click feels instant, then tell
            // Windows on a worker thread (the COM call can take a few hundred milliseconds).
            foreach (var speaker in _speakerService.GetSpeakers())
            {
                speaker.IsDefault = speaker.DeviceId == deviceId;
            }
            _pendingDefaultDeviceId = deviceId;
            _pendingDefaultUntil = DateTime.UtcNow.AddSeconds(3);
            UpdateDeviceSelection();

            _ = Task.Run(() =>
            {
                bool ok = _speakerService.SetDefaultSpeaker(deviceId);
                DispatcherQueue.TryEnqueue(() =>
                {
                    _pendingDefaultDeviceId = null;
                    _speakerService.RefreshDefaultFlags();
                    UpdateDeviceSelection();
                    Debug.WriteLine($"[VolumeMixer] Default speaker set: {deviceId} (ok={ok})");
                });
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] SetDefaultSpeaker error: {ex.Message}");
        }
    }

    /// <summary>Repaints a speaker row's icons immediately, before the device call completes.</summary>
    private void SetSpeakerGlyphs(string deviceId, bool muted)
    {
        if (!_speakerRows.TryGetValue(deviceId, out var row)) return;

        string glyph = muted ? "\uE74F" : "\uE767";
        if (row.MuteButton.Content is FontIcon muteIcon) muteIcon.Glyph = glyph;
        if (row.IconButton.Content is FontIcon leftIcon) leftIcon.Glyph = glyph;
    }

    /// <summary>Repaints a microphone row's mute icon immediately.</summary>
    private void SetMicrophoneGlyph(string deviceId, bool muted)
    {
        if (!_microphoneRows.TryGetValue(deviceId, out var row)) return;
        if (row.MuteButton.Content is FontIcon muteIcon)
        {
            muteIcon.Glyph = muted ? "\uEC54" : "\uE720";
        }
    }

    private void UpdateSpeakerIcon(string deviceId)
    {
        if (!_speakerRows.TryGetValue(deviceId, out var row)) return;

        try
        {
            var speaker = _speakerService.GetSpeaker(deviceId);
            if (speaker == null) return;

            string glyph = speaker.IsMuted() ? "\uE74F" : "\uE767";
            if (row.MuteButton?.Content is FontIcon muteIcon) muteIcon.Glyph = glyph;
            if (row.IconButton?.Content is FontIcon leftIcon) leftIcon.Glyph = glyph;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] UpdateSpeakerIcon error: {ex.Message}");
        }
    }

    private void SetDefaultMicrophone(string deviceId)
    {
        try
        {
            // Optimistic: highlight the new default immediately, then apply it off the UI thread.
            foreach (var mic in _microphoneService.GetMicrophones())
            {
                mic.IsDefault = mic.DeviceId == deviceId;
            }
            _pendingDefaultDeviceId = deviceId;
            _pendingDefaultUntil = DateTime.UtcNow.AddSeconds(3);
            UpdateDeviceSelection();

            _ = Task.Run(() =>
            {
                bool ok = _microphoneService.SetDefaultMicrophone(deviceId);
                DispatcherQueue.TryEnqueue(() =>
                {
                    _pendingDefaultDeviceId = null;
                    _microphoneService.RefreshDefaultFlags();
                    UpdateDeviceSelection();
                    Debug.WriteLine($"[VolumeMixer] Default microphone set: {deviceId} (ok={ok})");
                });
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] SetDefaultMicrophone error: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the microphone rows in step with the system: devices plugged/unplugged, the default
    /// device changed in Windows (or by another app), and volume/mute changed elsewhere.
    /// </summary>
    private void SyncMicrophones()
    {
        try
        {
            if ((DateTime.UtcNow - _lastSpeakerScan).TotalMilliseconds >= MicrophoneScanIntervalMs)
            {
                _lastSpeakerScan = DateTime.UtcNow;
                if (_speakerService.RefreshDevices())
                {
                    Debug.WriteLine("[VolumeMixer] Speaker devices changed - rebuilding rows");
                    RebuildMicrophoneRows();
                }
            }

            if (ServiceContainer.Settings.VolumeMixer.ShowMicrophoneVolume &&
                (DateTime.UtcNow - _lastMicrophoneScan).TotalMilliseconds >= MicrophoneScanIntervalMs)
            {
                _lastMicrophoneScan = DateTime.UtcNow;
                if (_microphoneService.RefreshDevices())
                {
                    Debug.WriteLine("[VolumeMixer] Microphone devices changed - rebuilding rows");
                    RebuildMicrophoneRows();
                }
            }

            // Selection changed in Windows (or by another app) — update highlights without a rebuild.
            // Skipped while our own set-default call is in flight, so the optimistic highlight holds.
            bool pendingSelection = _pendingDefaultDeviceId != null && DateTime.UtcNow < _pendingDefaultUntil;
            if (!pendingSelection &&
                (DateTime.UtcNow - _lastDefaultFlagsCheck).TotalMilliseconds >= 1000)
            {
                _lastDefaultFlagsCheck = DateTime.UtcNow;
                _pendingDefaultDeviceId = null;
                bool selectionChanged = _speakerService.RefreshDefaultFlags();
                if (ServiceContainer.Settings.VolumeMixer.ShowMicrophoneVolume)
                {
                    selectionChanged |= _microphoneService.RefreshDefaultFlags();
                }
                if (selectionChanged)
                {
                    UpdateDeviceSelection();
                }
            }

            // Device volume/mute reads are COM calls; 4 per second is live enough for an overlay.
            if ((DateTime.UtcNow - _lastDeviceValueSync).TotalMilliseconds >= 250)
            {
                _lastDeviceValueSync = DateTime.UtcNow;
                SyncSpeakerValues();
                SyncMicrophoneValues();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] SyncMicrophones error: {ex.Message}");
        }
    }

    /// <summary>Assigns a tooltip only when it differs — re-setting it on every poll disturbed the
    /// open tooltip (and its fade animation).</summary>
    private static void SetToolTipIfChanged(DependencyObject element, string text)
    {
        if (element == null) return;
        if (ToolTipService.GetToolTip(element) is string current && current == text) return;
        ToolTipService.SetToolTip(element, text);
    }

    /// <summary>
    /// Records the new volume and lets a short timer write it to the device. Dragging a slider
    /// raises dozens of changes per second; writing each one to the audio service made the thumb
    /// stutter. Only the latest value per device is written.
    /// </summary>
    private void QueueDeviceVolumeChange(string deviceId, float volume)
    {
        _userVolumeChangeAt[deviceId] = DateTime.UtcNow;
        _pendingDeviceVolume[deviceId] = Math.Clamp(volume, 0f, 100f);
        EnsureVolumeWriteTimer();
    }

    /// <summary>Coalesces master-slider writes the same way device and app sliders are coalesced.</summary>
    private void QueueMasterVolume(float scale)
    {
        _lastMasterSliderChange = DateTime.UtcNow;
        _pendingMasterVolume = Math.Clamp(scale, 0f, 1f);
        EnsureVolumeWriteTimer();
    }

    private void QueueSessionVolume(IEnumerable<string> sessionIds, float scale)
    {
        _lastActiveAppSliderChange = DateTime.UtcNow;
        scale = Math.Clamp(scale, 0f, 1f);
        foreach (var id in sessionIds) _pendingSessionVolume[id] = scale;
        EnsureVolumeWriteTimer();
    }

    private void EnsureVolumeWriteTimer()
    {
        if (_volumeWriteTimer == null)
        {
            _volumeWriteTimer = DispatcherQueue.CreateTimer();
            _volumeWriteTimer.Interval = TimeSpan.FromMilliseconds(DeviceVolumeWriteIntervalMs);
            _volumeWriteTimer.IsRepeating = true;
            _volumeWriteTimer.Tick += (s, e) => FlushDeviceVolumeChanges();
        }

        if (!_volumeWriteTimer.IsRunning) _volumeWriteTimer.Start();
    }

    private void FlushDeviceVolumeChanges()
    {
        if (_pendingDeviceVolume.Count == 0 && _pendingSessionVolume.Count == 0 && _pendingMasterVolume == null)
        {
            _volumeWriteTimer?.Stop();
            return;
        }

        var devicePending = _pendingDeviceVolume.ToArray();
        var sessionPending = _pendingSessionVolume.ToArray();
        var masterPending = _pendingMasterVolume;
        _pendingDeviceVolume.Clear();
        _pendingSessionVolume.Clear();
        _pendingMasterVolume = null;

        foreach (var (deviceId, volume) in devicePending)
        {
            try
            {
                var speaker = _speakerService.GetSpeaker(deviceId);
                if (speaker != null)
                {
                    speaker.SetVolume(volume);
                    continue;
                }

                _microphoneService.GetMicrophone(deviceId)?.SetVolume(volume);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeMixer] Flush volume error: {ex.Message}");
            }
        }

        try
        {
            foreach (var (sessionId, volume) in sessionPending)
            {
                _audioService?.SetSessionVolume(sessionId, volume);
            }

            if (masterPending.HasValue) _audioService?.SetMasterVolume(masterPending.Value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] Flush session volume error: {ex.Message}");
        }
    }

    private void SyncSpeakerValues()
    {
        foreach (var row in _speakerRows.Values)
        {
            try
            {
                // Collapsed rows don't need live values, and a row the user just dragged must not
                // be pushed back to a stale device reading (that made the thumb stutter).
                if (row.ControlsGrid?.Visibility == Visibility.Collapsed) continue;
                if (_userVolumeChangeAt.TryGetValue(row.DeviceId, out var changedAt) &&
                    (DateTime.UtcNow - changedAt).TotalMilliseconds < 1200) continue;

                var speaker = _speakerService.GetSpeaker(row.DeviceId);
                if (speaker == null) continue;

                float volume = speaker.GetVolume();
                if (Math.Abs(row.Slider.Value - volume) > 0.5)
                {
                    _isUpdatingMicrophoneVolume = true;
                    try { row.Slider.Value = volume; }
                    finally { _isUpdatingMicrophoneVolume = false; }
                }

                SetTextIfChanged(row.PercentText, $"{(int)Math.Round(volume)}%");

                string glyph = speaker.IsMuted() ? "\uE74F" : "\uE767";
                if (row.MuteButton.Content is FontIcon muteIcon && muteIcon.Glyph != glyph) muteIcon.Glyph = glyph;
                if (row.IconButton.Content is FontIcon leftIcon && leftIcon.Glyph != glyph) leftIcon.Glyph = glyph;
            }
            catch { }
        }
    }

    /// <summary>Assigns only when the text actually changed — repeated sets invalidate layout.</summary>
    private static void SetTextIfChanged(TextBlock block, string text)
    {
        if (block != null && block.Text != text) block.Text = text;
    }

    private void SyncMicrophoneValues()
    {
        foreach (var row in _microphoneRows.Values)
        {
            try
            {
                if (row.ControlsGrid?.Visibility == Visibility.Collapsed) continue;
                if (_userVolumeChangeAt.TryGetValue(row.DeviceId, out var changedAt) &&
                    (DateTime.UtcNow - changedAt).TotalMilliseconds < 1200) continue;

                var device = _microphoneService.GetMicrophone(row.DeviceId);
                if (device == null) continue;

                float volume = device.GetVolume();
                if (Math.Abs(row.Slider.Value - volume) > 0.5)
                {
                    _isUpdatingMicrophoneVolume = true;
                    try { row.Slider.Value = volume; }
                    finally { _isUpdatingMicrophoneVolume = false; }
                }

                SetTextIfChanged(row.PercentText, $"{(int)Math.Round(volume)}%");

                if (row.MuteButton.Content is FontIcon muteIcon)
                {
                    string glyph = device.IsMuted() ? "\uEC54" : "\uE720";
                    if (muteIcon.Glyph != glyph) muteIcon.Glyph = glyph;
                }
            }
            catch { }
        }
    }

    private void UpdateMicrophoneIcon(string deviceId)
    {
        if (!_microphoneRows.TryGetValue(deviceId, out var row))
            return;

        try
        {
            var device = _microphoneService.GetMicrophone(deviceId);
            if (device == null) return;

            // The left button shows selection, so only the right-hand mute button reflects mute.
            string glyph = device.IsMuted() ? "\uEC54" : "\uE720";

            if (row.MuteButton?.Content is FontIcon muteFontIcon)
            {
                muteFontIcon.Glyph = glyph;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] UpdateMicrophoneIcon error: {ex.Message}");
        }
    }

    private void UpdateMicrophonePercent(string deviceId, int percent)
    {
        if (!_microphoneRows.TryGetValue(deviceId, out var row) &&
            !_speakerRows.TryGetValue(deviceId, out row))
            return;

        try
        {
            if (row.PercentText != null)
            {
                row.PercentText.Text = $"{percent}%";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] UpdateMicrophonePercent error: {ex.Message}");
        }
    }

    private void OnMicrophoneAdded(object sender, string deviceId)
    {
        // Hot-plug support removed for compatibility
        // Call Refresh() manually if needed
    }

    private void OnMicrophoneRemoved(object sender, string deviceId)
    {
        // Hot-plug support removed for compatibility
        // Call Refresh() manually if needed
    }

    private void MicrophoneButton_Click(object sender, RoutedEventArgs e)
    {
        // Kept for compatibility - not used in multi-device mode
    }

    private void MicrophoneMuteButton_Click(object sender, RoutedEventArgs e)
    {
        // Kept for compatibility - not used in multi-device mode
    }

    private void MicrophoneVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Kept for compatibility - not used in multi-device mode
    }

    private void MicrophoneVolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Kept for compatibility - not used in multi-device mode
    }

    /// <summary>Every dynamic device row: speakers, microphones and the screen brightness row.</summary>
    private IEnumerable<MicrophoneRowUi> AllDeviceRows()
    {
        foreach (var row in _speakerRows.Values) yield return row;
        foreach (var row in _microphoneRows.Values) yield return row;
        if (_brightnessRow != null) yield return _brightnessRow;
    }

    private void ShowMicrophoneControls()
    {
        foreach (var row in AllDeviceRows())
        {
            try
            {
                var controlsGrid = row.ControlsGrid;
                if (controlsGrid != null)
                {
                    controlsGrid.Visibility = Visibility.Visible;
                    controlsGrid.Opacity = 1.0;
                }
            }
            catch { }
        }

        // Values are not polled while the controls are hidden — refresh them now that they show.
        SyncSpeakerValues();
        SyncMicrophoneValues();
        _ = SyncScreenBrightnessAsync(force: true);
    }

    private void HideMicrophoneControls()
    {
        foreach (var row in AllDeviceRows())
        {
            try
            {
                var controlsGrid = row.ControlsGrid;
                if (controlsGrid != null)
                {
                    controlsGrid.Visibility = Visibility.Collapsed;
                    controlsGrid.Opacity = 0.0;
                }
            }
            catch { }
        }
    }

    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Periodically re-enforces topmost z-order to stay above other topmost windows.
    /// </summary>
    private void EnsureTopmost()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd != 0)
            {
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, 
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] EnsureTopmost error: {ex.Message}");
        }
    }

    private void ApplySettings()
    {
        try
        {
            var settings = ServiceContainer.Settings.VolumeMixer;
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ApplySettings: IconSize={settings.IconSize}, IdleOpacity={settings.IdleOpacity}");

            // Check if XAML elements exist
            if (SpeakerButton == null)
            {
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] ApplySettings: ERROR - SpeakerButton is null!");
                return;
            }
            if (RootGrid == null)
            {
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] ApplySettings: ERROR - RootGrid is null!");
                return;
            }

            // Icon size
            SpeakerButton.Width = settings.IconSize;
            SpeakerButton.Height = settings.IconSize;
            SpeakerButton.CornerRadius = new CornerRadius(settings.IconSize / 2);
            SpeakerIcon.FontSize = settings.IconSize * 0.625; // 20/32

            if (ActiveAppIconButton != null)
            {
                ActiveAppIconButton.Width = settings.IconSize;
                ActiveAppIconButton.Height = settings.IconSize;
                ActiveAppIconButton.CornerRadius = new CornerRadius(settings.IconSize / 2);
            }
            if (ActiveAppHost != null)
            {
                ActiveAppHost.Width = settings.IconSize;
                ActiveAppHost.Height = settings.IconSize;
            }

            // Slider width
            MasterVolumeSlider.Width = settings.SliderWidth;
            if (ActiveAppVolumeSlider != null)
            {
                ActiveAppVolumeSlider.Width = settings.SliderWidth;
            }
            if (CameraBrightnessSlider != null)
            {
                CameraBrightnessSlider.Width = settings.SliderWidth;
            }

            // Update microphone sliders if any exist
            foreach (var row in _microphoneRows.Values)
            {
                try
                {
                    if (row.Slider != null)
                    {
                        row.Slider.Width = settings.SliderWidth;
                    }
                }
                catch { }
            }

            // Show or hide the microphone list when the setting changes at runtime
            if (settings.ShowMicrophoneVolume && _microphoneRows.Count == 0)
            {
                _ = InitializeMicrophoneAsync();
            }
            else if (!settings.ShowMicrophoneVolume && _microphoneRows.Count > 0)
            {
                RebuildMicrophoneRows(); // drops the mic rows, keeps any speaker rows
            }

            // Show or hide the screen brightness row when that setting changes
            bool wantsBrightnessRow = settings.ShowScreenBrightness && ServiceContainer.BrightnessService.IsAvailable;
            if (wantsBrightnessRow != (_brightnessRow != null))
            {
                RebuildMicrophoneRows();
            }

            // Idle opacity — applied as window (layered) alpha so the desktop behind the idle
            // chip is visible. Fading RootGrid.Opacity alone only blends the card against the
            // opaque window surface, which still looks solid.
            RootGrid.Opacity = 1.0;
            var targetOpacity = _state == OverlayState.Idle
                ? settings.IdleOpacity / 100.0
                : settings.HoverOpacity / 100.0;
            if (_layeredAlphaReady)
            {
                ApplyWindowAlpha(targetOpacity);
            }
            else
            {
                _overlayOpacity = Math.Clamp(targetOpacity, 0.0, 1.0);
            }

            // Drag mode feedback handled via smooth opacity change during drag

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Settings applied: IconSize={settings.IconSize}, SliderWidth={settings.SliderWidth}, IdleOpacity={settings.IdleOpacity}, DragMode={settings.DragMode}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ApplySettings error: {ex}");
            throw;
        }
    }

    // ── Session Management ─────────────────────────────────────────────────────

    private void StartPolling()
    {
        // Stop existing timer if running
        if (_pollingTimer != null)
        {
            _pollingTimer.Stop();
            _pollingTimer = null;
        }

        var settings = ServiceContainer.Settings.VolumeMixer;
        _pollingTimer = DispatcherQueue.CreateTimer();
        _pollingTimer.Interval = TimeSpan.FromMilliseconds(settings.PollingMs);
        _pollingTimer.IsRepeating = true;
        _pollingTimer.Tick += (s, e) => RefreshSessions();
        _pollingTimer.Start();

        System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Polling started: {settings.PollingMs}ms");

        // Pick up any camera brightness change made while the overlay was collapsed
        RefreshCameraBrightnessFromDevice();

        // Initial refresh
        RefreshSessions();
    }

    private void StopPolling()
    {
        _pollingTimer?.Stop();
        _pollingTimer = null;
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] Polling stopped");
    }

    private void RefreshSessions()
    {
        // Microphones/speakers: hot-plug, default-device and volume/mute changes from other apps.
        SyncMicrophones();

        // Monitor brightness changed from the brightness page or the keyboard keys
        _ = SyncScreenBrightnessAsync();

        // The per-app snapshot touches every audio session and repaints rows, so it runs slower
        // than the device sync — 150 ms of app-row churn was making the whole bar feel sluggish.
        if ((DateTime.UtcNow - _lastSessionSnapshot).TotalMilliseconds < SessionSnapshotIntervalMs) return;
        _lastSessionSnapshot = DateTime.UtcNow;

        // Post the session snapshot work to the audio thread; result comes back on UI thread.
        _audioService.GetAudioSessionsAsync(sessions =>
        {
            try
            {
                ProcessSessionSnapshot(sessions);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ProcessSessionSnapshot error: {ex.Message}");
            }
        });
    }

    private void ProcessSessionSnapshot(List<SessionInfo> sessions)
    {
        try
        {
            if (VerboseLog) System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ProcessSessionSnapshot: received {sessions.Count} sessions");
            if (VerboseLog)
            {
                foreach (var s in sessions)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeMixer]   Session: PID={s.ProcessId}, Name={GetProcessName(s.ProcessId)}, State={s.State}");
                }
            }
            
            var settings = ServiceContainer.Settings.VolumeMixer;

            // Apply blacklist filter
            var blacklist = settings.Blacklist
                .Select(b => b.ToLowerInvariant().TrimEnd())
                // Normalize: strip .exe for comparison since GetProcessName returns name without extension
                .Select(b => b.EndsWith(".exe") ? b[..^4] : b)
                .ToHashSet();
            if (VerboseLog) System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Blacklist: {string.Join(", ", blacklist)}");
            
            var beforeBlacklist = sessions.Count;
            sessions = sessions.Where(s =>
            {
                var processName = GetProcessName(s.ProcessId); // already lowercase, no .exe
                var isBlacklisted = blacklist.Contains(processName);
                if (isBlacklisted && VerboseLog)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeMixer] FILTERED (blacklist): {processName} (PID {s.ProcessId})");
                }
                return !isBlacklisted;
            }).ToList();
            if (VerboseLog) System.Diagnostics.Debug.WriteLine($"[VolumeMixer] After blacklist: {beforeBlacklist} → {sessions.Count} sessions");

            // Apply the "Show only active apps" filter. A session counts as playing when Windows
            // reports it as Active or its peak meter is non-zero. Hits are remembered briefly so a
            // quiet passage or gaps between beats don't make an app flicker out of the list.
            if (settings.OnlyActiveApps)
            {
                var beforeActive = sessions.Count;
                var now = DateTime.UtcNow;
                var liveIds = sessions.Select(s => s.SessionIdentifier).ToHashSet();

                foreach (var s in sessions)
                {
                    if (IsSessionAudible(s))
                        _lastAudible[s.SessionIdentifier] = now;
                }

                sessions = sessions.Where(s =>
                {
                    bool audible = IsSessionAudible(s)
                        || (_lastAudible.TryGetValue(s.SessionIdentifier, out var last)
                            && (now - last).TotalSeconds < ActiveAppGraceSeconds);

                    if (!audible && VerboseLog)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[VolumeMixer] FILTERED (not playing): {GetProcessName(s.ProcessId)} (PID {s.ProcessId}, State={s.State}, Peak={s.PeakValue:0.0000})");
                    }

                    return audible;
                }).ToList();

                // Forget tracking entries for sessions that no longer exist
                foreach (var key in _lastAudible.Keys.Where(k => !liveIds.Contains(k)).ToList())
                    _lastAudible.Remove(key);
                if (VerboseLog) System.Diagnostics.Debug.WriteLine($"[VolumeMixer] After active-only filter: {beforeActive} → {sessions.Count} sessions");
            }

            // Group by process name
            var groups = sessions
                .GroupBy(s => GetProcessName(s.ProcessId))
                .OrderBy(g => g.Key)
                .ToList();

            if (VerboseLog)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ProcessSessionSnapshot: {sessions.Count} sessions → {groups.Count} groups");
                foreach (var g in groups)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeMixer]   Group '{g.Key}': {g.Count()} sessions");
                }
            }

            // Update mixer panel rows only when panel is open
            if (_state == OverlayState.MixerOpen)
            {
                UpdateSessionRows(groups);
            }
            
            // Always update active app row when expanded or mixer open
            UpdateActiveAppSession(groups);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] RefreshSessions error: {ex.Message}");
        }
    }

    /// <summary>
    /// A session is considered to be playing audio when Windows marks it Active or its peak meter
    /// is above the noise floor.
    /// </summary>
    private static bool IsSessionAudible(SessionInfo session)
        => session.State == 1 || session.PeakValue > 0.0005f;

    /// <summary>
    /// Cached on purpose: the poll asks for the same handful of PIDs several times per tick and
    /// Process.GetProcessById is comparatively expensive.
    /// </summary>
    private string GetProcessName(int processId)
    {
        if (_processNameCache.TryGetValue(processId, out var cached)) return cached;

        string name;
        try
        {
            var process = Process.GetProcessById(processId);
            name = process.ProcessName.ToLowerInvariant();
        }
        catch
        {
            name = $"process-{processId}";
        }

        if (_processNameCache.Count > 256) _processNameCache.Clear();
        _processNameCache[processId] = name;
        return name;
    }

    private void UpdateSessionRows(List<IGrouping<string, SessionInfo>> groups)
    {
        var container = ActiveAppRowsContainer;
        System.Diagnostics.Debug.WriteLine($"[VolumeMixer] UpdateSessionRows: container={container?.Name}, has {container?.Children.Count ?? -1} children before update");
        
        var currentKeys = _appSessions.Keys.ToHashSet();
        var newKeys = groups.Select(g => g.Key).ToHashSet();

        // Remove closed apps
        var toRemove = currentKeys.Except(newKeys).ToList();
        foreach (var key in toRemove)
        {
            if (_appSessions.TryGetValue(key, out var session))
            {
                container.Children.Remove(session.Row);
                _appSessions.Remove(key);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Removed app: {key}");
            }
        }

        // Add new apps or update existing
        foreach (var group in groups)
        {
            var processName = group.Key;
            var first = group.First();
            var sessionIds = group.Select(s => s.SessionIdentifier).ToList();

            if (_appSessions.TryGetValue(processName, out var existing))
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer]   Updating existing: {processName}");
                existing.ViewModel.IsUpdating = true;
                existing.ViewModel.Volume = group.Max(s => s.Volume);
                existing.ViewModel.IsMuted = group.All(s => s.IsMuted);
                existing.ViewModel.IsUpdating = false;
                existing.SessionIds.Clear();
                existing.SessionIds.AddRange(sessionIds);
            }
            else
            {
                var (name, exePath) = _iconResolver.ResolveApp(first.ProcessId, first.DisplayName, first.IconPath);
                
                var viewModel = new MixerSessionViewModel
                {
                    Name = name,
                    ProcessName = processName + ".exe",
                    Volume = group.Max(s => s.Volume),
                    IsMuted = group.All(s => s.IsMuted),
                    Icon = null
                };

                var row = CreateAppRow(viewModel, sessionIds);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer]   Adding NEW: {name} ({processName})");
                container.Children.Add(row.Row);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer]     Container now has {container.Children.Count} children");
                _appSessions[processName] = (viewModel, new List<string>(sessionIds), row.Row, row.PlayIcon);

                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Added app: {name} ({processName})");
            }
        }

        UpdateEmptyState();

        // Sync play/pause icons after every row update so state is always current
        SyncAllPlayPauseIcons();
        
        // Resize only when the row count really changed — resizing the window (and its region) on
        // every poll tick made the mixer stutter.
        if (_state == OverlayState.MixerOpen && _appSessions.Count != _lastAppRowCount)
        {
            _lastAppRowCount = _appSessions.Count;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ResizeWindowToContent();
                if (VerboseLog) System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Resized window for {_appSessions.Count} apps");
            });
        }
    }

    private void UpdateEmptyState()
    {
        var isEmpty = ActiveAppRowsContainer.Children
            .OfType<UIElement>()
            .All(c => c == ActiveEmptyStateText);  // only the placeholder remains

        ActiveEmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

        if (isEmpty)
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] No apps playing audio");
    }

    private (Grid Row, FontIcon PlayIcon) CreateAppRow(MixerSessionViewModel viewModel, List<string> sessionIds)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });       // 0: icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1: slider
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });       // 2: %
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });       // 3: play/pause
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });       // 4: mute

        // Col 0 — App icon
        var appIconHost = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        var fallbackIcon = new FontIcon
        {
            Glyph = "\uE8A5",
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        appIconHost.Children.Add(fallbackIcon);
        _ = TryLoadAppIconAsync(viewModel.ProcessName, appIconHost, fallbackIcon);
        ToolTipService.SetToolTip(appIconHost, viewModel.Name);
        appIconHost.SetValue(Grid.ColumnProperty, 0);
        grid.Children.Add(appIconHost);

        // Col 1 — Volume slider
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100,
            Value = viewModel.VolumePercent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
            UseSystemFocusVisuals = false
        };
        ToolTipService.SetToolTip(slider, $"{viewModel.Name}: {viewModel.VolumePercent}%");
        slider.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Add(slider);

        // Col 2 — Percent label
        var percentText = new TextBlock
        {
            Text = $"{viewModel.VolumePercent}%",
            FontSize = 11,
            Width = 40,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        percentText.SetValue(Grid.ColumnProperty, 2);
        grid.Children.Add(percentText);

        // Col 3 — Play / Pause button (sends WM_APPCOMMAND to the app's window)
        var playPauseIcon = new FontIcon { Glyph = "\uE768", FontSize = 11 }; // Play glyph default
        var playPauseButton = new Button
        {
            Width = 28, Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Content = playPauseIcon,
            Background = (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(playPauseButton, $"Play / Pause ({viewModel.Name})");
        playPauseButton.SetValue(Grid.ColumnProperty, 3);
        grid.Children.Add(playPauseButton);

        // Col 4 — Mute button
        var muteIcon = new FontIcon { Glyph = viewModel.MuteIconGlyph, FontSize = 12 };
        var muteButton = new Button
        {
            Width = 28, Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Content = muteIcon,
            Background = (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(muteButton, (viewModel.IsMuted ? "Unmute " : "Mute ") + viewModel.Name);
        muteButton.SetValue(Grid.ColumnProperty, 4);
        grid.Children.Add(muteButton);

        // Wire events
        slider.ValueChanged += (s, e) =>
        {
            if (viewModel.IsUpdating) return;
            SetTextIfChanged(percentText, $"{(int)e.NewValue}%");
            QueueSessionVolume(sessionIds, (float)(e.NewValue / 100.0));
        };

        muteButton.Click += (s, e) =>
        {
            var newMute = !viewModel.IsMuted;
            viewModel.IsMuted = newMute;
            muteIcon.Glyph = viewModel.MuteIconGlyph;
            foreach (var id in sessionIds) _audioService.SetSessionMute(id, newMute);
        };

        playPauseButton.Click += (s, e) => _ = SendPlayPauseToProcessAsync(viewModel.ProcessName, playPauseIcon);

        return (grid, playPauseIcon);
    }

    /// <summary>
    /// Initializes the GlobalSystemMediaTransportControls session manager and
    /// keeps _smtcSessions up to date whenever sessions change.
    /// </summary>
    private async System.Threading.Tasks.Task InitSmtcAsync()
    {
        try
        {
            _smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            if (_smtcManager == null) return;

            RefreshSmtcSessions();
            _smtcManager.SessionsChanged += (s, e) => DispatcherQueue.TryEnqueue(RefreshSmtcSessions);

            // Sync icons immediately on init
            DispatcherQueue.TryEnqueue(SyncAllPlayPauseIcons);

            System.Diagnostics.Debug.WriteLine("[VolumeMixer] SMTC manager initialized");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] SMTC init error: {ex.Message}");
        }
    }

    private void RefreshSmtcSessions()
    {
        if (_smtcManager == null) return;
        _smtcSessions.Clear();
        try
        {
            foreach (var session in _smtcManager.GetSessions())
            {
                var appId = session.SourceAppUserModelId ?? string.Empty;
                var key = appId.Split('!')[0]
                               .Split('.').Last()
                               .ToLowerInvariant();
                _smtcSessions[key] = session;

                // Subscribe to playback status changes so icons stay in sync
                session.PlaybackInfoChanged -= OnSmtcPlaybackInfoChanged;
                session.PlaybackInfoChanged += OnSmtcPlaybackInfoChanged;

                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] SMTC session: {key} ({appId})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] RefreshSmtcSessions error: {ex.Message}");
        }

        // Sync all icons now
        SyncAllPlayPauseIcons();
    }

    /// <summary>
    /// Called by SMTC whenever any session's playback status changes.
    /// Runs on a system thread — dispatches UI update to the DispatcherQueue.
    /// </summary>
    private void OnSmtcPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession session, PlaybackInfoChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(SyncAllPlayPauseIcons);
    }

    /// <summary>
    /// Reads the current playback status from SMTC for each known session and
    /// updates the per-row play/pause icon and the universal header icon accordingly.
    /// </summary>
    private void SyncAllPlayPauseIcons()
    {
        // Each media session query is a WinRT call; once a second is plenty for a play/pause glyph.
        if ((DateTime.UtcNow - _lastSmtcSync).TotalMilliseconds < 1000)
        {
            UpdateActiveAppTooltip();
            return;
        }
        _lastSmtcSync = DateTime.UtcNow;

        // Sync per-app row icons
        foreach (var (procName, entry) in _appSessions)
        {
            // Find the matching SMTC session for this process
            GlobalSystemMediaTransportControlsSession? session = null;
            if (!_smtcSessions.TryGetValue(procName, out session))
                session = _smtcSessions.FirstOrDefault(kv =>
                    kv.Key.Contains(procName, StringComparison.OrdinalIgnoreCase) ||
                    procName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;

            if (session != null)
            {
                var status = session.GetPlaybackInfo()?.PlaybackStatus;
                bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                string glyph = playing ? "\uE769" : "\uE768"; // ⏸ or ▶
                if (entry.PlayIcon.Glyph != glyph) entry.PlayIcon.Glyph = glyph;
            }
            // If no SMTC session, leave icon as-is (stays at ▶ for apps without media transport)
        }

        // Sync universal header icon from the current (foreground) session
        var current = _smtcManager?.GetCurrentSession();
        bool universalPlaying = current?.GetPlaybackInfo()?.PlaybackStatus
                                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var universalGlyph = universalPlaying ? "\uE769" : "\uE768";
        if (UniversalPlayPauseIconAbove != null && UniversalPlayPauseIconAbove.Glyph != universalGlyph)
            UniversalPlayPauseIconAbove.Glyph = universalGlyph;
        if (UniversalPlayPauseIconBelow != null && UniversalPlayPauseIconBelow.Glyph != universalGlyph)
            UniversalPlayPauseIconBelow.Glyph = universalGlyph;

        UpdateActiveAppTooltip();
    }

    /// <summary>
    /// Sends play/pause to a specific app via SMTC. Falls back to WM_APPCOMMAND if no SMTC session found.
    /// </summary>
    private async System.Threading.Tasks.Task SendPlayPauseToProcessAsync(string processExeName, FontIcon icon)
    {
        try
        {
            // Strip .exe
            var procName = processExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processExeName[..^4].ToLowerInvariant()
                : processExeName.ToLowerInvariant();

            // Refresh sessions right before acting to get the latest state
            RefreshSmtcSessions();

            // Try exact match first, then partial match
            GlobalSystemMediaTransportControlsSession? session = null;
            if (_smtcSessions.TryGetValue(procName, out var exactMatch))
                session = exactMatch;
            else
                session = _smtcSessions.FirstOrDefault(kv =>
                    kv.Key.Contains(procName, StringComparison.OrdinalIgnoreCase) ||
                    procName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;

            if (session != null)
            {
                await session.TryTogglePlayPauseAsync();
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] SMTC play/pause → {procName}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] No SMTC session for {procName}, skipping");
            }

            // Flip the glyph regardless (best-effort visual feedback)
            icon.Glyph = icon.Glyph == "\uE768" ? "\uE769" : "\uE768";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] SendPlayPause error: {ex.Message}");
        }
    }

    /// <summary>
    /// Universal play/pause: toggles the currently active SMTC session.
    /// </summary>
    private async void UniversalPlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_smtcManager == null)
                _smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            var current = _smtcManager?.GetCurrentSession();
            if (current != null)
            {
                await current.TryTogglePlayPauseAsync();
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] Universal play/pause sent");
            }

            // Re-read status after the toggle and sync all icons
            SyncAllPlayPauseIcons();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] UniversalPlayPause error: {ex.Message}");
        }
    }

    private async void UniversalPrev_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_smtcManager == null)
                _smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var current = _smtcManager?.GetCurrentSession();
            if (current != null)
            {
                await current.TrySkipPreviousAsync();
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] Universal previous sent");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] UniversalPrev error: {ex.Message}");
        }
    }

    private async void UniversalNext_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_smtcManager == null)
                _smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var current = _smtcManager?.GetCurrentSession();
            if (current != null)
            {
                await current.TrySkipNextAsync();
                System.Diagnostics.Debug.WriteLine("[VolumeMixer] Universal next sent");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] UniversalNext error: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the .exe icon for <paramref name="processExeName"/> and swaps it into
    /// <paramref name="host"/>, replacing the <paramref name="fallback"/> glyph.
    /// </summary>
    private async System.Threading.Tasks.Task TryLoadAppIconAsync(
        string processExeName, Grid host, FontIcon fallback)
    {
        try
        {
            // Strip .exe extension
            var name = processExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processExeName[..^4] : processExeName;

            // Try to find the process
            var proc = Process.GetProcessesByName(name).FirstOrDefault();
            
            // Special handling for processes with different names
            if (proc == null)
            {
                // Handle "whatsapp.root" -> look for "WhatsApp" process
                if (name.Contains("whatsapp", StringComparison.OrdinalIgnoreCase))
                {
                    proc = Process.GetProcessesByName("WhatsApp").FirstOrDefault();
                }
                // Handle Chrome's various worker processes
                else if (name.Contains("chrome", StringComparison.OrdinalIgnoreCase))
                {
                    proc = Process.GetProcessesByName("chrome").FirstOrDefault();
                }
                // Handle Edge's worker processes
                else if (name.Contains("msedge", StringComparison.OrdinalIgnoreCase) || 
                         name.Contains("edgewebview", StringComparison.OrdinalIgnoreCase))
                {
                    proc = Process.GetProcessesByName("msedge").FirstOrDefault();
                }
                // Handle Spotify
                else if (name.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                {
                    proc = Process.GetProcessesByName("Spotify").FirstOrDefault();
                }
            }
            
            if (proc == null)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Icon load: process '{name}' not found");
                return;
            }

            string? exePath = null;
            try { exePath = proc.MainModule?.FileName; } catch { }
            if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Icon load: exe path not found for '{name}'");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Loading icon from: {exePath}");

            // Extract icon on background thread - try to get largest available size
            byte[]? pngBytes = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Special handling for UWP / Windows Store packaged apps (e.g. WhatsApp)
                    // Packaged executables often have generic/blank default Win32 icons, while the
                    // actual high-res branded icons are stored in the package's Assets folder.
                    if (exePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        var uwpBytes = TryExtractUwpAppIcon(exePath);
                        if (uwpBytes != null && uwpBytes.Length > 0)
                        {
                            return uwpBytes;
                        }
                    }

                    // Try multiple sizes to get the best quality
                    System.Drawing.Icon? bestIcon = null;
                    
                    // First try to extract large icon using shell
                    try
                    {
                        // Extract all icons from the file
                        var allIcons = ExtractIconsFromFile(exePath);
                        if (allIcons != null && allIcons.Length > 0)
                        {
                            // Find the largest icon (prefer 32x32 or larger)
                            bestIcon = allIcons.OrderByDescending(i => i.Width).FirstOrDefault();
                            // Dispose all except the one we're using
                            foreach (var ico in allIcons.Where(i => i != bestIcon))
                            {
                                ico?.Dispose();
                            }
                        }
                    }
                    catch { }
                    
                    // Fallback to ExtractAssociatedIcon if custom extraction failed
                    if (bestIcon == null)
                    {
                        bestIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                    
                    if (bestIcon == null) return null;
                    
                    using (bestIcon)
                    {
                        using var bmp = bestIcon.ToBitmap();
                        using var ms = new System.IO.MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Icon extraction error: {ex.Message}");
                    return null;
                }
            });

            if (pngBytes == null || pngBytes.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Icon bytes null or empty for '{name}'");
                return;
            }

            // Load BitmapImage on UI thread properly with await
            var imgSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = new System.IO.MemoryStream(pngBytes).AsRandomAccessStream();
            await imgSource.SetSourceAsync(stream);

            var img = new Image
            {
                Source = imgSource,
                Width = 20, 
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                IsHitTestVisible = false
            };

            host.Children.Remove(fallback);
            host.Children.Add(img);
            
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Icon loaded successfully for '{name}'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] TryLoadAppIconAsync error: {ex.Message}");
            /* keep fallback glyph */
        }
    }

    // Helper method to extract all icon sizes from an executable
    private static System.Drawing.Icon[]? ExtractIconsFromFile(string filePath)
    {
        try
        {
            // Use IconExtractor to get multiple sizes
            var iconExtractor = new IconExtractor(filePath);
            return iconExtractor.GetAllIcons();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// For packaged UWP/Store apps (like WhatsApp Desktop), extracts the branded app logo
    /// from the package manifest or Assets directory instead of the generic executable icon.
    /// </summary>
    private static byte[]? TryExtractUwpAppIcon(string exePath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                return null;

            // 1. Try AppxManifest.xml to find the exact declared logo file
            var manifestPath = System.IO.Path.Combine(dir, "AppxManifest.xml");
            if (System.IO.File.Exists(manifestPath))
            {
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(manifestPath);
                    var visualElements = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                    var logoRel = visualElements?.Attribute("Square44x44Logo")?.Value
                               ?? visualElements?.Attribute("Square150x150Logo")?.Value;

                    if (!string.IsNullOrEmpty(logoRel))
                    {
                        var logoFull = System.IO.Path.Combine(dir, logoRel);
                        var logoDir = System.IO.Path.GetDirectoryName(logoFull);
                        var logoBase = System.IO.Path.GetFileNameWithoutExtension(logoFull);

                        if (!string.IsNullOrEmpty(logoDir) && System.IO.Directory.Exists(logoDir))
                        {
                            // Look for targetsize or unplated variants (prefer unplated 48, 44, 32, 24, etc.)
                            var candidates = System.IO.Directory.GetFiles(logoDir, $"{logoBase}*.png");
                            if (candidates.Length > 0)
                            {
                                // Prefer targetsize-48 or 44 unplated/normal, or largest suitable size
                                var bestMatch = candidates.FirstOrDefault(f => f.Contains("targetsize-48_altform-unplated", StringComparison.OrdinalIgnoreCase))
                                             ?? candidates.FirstOrDefault(f => f.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase))
                                             ?? candidates.FirstOrDefault(f => f.Contains("targetsize-44", StringComparison.OrdinalIgnoreCase))
                                             ?? candidates.FirstOrDefault(f => f.Contains("targetsize-32", StringComparison.OrdinalIgnoreCase))
                                             ?? candidates.FirstOrDefault(f => f.Contains("targetsize-24", StringComparison.OrdinalIgnoreCase))
                                             ?? candidates.OrderByDescending(f => new System.IO.FileInfo(f).Length).FirstOrDefault();

                                if (bestMatch != null && System.IO.File.Exists(bestMatch))
                                {
                                    return System.IO.File.ReadAllBytes(bestMatch);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Direct search in common Assets directory
            var assetsDir = System.IO.Path.Combine(dir, "Assets");
            if (System.IO.Directory.Exists(assetsDir))
            {
                // Check for dedicated logo.ico
                var logoIco = System.IO.Path.Combine(assetsDir, "logo.ico");
                if (System.IO.File.Exists(logoIco))
                {
                    try
                    {
                        using var ico = new System.Drawing.Icon(logoIco, 32, 32);
                        using var bmp = ico.ToBitmap();
                        using var ms = new System.IO.MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return ms.ToArray();
                    }
                    catch { }
                }

                // Search for any AppList, StoreLogo, or Application logo pngs
                var pngFiles = System.IO.Directory.GetFiles(assetsDir, "*AppList*.png")
                    .Concat(System.IO.Directory.GetFiles(assetsDir, "*Logo*.png"))
                    .ToArray();

                if (pngFiles.Length > 0)
                {
                    var bestPng = pngFiles.FirstOrDefault(f => f.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase))
                               ?? pngFiles.FirstOrDefault(f => f.Contains("targetsize-44", StringComparison.OrdinalIgnoreCase))
                               ?? pngFiles.FirstOrDefault(f => f.Contains("targetsize-32", StringComparison.OrdinalIgnoreCase))
                               ?? pngFiles.OrderByDescending(f => new System.IO.FileInfo(f).Length).FirstOrDefault();

                    if (bestPng != null && System.IO.File.Exists(bestPng))
                    {
                        return System.IO.File.ReadAllBytes(bestPng);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] TryExtractUwpAppIcon error: {ex.Message}");
        }

        return null;
    }

    // Simple icon extractor helper class
    private class IconExtractor
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly string _filePath;

        public IconExtractor(string filePath)
        {
            _filePath = filePath;
        }

        public System.Drawing.Icon[]? GetAllIcons()
        {
            IntPtr[] largeIcons = null;
            try
            {
                // Get count of icons
                uint count = ExtractIconEx(_filePath, -1, null, null, 0);
                if (count == 0) return null;

                // Extract large icons
                largeIcons = new IntPtr[count];
                uint extracted = ExtractIconEx(_filePath, 0, largeIcons, null, count);
                
                if (extracted == 0) return null;

                // Convert to Icon array - clone each icon so we can destroy handles
                var icons = new List<System.Drawing.Icon>();
                for (int i = 0; i < extracted; i++)
                {
                    if (largeIcons[i] != IntPtr.Zero)
                    {
                        try
                        {
                            // Clone the icon from handle so we own a copy
                            var icon = System.Drawing.Icon.FromHandle(largeIcons[i]);
                            var clonedIcon = (System.Drawing.Icon)icon.Clone();
                            icons.Add(clonedIcon);
                        }
                        catch { }
                    }
                }

                return icons.Count > 0 ? icons.ToArray() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                // Always destroy the icon handles to prevent memory leaks
                if (largeIcons != null)
                {
                    foreach (var handle in largeIcons)
                    {
                        if (handle != IntPtr.Zero)
                        {
                            DestroyIcon(handle);
                        }
                    }
                }
            }
        }
    }

    // ── Active Application Volume Row Support ─────────────────────────────────

    private void CaptureForegroundWindow()
    {
        try
        {
            var myHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var fg = GetForegroundWindow();
            if (fg != 0 && fg != myHwnd)
            {
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid != 0 && pid != Environment.ProcessId)
                {
                    _lastForegroundProcessId = (int)pid;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] CaptureForegroundWindow error: {ex.Message}");
        }
    }

    private void OnForegroundEvent(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hWnd == 0) return;
            var myHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == myHwnd) return;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0 && pid != Environment.ProcessId)
            {
                _lastForegroundProcessId = (int)pid;
                if (_state == OverlayState.Expanded || _state == OverlayState.MixerOpen)
                {
                    DispatcherQueue.TryEnqueue(RefreshSessions);
                }
            }
        }
        catch { }
    }

    private void UpdateActiveAppSession(List<IGrouping<string, SessionInfo>> groups)
    {
        try
        {
            CaptureForegroundWindow();

            IGrouping<string, SessionInfo>? targetGroup = null;

            // 1. Direct match with current foreground process
            if (_lastForegroundProcessId > 0)
            {
                var fgName = GetProcessName(_lastForegroundProcessId);
                if (!string.IsNullOrEmpty(fgName) && !fgName.StartsWith("process-"))
                {
                    targetGroup = groups.FirstOrDefault(g =>
                        g.Key.Equals(fgName, StringComparison.OrdinalIgnoreCase) ||
                        g.Key.Contains(fgName, StringComparison.OrdinalIgnoreCase) ||
                        fgName.Contains(g.Key, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 2. Match with SMTC active/playing media session (e.g. Edge or Chrome playing music in background)
            if (targetGroup == null && _smtcManager != null)
            {
                var currentSmtc = _smtcManager.GetCurrentSession();
                if (currentSmtc != null)
                {
                    var smtcAppId = currentSmtc.SourceAppUserModelId ?? string.Empty;
                    var smtcKey = smtcAppId.Split('!')[0].Split('.').Last().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(smtcKey))
                    {
                        targetGroup = groups.FirstOrDefault(g =>
                            g.Key.Equals(smtcKey, StringComparison.OrdinalIgnoreCase) ||
                            g.Key.Contains(smtcKey, StringComparison.OrdinalIgnoreCase) ||
                            smtcKey.Contains(g.Key, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            // 3. Fallback to previously selected app if it still exists in the session list
            if (targetGroup == null && !string.IsNullOrEmpty(_activeAppProcessName))
            {
                targetGroup = groups.FirstOrDefault(g => g.Key.Equals(_activeAppProcessName, StringComparison.OrdinalIgnoreCase));
            }

            // 4. Fallback to any app currently active/playing or first group
            if (targetGroup == null && groups.Count > 0)
            {
                targetGroup = groups.FirstOrDefault(g => g.Any(s => s.State == 1)) ?? groups.First();
            }

            if (targetGroup != null)
            {
                _activeAppProcessName = targetGroup.Key;
                _activeAppSessionIds.Clear();
                _activeAppSessionIds.AddRange(targetGroup.Select(s => s.SessionIdentifier));

                var first = targetGroup.First();

                // Resolve the app name once per app instead of on every poll tick.
                if (_activeAppNameKey != targetGroup.Key)
                {
                    var (resolvedName, _) = _iconResolver.ResolveApp(first.ProcessId, first.DisplayName, first.IconPath);
                    _activeAppName = resolvedName;
                    _activeAppNameKey = targetGroup.Key;
                }
                var name = _activeAppName;
                _activeAppDisplayName = name;

                float maxVol = targetGroup.Max(s => s.Volume);
                bool allMuted = targetGroup.All(s => s.IsMuted);
                int volPercent = (int)Math.Round(maxVol * 100);

                // Only touch the row when something actually changed. Writing the same slider value
                // and tooltips 6-7 times a second was causing needless layout and tooltip churn.
                bool changed = volPercent != _lastActiveAppPercent
                    || allMuted != _lastActiveAppMuted
                    || !string.Equals(name, _lastActiveAppName, StringComparison.Ordinal);

                if (changed)
                {
                    _lastActiveAppPercent = volPercent;
                    _lastActiveAppMuted = allMuted;
                    _lastActiveAppName = name;

                    // While the user drags the active-app slider, the device reading lags behind —
                    // don't push it back into the thumb.
                    bool userDriving = (DateTime.UtcNow - _lastActiveAppSliderChange).TotalMilliseconds < 1200;

                    if (!userDriving && !_isUpdatingActiveAppSlider && Math.Abs(ActiveAppVolumeSlider.Value - volPercent) > 0.5)
                    {
                        _isUpdatingActiveAppSlider = true;
                        ActiveAppVolumeSlider.Value = volPercent;
                        _isUpdatingActiveAppSlider = false;
                    }
                    if (!userDriving) SetTextIfChanged(ActiveAppPercentText, $"{volPercent}%");

                    string appGlyph = allMuted ? "\uE74F" : "\uE767";
                    if (ActiveAppMuteIcon.Glyph != appGlyph) ActiveAppMuteIcon.Glyph = appGlyph;

                    UpdateActiveAppTooltip();
                    SetToolTipIfChanged(ActiveAppVolumeSlider, $"{name}: {volPercent}%");
                    SetToolTipIfChanged(ActiveAppMuteButton, allMuted ? $"Unmute {name}" : $"Mute {name}");
                }

                if (_loadedActiveAppIconKey != targetGroup.Key)
                {
                    _loadedActiveAppIconKey = targetGroup.Key;
                    _ = TryLoadAppIconAsync(targetGroup.Key + ".exe", ActiveAppIconContainer, ActiveAppFallbackIcon);
                }

                if (_state != OverlayState.Idle)
                {
                    ShowActiveAppRow(true);
                }
            }
            else
            {
                _activeAppProcessName = null;
                _activeAppSessionIds.Clear();
                ShowActiveAppRow(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] UpdateActiveAppSession error: {ex.Message}");
        }
    }

    private void ShowActiveAppRow(bool show)
    {
        if (_activeAppRowShown == show) return;
        _activeAppRowShown = show;

        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        ActiveAppHost.Visibility = vis;
        ActiveAppVolumeSlider.Visibility = vis;
        ActiveAppPercentText.Visibility = vis;
        ActiveAppMuteButton.Visibility = vis;

        if (_hasActiveAppRow != show)
        {
            _hasActiveAppRow = show;
            CardBorder.CornerRadius = new CornerRadius(_hasActiveAppRow ? 18 : 20);

            if (_state != OverlayState.Idle)
            {
                GetDimensionsForState(_state, out int targetW, out int targetH);
                var settings = ServiceContainer.Settings.VolumeMixer;
                AnimateToSize(targetW, targetH, isExpanding: true, durationMs: settings.AnimationMs);
            }
        }
    }

    private void ActiveAppVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ActiveAppPercentText != null)
        {
            ActiveAppPercentText.Text = $"{(int)e.NewValue}%";
        }

        if (!_isUpdatingActiveAppSlider && _audioService != null && _activeAppSessionIds.Count > 0)
        {
            QueueSessionVolume(_activeAppSessionIds, (float)(e.NewValue / 100.0));
        }
    }

    private void ActiveAppVolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        var step = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) ? 1 : 2;

        if (delta > 0)
        {
            ActiveAppVolumeSlider.Value = Math.Min(100, ActiveAppVolumeSlider.Value + step);
        }
        else
        {
            ActiveAppVolumeSlider.Value = Math.Max(0, ActiveAppVolumeSlider.Value - step);
        }

        e.Handled = true;
    }

    private void ActiveAppMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAppSessionIds.Count == 0 || _audioService == null) return;

        bool isCurrentlyMuted = ActiveAppMuteIcon.Glyph == "\uE74F";
        bool newMute = !isCurrentlyMuted;

        ActiveAppMuteIcon.Glyph = newMute ? "\uE74F" : "\uE767";

        foreach (var id in _activeAppSessionIds)
        {
            _audioService.SetSessionMute(id, newMute);
        }

        if (!string.IsNullOrEmpty(_activeAppDisplayName))
        {
            ToolTipService.SetToolTip(ActiveAppMuteButton, newMute ? $"Unmute {_activeAppDisplayName}" : $"Mute {_activeAppDisplayName}");
        }

        UpdateActiveAppTooltip();
    }

    private void UpdateActiveAppTooltip()
    {
        if (ActiveAppIconButton == null) return;
        var name = !string.IsNullOrEmpty(_activeAppDisplayName) ? _activeAppDisplayName : _activeAppProcessName;
        if (!string.IsNullOrEmpty(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        string action;
        if (_smtcManager != null && !string.IsNullOrEmpty(_activeAppProcessName))
        {
            var procKey = _activeAppProcessName.ToLowerInvariant();
            if (procKey.EndsWith(".exe")) procKey = procKey[..^4];

            GlobalSystemMediaTransportControlsSession? session = null;
            if (_smtcSessions.TryGetValue(procKey, out var s))
            {
                session = s;
            }
            else
            {
                session = _smtcSessions.FirstOrDefault(kv =>
                    kv.Key.Contains(procKey, StringComparison.OrdinalIgnoreCase) ||
                    procKey.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
            }

            if (session == null)
            {
                try
                {
                    var sessions = _smtcManager.GetSessions();
                    session = sessions.FirstOrDefault(sess =>
                    {
                        var id = sess.SourceAppUserModelId ?? "";
                        return id.Contains(procKey, StringComparison.OrdinalIgnoreCase);
                    });
                }
                catch { }
            }

            if (session != null)
            {
                var status = session.GetPlaybackInfo()?.PlaybackStatus;
                action = status switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Pause",
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Play",
                    _ => "Play / Pause"
                };
            }
            else
            {
                bool isMuted = ActiveAppMuteIcon?.Glyph == "\uE74F";
                action = isMuted ? "Unmute" : "Mute";
            }
        }
        else
        {
            bool isMuted = ActiveAppMuteIcon?.Glyph == "\uE74F";
            action = isMuted ? "Unmute" : "Mute";
        }

        string tooltipText = string.IsNullOrEmpty(name) ? action : $"{action} ({name})";

        SetToolTipIfChanged(ActiveAppIconButton, tooltipText);
    }

    private async void ActiveAppIconButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeAppProcessName)) return;

        bool toggled = false;
        try
        {
            var procKey = _activeAppProcessName.ToLowerInvariant();
            if (procKey.EndsWith(".exe")) procKey = procKey[..^4];

            GlobalSystemMediaTransportControlsSession? session = null;
            if (_smtcSessions.TryGetValue(procKey, out var s))
            {
                session = s;
            }
            else
            {
                session = _smtcSessions.FirstOrDefault(kv =>
                    kv.Key.Contains(procKey, StringComparison.OrdinalIgnoreCase) ||
                    procKey.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
            }

            if (session == null && _smtcManager != null)
            {
                var sessions = _smtcManager.GetSessions();
                session = sessions.FirstOrDefault(sess =>
                {
                    var id = sess.SourceAppUserModelId ?? "";
                    return id.Contains(procKey, StringComparison.OrdinalIgnoreCase);
                });
            }

            if (session != null)
            {
                await session.TryTogglePlayPauseAsync();
                toggled = true;
            }
        }
        catch { }

        if (!toggled)
        {
            ActiveAppMuteButton_Click(sender, e);
        }

        UpdateActiveAppTooltip();
    }

    private void SetupAudioEventHandlers()
    {
        _audioService.MasterVolumeChanged += OnAudioMasterVolumeChanged;
        _audioService.MasterMuteChanged += OnAudioMasterMuteChanged;
        _audioService.DefaultDeviceChanged += OnAudioDefaultDeviceChanged;
        _audioService.AudioSessionCreated += OnAudioSessionCreated;
    }

    /// <summary>
    /// Called (on the UI thread via DispatcherQueue) whenever a new audio session is created —
    /// for example, when Chrome starts playing audio. Triggers an immediate session refresh so
    /// the new app appears in the mixer without requiring a toggle.
    /// </summary>
    private void OnAudioSessionCreated(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] New audio session detected — refreshing sessions");
        // Only refresh if the mixer panel or expanded bar is visible (avoid unnecessary work when idle)
        if (_state == OverlayState.MixerOpen || _state == OverlayState.Expanded)
        {
            RefreshSessions();
        }
    }

    private void OnAudioMasterVolumeChanged(object? sender, float volume)
    {
        if (_isUpdatingSlider)
            return;

        // Ignore the echo of our own coalesced write while the user is dragging the slider.
        if ((DateTime.UtcNow - _lastMasterSliderChange).TotalMilliseconds < 800)
            return;

        _isUpdatingSlider = true;
        MasterVolumeSlider.Value = volume * 100;
        if (PercentText != null) PercentText.Text = $"{(int)(volume * 100)}%";
        _isUpdatingSlider = false;
        UpdateSpeakerIcon();
    }

    private void OnAudioMasterMuteChanged(object? sender, bool mute)
    {
        UpdateSpeakerIcon();
    }

    private void OnAudioDefaultDeviceChanged(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] Default device changed, refreshing state");
        UpdateMasterVolumeFromAudio();
    }

    private void UpdateMasterVolumeFromAudio()
    {
        try
        {
            var volume = _audioService.GetMasterVolume();

            // Skip if audio thread hasn't initialized yet — MasterVolumeChanged will fire when ready
            if (volume < 0) return;

            _isUpdatingSlider = true;
            MasterVolumeSlider.Value = volume * 100;
            if (PercentText != null) PercentText.Text = $"{(int)(volume * 100)}%";
            _isUpdatingSlider = false;

            UpdateSpeakerIcon();

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Updated from audio: Volume={volume:P0}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] UpdateMasterVolumeFromAudio error: {ex.Message}");
        }
    }

    private void UpdateSpeakerIcon()
    {
        // Null checks - can be called during initialization
        if (_audioService == null || SpeakerIcon == null || MasterVolumeSlider == null)
            return;
            
        var mute = _audioService.GetMasterMute();
        var volume = (int)MasterVolumeSlider.Value;

        if (mute || volume == 0)
        {
            SpeakerIcon.Glyph = "\uE74F"; // Muted
        }
        else if (volume < 33)
        {
            SpeakerIcon.Glyph = "\uE993"; // Low
        }
        else if (volume < 66)
        {
            SpeakerIcon.Glyph = "\uE994"; // Medium
        }
        else
        {
            SpeakerIcon.Glyph = "\uE767"; // High
        }
    }

    private void ApplyTheme()
    {
        var settings = ServiceContainer.Settings.VolumeMixer;
        var elementTheme = settings.ThemeMode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = elementTheme;
        }

        System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Theme applied: {settings.ThemeMode}");
    }

    private void PositionWindow()
    {
        var settings = ServiceContainer.Settings.VolumeMixer;
        
        try
        {
            GetDimensionsForState(_state, out int totalWidth, out int totalHeight);

            if (settings.CustomX.HasValue && settings.CustomY.HasValue)
            {
                // CustomX/CustomY hold the idle chip's top-left, i.e. the speaker icon anchor
                _customAnchorCenterX = (int)settings.CustomX.Value + (settings.IconSize + 12) / 2;
                _customAnchorY = (int)settings.CustomY.Value;
                _customAnchorRight = settings.CustomAnchorRight;
            }

            ComputeWindowPosition(totalWidth, totalHeight, out int newX, out int newY);
            var bounds = new RectInt32(newX, newY, totalWidth, totalHeight);
            AppWindow?.MoveAndResize(bounds);
            UpdateWindowRegion(totalWidth, totalHeight);

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Positioned at ({newX}, {newY}) size ({totalWidth}x{totalHeight})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Position error: {ex}");
        }
    }

    // ── State Machine ──────────────────────────────────────────────────────────

    private void TransitionToExpanded()
    {
        if (_state == OverlayState.Expanded || _state == OverlayState.MixerOpen)
            return;

        _state = OverlayState.Expanded;
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] State: Idle → Expanded");

        var settings = ServiceContainer.Settings.VolumeMixer;

        // Animate opacity of root to HoverOpacity
        AnimateOpacity(settings.HoverOpacity / 100.0, settings.AnimationMs);

        // Capture foreground window and start polling sessions so the active app slider shows immediately
        CaptureForegroundWindow();
        StartPolling();

        GetDimensionsForState(OverlayState.Expanded, out int targetW, out int targetH);
        AnimateToSize(targetW, targetH, isExpanding: true, durationMs: settings.AnimationMs);
    }

    private void StartCollapseTimer()
    {
        _hideDelayTimer?.Stop();
        
        var settings = ServiceContainer.Settings.VolumeMixer;
        _hideDelayTimer = DispatcherQueue.CreateTimer();
        _hideDelayTimer.Interval = TimeSpan.FromMilliseconds(settings.MixerHideDelayMs);
        _hideDelayTimer.IsRepeating = false;
        _hideDelayTimer.Tick += (s, e) =>
        {
            if (!_isPointerOverBar && !_isPointerOverPanel)
            {
                CollapseToIdle();
            }
        };
        _hideDelayTimer.Start();

        System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Collapse timer started: {settings.MixerHideDelayMs}ms");
    }

    private void CollapseToIdle()
    {
        if (_state == OverlayState.Idle)
            return;

        // Close mixer panel first if open
        if (_state == OverlayState.MixerOpen)
        {
            MixerPanelAbove.Visibility = Visibility.Collapsed;
            MixerPanelBelow.Visibility = Visibility.Collapsed;
        }

        StopPolling();

        _state = OverlayState.Idle;
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] State: Expanded → Idle");

        // Hide active app row state
        ShowActiveAppRow(false);

        var settings = ServiceContainer.Settings.VolumeMixer;

        // Animate opacity of root to IdleOpacity
        AnimateOpacity(settings.IdleOpacity / 100.0, settings.AnimationMs);

        GetDimensionsForState(OverlayState.Idle, out int targetW, out int targetH);
        AnimateToSize(targetW, targetH, isExpanding: false, durationMs: settings.AnimationMs);
    }

    private void ToggleMixerPanel()
    {
        // Panels open toward the interior: below the bar when anchored near the top, above it when
        // anchored near the bottom (dragged chips use their actual position, presets their name).
        bool panelIsBelow = !IsAnchoredBottom();
        var settings = ServiceContainer.Settings.VolumeMixer;

        if (_state == OverlayState.MixerOpen)
        {
            MixerPanelAbove.Visibility = Visibility.Collapsed;
            MixerPanelBelow.Visibility = Visibility.Collapsed;
            _state = OverlayState.Expanded;
            // Keep polling going while expanded so active app slider stays live
            ResizeWindowToContent();
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] State: MixerOpen → Expanded");
        }
        else
        {
            // Reload settings to pick up any changes made in settings page
            ApplySettings();
            
            // Clear stale rows so polling always starts fresh
            _appSessions.Clear();
            AppRowsContainer.Children.Clear();
            AppRowsContainer.Children.Add(EmptyStateText);
            AppRowsContainerAbove.Children.Clear();
            AppRowsContainerAbove.Children.Add(EmptyStateTextAbove);

            if (panelIsBelow)
            {
                MixerPanelBelow.Visibility = Visibility.Visible;
                MixerPanelAbove.Visibility = Visibility.Collapsed;
            }
            else
            {
                MixerPanelAbove.Visibility = Visibility.Visible;
                MixerPanelBelow.Visibility = Visibility.Collapsed;
            }

            _state = OverlayState.MixerOpen;
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] State: Expanded → MixerOpen");

            // Apply hover opacity when mixer panel opens (user just clicked the icon)
            AnimateOpacity(settings.HoverOpacity / 100.0, settings.AnimationMs);

            // Start/restart polling - this will immediately refresh sessions with new state
            StopPolling(); // Explicitly stop first to ensure clean restart
            StartPolling();
            
            ResizeWindowToContent();

            // Sync all play/pause icons when the panel opens
            SyncAllPlayPauseIcons();
        }
    }

    // Always route to the currently-visible panel's container
    private StackPanel ActiveAppRowsContainer =>
        MixerPanelAbove.Visibility == Visibility.Visible ? AppRowsContainerAbove : AppRowsContainer;

    private TextBlock ActiveEmptyStateText =>
        MixerPanelAbove.Visibility == Visibility.Visible ? EmptyStateTextAbove : EmptyStateText;

    private void PositionMixerPanel()
    {
        // No-op: layout handled by StackPanel in XAML
    }

    private void AnimateOpacity(double targetOpacity, int durationMs)
    {
        double target = Math.Clamp(targetOpacity, 0.0, 1.0);

        _opacityTimer?.Stop();
        _opacityTimer = null;

        if (!_layeredAlphaReady || durationMs <= 0)
        {
            ApplyWindowAlpha(target);
            return;
        }

        double startOpacity = _overlayOpacity;
        var startTime = DateTime.UtcNow;

        _opacityTimer = DispatcherQueue.CreateTimer();
        _opacityTimer.Interval = TimeSpan.FromMilliseconds(16);
        _opacityTimer.IsRepeating = true;
        _opacityTimer.Tick += (s, e) =>
        {
            double progress = Math.Min(1.0, (DateTime.UtcNow - startTime).TotalMilliseconds / durationMs);
            double ease = 1.0 - Math.Pow(1.0 - progress, 3);
            ApplyWindowAlpha(startOpacity + (target - startOpacity) * ease);

            if (progress >= 1.0)
            {
                _opacityTimer?.Stop();
                _opacityTimer = null;
            }
        };
        _opacityTimer.Start();
    }

    /// <summary>
    /// Sets the whole window's opacity through Win32 layered-window alpha. Unlike RootGrid.Opacity
    /// this really blends the overlay with whatever is behind it, so the idle chip is see-through
    /// at the configured idle opacity.
    /// </summary>
    private void ApplyWindowAlpha(double opacity)
    {
        _overlayOpacity = Math.Clamp(opacity, 0.0, 1.0);
        if (!_layeredAlphaReady) return;

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == 0) return;

            nint exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                SetWindowPos(hWnd, nint.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }

            byte alpha = (byte)Math.Clamp((int)Math.Round(_overlayOpacity * 255.0), 1, 255);
            SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ApplyWindowAlpha error: {ex.Message}");
        }
    }

    private void GetDimensionsForState(OverlayState state, out int width, out int height)
    {
        var settings = ServiceContainer.Settings.VolumeMixer;
        int iconSize = settings.IconSize;
        int baseBarHeight = iconSize + 12; // 32 + 12 = 44px
        
        // Height must match the real layout: every control row is at least one icon high, each
        // device row adds a 4px top margin, and the device rows sit in a StackPanel with 4px
        // spacing between them. Under-counting clips the last row (camera brightness).
        int rowContent = Math.Max(iconSize, 32); // Slider rows are at least 32px tall
        int rowGap = 4;
        int deviceRows = _speakerRowCount + _microphoneRowCount + _brightnessRowCount;

        int barContentHeight = rowContent; // Master volume
        if (_hasActiveAppRow) barContentHeight += rowContent + rowGap;
        if (deviceRows > 0)
        {
            barContentHeight += deviceRows * (rowContent + rowGap) + (deviceRows - 1) * rowGap;
        }
        if (_hasCameraRow) barContentHeight += rowContent + rowGap;

        // CardBorder padding (6 top + 6 bottom) plus a little breathing room so the last row
        // (camera brightness) is never clipped by the rounded card edge.
        int barHeight = barContentHeight + 12 + 6;

        if (state == OverlayState.Idle)
        {
            // Exact circular chip (iconSize + 12 padding, e.g. 44x44)
            width = baseBarHeight;
            height = baseBarHeight;
        }
        else if (state == OverlayState.MixerOpen)
        {
            int expandedW = iconSize + settings.SliderWidth + 102;
            width = Math.Max(expandedW, 340);

            // Calculate mixer panel height based on app count
            int headerHeight = 28;
            int dividerHeight = 15;
            int rowHeight = 40;
            int appCount = _appSessions.Count;
            int spacingHeight = appCount > 1 ? (appCount - 1) * 6 : 0;
            int contentHeight = (appCount * rowHeight) + spacingHeight;
            int paddingHeight = 26;

            int panelHeight = headerHeight + dividerHeight + contentHeight + paddingHeight;
            // Minimum height: ensure empty state message fits (text + 48px margins)
            panelHeight = Math.Max(panelHeight, 120);
            panelHeight = Math.Min(panelHeight, 400);

            height = barHeight + 4 + panelHeight; // Spacing = 4
        }
        else // Expanded
        {
            width = iconSize + settings.SliderWidth + 102;
            height = barHeight;
        }
    }

    /// <summary>
    /// Horizontal offset from the window's left edge to the speaker icon's centre for the current
    /// state. The idle chip is a circle around the icon; the bar keeps the icon at
    /// CardBorder.padding (8) + icon size / 2.
    /// </summary>
    private int GetIconCenterOffsetX()
    {
        int iconSize = ServiceContainer.Settings.VolumeMixer.IconSize;
        return _state == OverlayState.Idle
            ? (iconSize + 12) / 2
            : iconSize / 2 + 8;
    }

    /// <summary>
    /// Snaps the window flush against the nearest work-area edge (left/right for X, top/bottom for
    /// Y) when it was dropped within <see cref="EdgeSnapThreshold"/> pixels of one, so the overlay
    /// parks exactly on the edge instead of a few pixels away.
    /// </summary>
    private void SnapWindowToNearestEdge()
    {
        var appWindow = AppWindow;
        if (appWindow == null) return;

        try
        {
            var pos = appWindow.Position;
            var size = appWindow.Size;
            var workArea = DisplayArea.Primary.WorkArea;

            int minX = workArea.X;
            int maxX = workArea.X + workArea.Width - size.Width;
            int minY = workArea.Y;
            int maxY = workArea.Y + workArea.Height - size.Height;

            int newX = pos.X;
            if (Math.Abs(pos.X - minX) <= EdgeSnapThreshold) newX = minX;
            else if (Math.Abs(pos.X - maxX) <= EdgeSnapThreshold) newX = maxX;

            int newY = pos.Y;
            if (Math.Abs(pos.Y - minY) <= EdgeSnapThreshold) newY = minY;
            else if (Math.Abs(pos.Y - maxY) <= EdgeSnapThreshold) newY = maxY;

            newX = Math.Clamp(newX, minX, maxX);
            newY = Math.Clamp(newY, minY, maxY);

            if (newX != pos.X || newY != pos.Y)
            {
                appWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
                System.Diagnostics.Debug.WriteLine(
                    $"[VolumeMixer] Snapped to edge: ({pos.X},{pos.Y}) → ({newX},{newY})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] SnapWindowToNearestEdge error: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists the current position as the speaker icon's screen anchor (stored as the idle
    /// chip's top-left in CustomX/CustomY). Every state is then positioned from that icon anchor,
    /// so the overlay never drifts sideways and the chip can be parked flush against a screen edge.
    /// </summary>
    private void SaveWindowPositionAsIconAnchor()
    {
        var appWindow = AppWindow;
        if (appWindow == null) return;

        // Park flush against an edge when the overlay was dropped near one
        SnapWindowToNearestEdge();

        var pos = appWindow.Position;
        var size = appWindow.Size;
        var settings = ServiceContainer.Settings.VolumeMixer;
        var workArea = DisplayArea.Primary.WorkArea;

        int idleWidth = settings.IconSize + 12;
        int idleHeight = settings.IconSize + 12;
        int iconCenterX = pos.X + GetIconCenterOffsetX();

        // Dropped against the right end of the work area → keep the RIGHT edge anchored, so the
        // collapsed chip stays on the right edge and the bar grows inward from it.
        bool anchorRight = pos.X + size.Width >= workArea.X + workArea.Width - 12;

        // Anchor Y is the idle chip's top edge. For a bottom-anchored overlay keep the chip's
        // BOTTOM edge fixed, so expanding grows upward and collapsing returns to the same spot.
        bool bottomAnchored = pos.Y + size.Height / 2 > workArea.Y + workArea.Height / 2;
        int chipTop = bottomAnchored ? pos.Y + size.Height - idleHeight : pos.Y;

        _customAnchorCenterX = iconCenterX;
        _customAnchorY = chipTop;
        _customAnchorRight = anchorRight;

        settings.CustomX = anchorRight
            ? pos.X + size.Width - idleWidth
            : iconCenterX - idleWidth / 2;
        settings.CustomY = chipTop;
        settings.CustomAnchorRight = anchorRight;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

        System.Diagnostics.Debug.WriteLine(
            $"[VolumeMixer] Position saved: icon anchor ({iconCenterX}, {pos.Y}), right-anchored: {anchorRight}");
    }

    /// <summary>
    /// True when the overlay is anchored towards the bottom of the work area, so the bar/panel has
    /// to grow upward and the chip's bottom edge stays where it was dropped (bottom-left /
    /// bottom-right corners). Falls back to the named icon position when nothing was dragged.
    /// </summary>
    private bool IsAnchoredBottom()
    {
        var settings = ServiceContainer.Settings.VolumeMixer;

        int? chipTop = _customAnchorY
            ?? (settings.CustomY.HasValue ? (int)settings.CustomY.Value : null);

        if (!chipTop.HasValue)
            return settings.IconPosition.StartsWith("bottom");

        var workArea = DisplayArea.Primary.WorkArea;
        int idleHeight = settings.IconSize + 12;
        int chipCenterY = chipTop.Value + idleHeight / 2;

        return chipCenterY > workArea.Y + workArea.Height / 2;
    }

    private void ComputeWindowPosition(int totalWidth, int totalHeight, out int newX, out int newY)
    {
        var settings = ServiceContainer.Settings.VolumeMixer;
        var displayArea = DisplayArea.Primary;
        var workArea = displayArea.WorkArea;
        const int margin = 12;

        int iconSize = settings.IconSize;
        int idleHeight = iconSize + 12;

        if (settings.CustomX.HasValue && settings.CustomY.HasValue)
        {
            int idleWidth = iconSize + 12;

            if (_customAnchorRight)
            {
                // Right edge of the window stays on the anchor, so the collapsed chip stays against
                // the right end of the screen while the bar grows inward from it.
                newX = ((int)settings.CustomX.Value + idleWidth) - totalWidth;
            }
            else
            {
                // Anchor on the speaker icon's screen position, not the window centre. Anchoring on
                // the centre made the collapsed chip land in the middle of the expanded bar and
                // kept it away from the screen edges.
                int iconCenterX = _customAnchorCenterX ?? ((int)settings.CustomX.Value + idleWidth / 2);
                newX = iconCenterX - GetIconCenterOffsetX();
            }

            int chipTop = _customAnchorY ?? (int)settings.CustomY.Value;

            if (IsAnchoredBottom())
            {
                // Bottom anchoring (bottom-left / bottom-right corners): keep the chip's bottom edge
                // fixed and grow the bar/panel upward from it.
                newY = chipTop + idleHeight - totalHeight;
            }
            else
            {
                newY = chipTop;
            }

            newX = Math.Max(workArea.X, Math.Min(workArea.X + workArea.Width - totalWidth, newX));
            newY = Math.Max(workArea.Y, Math.Min(workArea.Y + workArea.Height - totalHeight, newY));
        }
        else
        {
            newX = settings.IconPosition switch
            {
                "top-left" or "bottom-left" => workArea.X + margin,
                "top-right" or "bottom-right" => workArea.X + workArea.Width - totalWidth - margin,
                "top-center" or "bottom-center" => workArea.X + (workArea.Width - totalWidth) / 2,
                _ => workArea.X + (workArea.Width - totalWidth) / 2
            };

            newY = settings.IconPosition switch
            {
                "top-left" or "top-right" or "top-center" => workArea.Y + margin,
                "bottom-left" or "bottom-right" or "bottom-center" => workArea.Y + workArea.Height - totalHeight - margin,
                _ => workArea.Y + margin
            };
        }
    }

    private void AnimateToSize(int targetWidth, int targetHeight, bool isExpanding, int durationMs = 250)
    {
        var appWindow = AppWindow;
        if (appWindow == null) return;

        var currentSize = appWindow.Size;
        int startW = currentSize.Width;
        int startH = currentSize.Height;

        if (startW == targetWidth && startH == targetHeight && _sizeAnimationTimer == null)
        {
            return;
        }

        if (durationMs <= 0)
        {
            _sizeAnimationTimer?.Stop();
            _sizeAnimationTimer = null;
            ComputeWindowPosition(targetWidth, targetHeight, out int x, out int y);
            appWindow.MoveAndResize(new RectInt32(x, y, targetWidth, targetHeight));
            UpdateWindowRegion(targetWidth, targetHeight);
            CardBorder.Width = double.NaN;
            if (isExpanding)
            {
                if (ExpandableControlsGrid != null)
                {
                    ExpandableControlsGrid.Visibility = Visibility.Visible;
                    ExpandableControlsGrid.Opacity = 1.0;
                }
                ShowMicrophoneControls();
                CardBorder.Padding = new Thickness(8, 6, 8, 6);
                CardBorder.CornerRadius = new CornerRadius(_hasActiveAppRow ? 18 : 20);
            }
            else
            {
                if (ExpandableControlsGrid != null)
                {
                    ExpandableControlsGrid.Visibility = Visibility.Collapsed;
                    ExpandableControlsGrid.Opacity = 0.0;
                }
                HideMicrophoneControls();
                CardBorder.Padding = new Thickness(6);
                int idleH = ServiceContainer.Settings.VolumeMixer.IconSize + 12;
                CardBorder.CornerRadius = new CornerRadius(idleH / 2.0);
            }
            return;
        }

        _sizeAnimationTimer?.Stop();

        _animStartW = startW;
        _animTargetW = targetWidth;
        _animStartH = startH;
        _animTargetH = targetHeight;
        _animStartTime = DateTime.UtcNow;
        _animDurationMs = durationMs;
        _isExpanding = isExpanding;

        if (isExpanding)
        {
            if (ExpandableControlsGrid != null)
            {
                ExpandableControlsGrid.Visibility = Visibility.Visible;
                ExpandableControlsGrid.Opacity = 0;
            }
            ShowMicrophoneControls();
            CardBorder.Padding = new Thickness(8, 6, 8, 6);
        }

        _sizeAnimationTimer = DispatcherQueue.CreateTimer();
        _sizeAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _sizeAnimationTimer.IsRepeating = true;
        _sizeAnimationTimer.Tick += (s, e) =>
        {
            var elapsed = (DateTime.UtcNow - _animStartTime).TotalMilliseconds;
            var progress = Math.Min(1.0, elapsed / _animDurationMs);

            // Cubic ease out: smooth decelerating motion
            double ease = 1.0 - Math.Pow(1.0 - progress, 3);

            int curW = (int)Math.Round(_animStartW + (_animTargetW - _animStartW) * ease);
            int curH = (int)Math.Round(_animStartH + (_animTargetH - _animStartH) * ease);

            // Compute X and Y based on current W and H
            ComputeWindowPosition(curW, curH, out int curX, out int curY);

            // Move and resize window
            appWindow.MoveAndResize(new RectInt32(curX, curY, curW, curH));
            UpdateWindowRegion(curW, curH);

            // Adjust controls opacity during expansion/collapse
            if (ExpandableControlsGrid != null)
            {
                if (_isExpanding)
                {
                    // Fade in once width has expanded slightly (past 20%)
                    ExpandableControlsGrid.Opacity = Math.Clamp((progress - 0.2) / 0.8, 0.0, 1.0);
                }
                else
                {
                    // Fade out early in the collapse (during first 40%)
                    ExpandableControlsGrid.Opacity = Math.Clamp(1.0 - (progress / 0.4), 0.0, 1.0);
                }
            }

            // Adjust corner radius smoothly between pill (22) and rounded bar (18/20)
            int idleH = ServiceContainer.Settings.VolumeMixer.IconSize + 12;
            double targetRadius = _isExpanding ? (_hasActiveAppRow ? 18.0 : 20.0) : (idleH / 2.0);
            double startRadius = _isExpanding ? (idleH / 2.0) : (_hasActiveAppRow ? 18.0 : 20.0);
            double curRadius = startRadius + (targetRadius - startRadius) * ease;
            CardBorder.CornerRadius = new CornerRadius(curRadius);

            if (progress >= 1.0)
            {
                _sizeAnimationTimer?.Stop();
                _sizeAnimationTimer = null;

                if (!_isExpanding)
                {
                    if (ExpandableControlsGrid != null)
                    {
                        ExpandableControlsGrid.Visibility = Visibility.Collapsed;
                        ExpandableControlsGrid.Opacity = 0.0;
                    }
                    CardBorder.Padding = new Thickness(6);
                    CardBorder.CornerRadius = new CornerRadius(idleH / 2.0);
                }
                else
                {
                    if (ExpandableControlsGrid != null)
                    {
                        ExpandableControlsGrid.Opacity = 1.0;
                    }
                    CardBorder.CornerRadius = new CornerRadius(_hasActiveAppRow ? 18 : 20);
                }
                CardBorder.Width = double.NaN;
                UpdateWindowRegion(targetWidth, targetHeight);
            }
        };
        _sizeAnimationTimer.Start();
    }

    private void ResizeWindowToContent()
    {
        try
        {
            GetDimensionsForState(_state, out int totalWidth, out int totalHeight);

            if (_sizeAnimationTimer != null)
            {
                _animTargetW = totalWidth;
                _animTargetH = totalHeight;
                return;
            }

            var appWindow = AppWindow;
            if (appWindow != null)
            {
                ComputeWindowPosition(totalWidth, totalHeight, out int newX, out int newY);
                appWindow.MoveAndResize(new RectInt32(newX, newY, totalWidth, totalHeight));
                UpdateWindowRegion(totalWidth, totalHeight);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Resized to {totalWidth}x{totalHeight} at ({newX},{newY})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Resize error: {ex}");
        }
    }

    // ── Event Handlers ─────────────────────────────────────────────────────────

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverBar = true;
        _hideDelayTimer?.Stop();

        // Capture current foreground window right as pointer enters
        CaptureForegroundWindow();

        // If currently animating collapse, reverse immediately back to expanded!
        if (_sizeAnimationTimer != null && !_isExpanding)
        {
            _hoverDelayTimer?.Stop();
            TransitionToExpanded();
            return;
        }

        // Start hover delay timer
        if (_state == OverlayState.Idle)
        {
            var settings = ServiceContainer.Settings.VolumeMixer;
            
            _hoverDelayTimer?.Stop();
            _hoverDelayTimer = DispatcherQueue.CreateTimer();
            _hoverDelayTimer.Interval = TimeSpan.FromMilliseconds(settings.HoverDelayMs);
            _hoverDelayTimer.IsRepeating = false;
            _hoverDelayTimer.Tick += (s, args) =>
            {
                if (_isPointerOverBar && _state == OverlayState.Idle)
                {
                    TransitionToExpanded();
                }
            };
            _hoverDelayTimer.Start();

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Hover delay started: {settings.HoverDelayMs}ms");
        }
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverBar = false;
        _hoverDelayTimer?.Stop();

        // Start hide delay timer if not dragging and not over panel
        if (!_isDragging && !_isPointerOverPanel && (_state != OverlayState.Idle || (_sizeAnimationTimer != null && _isExpanding)))
        {
            StartCollapseTimer();
        }
    }

    private void MixerPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPanel = true;
        _hideDelayTimer?.Stop();
        
        // Apply hover opacity when hovering over mixer panel
        var settings = ServiceContainer.Settings.VolumeMixer;
        AnimateOpacity(settings.HoverOpacity / 100.0, settings.AnimationMs);
    }

    private void MixerPanel_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPanel = false;

        // Start hide delay timer if not over bar
        if (!_isPointerOverBar && _state != OverlayState.Idle)
        {
            StartCollapseTimer();
        }
    }

    private void SpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle master mute (real audio control)
        var currentMute = _audioService.GetMasterMute();
        _audioService.SetMasterMute(!currentMute);
        
        System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Master mute toggled: {!currentMute}");
    }

    private void MixerButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMixerPanel();
    }

    private void MasterVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Null check - this can fire during initialization before XAML elements are ready
        if (PercentText != null)
        {
            PercentText.Text = $"{(int)e.NewValue}%";
        }
        
        // Set real system volume (avoid feedback loop); coalesced so dragging stays smooth
        if (!_isUpdatingSlider && _audioService != null)
        {
            QueueMasterVolume((float)(e.NewValue / 100.0));
        }
        
        UpdateSpeakerIcon();
    }

    private void MasterVolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        var step = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) ? 1 : 2;
        
        if (delta > 0)
        {
            MasterVolumeSlider.Value = Math.Min(100, MasterVolumeSlider.Value + step);
        }
        else
        {
            MasterVolumeSlider.Value = Math.Max(0, MasterVolumeSlider.Value - step);
        }

        e.Handled = true;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Escape key closes mixer panel
        if (e.Key == Windows.System.VirtualKey.Escape && _state == OverlayState.MixerOpen)
        {
            ToggleMixerPanel();
            e.Handled = true;
        }
    }

    // ── Drag Mode (Smooth screen-coordinate dragging like ClipboardPopupWindow) ──

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var settings = ServiceContainer.Settings.VolumeMixer;
        if (!settings.DragMode || AppWindow == null)
            return;

        var pointer = e.GetCurrentPoint(RootGrid);
        if (pointer.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _hideDelayTimer?.Stop();

            GetCursorPos(out _dragStartCursor);
            _dragStartWindowPos = AppWindow.Position;
            var size = AppWindow.Size;
            _dragStartWidth = size.Width;
            _dragStartHeight = size.Height;
            _lastMoveX = _dragStartWindowPos.X;
            _lastMoveY = _dragStartWindowPos.Y;

            // Visual feedback - use hover opacity for visibility during drag
            ApplyWindowAlpha(settings.HoverOpacity / 100.0);

            (sender as UIElement)?.CapturePointer(e.Pointer);
            e.Handled = true;
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Drag started");
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || AppWindow == null)
            return;

        try
        {
            GetCursorPos(out POINT currentCursor);
            int dx = currentCursor.x - _dragStartCursor.x;
            int dy = currentCursor.y - _dragStartCursor.y;

            int targetX = _dragStartWindowPos.X + dx;
            int targetY = _dragStartWindowPos.Y + dy;

            // Clamp to work area
            var workArea = DisplayArea.Primary.WorkArea;
            int newX = Math.Max(workArea.X, Math.Min(workArea.X + workArea.Width - _dragStartWidth, targetX));
            int newY = Math.Max(workArea.Y, Math.Min(workArea.Y + workArea.Height - _dragStartHeight, targetY));

            if (newX != _lastMoveX || newY != _lastMoveY)
            {
                _lastMoveX = newX;
                _lastMoveY = newY;
                AppWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Drag move error: {ex.Message}");
        }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        // Restore appropriate opacity based on pointer position
        var settings = ServiceContainer.Settings.VolumeMixer;
        
        // Check if pointer is still over the overlay after drag
        var pointer = e.GetCurrentPoint(RootGrid);
        var bounds = new Windows.Foundation.Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
        bool pointerStillOver = bounds.Contains(pointer.Position);
        
        // Use hover opacity if pointer is still over, otherwise idle
        ApplyWindowAlpha(pointerStillOver ? (settings.HoverOpacity / 100.0) : (settings.IdleOpacity / 100.0));

        // Save custom position (anchored on the speaker icon, not the window centre)
        SaveWindowPositionAsIconAnchor();
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] Drag ended");
    }

    private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        var settings = ServiceContainer.Settings.VolumeMixer;
        ApplyWindowAlpha(settings.IdleOpacity / 100.0);

        SaveWindowPositionAsIconAnchor();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when settings change externally - reapplies all settings to the window.
    /// </summary>
    public void RefreshSettings()
    {
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] RefreshSettings called");
        ApplySettings();
        ApplyTheme();
        PositionWindow();
        
        // Reinitialize camera and microphone to reflect visibility changes
        _ = InitializeCameraAsync();
        _ = InitializeMicrophoneAsync();
    }

    public new void Close()
    {
        _hoverDelayTimer?.Stop();
        _hideDelayTimer?.Stop();
        _pollingTimer?.Stop();
        _topmostTimer?.Stop();
        _cameraSyncTimer?.Stop();
        _cameraSyncTimer = null;
        _sizeAnimationTimer?.Stop();
        _sizeAnimationTimer = null;

        if (_foregroundEventHook != nint.Zero)
        {
            UnhookWinEvent(_foregroundEventHook);
            _foregroundEventHook = nint.Zero;
            _foregroundEventDelegate = null;
        }

        // Unsubscribe from audio events
        _audioService.MasterVolumeChanged -= OnAudioMasterVolumeChanged;
        _audioService.MasterMuteChanged -= OnAudioMasterMuteChanged;
        _audioService.DefaultDeviceChanged -= OnAudioDefaultDeviceChanged;
        _audioService.AudioSessionCreated -= OnAudioSessionCreated;

        // Dispose camera service
        try
        {
            if (_cameraService != null)
            {
                _cameraService.BrightnessChanged -= OnCameraBrightnessChanged;
                _cameraService.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Camera disposal error: {ex.Message}");
        }

        // Dispose microphone service
        try
        {
            _microphoneService?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Microphone disposal error: {ex.Message}");
        }

        try
        {
            base.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Close error: {ex.Message}");
        }
    }
}
