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
    private OverlayState _state = OverlayState.Idle;
    private DispatcherQueueTimer? _hoverDelayTimer;
    private DispatcherQueueTimer? _hideDelayTimer;
    private DispatcherQueueTimer? _pollingTimer;
    private DispatcherQueueTimer? _topmostTimer;  // Periodically re-enforce topmost z-order
    private bool _isPointerOverBar;
    private bool _isPointerOverPanel;
    private bool _isUpdatingSlider; // Flag to prevent feedback loops

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, IntPtr lpRect, bool bErase);

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

    // SMTC session cache: processName (no .exe) → SMTC session
    private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _smtcSessions = new();

    public VolumeOverlayWindow(AudioMixerService audioService)
    {
        try
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _iconResolver = new AppIconResolver();

            // Suppress slider ValueChanged from XAML default Value="50" writing to the audio device
            _isUpdatingSlider = true;
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling InitializeComponent...");
            this.InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] InitializeComponent complete");
            _isUpdatingSlider = false;
            
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
                // Log current window state
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ApplyNoActivateStyle called. HWND: {hWnd}");
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Window Visible: {this.Visible}, Content: {this.Content != null}");
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] RootGrid Visibility: {RootGrid.Visibility}, Opacity: {RootGrid.Opacity}");
                
                nint exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Current ExStyle: 0x{exStyle:X}");
                
                // NOTE: Removed WS_EX_TOOLWINDOW as it can make the window invisible in some scenarios
                // We only need WS_EX_NOACTIVATE (prevents focus steal) and WS_EX_TOPMOST (always on top)
                exStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle);
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] New ExStyle: 0x{exStyle:X}");

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

            // Idle opacity
            RootGrid.Opacity = settings.IdleOpacity / 100.0;

            // Drag mode outline and cursor
            DragOutline.Visibility = settings.DragMode ? Visibility.Visible : Visibility.Collapsed;
            // Note: Grid.Cursor not available in WinUI 3 - cursor change would need UIElement.ProtectedCursor
            // Drag outline provides visual feedback instead

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
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ProcessSessionSnapshot: received {sessions.Count} sessions");
            foreach (var s in sessions)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer]   Session: PID={s.ProcessId}, Name={GetProcessName(s.ProcessId)}, State={s.State}");
            }
            
            var settings = ServiceContainer.Settings.VolumeMixer;

            // Apply blacklist filter
            var blacklist = settings.Blacklist
                .Select(b => b.ToLowerInvariant().TrimEnd())
                // Normalize: strip .exe for comparison since GetProcessName returns name without extension
                .Select(b => b.EndsWith(".exe") ? b[..^4] : b)
                .ToHashSet();
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Blacklist: {string.Join(", ", blacklist)}");
            
            var beforeBlacklist = sessions.Count;
            sessions = sessions.Where(s =>
            {
                var processName = GetProcessName(s.ProcessId); // already lowercase, no .exe
                var isBlacklisted = blacklist.Contains(processName);
                if (isBlacklisted)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeMixer] FILTERED (blacklist): {processName} (PID {s.ProcessId})");
                }
                return !isBlacklisted;
            }).ToList();
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] After blacklist: {beforeBlacklist} → {sessions.Count} sessions");

            // Apply active-only filter
            // NOTE: State-based filtering is disabled because Windows audio sessions report State=0 (inactive)
            // even when playing audio. The State field is unreliable for determining active playback.
            // Instead, we rely on the fact that GetAudioSessionsAsync only returns sessions with non-zero volume.
            if (false && settings.OnlyActiveApps) // DISABLED - State is unreliable
            {
                var beforeActive = sessions.Count;
                sessions = sessions.Where(s =>
                {
                    var isActive = s.State == 1;
                    if (!isActive)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VolumeMixer] FILTERED (inactive): {GetProcessName(s.ProcessId)} (PID {s.ProcessId}, State={s.State})");
                    }
                    return isActive;
                }).ToList();
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] After active-only filter: {beforeActive} → {sessions.Count} sessions");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Active-only filter disabled (State field unreliable)");
            }

            // Group by process name
            var groups = sessions
                .GroupBy(s => GetProcessName(s.ProcessId))
                .OrderBy(g => g.Key)
                .ToList();

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] ProcessSessionSnapshot: {sessions.Count} sessions → {groups.Count} groups");
            foreach (var g in groups)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer]   Group '{g.Key}': {g.Count()} sessions");
            }

            // Update UI
            UpdateSessionRows(groups);
            UpdateActiveAppSession(groups);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] RefreshSessions error: {ex.Message}");
        }
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return process.ProcessName.ToLowerInvariant();
        }
        catch
        {
            return $"process-{processId}";
        }
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
        
        // Resize window to fit the new row count after a short delay to ensure layout is complete
        if (_state == OverlayState.MixerOpen)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ResizeWindowToContent();
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Resized window for {_appSessions.Count} apps");
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
        ToolTipService.SetToolTip(playPauseButton, "Play / Pause");
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
        muteButton.SetValue(Grid.ColumnProperty, 4);
        grid.Children.Add(muteButton);

        ToolTipService.SetToolTip(grid, viewModel.Name);

        // Wire events
        slider.ValueChanged += (s, e) =>
        {
            if (viewModel.IsUpdating) return;
            percentText.Text = $"{(int)e.NewValue}%";
            var vol = (float)(e.NewValue / 100.0);
            foreach (var id in sessionIds) _audioService.SetSessionVolume(id, vol);
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
                entry.PlayIcon.Glyph = playing ? "\uE769" : "\uE768"; // ⏸ or ▶
            }
            // If no SMTC session, leave icon as-is (stays at ▶ for apps without media transport)
        }

        // Sync universal header icon from the current (foreground) session
        var current = _smtcManager?.GetCurrentSession();
        bool universalPlaying = current?.GetPlaybackInfo()?.PlaybackStatus
                                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var universalGlyph = universalPlaying ? "\uE769" : "\uE768";
        if (UniversalPlayPauseIconAbove != null) UniversalPlayPauseIconAbove.Glyph = universalGlyph;
        if (UniversalPlayPauseIconBelow != null) UniversalPlayPauseIconBelow.Glyph = universalGlyph;

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
                // Handle other edge cases here
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

            // Extract icon bytes on background thread
            byte[]? pngBytes = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon == null) return null;
                    using var bmp = icon.ToBitmap();
                    using var ms = new System.IO.MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
                catch { return null; }
            });

            if (pngBytes == null) return;

            // Load BitmapImage on UI thread properly with await
            var imgSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = new System.IO.MemoryStream(pngBytes).AsRandomAccessStream();
            await imgSource.SetSourceAsync(stream);

            var img = new Image
            {
                Source = imgSource,
                Width = 20, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
            };

            host.Children.Remove(fallback);
            host.Children.Add(img);
        }
        catch { /* keep fallback glyph */ }
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
                var (name, exePath) = _iconResolver.ResolveApp(first.ProcessId, first.DisplayName, first.IconPath);
                _activeAppDisplayName = name;

                float maxVol = targetGroup.Max(s => s.Volume);
                bool allMuted = targetGroup.All(s => s.IsMuted);
                int volPercent = (int)Math.Round(maxVol * 100);

                _isUpdatingActiveAppSlider = true;
                ActiveAppVolumeSlider.Value = volPercent;
                ActiveAppPercentText.Text = $"{volPercent}%";
                _isUpdatingActiveAppSlider = false;

                ActiveAppMuteIcon.Glyph = allMuted ? "\uE74F" : "\uE767";

                UpdateActiveAppTooltip();
                ToolTipService.SetToolTip(ActiveAppVolumeSlider, $"{name}: {volPercent}%");
                ToolTipService.SetToolTip(ActiveAppMuteButton, allMuted ? $"Unmute {name}" : $"Mute {name}");

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
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        ActiveAppHost.Visibility = vis;
        ActiveAppVolumeSlider.Visibility = vis;
        ActiveAppPercentText.Visibility = vis;
        ActiveAppMuteButton.Visibility = vis;

        if (_hasActiveAppRow != show)
        {
            _hasActiveAppRow = show;
            CardBorder.CornerRadius = new CornerRadius(_hasActiveAppRow ? 20 : 24);
            ResizeWindowToContent();
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
            var vol = (float)(e.NewValue / 100.0);
            foreach (var id in _activeAppSessionIds)
            {
                _audioService.SetSessionVolume(id, vol);
            }
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

        if (_smtcManager != null && !string.IsNullOrEmpty(_activeAppProcessName))
        {
            var procKey = _activeAppProcessName.ToLowerInvariant();
            var session = _smtcSessions.FirstOrDefault(kv =>
                kv.Key.Equals(procKey, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Contains(procKey, StringComparison.OrdinalIgnoreCase) ||
                procKey.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;

            if (session != null)
            {
                var status = session.GetPlaybackInfo()?.PlaybackStatus;
                string action = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "Pause" : "Play";
                ToolTipService.SetToolTip(ActiveAppIconButton, string.IsNullOrEmpty(name) ? action : $"{action} ({name})");
                return;
            }
        }

        bool isMuted = ActiveAppMuteIcon?.Glyph == "\uE74F";
        string muteAction = isMuted ? "Unmute" : "Mute";
        ToolTipService.SetToolTip(ActiveAppIconButton, string.IsNullOrEmpty(name) ? muteAction : $"{muteAction} ({name})");
    }

    private async void ActiveAppIconButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeAppProcessName)) return;

        bool toggled = false;
        try
        {
            var procKey = _activeAppProcessName.ToLowerInvariant();
            var session = _smtcSessions.FirstOrDefault(kv =>
                kv.Key.Contains(procKey, StringComparison.OrdinalIgnoreCase) ||
                procKey.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;

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
    }

    private void OnAudioMasterVolumeChanged(object? sender, float volume)
    {
        if (_isUpdatingSlider)
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
            var displayArea = DisplayArea.Primary;
            var workArea = displayArea.WorkArea;
            var dpi = (double)displayArea.OuterBounds.Width / (double)displayArea.WorkArea.Width;

            // Calculate chip size (icon only)
            int chipWidth = settings.IconSize + 24; // icon + padding
            int chipHeight = settings.IconSize + 16;

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Calculated chip size: {chipWidth}x{chipHeight}");
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] WorkArea: X={workArea.X}, Y={workArea.Y}, W={workArea.Width}, H={workArea.Height}");

            int x, y;

            // If custom position exists, use it
            if (settings.CustomX.HasValue && settings.CustomY.HasValue)
            {
                x = (int)settings.CustomX.Value;
                y = (int)settings.CustomY.Value;
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Using custom position: ({x}, {y})");
            }
            else
            {
                // Calculate position based on IconPosition setting
                const int margin = 12;

                x = settings.IconPosition switch
                {
                    "top-left" => workArea.X + margin,
                    "top-right" => workArea.X + workArea.Width - chipWidth - margin,
                    "top-center" => workArea.X + (workArea.Width - chipWidth) / 2,
                    "bottom-left" => workArea.X + margin,
                    "bottom-right" => workArea.X + workArea.Width - chipWidth - margin,
                    "bottom-center" => workArea.X + (workArea.Width - chipWidth) / 2,
                    _ => workArea.X + (workArea.Width - chipWidth) / 2
                };

                y = settings.IconPosition switch
                {
                    "top-left" => workArea.Y + margin,
                    "top-right" => workArea.Y + margin,
                    "top-center" => workArea.Y + margin,
                    "bottom-left" => workArea.Y + workArea.Height - chipHeight - margin,
                    "bottom-right" => workArea.Y + workArea.Height - chipHeight - margin,
                    "bottom-center" => workArea.Y + workArea.Height - chipHeight - margin,
                    _ => workArea.Y + margin
                };
                
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Calculated position for '{settings.IconPosition}': ({x}, {y})");
            }

            var bounds = new RectInt32(x, y, chipWidth, chipHeight);
            AppWindow?.MoveAndResize(bounds);

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Positioned at ({x}, {y}) size ({chipWidth}x{chipHeight})");
            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] AppWindow bounds after move: {AppWindow?.Position.X},{AppWindow?.Position.Y} {AppWindow?.Size.Width}x{AppWindow?.Size.Height}");
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

        // Show slider, percent, and mixer button
        MasterVolumeSlider.Visibility = Visibility.Visible;
        PercentText.Visibility = Visibility.Visible;
        MixerButton.Visibility = Visibility.Visible;

        // Animate opacity to HoverOpacity
        var settings = ServiceContainer.Settings.VolumeMixer;
        AnimateOpacity(settings.HoverOpacity / 100.0, settings.AnimationMs);

        // Capture foreground window and start polling sessions so the active app slider shows immediately
        CaptureForegroundWindow();
        StartPolling();

        // Resize window to fit expanded content
        ResizeWindowToContent();
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

        // Hide slider, percent, and mixer button
        MasterVolumeSlider.Visibility = Visibility.Collapsed;
        PercentText.Visibility = Visibility.Collapsed;
        MixerButton.Visibility = Visibility.Collapsed;

        // Hide active app row
        ShowActiveAppRow(false);

        // Animate opacity to IdleOpacity
        var settings = ServiceContainer.Settings.VolumeMixer;
        AnimateOpacity(settings.IdleOpacity / 100.0, settings.AnimationMs);

        // Resize window back to chip size
        ResizeWindowToContent();
    }

    private void ToggleMixerPanel()
    {
        bool panelIsBelow = !ServiceContainer.Settings.VolumeMixer.IconPosition.StartsWith("bottom");

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

            ResizeWindowToContent();
            StartPolling();

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
        if (durationMs == 0)
        {
            RootGrid.Opacity = targetOpacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, RootGrid);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Begin();
    }

    private void ResizeWindowToContent()
    {
        try
        {
            var settings = ServiceContainer.Settings.VolumeMixer;
            int baseBarHeight = settings.IconSize + 16; // chip/pill height (e.g. 48px)
            int barHeight = _hasActiveAppRow ? baseBarHeight + settings.IconSize + 8 : baseBarHeight;
            int barWidth;
            int totalHeight;
            int totalWidth;

            if (_state == OverlayState.Idle)
            {
                // Chip: just the icon pill
                barWidth = settings.IconSize + 24;
                totalWidth = barWidth;
                totalHeight = baseBarHeight;
            }
            else if (_state == OverlayState.MixerOpen)
            {
                // Bar width + at least 348 to fit the mixer panel (MinWidth=320 + 2*14 padding)
                barWidth = settings.IconSize + 24 + settings.SliderWidth + 12 + 44 + 8 + 32 + 20;
                totalWidth = Math.Max(barWidth, 348);
                
                // Calculate panel height based on app count (more reliable than Measure)
                int headerHeight = 28;
                int dividerHeight = 15;
                int rowHeight = 40;
                int appCount = _appSessions.Count;
                int spacingHeight = appCount > 1 ? (appCount - 1) * 6 : 0;
                int contentHeight = (appCount * rowHeight) + spacingHeight;
                int paddingHeight = 26;
                
                int panelHeight = headerHeight + dividerHeight + contentHeight + paddingHeight;
                panelHeight = Math.Max(panelHeight, 100); // minimum when empty
                panelHeight = Math.Min(panelHeight, 400); // respect MaxHeight
                
                totalHeight = barHeight + 6 + panelHeight;
                
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Calculated height: apps={appCount}, panelH={panelHeight}, totalH={totalHeight}");
            }
            else // Expanded
            {
                barWidth = settings.IconSize + 24 + settings.SliderWidth + 12 + 44 + 8 + 32 + 20;
                totalWidth = barWidth;
                totalHeight = barHeight;
            }

            var appWindow = AppWindow;
            if (appWindow != null)
            {
                var displayArea = DisplayArea.Primary;
                var workArea = displayArea.WorkArea;
                const int margin = 12;

                int newX;
                int newY;

                if (settings.CustomX.HasValue && settings.CustomY.HasValue)
                {
                    newX = (int)settings.CustomX.Value;
                    newY = (int)settings.CustomY.Value;

                    if (_state == OverlayState.MixerOpen && settings.IconPosition.StartsWith("bottom"))
                    {
                        int panelH = totalHeight - barHeight - 6;
                        newY = (int)settings.CustomY.Value - panelH - 6;
                    }
                    else if (_hasActiveAppRow && settings.IconPosition.StartsWith("bottom"))
                    {
                        int diff = barHeight - baseBarHeight;
                        newY = (int)settings.CustomY.Value - diff;
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

                appWindow.MoveAndResize(new RectInt32(newX, newY, totalWidth, totalHeight));
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
        if (!_isDragging && !_isPointerOverPanel && _state != OverlayState.Idle)
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
        
        // Set real system volume (avoid feedback loop)
        if (!_isUpdatingSlider && _audioService != null)
        {
            _audioService.SetMasterVolume((float)(e.NewValue / 100.0));
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

            // Visual feedback
            RootGrid.Opacity = Math.Max(0.7, (settings.IdleOpacity / 100.0) - 0.1);

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

        // Restore normal opacity
        var settings = ServiceContainer.Settings.VolumeMixer;
        RootGrid.Opacity = settings.IdleOpacity / 100.0;

        // Save custom position
        var appWindow = AppWindow;
        if (appWindow != null)
        {
            var pos = appWindow.Position;
            ServiceContainer.Settings.VolumeMixer.CustomX = pos.X;
            ServiceContainer.Settings.VolumeMixer.CustomY = pos.Y;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Drag ended, position saved: ({pos.X}, {pos.Y})");
        }
    }

    private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        var settings = ServiceContainer.Settings.VolumeMixer;
        RootGrid.Opacity = settings.IdleOpacity / 100.0;

        var appWindow = AppWindow;
        if (appWindow != null)
        {
            var pos = appWindow.Position;
            ServiceContainer.Settings.VolumeMixer.CustomX = pos.X;
            ServiceContainer.Settings.VolumeMixer.CustomY = pos.Y;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
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
    }

    public new void Close()
    {
        _hoverDelayTimer?.Stop();
        _hideDelayTimer?.Stop();
        _pollingTimer?.Stop();
        _topmostTimer?.Stop();

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
