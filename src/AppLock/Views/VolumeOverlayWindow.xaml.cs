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
using AppLock.Services;
using AppLock.Services.VolumeMixer;
using AppLock.Models;
using AppLock.ViewModels;
using Windows.Graphics;

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
    private bool _isDragging;
    private bool _isUpdatingSlider; // Flag to prevent feedback loops
    private Windows.Foundation.Point _dragStartPoint;
    private int _dragStartX;
    private int _dragStartY;
    private int _dragStartWidth;
    private int _dragStartHeight;

    // Session tracking
    private readonly Dictionary<string, (MixerSessionViewModel ViewModel, List<string> SessionIds, Grid Row)> _appSessions = new();

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public VolumeOverlayWindow(AudioMixerService audioService)
    {
        try
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _iconResolver = new AppIconResolver();
            
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Calling InitializeComponent...");
            this.InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] InitializeComponent complete");
            
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

            // Slider width
            MasterVolumeSlider.Width = settings.SliderWidth;

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
        if (_pollingTimer != null)
            return;

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
            var settings = ServiceContainer.Settings.VolumeMixer;

            // Apply blacklist filter
            var blacklist = settings.Blacklist.Select(b => b.ToLowerInvariant()).ToHashSet();
            sessions = sessions.Where(s =>
            {
                var processName = GetProcessName(s.ProcessId);
                return !blacklist.Contains(processName.ToLowerInvariant());
            }).ToList();

            // Apply active-only filter
            if (settings.OnlyActiveApps)
            {
                // Filter to only active sessions (State == 1 means AudioSessionStateActive)
                // NAudio AudioSessionState: 0=Inactive, 1=Active, 2=Expired
                sessions = sessions.Where(s => s.State == 1).ToList();
            }

            // Group by process name
            var groups = sessions
                .GroupBy(s => GetProcessName(s.ProcessId))
                .OrderBy(g => g.Key)
                .ToList();

            // Update UI
            UpdateSessionRows(groups);
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
        var currentKeys = _appSessions.Keys.ToHashSet();
        var newKeys = groups.Select(g => g.Key).ToHashSet();

        // Remove closed apps
        var toRemove = currentKeys.Except(newKeys).ToList();
        foreach (var key in toRemove)
        {
            if (_appSessions.TryGetValue(key, out var session))
            {
                AppRowsContainer.Children.Remove(session.Row);
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
                // Update existing app
                existing.ViewModel.IsUpdating = true;
                existing.ViewModel.Volume = group.Max(s => s.Volume);
                existing.ViewModel.IsMuted = group.All(s => s.IsMuted);
                existing.ViewModel.IsUpdating = false;
                existing.SessionIds.Clear();
                existing.SessionIds.AddRange(sessionIds);
            }
            else
            {
                // Add new app
                var (name, exePath) = _iconResolver.ResolveApp(first.ProcessId, first.DisplayName, first.IconPath);
                
                var viewModel = new MixerSessionViewModel
                {
                    Name = name,
                    ProcessName = processName + ".exe",
                    Volume = group.Max(s => s.Volume),
                    IsMuted = group.All(s => s.IsMuted),
                    Icon = null  // Icon extraction deferred to M6
                };

                var row = CreateAppRow(viewModel, sessionIds);
                AppRowsContainer.Children.Add(row);
                _appSessions[processName] = (viewModel, new List<string>(sessionIds), row);

                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Added app: {name} ({processName})");
            }
        }

        // Update empty state
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyStateText.Visibility = _appSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        
        if (_appSessions.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] No apps playing audio");
        }
    }

    private Grid CreateAppRow(MixerSessionViewModel viewModel, List<string> sessionIds)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            Margin = new Thickness(0, 4, 0, 4)
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Slider
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Percent
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Mute

        // App Icon
        var appIcon = new FontIcon
        {
            Glyph = "\uE71D", // Generic app icon
            FontSize = 24
        };
        appIcon.SetValue(Grid.ColumnProperty, 0);
        grid.Children.Add(appIcon);

        // Volume Slider
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = viewModel.VolumePercent,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Add(slider);

        // Percent Text
        var percentText = new TextBlock
        {
            Text = $"{viewModel.VolumePercent}%",
            FontSize = 12,
            MinWidth = 44,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        percentText.SetValue(Grid.ColumnProperty, 2);
        grid.Children.Add(percentText);

        // Mute Button
        var muteButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Content = new FontIcon 
            { 
                Glyph = viewModel.MuteIconGlyph, 
                FontSize = 14 
            }
        };
        muteButton.SetValue(Grid.ColumnProperty, 3);
        grid.Children.Add(muteButton);

        // Wire up events
        slider.ValueChanged += (s, e) =>
        {
            if (viewModel.IsUpdating)
                return;

            var newVolume = (float)(e.NewValue / 100.0);
            percentText.Text = $"{(int)e.NewValue}%";
            
            // Set volume for all sessions of this app
            foreach (var sessionId in sessionIds)
            {
                _audioService.SetSessionVolume(sessionId, newVolume);
            }
        };

        muteButton.Click += (s, e) =>
        {
            var newMute = !viewModel.IsMuted;
            viewModel.IsMuted = newMute;
            
            if (muteButton.Content is FontIcon icon)
            {
                icon.Glyph = viewModel.MuteIconGlyph;
            }

            // Set mute for all sessions of this app
            foreach (var sessionId in sessionIds)
            {
                _audioService.SetSessionMute(sessionId, newMute);
            }
        };

        // Tooltip
        ToolTipService.SetToolTip(grid, viewModel.ToolTipText);

        return grid;
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
        _isUpdatingSlider = false;
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
            var mute = _audioService.GetMasterMute();

            _isUpdatingSlider = true;
            MasterVolumeSlider.Value = volume * 100;
            _isUpdatingSlider = false;

            UpdateSpeakerIcon();

            System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Updated from audio: Volume={volume:P0}, Mute={mute}");
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
            MixerPanel.Visibility = Visibility.Collapsed;
            StopPolling();
        }

        _state = OverlayState.Idle;
        System.Diagnostics.Debug.WriteLine("[VolumeMixer] State: Expanded → Idle");

        // Hide slider, percent, and mixer button
        MasterVolumeSlider.Visibility = Visibility.Collapsed;
        PercentText.Visibility = Visibility.Collapsed;
        MixerButton.Visibility = Visibility.Collapsed;

        // Animate opacity to IdleOpacity
        var settings = ServiceContainer.Settings.VolumeMixer;
        AnimateOpacity(settings.IdleOpacity / 100.0, settings.AnimationMs);

        // Resize window back to chip size
        ResizeWindowToContent();
    }

    private void ToggleMixerPanel()
    {
        if (MixerPanel.Visibility == Visibility.Visible)
        {
            // Close mixer
            MixerPanel.Visibility = Visibility.Collapsed;
            _state = OverlayState.Expanded;
            StopPolling();
            ResizeWindowToContent(); // Shrink window back to bar size
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] State: MixerOpen → Expanded");
        }
        else
        {
            // Open mixer
            MixerPanel.Visibility = Visibility.Visible;
            _state = OverlayState.MixerOpen;
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] State: Expanded → MixerOpen");

            // Position mixer panel relative to icon position
            PositionMixerPanel();
            
            // Resize window to accommodate mixer panel
            ResizeWindowToContent();

            // Start polling for active audio sessions
            StartPolling();
        }
    }

    private void PositionMixerPanel()
    {
        var settings = ServiceContainer.Settings.VolumeMixer;
        
        // For bottom-* positions, panel goes above the bar
        if (settings.IconPosition.StartsWith("bottom"))
        {
            MixerPanel.VerticalAlignment = VerticalAlignment.Bottom;
            MixerPanel.Margin = new Thickness(0, 0, 0, 60);
        }
        else
        {
            // For top-* positions, panel goes below the bar
            MixerPanel.VerticalAlignment = VerticalAlignment.Top;
            MixerPanel.Margin = new Thickness(0, 60, 0, 0);
        }
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
            int width, height;

            if (_state == OverlayState.Idle)
            {
                // Chip size: just the icon
                width = settings.IconSize + 24;
                height = settings.IconSize + 16;
            }
            else if (_state == OverlayState.MixerOpen)
            {
                // Mixer open: need space for expanded bar + mixer panel
                width = settings.IconSize + 24 + settings.SliderWidth + 12 + 44 + 8 + 32 + 8 + 24; // expanded bar width
                width = Math.Max(width, 340); // Ensure at least 340px for mixer panel (MinWidth=320 + padding)
                height = settings.IconSize + 16 + 12 + 380; // bar height + margin + panel max height
                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Mixer open size: {width}x{height}");
            }
            else
            {
                // Expanded: icon + slider + percent + mixer button
                width = settings.IconSize + 24 + settings.SliderWidth + 12 + 44 + 8 + 32 + 8 + 24; // padding + margins
                height = settings.IconSize + 16;
            }

            var appWindow = AppWindow;
            if (appWindow != null)
            {
                var currentBounds = appWindow.Position;
                var newBounds = new RectInt32(currentBounds.X, currentBounds.Y, width, height);
                appWindow.MoveAndResize(newBounds);

                System.Diagnostics.Debug.WriteLine($"[VolumeMixer] Resized to {width}x{height}");
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

        // Start hide delay timer if not over panel
        if (!_isPointerOverPanel && _state != OverlayState.Idle)
        {
            StartCollapseTimer();
        }
    }

    private void MixerPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPanel = true;
        _hideDelayTimer?.Stop();
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

    // ── Drag Mode ──────────────────────────────────────────────────────────────

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var settings = ServiceContainer.Settings.VolumeMixer;
        if (!settings.DragMode)
            return;

        var pointer = e.GetCurrentPoint(RootGrid);
        if (pointer.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = pointer.Position;
            
            var appWindow = AppWindow;
            if (appWindow != null)
            {
                var pos = appWindow.Position;
                var size = appWindow.Size;
                _dragStartX = pos.X;
                _dragStartY = pos.Y;
                _dragStartWidth = size.Width;
                _dragStartHeight = size.Height;
            }
            
            RootGrid.CapturePointer(e.Pointer);
            System.Diagnostics.Debug.WriteLine("[VolumeMixer] Drag started");
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        var pointer = e.GetCurrentPoint(null);
        var currentPos = pointer.Position;

        var deltaX = (int)(currentPos.X - _dragStartPoint.X);
        var deltaY = (int)(currentPos.Y - _dragStartPoint.Y);

        var newX = _dragStartX + deltaX;
        var newY = _dragStartY + deltaY;

        // Clamp to work area
        var workArea = DisplayArea.Primary.WorkArea;
        newX = Math.Max(workArea.X, Math.Min(workArea.X + workArea.Width - _dragStartWidth, newX));
        newY = Math.Max(workArea.Y, Math.Min(workArea.Y + workArea.Height - _dragStartHeight, newY));

        var newBounds = new RectInt32(newX, newY, _dragStartWidth, _dragStartHeight);
        AppWindow?.MoveAndResize(newBounds);
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

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

    // ── Public API ─────────────────────────────────────────────────────────────

    public void RefreshSettings()
    {
        ApplySettings();
        ApplyTheme();
        PositionWindow();
    }

    public new void Close()
    {
        _hoverDelayTimer?.Stop();
        _hideDelayTimer?.Stop();

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
