using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
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

    // Track "small" layout mode (< 400px wide)
    private bool _isSmallLayout;
    // Selected duration in minutes (0 = no limit, -1 = custom)
    private int _selectedMinutes = 0;

    public LockOverlayWindow(nint targetWindowHandle, string windowTitle)
    {
        _targetWindowHandle = targetWindowHandle;
        _windowTitle = windowTitle;
        _authService = ServiceContainer.AuthService;
        _displayHelper = ServiceContainer.DisplayHelper;

        InitializeComponent();

        TitleText.Text = $"Program \"{windowTitle}\" is already locked.";

        // Make this window topmost and borderless
        SetupOverlayStyle();

        // Position over target window
        PositionOverTarget();

        // Set initial button selection (No limit)
        SelectDurationButton(BtnNoLimit, BtnNoLimitS, 0);
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

            // Make it topmost
            PInvoke.SetWindowPos(
                handle,
                new HWND((nint)(-1)), // HWND_TOPMOST
                0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        }
    }

    public void PositionOverTarget()
    {
        unsafe
        {
            var targetHwnd = new HWND(_targetWindowHandle);
            
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
            
            // Ensure minimum size
            width = Math.Max(width, 320);
            height = Math.Max(height, 400);
            
            PInvoke.SetWindowPos(
                hwnd,
                new HWND((nint)(-1)), // HWND_TOPMOST
                rect.left,
                rect.top,
                width,
                height,
                SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
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
        if (selected)
            btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        else
            btn.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
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
        // Minimize the target window and close overlay
        unsafe
        {
            var targetHwnd = new HWND(_targetWindowHandle);
            PInvoke.ShowWindow(targetHwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
        }
        Close();
    }

    private async void WindowsHelloButton_Click(object sender, RoutedEventArgs e)
    {
        // Windows Hello auth (future: use UserConsentVerifier)
        // For now, show "not configured" info
        var dialog = new ContentDialog
        {
            Title = "Windows Hello",
            Content = "Windows Hello authentication is not configured yet.",
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
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
}
