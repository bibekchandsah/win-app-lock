using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AppLock.Views;

public sealed partial class LockOverlayWindow : Window
{
    private readonly nint _targetWindowHandle;
    private readonly string _windowTitle;
    private readonly AuthService _authService;
    private readonly DisplayHelper _displayHelper;

    public bool IsUnlocked { get; private set; }
    public TimeSpan UnlockDuration { get; private set; }
    public bool IsOverlayVisible { get; private set; } = true;
    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    // Track "small" layout mode (< 400px wide)
    private bool _isSmallLayout;
    // Selected duration in minutes (0 = no limit, -1 = custom)
    private int _selectedMinutes = 0;
    // Whether the overlay should stay on top of ALL windows (setting: Keep lock screen on top)
    private bool _keepOnTop;
    private System.Threading.CancellationTokenSource? _helloCts;

    // Window drag and resize state
    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    private const int IDC_ARROW = 32512;
    private const int IDC_SIZENWSE = 32642;
    private const int IDC_SIZENESW = 32643;
    private const int IDC_SIZEWE = 32644;
    private const int IDC_SIZENS = 32645;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint LoadCursor(nint hInstance, int lpCursorName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);

    private bool _isDraggingWindow;
    private bool _isResizingWindow;
    private ResizeEdge _activeResizeEdge;
    private System.Drawing.Point _dragStartCursor;
    private RECT _targetStartRect;

    public LockOverlayWindow(nint targetWindowHandle, string windowTitle)
    {
        _targetWindowHandle = targetWindowHandle;
        _windowTitle = windowTitle;
        _authService = ServiceContainer.AuthService;
        _displayHelper = ServiceContainer.DisplayHelper;
        _keepOnTop = ServiceContainer.Settings.KeepLockScreenOnTop;

        InitializeComponent();

        TitleText.Text = $"Program \"{windowTitle}\" is already locked.";

        // Set dynamic emergency hotkey label from settings
        var hotkeyStr = ServiceContainer.Settings.EmergencyHotkey;
        if (!string.IsNullOrWhiteSpace(hotkeyStr))
            EmergencyHotkeyText.Text = hotkeyStr;

        // Make this window topmost and borderless
        SetupOverlayStyle();

        // Position over target window
        PositionOverTarget();

        // Apply theme immediately
        ApplyTheme(ServiceContainer.CurrentTheme);

        // Set initial button selection (No limit)
        SelectDurationButton(BtnNoLimit, BtnNoLimitS, 0);

        // Initialize Windows Hello button visibility asynchronously
        _ = InitializeWindowsHelloAsync();

        HelloCancelButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
        {
            CancelWindowsHello();
        }), handledEventsToo: true);

        // Re-apply theme once the window's visual tree is fully loaded
        RootGrid.Loaded += (s, e) =>
        {
            ApplyTheme(ServiceContainer.CurrentTheme);
        };

        // Also react to live theme changes
        RootGrid.ActualThemeChanged += (s, e) =>
        {
            ApplyThemeBrushes();
        };

        // Wire window drag and resize events
        RootGrid.PointerPressed += RootGrid_PointerPressed;
        RootGrid.PointerMoved += RootGrid_PointerMoved;
        RootGrid.PointerReleased += RootGrid_PointerReleased;
        RootGrid.PointerCanceled += RootGrid_PointerCanceled;
        RootGrid.PointerExited += RootGrid_PointerExited;
    }

    /// <summary>
    /// Shows the Windows Hello button only when the setting is enabled AND
    /// the device has a configured biometric/PIN verifier.
    /// </summary>
    private async Task InitializeWindowsHelloAsync()
    {
        var settings = ServiceContainer.Settings;
        if (!settings.WindowsHelloEnabled)
        {
            // Setting disabled — keep button collapsed
            return;
        }

        bool available = await ServiceContainer.HelloService.IsAvailableAsync();
        if (available)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                WindowsHelloButton.Visibility = Visibility.Visible;
            });
        }
    }

    private void SetupOverlayStyle()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        unsafe
        {
            var handle = new HWND(hwnd);

            // Remove titlebar and borders
            const int WS_CAPTION = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;

            var style = (int)PInvoke.GetWindowLongPtr(handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME);
            PInvoke.SetWindowLongPtr(handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)style);

            // Z-order: HWND_TOPMOST (-1) if setting is ON, otherwise HWND_TOP (0)
            // HWND_TOP places it above normal windows but lets other topmost windows cover it.
            var insertAfter = _keepOnTop
                ? new HWND((nint)(-1))  // HWND_TOPMOST — above everything
                : new HWND((nint)(0));  // HWND_TOP — above normal windows only

            PInvoke.SetWindowPos(
                handle,
                insertAfter,
                0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

            // Request Windows 11 rounded corners via DWM
            const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            uint cornerPreference = 2; // DWMWCP_ROUND (round window corners to match Windows 11)
            PInvoke.DwmSetWindowAttribute(
                handle,
                (DWMWINDOWATTRIBUTE)DWMWA_WINDOW_CORNER_PREFERENCE,
                &cornerPreference,
                sizeof(uint));

            // Do not show overlay as a separate window in taskbar or Alt+Tab switchers
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.IsShownInSwitchers = false;
            }
        }
    }

    /// <summary>
    /// Hides the overlay window when the target window is minimized.
    /// </summary>
    public void HideOverlay()
    {
        IsOverlayVisible = false;
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }

    public void PositionOverTarget()
    {
        IsOverlayVisible = true;
        unsafe
        {
            var targetHwnd = new HWND(_targetWindowHandle);
            
            // If target window is minimized, hide overlay and return
            if (PInvoke.IsIconic(targetHwnd))
            {
                HideOverlay();
                return;
            }

            // Get window bounds without shadow (more accurate)
            var rect = _displayHelper.GetWindowBoundsWithoutShadow(targetHwnd);
            
            // Get monitor and DPI info
            var monitor = _displayHelper.GetMonitorFromWindow(targetHwnd);
            var monitorInfo = _displayHelper.GetMonitorInfo(monitor);
            
            if (monitorInfo != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Overlay positioning - Monitor: {monitorInfo.Width}x{monitorInfo.Height}, " +
                    $"DPI: {monitorInfo.DpiX}, Scale: {monitorInfo.ScaleFactor:F2}x");
            }

            var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
            
            // Use DWM extended frame bounds for better accuracy
            var width = rect.right - rect.left;
            var height = rect.bottom - rect.top;
            
            // Ensure minimum size (allows small windows like Notepad/Notes ~280x200 to resize properly)
            width = Math.Max(width, 280);
            height = Math.Max(height, 200);

            // Windows 11 rounded corners:
            // If target is maximized, square corners (DWMWCP_DONOTROUND); otherwise round corners (DWMWCP_ROUND)
            bool isMaximized = PInvoke.IsZoomed(targetHwnd);
            const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            uint cornerPreference = isMaximized ? 1u : 2u;
            PInvoke.DwmSetWindowAttribute(
                hwnd,
                (DWMWINDOWATTRIBUTE)DWMWA_WINDOW_CORNER_PREFERENCE,
                &cornerPreference,
                sizeof(uint));

            RootGrid.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(10);

            // Z-order:
            //   KeepOnTop ON  → HWND_TOPMOST (-1): overlay stays above everything
            //   KeepOnTop OFF → HWND_TOP (0): overlay stays at top of normal windows, right over target
            var insertAfter = _keepOnTop
                ? new HWND((nint)(-1))          // HWND_TOPMOST
                : new HWND((nint)(0));           // HWND_TOP (above target)
            
            PInvoke.SetWindowPos(
                hwnd,
                insertAfter,
                rect.left,
                rect.top,
                width,
                height,
                SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

            // Note: We do NOT reposition or z-order the target window itself here.
            // Calling SetWindowPos on target (even with NOMOVE/NOSIZE) can cause Explorer
            // and Excel to re-measure/resize themselves unexpectedly.
            // The overlay being HWND_TOP is sufficient to appear above the target.

            // Re-assert foreground / top so keyboard focus goes to overlay PasswordBox
            // (Skip during active drag or resize to prevent stutter)
            if (!_isDraggingWindow && !_isResizingWindow)
            {
                uint targetThreadId = PInvoke.GetWindowThreadProcessId(targetHwnd, null);
                uint currentThreadId = PInvoke.GetCurrentThreadId();
                if (targetThreadId != 0 && targetThreadId != currentThreadId)
                {
                    PInvoke.AttachThreadInput(targetThreadId, currentThreadId, true);
                    PInvoke.BringWindowToTop(hwnd);
                    PInvoke.SetForegroundWindow(hwnd);
                    PInvoke.AttachThreadInput(targetThreadId, currentThreadId, false);
                }
                else
                {
                    PInvoke.BringWindowToTop(hwnd);
                    PInvoke.SetForegroundWindow(hwnd);
                }
            }
        }
    }

    /// <summary>
    /// Adjusts card size, padding and duration grid layout based on overlay width/height.
    /// This makes the overlay fully responsive for tiny windows (Calculator ~300×500px)
    /// and large windows (1920×1080).
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        double height = e.NewSize.Height;

        bool isSmall = width < 420 || height < 480;

        if (isSmall == _isSmallLayout) return; // no change
        _isSmallLayout = isSmall;

        if (isSmall)
        {
            // Compact layout for small windows (e.g., Calculator ~300×550)
            CardBorder.Padding = new Thickness(16, 14, 16, 14);
            CardBorder.MaxWidth = Math.Min(width - 16, 380);
            CardBorder.MaxHeight = Math.Min(height - 16, 480);

            ContentPanel.Spacing = 12;

            // Switch to 2-row duration grid
            DurationGrid.Visibility = Visibility.Collapsed;
            DurationGridSmall.Visibility = Visibility.Visible;

            // Sync selection state to small buttons
            SyncSmallButtonSelection(_selectedMinutes);
        }
        else
        {
            // Full layout for normal/large windows
            CardBorder.Padding = new Thickness(32, 28, 32, 28);
            CardBorder.MaxWidth = 520;
            CardBorder.MaxHeight = 600;

            ContentPanel.Spacing = 20;

            // Switch to 1-row duration grid
            DurationGrid.Visibility = Visibility.Visible;
            DurationGridSmall.Visibility = Visibility.Collapsed;

            // Sync selection state to normal buttons
            SyncNormalButtonSelection(_selectedMinutes);
        }
    }

    // --- Duration button selection logic ---

    private void DurationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (!int.TryParse(btn.Tag?.ToString(), out int minutes)) return;

        _selectedMinutes = minutes;

        // Show/hide auto-relock hint and custom input
        AutoRelockHint.Visibility = minutes != 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        CustomDurationPanel.Visibility = minutes == -1
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Apply accent style to selected, default to others
        if (_isSmallLayout)
            SyncSmallButtonSelection(minutes);
        else
            SyncNormalButtonSelection(minutes);
    }

    private void SyncNormalButtonSelection(int selectedMinutes)
    {
        SetButtonSelected(BtnNoLimit,  selectedMinutes == 0);
        SetButtonSelected(Btn15Min,    selectedMinutes == 15);
        SetButtonSelected(Btn30Min,    selectedMinutes == 30);
        SetButtonSelected(Btn60Min,    selectedMinutes == 60);
        SetButtonSelected(BtnCustom,   selectedMinutes == -1);
    }

    private void SyncSmallButtonSelection(int selectedMinutes)
    {
        SetButtonSelected(BtnNoLimitS, selectedMinutes == 0);
        SetButtonSelected(Btn15MinS,   selectedMinutes == 15);
        SetButtonSelected(Btn30MinS,   selectedMinutes == 30);
        SetButtonSelected(Btn60MinS,   selectedMinutes == 60);
        SetButtonSelected(BtnCustomS,  selectedMinutes == -1);
    }

    private void SetButtonSelected(Button btn, bool selected)
    {
        if (btn == null) return;
        bool isLight = IsLightMode();

        if (selected)
        {
            btn.Background = new SolidColorBrush(Color.FromArgb(255, 138, 43, 226)); // Accent purple
            btn.Foreground = new SolidColorBrush(Colors.White);
            btn.BorderBrush = new SolidColorBrush(Colors.Transparent);
            btn.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
        else
        {
            btn.Background = new SolidColorBrush(Colors.Transparent);
            btn.BorderBrush = new SolidColorBrush(Colors.Transparent);
            btn.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 35, 35, 35))
                : new SolidColorBrush(Color.FromArgb(255, 220, 220, 220));
            btn.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void SelectDurationButton(Button normalBtn, Button smallBtn, int minutes)
    {
        _selectedMinutes = minutes;
        SyncNormalButtonSelection(minutes);
        SyncSmallButtonSelection(minutes);
    }

    // --- Password & Auth ---

    private void PasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            TryUnlock();
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => TryUnlock();

    private void TryUnlock()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            ShowError("Please enter your password.");
            return;
        }

        if (_authService.VerifyPassword(password))
        {
            IsUnlocked = true;
            UnlockDuration = GetSelectedDuration();
            Close();
        }
        else
        {
            ShowError("Incorrect password. Please try again.");
            PasswordBox.Password = string.Empty;
            PasswordBox.Focus(FocusState.Programmatic);
        }
    }

    private TimeSpan GetSelectedDuration()
    {
        return _selectedMinutes switch
        {
            0  => TimeSpan.Zero,           // No limit
            -1 => TimeSpan.FromMinutes(CustomMinutesBox.Value >= 1 ? CustomMinutesBox.Value : 60),
            _  => TimeSpan.FromMinutes(_selectedMinutes)
        };
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Minimize the target window and hide overlay (target remains locked!)
        unsafe
        {
            var targetHwnd = new HWND(_targetWindowHandle);
            PInvoke.ShowWindow(targetHwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
        }
        HideOverlay();
    }

    /// <summary>
    /// Dynamically updates the "keep lock screen on top" setting for this active overlay.
    /// </summary>
    public void UpdateKeepOnTop(bool keepOnTop)
    {
        _keepOnTop = keepOnTop;
        PositionOverTarget();
    }

    /// <summary>
    /// Dynamically updates the theme for this active overlay.
    /// </summary>
    public void UpdateTheme(ElementTheme theme)
    {
        ApplyTheme(theme);
    }

    private bool IsLightMode()
    {
        var theme = ServiceContainer.CurrentTheme;
        if (theme == ElementTheme.Light) return true;
        if (theme == ElementTheme.Dark) return false;

        if (RootGrid != null)
        {
            if (RootGrid.RequestedTheme == ElementTheme.Light) return true;
            if (RootGrid.RequestedTheme == ElementTheme.Dark) return false;
            if (RootGrid.ActualTheme == ElementTheme.Light) return true;
            if (RootGrid.ActualTheme == ElementTheme.Dark) return false;
        }

        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            var bg = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return bg.R > 128;
        }
        catch
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Light;
        }
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (RootGrid != null)
        {
            RootGrid.RequestedTheme = theme;
        }
        ApplyThemeBrushes();
    }

    private void ApplyThemeBrushes()
    {
        if (RootGrid == null || CardBorder == null) return;

        bool isLight = IsLightMode();

        // 1. Full-window overlay backdrop scrim (soft frosted light overlay vs dark)
        RootGrid.Background = isLight
            ? new SolidColorBrush(Color.FromArgb(160, 238, 238, 238)) // Light translucent frosted scrim
            : new SolidColorBrush(Color.FromArgb(180, 20, 20, 20));    // Dark translucent scrim

        // 2. Card container (crisp clean white in light mode, dark surface in dark mode)
        CardBorder.Background = isLight
            ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(255, 44, 44, 44));

        CardBorder.BorderBrush = isLight
            ? new SolidColorBrush(Color.FromArgb(255, 225, 225, 225))
            : new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));

        CardBorder.CornerRadius = new CornerRadius(16);

        // 3. Simulated drop shadow behind card
        if (CardShadow != null)
        {
            CardShadow.Background = new SolidColorBrush(Colors.Black);
            CardShadow.Opacity = isLight ? 0.08 : 0.30;
            CardShadow.CornerRadius = new CornerRadius(18);
        }

        // 4. Lock icon badge at top
        if (LockIconBadge != null && LockIconGlyph != null)
        {
            if (isLight)
            {
                // Soft light blue background with vibrant blue lock glyph (matching Image 1)
                LockIconBadge.Background = new SolidColorBrush(Color.FromArgb(255, 235, 243, 252)); // #EBF3FC
                LockIconGlyph.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));    // #0078D4
            }
            else
            {
                // Accent purple background with white glyph
                LockIconBadge.Background = Application.Current.Resources.TryGetValue("AccentBrush", out var ab) && ab is Brush accentBrush
                    ? accentBrush
                    : new SolidColorBrush(Color.FromArgb(255, 138, 43, 226));
                LockIconGlyph.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        // 5. Typography
        if (TitleText != null)
        {
            TitleText.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 24, 24, 24))
                : new SolidColorBrush(Colors.White);
        }

        if (SubtitleText != null)
        {
            SubtitleText.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
                : new SolidColorBrush(Color.FromArgb(255, 170, 170, 170));
        }

        // 6. Password box
        if (PasswordBox != null)
        {
            PasswordBox.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
            PasswordBox.Background = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 250, 250, 250))
                : new SolidColorBrush(Color.FromArgb(255, 45, 45, 45));
            PasswordBox.BorderBrush = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 210, 210, 210))
                : new SolidColorBrush(Color.FromArgb(255, 75, 75, 75));
            PasswordBox.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 24, 24, 24))
                : new SolidColorBrush(Colors.White);
        }

        // 7. Duration label & hint
        if (DurationLabel != null)
        {
            DurationLabel.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
                : new SolidColorBrush(Color.FromArgb(255, 170, 170, 170));
        }

        // 8. Duration pill track
        var trackBg = isLight
            ? new SolidColorBrush(Color.FromArgb(255, 235, 235, 235)) // #EBEBEB
            : new SolidColorBrush(Color.FromArgb(255, 36, 36, 36));     // #242424
        var trackBorder = isLight
            ? new SolidColorBrush(Color.FromArgb(255, 222, 222, 222))
            : new SolidColorBrush(Color.FromArgb(255, 55, 55, 55));

        if (DurationGrid != null)
        {
            DurationGrid.Background = trackBg;
            DurationGrid.BorderBrush = trackBorder;
        }
        if (DurationGridSmall != null)
        {
            DurationGridSmall.Background = trackBg;
            DurationGridSmall.BorderBrush = trackBorder;
        }

        // Re-sync duration buttons selection and colors for current theme
        SyncNormalButtonSelection(_selectedMinutes);
        SyncSmallButtonSelection(_selectedMinutes);

        // 9. Action buttons
        if (CancelButton != null)
        {
            CancelButton.Background = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 245, 245, 245))
                : new SolidColorBrush(Color.FromArgb(255, 52, 52, 52));
            CancelButton.BorderBrush = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 218, 218, 218))
                : new SolidColorBrush(Color.FromArgb(255, 75, 75, 75));
            CancelButton.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 24, 24, 24))
                : new SolidColorBrush(Colors.White);
        }

        // 10. Windows Hello button
        if (WindowsHelloButton != null)
        {
            WindowsHelloButton.Background = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 245, 245, 245))
                : new SolidColorBrush(Color.FromArgb(255, 52, 52, 52));
            WindowsHelloButton.BorderBrush = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 218, 218, 218))
                : new SolidColorBrush(Color.FromArgb(255, 75, 75, 75));
            WindowsHelloButton.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 24, 24, 24))
                : new SolidColorBrush(Colors.White);
        }

        // 11. Divider
        if (CardDivider != null)
        {
            CardDivider.Background = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 230, 230, 230))
                : new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));
        }

        // 12. Emergency lock footer
        if (EmergencyLabel != null)
        {
            EmergencyLabel.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 110, 110, 110))
                : new SolidColorBrush(Color.FromArgb(255, 160, 160, 160));
        }
        if (EmergencyHotkeyBorder != null)
        {
            EmergencyHotkeyBorder.Background = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 242, 242, 242))
                : new SolidColorBrush(Color.FromArgb(255, 45, 45, 45));
            EmergencyHotkeyBorder.BorderBrush = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 218, 218, 218))
                : new SolidColorBrush(Color.FromArgb(255, 65, 65, 65));
        }
        if (EmergencyHotkeyText != null)
        {
            EmergencyHotkeyText.Foreground = isLight
                ? new SolidColorBrush(Color.FromArgb(255, 30, 30, 30))
                : new SolidColorBrush(Colors.White);
        }
    }

    private async void WindowsHelloButton_Click(object sender, RoutedEventArgs e)
    {
        WindowsHelloButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;

        // Switch to the Windows Hello pending panel, keeping the overlay visible so content stays hidden
        ContentScrollViewer.Visibility = Visibility.Collapsed;
        HelloActivePanel.Visibility = Visibility.Visible;

        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (!PInvoke.IsWindowEnabled(hwnd))
        {
            PInvoke.EnableWindow(hwnd, true);
        }

        try
        {
            _helloCts = new System.Threading.CancellationTokenSource();

            bool verified = await ServiceContainer.HelloService.RequestVerificationAsync(
                $"Unlock \"{_windowTitle}\" with Windows Hello",
                _helloCts.Token,
                hwnd);

            if (verified)
            {
                IsUnlocked = true;
                UnlockDuration = GetSelectedDuration();
                Close();
                return; // Closing overlay
            }

            if (_helloCts?.IsCancellationRequested != true)
            {
                ShowError("Windows Hello verification failed. Try your password.");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user clicking "Cancel Windows Hello"
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Windows Hello error: {ex.Message}");
            ShowError("Windows Hello is not available. Use your password.");
            WindowsHelloButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _helloCts?.Dispose();
            _helloCts = null;

            // If not unlocked, restore normal overlay view and reposition
            if (IsUnlocked == false)
            {
                HelloActivePanel.Visibility = Visibility.Collapsed;
                ContentScrollViewer.Visibility = Visibility.Visible;

                PositionOverTarget();
                PasswordBox.Focus(FocusState.Programmatic);
            }
            WindowsHelloButton.IsEnabled = true;
        }
    }

    private void HelloCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelWindowsHello();
    }

    private void CancelWindowsHello()
    {
        try
        {
            _helloCts?.Cancel();
            HelloService.DismissHelloPrompt();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error cancelling Windows Hello: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets or clears the HWND_TOPMOST flag. Respects _keepOnTop:
    /// when restoring (topmost=true) we only go TOPMOST if the setting demands it.
    /// </summary>
    private void SetTopmost(bool topmost)
    {
        try
        {
            var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
            HWND insertAfter;
            if (topmost && _keepOnTop)
                insertAfter = new HWND((nint)(-1)); // HWND_TOPMOST
            else if (topmost)
                insertAfter = new HWND((nint)(0));  // HWND_TOP
            else
                insertAfter = new HWND((nint)(-2)); // HWND_NOTOPMOST

            PInvoke.SetWindowPos(
                hwnd,
                insertAfter,
                0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SetTopmost({topmost}) failed: {ex.Message}");
        }
    }

    private async void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_authService.HasSecurityQuestion)
        {
            ShowError("No security question set. Configure one in Settings.");
            return;
        }

        var verifyDialog = new VerifySecurityQuestionDialog(_authService)
        {
            XamlRoot = this.Content.XamlRoot
        };
        await verifyDialog.ShowAsync();

        if (verifyDialog.IsVerified)
        {
            await Task.Delay(150);
            var resetDialog = new ResetPasswordDialog(_authService)
            {
                XamlRoot = this.Content.XamlRoot
            };
            await resetDialog.ShowAsync();

            if (resetDialog.PasswordReset)
            {
                IsUnlocked = true;
                UnlockDuration = GetSelectedDuration();
                Close();
            }
        }
    }

    // --- Overlay Drag Implementation (moves both target window and overlay together) ---

    // --- Overlay Drag and Resize Implementation (moves & resizes target and overlay together) ---

    private ResizeEdge GetResizeEdge(Windows.Foundation.Point pt)
    {
        var targetHwnd = new HWND(_targetWindowHandle);
        if (PInvoke.IsZoomed(targetHwnd)) return ResizeEdge.None;

        const double b = 10.0; // 10-pixel resize hit zone for smooth mouse grabbing
        ResizeEdge edge = ResizeEdge.None;

        if (pt.X <= b) edge |= ResizeEdge.Left;
        else if (pt.X >= RootGrid.ActualWidth - b) edge |= ResizeEdge.Right;

        if (pt.Y <= b) edge |= ResizeEdge.Top;
        else if (pt.Y >= RootGrid.ActualHeight - b) edge |= ResizeEdge.Bottom;

        return edge;
    }

    private void UpdateCursor(ResizeEdge edge)
    {
        nint cursor = edge switch
        {
            ResizeEdge.Left | ResizeEdge.Top => LoadCursor(0, IDC_SIZENWSE),
            ResizeEdge.Right | ResizeEdge.Bottom => LoadCursor(0, IDC_SIZENWSE),
            ResizeEdge.Right | ResizeEdge.Top => LoadCursor(0, IDC_SIZENESW),
            ResizeEdge.Left | ResizeEdge.Bottom => LoadCursor(0, IDC_SIZENESW),
            ResizeEdge.Left => LoadCursor(0, IDC_SIZEWE),
            ResizeEdge.Right => LoadCursor(0, IDC_SIZEWE),
            ResizeEdge.Top => LoadCursor(0, IDC_SIZENS),
            ResizeEdge.Bottom => LoadCursor(0, IDC_SIZENS),
            _ => LoadCursor(0, IDC_ARROW)
        };
        if (cursor != 0)
        {
            SetCursor(cursor);
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(RootGrid);
        if (!pt.Properties.IsLeftButtonPressed) return;

        var targetHwnd = new HWND(_targetWindowHandle);
        if (PInvoke.IsZoomed(targetHwnd))
        {
            return; // Do not resize or drag maximized window
        }

        var edge = GetResizeEdge(pt.Position);
        if (edge != ResizeEdge.None)
        {
            // Start resizing!
            RECT rc = default;
            if (PInvoke.GetWindowRect(targetHwnd, out rc) && PInvoke.GetCursorPos(out var cursorPt))
            {
                _dragStartCursor = cursorPt;
                _targetStartRect = rc;
                _activeResizeEdge = edge;
                _isResizingWindow = true;
                RootGrid.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            return;
        }

        if (IsInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        {
            RECT rc = default;
            if (PInvoke.GetWindowRect(targetHwnd, out rc) && PInvoke.GetCursorPos(out var cursorPt))
            {
                _dragStartCursor = cursorPt;
                _targetStartRect = rc;
                _isDraggingWindow = true;
                RootGrid.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizingWindow)
        {
            UpdateCursor(_activeResizeEdge);
            if (PInvoke.GetCursorPos(out var cursorPt))
            {
                int dx = cursorPt.X - _dragStartCursor.X;
                int dy = cursorPt.Y - _dragStartCursor.Y;

                int origW = _targetStartRect.right - _targetStartRect.left;
                int origH = _targetStartRect.bottom - _targetStartRect.top;

                int newX = _targetStartRect.left;
                int newY = _targetStartRect.top;
                int newW = origW;
                int newH = origH;

                if (_activeResizeEdge.HasFlag(ResizeEdge.Right))
                {
                    newW = Math.Max(320, origW + dx);
                }
                else if (_activeResizeEdge.HasFlag(ResizeEdge.Left))
                {
                    int targetW = origW - dx;
                    if (targetW >= 320)
                    {
                        newX = _targetStartRect.left + dx;
                        newW = targetW;
                    }
                    else
                    {
                        newX = _targetStartRect.right - 320;
                        newW = 320;
                    }
                }

                if (_activeResizeEdge.HasFlag(ResizeEdge.Bottom))
                {
                    newH = Math.Max(240, origH + dy);
                }
                else if (_activeResizeEdge.HasFlag(ResizeEdge.Top))
                {
                    int targetH = origH - dy;
                    if (targetH >= 240)
                    {
                        newY = _targetStartRect.top + dy;
                        newH = targetH;
                    }
                    else
                    {
                        newY = _targetStartRect.bottom - 240;
                        newH = 240;
                    }
                }

                var targetHwnd = new HWND(_targetWindowHandle);
                PInvoke.SetWindowPos(
                    targetHwnd,
                    HWND.Null,
                    newX,
                    newY,
                    newW,
                    newH,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

                PositionOverTarget();
                e.Handled = true;
            }
            return;
        }

        if (_isDraggingWindow)
        {
            if (PInvoke.GetCursorPos(out var cursorPt))
            {
                int dx = cursorPt.X - _dragStartCursor.X;
                int dy = cursorPt.Y - _dragStartCursor.Y;

                var targetHwnd = new HWND(_targetWindowHandle);
                int newX = _targetStartRect.left + dx;
                int newY = _targetStartRect.top + dy;

                PInvoke.SetWindowPos(
                    targetHwnd,
                    HWND.Null,
                    newX,
                    newY,
                    0,
                    0,
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

                PositionOverTarget();
                e.Handled = true;
            }
            return;
        }

        // Hover cursor check
        var pt = e.GetCurrentPoint(RootGrid);
        var edge = GetResizeEdge(pt.Position);
        if (edge != ResizeEdge.None)
        {
            UpdateCursor(edge);
        }
        else
        {
            UpdateCursor(ResizeEdge.None);
        }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizingWindow || _isDraggingWindow)
        {
            _isResizingWindow = false;
            _isDraggingWindow = false;
            _activeResizeEdge = ResizeEdge.None;
            RootGrid.ReleasePointerCapture(e.Pointer);
            UpdateCursor(ResizeEdge.None);
            e.Handled = true;

            var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
            PInvoke.BringWindowToTop(hwnd);
            PInvoke.SetForegroundWindow(hwnd);
        }
    }

    private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizingWindow || _isDraggingWindow)
        {
            _isResizingWindow = false;
            _isDraggingWindow = false;
            _activeResizeEdge = ResizeEdge.None;
            RootGrid.ReleasePointerCapture(e.Pointer);
            UpdateCursor(ResizeEdge.None);
        }
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingWindow && !_isDraggingWindow)
        {
            UpdateCursor(ResizeEdge.None);
        }
    }

    private static bool IsInteractiveControl(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button ||
                element is PasswordBox ||
                element is TextBox ||
                element is HyperlinkButton ||
                element is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar ||
                element is ComboBox)
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}
