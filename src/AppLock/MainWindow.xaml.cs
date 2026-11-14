using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AppLock.Views;
using AppLock.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace AppLock;

public sealed partial class MainWindow : Window
{
    // Win32 subclassing for WM_HOTKEY interception
    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nint uIdSubclass, nint dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nint uIdSubclass);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private SUBCLASSPROC? _subclassProc; // keep delegate alive to prevent GC

    private SystemBackdropConfiguration? _backdropConfiguration;
    private MicaController? _micaController;
    private Microsoft.UI.Windowing.AppWindow? _appWindow;
    private bool _isExplicitExit = false;
    private bool _isAuthDialogOpen = false;
    private bool _loginShown = false;
    private bool _isMainAuthenticated = false;
    private int _emergencyHotkeyId = -1;
    private int _ringLightHotkeyId = -1;
    private int _notificationToggleHotkeyId = -1;
    private HWND _hwnd;
    private System.Drawing.Icon? _windowIcon;
    private bool _isNavPaneExpanded = false;
    private bool _startInTray;
    private DispatcherTimer? _toastTimer;
    private Windows.UI.ViewManagement.UISettings? _uiSettings;

    internal static MainWindow? Instance { get; private set; }
    internal HWND Hwnd => _hwnd;

    /// <param name="startInTray">
    /// True when Windows launched the app (autostart): the window is created and hidden so only
    /// the tray icon shows, and the unlock prompt waits until the user opens the window.
    /// </param>
    public MainWindow(bool startInTray = false)
    {
        Instance = this;
        _startInTray = startInTray;
        InitializeComponent();
        
        // Get window handle
        var hWndNative = WindowNative.GetWindowHandle(this);
        _hwnd = new HWND(hWndNative);
        
        // Set window size & wire closing event
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndNative);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        _appWindow.Closing += AppWindow_Closing;
        
        // Configure custom title bar and window/taskbar icons
        SetupTitleBar();
        SetAppIcon();

        // Listen for Windows system accent color changes
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncAppAccentColor();
                    UpdateTitleBarTheme();
                    UpdateNavSelection(ContentFrame.CurrentSourcePageType);
                    UpdateToastTheme();
                });
            };
        }
        catch { }
        SyncAppAccentColor();

        // Initialize in-app toast notification service host
        InitializeInAppToast();

        // Wire navigation selection tracking
        ContentFrame.Navigated += ContentFrame_Navigated;

        // Set up Mica backdrop
        SetupMica();
        
        // Set theme from user settings and keep title bar & nav in sync
        if (Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = ServiceContainer.CurrentTheme;
            rootElement.ActualThemeChanged += (s, e) =>
            {
                UpdateTitleBarTheme();
                UpdateNavSelection(ContentFrame.CurrentSourcePageType);
                UpdateToastTheme();
            };
        }
        UpdateTitleBarTheme();
        UpdateToastTheme();
        
        // Initialize collapsible navigation pane from settings
        SetNavPaneExpanded(ServiceContainer.Settings.IsNavPaneExpanded, saveSetting: false, animate: false);

        // Show/hide BitLocker nav based on platform support
        UpdateBitLockerNavVisibility();

        // Wire localization
        ServiceContainer.LocalizationService.LanguageChanged += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(() => ApplyLocalization());
        };
        ApplyLocalization();

        // Initialize services
        InitializeServices();
        
        // Check first run
        if (ServiceContainer.AuthService.IsFirstRun)
        {
            ShowFirstRun();
        }
        else
        {
            // CRITICAL: Immediately lock all protected apps on app launch,
            // regardless of whether the user has entered the password for MainWindow yet!
            if (ServiceContainer.Settings.IsProtectionEnabled)
            {
                System.Diagnostics.Debug.WriteLine("App startup: Starting OverlayManager immediately to lock protected apps...");
                ServiceContainer.OverlayManager.Start();
            }

            // Hide navigation rail while login prompt is pending
            NavRailCard.Visibility = Visibility.Collapsed;
            Grid.SetColumn(ContentFrame, 0);
            ContentFrame.SetValue(Grid.ColumnSpanProperty, 2);

            // Show login immediately after loading completes
            ContentFrame.Loaded += async (s, e) =>
            {
                // Startup launches stay in the tray without a prompt; Show() asks for the
                // password once the user actually opens the window.
                if (!_loginShown && !_startInTray)
                {
                    _loginShown = true;
                    await ShowLogin();
                }
            };
        }
    }

    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        try
        {
            var iconPngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icons", "32x32.png");
            if (System.IO.File.Exists(iconPngPath))
            {
                AppTitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPngPath));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load title icon bitmap: {ex.Message}");
        }

        UpdateTitleBarTheme();
    }

    private void SetAppIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icons", "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _appWindow?.SetIcon(iconPath);

                _windowIcon = new System.Drawing.Icon(iconPath);
                var hIcon = _windowIcon.Handle;
                if (hIcon != IntPtr.Zero)
                {
                    const uint WM_SETICON = 0x0080;
                    PInvoke.SendMessage(_hwnd, WM_SETICON, (WPARAM)(nuint)1 /* ICON_BIG */, (LPARAM)hIcon);
                    PInvoke.SendMessage(_hwnd, WM_SETICON, (WPARAM)(nuint)0 /* ICON_SMALL */, (LPARAM)hIcon);
                }
                System.Diagnostics.Debug.WriteLine($"Window and taskbar icon loaded from: {iconPath}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set window icon: {ex.Message}");
        }
    }

    public bool IsDarkMode
    {
        get
        {
            if (Content is FrameworkElement rootElement)
            {
                if (rootElement.RequestedTheme == ElementTheme.Default)
                {
                    return Application.Current.RequestedTheme == ApplicationTheme.Dark;
                }
                return rootElement.RequestedTheme == ElementTheme.Dark;
            }
            return ServiceContainer.CurrentTheme == ElementTheme.Dark;
        }
    }

    public void UpdateTitleBarTheme()
    {
        bool isDark = IsDarkMode;

        if (AppTitleText != null)
        {
            AppTitleText.Foreground = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 50, 50, 50));
        }

        // 1. Sync DWM Immersive Dark Mode for Win32 frame/borders
        unsafe
        {
            const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            int useDarkMode = isDark ? 1 : 0;
            PInvoke.DwmSetWindowAttribute(_hwnd, (DWMWINDOWATTRIBUTE)DWMWA_USE_IMMERSIVE_DARK_MODE, &useDarkMode, sizeof(int));
        }

        // 2. Sync AppWindowTitleBar caption button colors
        if (_appWindow?.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

            if (isDark)
            {
                _appWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                _appWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                _appWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(30, 255, 255, 255);
                _appWindow.TitleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                _appWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(50, 255, 255, 255);
                _appWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 255, 255, 255);
            }
            else
            {
                _appWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                _appWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                _appWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(30, 0, 0, 0);
                _appWindow.TitleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                _appWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(50, 0, 0, 0);
                _appWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 0, 0, 0);
            }
        }
    }

    public void UpdateNavTheme()
    {
        UpdateNavSelection(ContentFrame.CurrentSourcePageType);
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        UpdateNavSelection(e.SourcePageType);
    }

    public static Windows.UI.Color GetWindowAccentColor()
    {
        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            return uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Windows.UI.Color.FromArgb(255, 0, 120, 212);
        }
    }

    public static void SyncAppAccentColor()
    {
        try
        {
            var accentColor = GetWindowAccentColor();
            Application.Current.Resources["AccentColor"] = accentColor;
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(accentColor);
        }
        catch { }
    }

    private void UpdateNavSelection(Type? pageType)
    {
        bool isProtection = pageType == typeof(ProtectionPage);
        bool isFeedback = pageType == typeof(FeedbackPage);
        bool isSettings = pageType == typeof(SettingsPage);
        bool isBitLocker = pageType == typeof(BitLockerPage);
        bool isInternetAccess = pageType == typeof(InternetAccessPage);
        bool isOthers = pageType == typeof(OthersPage) ||
                        pageType == typeof(BrightnessPage) ||
                        pageType == typeof(EyeCarePage) ||
                        pageType == typeof(ClipboardSettingsPage) ||
                        pageType == typeof(ClipboardHistoryPage) ||
                        pageType == typeof(VolumeMixerSettingsPage) ||
                        pageType == typeof(NotificationsPage) ||
                        pageType == typeof(KeyboardPage) ||
                        pageType == typeof(SpeedMeterPage) ||
                        pageType == typeof(RingLightPage);

        bool isDark = IsDarkMode;
        var windowAccentColor = GetWindowAccentColor();

        // Active accent: Exact Windows accent color
        var activeAccentBrush = new SolidColorBrush(windowAccentColor);

        // Active pill background: Subtle Fluent tint of the exact Windows accent color
        var activeBgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
            isDark ? (byte)42 : (byte)26,
            windowAccentColor.R,
            windowAccentColor.G,
            windowAccentColor.B));

        // Inactive icon: Crisp fluent secondary gray (#C5FFFFFF in dark, #3C3C3C in light mode - completely visible!)
        var inactiveIconBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(197, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(220, 60, 60, 60));

        // Active text
        var activeTextBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 25, 25));

        // Inactive text
        var inactiveTextBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(200, 80, 80, 80));

        // Disabled items
        var disabledBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(90, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(100, 120, 120, 120));

        UpdateNavItem(isProtection, ProtectionNavBackground, ProtectionNavIndicator, ProtectionNavIcon, ProtectionNavLabel,
            activeBgBrush, activeAccentBrush, inactiveIconBrush, activeTextBrush, inactiveTextBrush);

        UpdateNavItem(isFeedback, FeedbackNavBackground, FeedbackNavIndicator, FeedbackNavIcon, FeedbackNavLabel,
            activeBgBrush, activeAccentBrush, inactiveIconBrush, activeTextBrush, inactiveTextBrush);

        UpdateNavItem(isSettings, SettingsNavBackground, SettingsNavIndicator, SettingsNavIcon, SettingsNavLabel,
            activeBgBrush, activeAccentBrush, inactiveIconBrush, activeTextBrush, inactiveTextBrush);

        // Internet Access nav
        UpdateNavItem(isInternetAccess, InternetAccessNavBackground, InternetAccessNavIndicator, InternetAccessNavIcon, InternetAccessNavLabel,
            activeBgBrush, activeAccentBrush, inactiveIconBrush, activeTextBrush, inactiveTextBrush);

        // BitLocker nav (only if visible / supported)
        if (BitLockerNavButton?.Visibility == Visibility.Visible)
        {
            UpdateNavItem(isBitLocker, BitLockerNavBackground, BitLockerNavIndicator, BitLockerNavIcon, BitLockerNavLabel,
                activeBgBrush, activeAccentBrush, inactiveIconBrush, activeTextBrush, inactiveTextBrush);
        }

        // Others nav
        UpdateNavItem(isOthers, OthersNavBackground, OthersNavIndicator, OthersNavIcon, OthersNavLabel,
            activeBgBrush, activeAccentBrush, inactiveIconBrush, activeTextBrush, inactiveTextBrush);

        // Update toggle button and disabled nav items
        // Slide the accent pill to whichever row is selected.
        Button? selectedButton =
            isProtection ? ProtectionNavButton :
            isFeedback ? FeedbackNavButton :
            isSettings ? SettingsNavButton :
            isInternetAccess ? InternetAccessNavButton :
            isBitLocker ? BitLockerNavButton :
            isOthers ? OthersNavButton : null;

        MoveNavSelectionPill(selectedButton, animate: true);

        if (NavTogglePaneIcon != null) NavTogglePaneIcon.Foreground = inactiveIconBrush;
        if (RulesNavIcon != null) RulesNavIcon.Foreground = disabledBrush;
        if (RulesNavLabel != null) RulesNavLabel.Foreground = disabledBrush;
        if (WebsitesNavIcon != null) WebsitesNavIcon.Foreground = disabledBrush;
        if (WebsitesNavLabel != null) WebsitesNavLabel.Foreground = disabledBrush;
    }

    private void UpdateNavItem(
        bool isSelected,
        Border? background,
        Border? indicator,
        FontIcon? icon,
        TextBlock? label,
        Brush activeBg,
        Brush activeAccent,
        Brush inactiveIcon,
        Brush activeText,
        Brush inactiveText)
    {
        if (background != null)
        {
            background.Background = activeBg;
            background.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        }
        if (indicator != null)
        {
            // Selection is drawn by the single sliding pill (NavSelectionPill) instead.
            indicator.Background = activeAccent;
            indicator.Visibility = Visibility.Collapsed;
        }
        if (icon != null)
        {
            icon.Foreground = isSelected ? activeAccent : inactiveIcon;
        }
        if (label != null)
        {
            label.Foreground = isSelected ? activeText : inactiveText;
            label.FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    public void UpdateToastTheme()
    {
        if (InAppToastCard == null) return;

        bool isDark = IsDarkMode;
        InAppToastCard.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;

        var bgBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 36))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

        var borderBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 65, 65))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 215, 215, 215));

        // Use exact Windows accent color for the notification vertical line
        var windowAccentColor = GetWindowAccentColor();
        var accentBrush = new SolidColorBrush(windowAccentColor);

        var titleBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 24));

        var msgBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(220, 210, 210, 210))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(220, 80, 80, 80));

        var closeBrush = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(200, 180, 180, 180))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(200, 100, 100, 100));

        InAppToastCard.Background = bgBrush;
        InAppToastCard.BorderBrush = borderBrush;
        if (ToastAccentBar != null) ToastAccentBar.Background = accentBrush;
        if (ToastTitleText != null) ToastTitleText.Foreground = titleBrush;
        if (ToastMessageText != null) ToastMessageText.Foreground = msgBrush;
        if (ToastCloseIcon != null) ToastCloseIcon.Foreground = closeBrush;
    }

    private void SetupMica()
    {
        if (MicaController.IsSupported())
        {
            _backdropConfiguration = new SystemBackdropConfiguration();
            _micaController = new MicaController();
            _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
            
            SystemBackdrop = new MicaBackdrop() { Kind = MicaKind.Base };
        }
        else if (RootGrid != null)
        {
            // No Mica (older Windows): fall back to the solid purple shell colour.
            RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ShellWindowFallbackBrush"];
        }
    }

    private void ShowFirstRun()
    {
        var firstRunPage = new FirstRunPage(ServiceContainer.AuthService);
        firstRunPage.FirstRunCompleted += (s, e) =>
        {
            OnFirstRunComplete();
        };
        ContentFrame.Content = firstRunPage;
        
        // Hide navigation rail during first run
        NavRailCard.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ContentFrame, 0);
        ContentFrame.SetValue(Grid.ColumnSpanProperty, 2);
    }

    public void OnFirstRunComplete()
    {
        _isMainAuthenticated = true;

        // Restore navigation rail
        Grid.SetColumn(ContentFrame, 1);
        ContentFrame.ClearValue(Grid.ColumnSpanProperty);
        NavRailCard.Visibility = Visibility.Visible;
        
        // Navigate to protection page
        ContentFrame.Navigate(typeof(ProtectionPage));

        // Start overlay manager if protection is enabled
        if (ServiceContainer.Settings.IsProtectionEnabled)
        {
            ServiceContainer.OverlayManager.Start();
        }
    }

    private async Task ShowLogin()
    {
        if (_isMainAuthenticated) return;
        if (_isAuthDialogOpen) return;
        _isAuthDialogOpen = true;

        try
        {
            // The window may have only just been restored from the tray, so give the XAML tree a
            // moment to produce a XamlRoot before hosting a dialog on it.
            for (int attempt = 0; attempt < 25 && ContentFrame.XamlRoot == null && Content?.XamlRoot == null; attempt++)
            {
                await Task.Delay(100);
            }

            while (!_isMainAuthenticated)
            {
                var dialog = new AuthDialog(ServiceContainer.AuthService)
                {
                    XamlRoot = ContentFrame.XamlRoot ?? Content?.XamlRoot,
                    Title = "AppLock is locked",
                    IntroText = "Enter your AppLock password to unlock."
                };

                try
                {
                    await dialog.ShowAsync();
                }
                catch (Exception ex)
                {
                    // Another ContentDialog was still open (e.g. one that was open when the idle
                    // lock fired). Close it and try the prompt once more instead of leaving the
                    // window usable without a password.
                    IdleService.Log($"Unlock prompt failed ({ex.Message}) - closing leftover dialogs");
                    CloseOpenDialogs();
                    await Task.Delay(150);
                    try
                    {
                        await dialog.ShowAsync();
                    }
                    catch (Exception ex2)
                    {
                        IdleService.Log($"Unlock prompt could not be shown: {ex2.Message}");
                        _isMainAuthenticated = false;
                        Hide();
                        _loginShown = false;
                        break;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"IsAuthenticated: {dialog.IsAuthenticated}");

                // Check IsAuthenticated - this is set to true only when password is correct
                if (dialog.IsAuthenticated)
                {
                    _isMainAuthenticated = true;
                    System.Diagnostics.Debug.WriteLine("Authentication successful");
                    IdleService.Log("AppLock unlocked");

                    // Restore navigation rail. The page the user was on is kept; only an empty
                    // frame (first unlock after launch) is filled with the protection page.
                    Grid.SetColumn(ContentFrame, 1);
                    ContentFrame.ClearValue(Grid.ColumnSpanProperty);
                    NavRailCard.Visibility = Visibility.Visible;

                    if (ContentFrame.Content == null)
                    {
                        ContentFrame.Navigate(typeof(ProtectionPage));
                    }

                    // Start overlay manager if protection is enabled
                    try
                    {
                        if (ServiceContainer.Settings.IsProtectionEnabled)
                        {
                            ServiceContainer.OverlayManager.Start();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR during initialization: {ex.Message}");
                    }

                    NoteWindowActivity();
                    break;
                }
                else
                {
                    // Cancelled: stay locked and minimized to the tray. Nothing in the window may be
                    // used without the password.
                    _isMainAuthenticated = false;
                    Hide();
                    _loginShown = false;
                    IdleService.Log("Unlock cancelled - AppLock kept locked in the tray");
                    break;
                }
            }
        }
        finally
        {
            _isAuthDialogOpen = false;
        }
    }

    private void ProtectionNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMainAuthenticated) return;
        if (ContentFrame.CurrentSourcePageType != typeof(ProtectionPage))
        {
            ContentFrame.Navigate(typeof(ProtectionPage));
        }
    }

    private void FeedbackNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMainAuthenticated) return;
        if (ContentFrame.CurrentSourcePageType != typeof(FeedbackPage))
        {
            ContentFrame.Navigate(typeof(FeedbackPage));
        }
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMainAuthenticated) return;
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
        {
            ContentFrame.Navigate(typeof(SettingsPage));
        }
    }

    private void UpdateBitLockerNavVisibility()
    {
        if (BitLockerNavButton != null)
        {
            BitLockerNavButton.Visibility = ServiceContainer.BitLockerSupportService.IsSupported
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void BitLockerNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMainAuthenticated) return;
        if (ContentFrame.CurrentSourcePageType != typeof(BitLockerPage))
        {
            ContentFrame.Navigate(typeof(BitLockerPage));
        }
    }

    private void InternetAccessNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMainAuthenticated) return;
        if (ContentFrame.CurrentSourcePageType != typeof(InternetAccessPage))
        {
            ContentFrame.Navigate(typeof(InternetAccessPage));
        }
    }

    private void OthersNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMainAuthenticated) return;
        if (ContentFrame.CurrentSourcePageType != typeof(OthersPage))
        {
            ContentFrame.Navigate(typeof(OthersPage));
        }
    }

    private void NavTogglePaneButton_Click(object sender, RoutedEventArgs e)
    {
        AnimateNavToggleIcon();
        SetNavPaneExpanded(!_isNavPaneExpanded, saveSetting: true);
    }

    /// <summary>Current rotation of the burger icon, so each click spins on from where it stopped.</summary>
    private float _navToggleIconRotation;

    /// <summary>
    /// Spins the burger a half turn with a short settle — the same feel as the Windows Settings pane
    /// toggle. Driven through the composition layer (not a XAML storyboard property path) so the icon
    /// really rotates about its own centre on every click.
    /// </summary>
    private void AnimateNavToggleIcon()
    {
        try
        {
            var icon = NavTogglePaneIcon;
            if (icon == null) return;

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;

            float centerX = (float)icon.ActualWidth / 2f;
            float centerY = (float)icon.ActualHeight / 2f;
            if (centerX <= 0 || centerY <= 0) { centerX = 8f; centerY = 8f; }
            visual.CenterPoint = new System.Numerics.Vector3(centerX, centerY, 0f);

            float from = _navToggleIconRotation;
            float to = from + 180f;
            _navToggleIconRotation = to;

            var settle = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.16f, 0.9f),
                new System.Numerics.Vector2(0.28f, 1f));

            var spin = compositor.CreateScalarKeyFrameAnimation();
            spin.Duration = TimeSpan.FromMilliseconds(360);
            spin.InsertKeyFrame(0f, from);
            spin.InsertKeyFrame(0.65f, from + 194f, settle); // small overshoot, then settle back
            spin.InsertKeyFrame(1f, to, settle);

            visual.StartAnimation("RotationAngleInDegrees", spin);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav toggle icon animation error: {ex.Message}");
        }
    }

    /// <summary>Radius of the content panel's rounded corner, matching the pane/chat-list seam.</summary>
    private const float ContentCornerRadius = 12f;

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyContentCornerClip(e.NewSize);
    }

    /// <summary>
    /// Clips the content card to its rounded rectangle, so page content is cut at the corners like
    /// the card background and border are.
    /// </summary>
    private void ApplyContentCornerClip(Windows.Foundation.Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ContentHost);
            var compositor = visual.Compositor;

            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new System.Numerics.Vector2(ContentCornerRadius, ContentCornerRadius);
            geometry.Size = new System.Numerics.Vector2((float)size.Width, (float)size.Height);

            visual.Clip = compositor.CreateGeometricClip(geometry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Content corner clip error: {ex.Message}");
        }
    }

    // 50 keeps the 40 px nav tiles plus their 4 px insets inside the card's 1 px border.
    private const double NavPaneCollapsedWidth = 50;
    private const double NavPaneExpandedWidth = 300;

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _navRailStoryboard;

    public void SetNavPaneExpanded(bool expanded, bool saveSetting = true, bool animate = true)
    {
        _isNavPaneExpanded = expanded;

        if (expanded)
        {
            // Labels are revealed by the rail's clip as it widens, and fade in alongside it.
            SetNavLabelsVisible(true);
        }

        if (animate)
        {
            AnimateNavRailWidth(expanded ? NavPaneExpandedWidth : NavPaneCollapsedWidth);
            AnimateNavLabelsOpacity(expanded ? 1f : 0f);
        }
        else
        {
            _navRailStoryboard?.Stop();
            _navRailStoryboard = null;
            if (NavRailCard != null)
            {
                NavRailCard.Width = expanded ? NavPaneExpandedWidth : NavPaneCollapsedWidth;
            }
            SetNavLabelsVisible(expanded);
            SetNavLabelsOpacity(expanded ? 1 : 0);
        }

        if (saveSetting && ServiceContainer.Settings != null)
        {
            ServiceContainer.Settings.IsNavPaneExpanded = expanded;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }

    private void SetNavLabelsVisible(bool visible)
    {
        var labelVis = visible ? Visibility.Visible : Visibility.Collapsed;
        if (ProtectionNavLabel != null) ProtectionNavLabel.Visibility = labelVis;
        if (RulesNavLabel != null) RulesNavLabel.Visibility = labelVis;
        if (WebsitesNavLabel != null) WebsitesNavLabel.Visibility = labelVis;
        if (FeedbackNavLabel != null) FeedbackNavLabel.Visibility = labelVis;
        if (SettingsNavLabel != null) SettingsNavLabel.Visibility = labelVis;
        if (BitLockerNavLabel != null) BitLockerNavLabel.Visibility = labelVis;
        if (InternetAccessNavLabel != null) InternetAccessNavLabel.Visibility = labelVis;
        if (OthersNavLabel != null) OthersNavLabel.Visibility = labelVis;
    }

    /// <summary>
    /// Slides the rail between its collapsed and expanded width. Width is a layout property, so it
    /// needs a dependent animation; the Auto column (and therefore the content) follows each frame.
    /// </summary>
    private void AnimateNavRailWidth(double to)
    {
        if (NavRailCard == null) return;

        _navRailStoryboard?.Stop();

        try
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EnableDependentAnimation = true,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut
                }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, NavRailCard);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Width");

            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, __) =>
            {
                if (NavRailCard != null) NavRailCard.Width = to;

                var expandedNow = to > NavPaneCollapsedWidth + 1;
                SetNavLabelsVisible(expandedNow);

                // Park the collapsed labels at opacity 0 so the next expand starts from a clean base.
                if (!expandedNow) SetNavLabelsOpacity(0);

                _navRailStoryboard = null;
            };

            _navRailStoryboard = storyboard;
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            // If the width animation cannot run, still land on the requested width.
            NavRailCard.Width = to;
            var landingExpanded = to > NavPaneCollapsedWidth + 1;
            SetNavLabelsVisible(landingExpanded);
            SetNavLabelsOpacity(landingExpanded ? 1 : 0);
            _navRailStoryboard = null;
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav rail animation error: {ex.Message}");
        }
    }

    private IEnumerable<TextBlock> GetNavLabels()
    {
        if (ProtectionNavLabel != null) yield return ProtectionNavLabel;
        if (RulesNavLabel != null) yield return RulesNavLabel;
        if (WebsitesNavLabel != null) yield return WebsitesNavLabel;
        if (FeedbackNavLabel != null) yield return FeedbackNavLabel;
        if (SettingsNavLabel != null) yield return SettingsNavLabel;
        if (BitLockerNavLabel != null) yield return BitLockerNavLabel;
        if (InternetAccessNavLabel != null) yield return InternetAccessNavLabel;
        if (OthersNavLabel != null) yield return OthersNavLabel;
    }

    /// <summary>Fades the nav labels in/out so they don't pop when the pane animates.</summary>
    private void AnimateNavLabelsOpacity(float to)
    {
        if (NavRail == null) return;

        try
        {
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                .GetElementVisual(NavRail).Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0f, 0f),
                new System.Numerics.Vector2(0.58f, 1f));

            foreach (var label in GetNavLabels())
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(label);

                // Set the resting opacity first. A row going Collapsed -> Visible is re-realized, which can
                // drop a composition animation started in the same pass; with the base value already at 1
                // the text is still visible if that happens (the cause of the "no labels on first expand"
                // bug), and the fade simply plays when it survives.
                visual.StopAnimation("Opacity");
                label.Opacity = 1;

                var animation = compositor.CreateScalarKeyFrameAnimation();
                animation.Duration = TimeSpan.FromMilliseconds(to > 0.5f ? 160 : 120);
                animation.InsertKeyFrame(0f, to > 0.5f ? 0f : 1f);
                animation.InsertKeyFrame(1f, to, ease);
                visual.StartAnimation("Opacity", animation);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav label fade error: {ex.Message}");
        }
    }

    private void SetNavLabelsOpacity(double value)
    {
        foreach (var label in GetNavLabels())
        {
            try
            {
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                    .GetElementVisual(label).StopAnimation("Opacity");
            }
            catch { }

            label.Opacity = value;
        }
    }

    private bool _navPillPositioned;
    private Button? _pendingPillItem;
    private bool _pillPlacementQueued;

    /// <summary>Slides the single accent pill to the selected row.</summary>
    private void MoveNavSelectionPill(Button? item, bool animate)
    {
        if (item == null || NavSelectionPill == null || NavRail == null) return;

        // Rows have no height until layout has run (e.g. right after the rail becomes visible). Remember
        // the selection and let the rail's next layout place the pill — never re-enqueue from here, or a
        // hidden/unlaid-out rail spins the dispatcher forever.
        if (item.ActualHeight <= 0)
        {
            _pendingPillItem = item;
            return;
        }

        _pendingPillItem = null;

        try
        {
            var origin = item.TransformToVisual(NavRail).TransformPoint(new Windows.Foundation.Point(0, 0));
            var targetY = (float)(origin.Y + (item.ActualHeight - NavSelectionPill.Height) / 2.0);
            if (float.IsNaN(targetY) || float.IsInfinity(targetY)) return;

            NavSelectionPill.Visibility = Visibility.Visible;

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(NavSelectionPill);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(NavSelectionPill, true);

            if (!animate || !_navPillPositioned)
            {
                visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0f, targetY, 0f));
                _navPillPositioned = true;
                return;
            }

            var compositor = visual.Compositor;
            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(180);
            animation.InsertKeyFrame(1f, new System.Numerics.Vector3(0f, targetY, 0f),
                compositor.CreateCubicBezierEasingFunction(
                    new System.Numerics.Vector2(0.2f, 0f),
                    new System.Numerics.Vector2(0f, 1f)));

            visual.StartAnimation("Translation", animation);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav pill error: {ex.Message}");
        }
    }

    private void NavTogglePaneButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => AnimateNavToggleHover(0.9f);

    private void NavTogglePaneButton_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetNavToggleFill("ShellNavHoverBrush");
        AnimateNavToggleHover(0f);
    }

    private void NavTogglePaneButton_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetNavToggleFill("ShellNavPressedBrush");
        AnimateNavToggleIconScale(0.92f);
    }

    private void NavTogglePaneButton_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetNavToggleFill("ShellNavHoverBrush");
        AnimateNavToggleIconScale(1f);
    }

    /// <summary>Swaps the hover tile to the pressed token while the button is held down.</summary>
    private void SetNavToggleFill(string resourceKey)
    {
        if (NavToggleHoverFill == null) return;

        try
        {
            if (Application.Current.Resources[resourceKey] is Microsoft.UI.Xaml.Media.Brush brush)
            {
                NavToggleHoverFill.Background = brush;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav toggle fill error: {ex.Message}");
        }
    }

    /// <summary>Fades the rounded hover tile in/out over 150 ms instead of snapping.</summary>
    private void AnimateNavToggleHover(float target)
    {
        if (NavToggleHoverFill == null) return;

        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(NavToggleHoverFill);
            var compositor = visual.Compositor;

            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(150);
            animation.InsertKeyFrame(1f, target, compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0f, 0f),
                new System.Numerics.Vector2(0.58f, 1f)));

            visual.StartAnimation("Opacity", animation);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav toggle hover error: {ex.Message}");
        }
    }

    /// <summary>Scales the burger icon while pressed (about 0.92).</summary>
    private void AnimateNavToggleIconScale(float target)
    {
        if (NavTogglePaneIcon == null) return;

        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(NavTogglePaneIcon);
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)NavTogglePaneIcon.ActualWidth / 2f,
                (float)NavTogglePaneIcon.ActualHeight / 2f,
                0f);

            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(110);
            animation.InsertKeyFrame(1f, new System.Numerics.Vector3(target, target, 1f));

            visual.StartAnimation("Scale", animation);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav toggle scale error: {ex.Message}");
        }
    }

    /// <summary>Clips the rail to its own bounds so labels are cut off while the pane is collapsing.</summary>
    private void NavRail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(NavRail);
            var compositor = visual.Compositor;

            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new System.Numerics.Vector2(0f, 0f);
            geometry.Size = new System.Numerics.Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);

            visual.Clip = compositor.CreateGeometricClip(geometry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Nav rail clip error: {ex.Message}");
        }

        // The rail just laid out, so a selection that arrived before layout can now be placed.
        TryPlacePendingPill();
    }

    /// <summary>Places a pill that was requested before the rows had any height.</summary>
    private void TryPlacePendingPill()
    {
        if (_pendingPillItem == null || _pillPlacementQueued) return;

        _pillPlacementQueued = true;
        var item = _pendingPillItem;
        _pendingPillItem = null;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pillPlacementQueued = false;
            if (item != null)
            {
                MoveNavSelectionPill(item, animate: false);
            }
        });
    }

    public void ApplyLocalization()
    {
        var loc = ServiceContainer.LocalizationService;
        if (AppTitleText != null) AppTitleText.Text = loc["AppTitle"];
        if (ProtectionNavLabel != null) ProtectionNavLabel.Text = loc["NavProtection"];
        if (RulesNavLabel != null) RulesNavLabel.Text = loc["NavRules"];
        if (WebsitesNavLabel != null) WebsitesNavLabel.Text = loc["NavWebsites"];
        if (FeedbackNavLabel != null) FeedbackNavLabel.Text = loc["NavFeedback"];
        if (SettingsNavLabel != null) SettingsNavLabel.Text = loc["NavSettings"];

        if (ProtectionNavButton != null) ToolTipService.SetToolTip(ProtectionNavButton, loc["NavProtection"]);
        if (RulesNavButton != null) ToolTipService.SetToolTip(RulesNavButton, loc["NavRules"]);
        if (WebsitesNavButton != null) ToolTipService.SetToolTip(WebsitesNavButton, loc["NavWebsites"]);
        if (FeedbackNavButton != null) ToolTipService.SetToolTip(FeedbackNavButton, loc["NavFeedback"]);
        if (SettingsNavButton != null) ToolTipService.SetToolTip(SettingsNavButton, loc["NavSettings"]);
        if (NavTogglePaneButton != null) ToolTipService.SetToolTip(NavTogglePaneButton, loc["NavToggle"]);
        if (BitLockerNavLabel != null) BitLockerNavLabel.Text = "BitLocker";
        if (BitLockerNavButton != null) ToolTipService.SetToolTip(BitLockerNavButton, "BitLocker");
        if (InternetAccessNavLabel != null) InternetAccessNavLabel.Text = "Internet Access";
        if (InternetAccessNavButton != null) ToolTipService.SetToolTip(InternetAccessNavButton, "Internet Access");
        if (OthersNavLabel != null) OthersNavLabel.Text = "Others";
        if (OthersNavButton != null) ToolTipService.SetToolTip(OthersNavButton, "Others");
    }

    private void InitializeServices()
    {
        // Set window handle for hotkey service
        ServiceContainer.HotkeyService.SetWindowHandle(_hwnd);

        // Subclass the window to intercept WM_HOTKEY messages
        InstallWndProcSubclass();

        // Register emergency hotkey if enabled
        RegisterEmergencyHotkey();

        // Register Ring Light toggle hotkey (Ctrl+Shift+L)
        RegisterRingLightHotkey();

        // Register Notification toggle hotkey (Alt+N)
        RegisterNotificationToggleHotkey();

        // Our own input clock (Windows' idle counter is unreliable on some machines)
        InputActivityMonitor.Start();

        // Start idle monitoring if configured
        StartIdleMonitoring();
        InitializeIdleLockTracking();

        // Wire up idle service event
        ServiceContainer.IdleService.IdleTimeReached += IdleService_IdleTimeReached;

        // Initialize and wire up tray service
        InitializeTrayService();

        // Start Self-Discipline service (idle relock, quota, schedule)
        var sdService = ServiceContainer.GetOrCreateSelfDisciplineService(DispatcherQueue);
        sdService.Start();

        // Initialize global brightness hotkeys
        ServiceContainer.InitializeBrightnessHotkeys(DispatcherQueue);

        // Initialize Volume Mixer hotkeys
        ServiceContainer.InitializeVolumeMixerHotkeys(DispatcherQueue);

        // Initialize Eye Care scheduler
        ServiceContainer.InitializeEyeCare(DispatcherQueue);

        // Safety net: restore all DWM thumbnails even on unexpected process exit
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try { ServiceContainer.ThumbnailService.Dispose(); } catch { }
        };
    }

    private void InstallWndProcSubclass()
    {
        // Keep a strong reference to the delegate so GC doesn't collect it
        _subclassProc = WndProcSubclass;
        bool ok = SetWindowSubclass(_hwnd.Value, _subclassProc, 1, 0);
        System.Diagnostics.Debug.WriteLine($"WndProc subclass installed: {ok}");
    }

    private nint WndProcSubclass(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_CLOSE && !_isExplicitExit)
        {
            DispatcherQueue.TryEnqueue(async () => await HandleCloseRequestAsync());
            return 0; // Prevent Win32 window destruction
        }

        if (uMsg == WM_HOTKEY)
        {
            int hotkeyId = (int)wParam;
            System.Diagnostics.Debug.WriteLine($"WM_HOTKEY received: id={hotkeyId}");
            ServiceContainer.HotkeyService.ProcessHotkeyMessage(hotkeyId);
            return 0;
        }
        if (uMsg == WM_DISPLAYCHANGE)
        {
            // Dispatch to UI thread — this message arrives on the message thread
            DispatcherQueue.TryEnqueue(() =>
            {
                ServiceContainer.RingLightService.OnDisplayChanged();
            });
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void InitializeTrayService()
    {
        try
        {
            ServiceContainer.TrayService.Initialize();
            
            // Wire up tray events
            ServiceContainer.TrayService.OpenMainWindowRequested += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() => Show());
            };
            
            ServiceContainer.TrayService.EmergencyLockRequested += (s, e) =>
            {
                OnEmergencyLockHotkey();
            };
            
            ServiceContainer.TrayService.ExitRequested += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(async () => await HandleExitRequestAsync());
            };
            
            ServiceContainer.TrayService.ProtectionToggleRequested += (s, newState) =>
            {
                DispatcherQueue.TryEnqueue(async () => await HandleProtectionToggleAsync(newState));
            };
            
            ServiceContainer.TrayService.BrightnessHotkeysToggleRequested += (s, newState) =>
            {
                DispatcherQueue.TryEnqueue(() => HandleBrightnessHotkeysToggle(newState));
            };
            
            ServiceContainer.TrayService.BlurHotkeyToggleRequested += (s, newState) =>
            {
                DispatcherQueue.TryEnqueue(() => HandleBlurHotkeyToggle(newState));
            };
            
            ServiceContainer.TrayService.VolumeMixerToggleRequested += (s, newState) =>
            {
                DispatcherQueue.TryEnqueue(() => HandleVolumeMixerToggle(newState));
            };
            
            System.Diagnostics.Debug.WriteLine("Tray service initialized");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error initializing tray service: {ex.Message}");
        }
    }

    private async Task HandleProtectionToggleAsync(bool enable)
    {
        if (_isAuthDialogOpen) return;
        _isAuthDialogOpen = true;

        try
        {
            Show();
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                Title = enable ? "Enable Protection" : "Disable Protection",
                XamlRoot = Content?.XamlRoot
            };

            await dialog.ShowAsync();

            if (dialog.IsAuthenticated)
            {
                if (enable)
                {
                    ServiceContainer.LockManager.EnableProtection();
                    ServiceContainer.OverlayManager.Start();
                }
                else
                {
                    ServiceContainer.LockManager.DisableProtection();
                    ServiceContainer.OverlayManager.Stop();
                }
                
                // Update tray
                ServiceContainer.TrayService.UpdateProtectionMenuItems();
                
                // Show notification
                await ShowNotificationAsync(
                    "Protection " + (enable ? "Enabled" : "Disabled"),
                    "AppLock protection is now " + (enable ? "active" : "inactive") + "."
                );
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during protection toggle: {ex.Message}");
        }
        finally
        {
            _isAuthDialogOpen = false;
        }
    }

    private void HandleBrightnessHotkeysToggle(bool enable)
    {
        try
        {
            ServiceContainer.Settings.Brightness.HotkeysEnabled = enable;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            
            // Update tray menu
            ServiceContainer.TrayService.UpdateBrightnessMenuItems();
            
            System.Diagnostics.Debug.WriteLine($"Brightness hotkeys {(enable ? "enabled" : "disabled")} from tray");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error toggling brightness hotkeys: {ex.Message}");
        }
    }

    private void HandleBlurHotkeyToggle(bool enable)
    {
        try
        {
            ServiceContainer.Settings.Brightness.Blur.HotkeyEnabled = enable;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            
            // Update tray menu
            ServiceContainer.TrayService.UpdateBrightnessMenuItems();
            
            System.Diagnostics.Debug.WriteLine($"Blur hotkey {(enable ? "enabled" : "disabled")} from tray");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error toggling blur hotkey: {ex.Message}");
        }
    }

    private void HandleVolumeMixerToggle(bool enable)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Volume Mixer toggle requested from tray: {enable}");
            ServiceContainer.SetVolumeMixerEnabled(enable);
            
            System.Diagnostics.Debug.WriteLine($"Volume Mixer {(enable ? "enabled" : "disabled")} from tray");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error toggling Volume Mixer: {ex.Message}");
        }
    }

    private async Task HandleCloseRequestAsync()
    {
        if (_isAuthDialogOpen) return;
        _isAuthDialogOpen = true;

        try
        {
            Show(); // Ensure window is visible so auth dialog can be displayed
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                Title = "Exit AppLock",
                PrimaryButtonText = "Exit AppLock",
                CloseButtonText = "Minimize to Tray",
                XamlRoot = Content?.XamlRoot
            };

            await dialog.ShowAsync();

            if (dialog.IsAuthenticated)
            {
                _isExplicitExit = true;
                MainWindow_ActualClose();

                // Do NOT call Application.Current.Exit() here. The window's close was cancelled
                // above and a dialog was awaited, so tearing the same window down now makes WinUI
                // dereference the half-destroyed native window (0xc0000005) and Windows reports
                // "Exception Processing Message 0xc0000005 - Unexpected parameters". Everything is
                // already cleaned up, so end the process directly.
                Environment.Exit(0);
            }
            else
            {
                // User cancelled or chose to minimize to tray
                Hide();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during close request: {ex.Message}");
            Hide();
        }
        finally
        {
            _isAuthDialogOpen = false;
        }
    }

    private async Task HandleExitRequestAsync()
    {
        await HandleCloseRequestAsync();
    }

    private void RegisterEmergencyHotkey()
    {
        var settings = ServiceContainer.Settings;
        
        if (settings.EmergencyHotkeyEnabled && !string.IsNullOrEmpty(settings.EmergencyHotkey))
        {
            try
            {
                var (modifiers, key) = HotkeyService.ParseHotkey(settings.EmergencyHotkey);
                _emergencyHotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(
                    modifiers,
                    key,
                    OnEmergencyLockHotkey
                );
                
                if (_emergencyHotkeyId > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Emergency hotkey registered: {settings.EmergencyHotkey}");
                    // Show badge in overlay (registration succeeded)
                    ServiceContainer.OverlayManager.UpdateEmergencyHotkey(settings.EmergencyHotkey, true);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to register emergency hotkey: {settings.EmergencyHotkey}");
                    // Hide badge — hotkey is non-functional (common: Ctrl+Alt = AltGr conflict on non-US keyboards)
                    ServiceContainer.OverlayManager.UpdateEmergencyHotkey(settings.EmergencyHotkey, false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error registering emergency hotkey: {ex.Message}");
            }
        }
    }

    private void UnregisterEmergencyHotkey()
    {
        if (_emergencyHotkeyId > 0)
        {
            ServiceContainer.HotkeyService.UnregisterHotkey(_emergencyHotkeyId);
            _emergencyHotkeyId = -1;
        }
    }

    internal void RegisterRingLightHotkey()
    {
        UnregisterRingLightHotkey();
        try
        {
            var hotkeyStr = ServiceContainer.Settings.RingLight.ToggleHotkey;
            if (string.IsNullOrWhiteSpace(hotkeyStr)) return;

            var (modifiers, key) = HotkeyService.ParseHotkey(hotkeyStr);
            if (key == 0) return;

            _ringLightHotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(
                modifiers,
                key,
                () =>
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        bool nextState = !ServiceContainer.RingLightService.IsOn;
                        ServiceContainer.SetRingLightEnabled(nextState);
                        if (ServiceContainer.Settings.RingLight.ShowNotificationOnToggle)
                        {
                            ShowToastNotification(new AppNotification
                            {
                                Title = "Ring Light",
                                Message = nextState ? "Ring light turned on" : "Ring light turned off"
                            });
                        }
                    });
                }
            );
            System.Diagnostics.Debug.WriteLine(
                _ringLightHotkeyId > 0
                    ? $"[RingLight] Hotkey {hotkeyStr} registered"
                    : $"[RingLight] Failed to register {hotkeyStr} (conflict?)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RingLight] Error registering hotkey: {ex.Message}");
        }
    }

    private void UnregisterRingLightHotkey()
    {
        if (_ringLightHotkeyId > 0)
        {
            ServiceContainer.HotkeyService.UnregisterHotkey(_ringLightHotkeyId);
            _ringLightHotkeyId = -1;
        }
    }

    /// <summary>
    /// Re-register the emergency hotkey after a change in Settings.
    /// Called by SettingsPage after saving the new hotkey string.
    /// Returns true if registered successfully, false otherwise.
    /// </summary>
    internal bool ReregisterEmergencyHotkey()
    {
        UnregisterEmergencyHotkey();
        RegisterEmergencyHotkey();
        bool success = _emergencyHotkeyId > 0;
        System.Diagnostics.Debug.WriteLine($"Emergency hotkey re-registered (success={success}): {ServiceContainer.Settings.EmergencyHotkey}");
        return success;
    }

    internal void RegisterNotificationToggleHotkey()
    {
        UnregisterNotificationToggleHotkey();
        try
        {
            var hotkey = ServiceContainer.Settings.NotificationCustom.ToggleHotkey;
            if (hotkey == null || hotkey.VirtualKey == 0) return;

            _notificationToggleHotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(
                (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)hotkey.Modifiers,
                hotkey.VirtualKey,
                OnNotificationToggleHotkey
            );

            if (_notificationToggleHotkeyId > 0)
            {
                System.Diagnostics.Debug.WriteLine($"Notification toggle hotkey registered: {HotkeyParser.FormatHotkey(hotkey.Modifiers, hotkey.VirtualKey)}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Failed to register notification toggle hotkey");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error registering notification toggle hotkey: {ex.Message}");
        }
    }

    internal void UnregisterNotificationToggleHotkey()
    {
        if (_notificationToggleHotkeyId > 0)
        {
            ServiceContainer.HotkeyService.UnregisterHotkey(_notificationToggleHotkeyId);
            _notificationToggleHotkeyId = -1;
        }
    }

    private void OnNotificationToggleHotkey()
    {
        System.Diagnostics.Debug.WriteLine("Notification toggle hotkey pressed!");
        
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var newEnabled = !ServiceContainer.Settings.NotificationCustom.Enabled;
                ServiceContainer.SetNotificationCustomizerEnabled(newEnabled);
                
                // Show notification
                var status = newEnabled ? "enabled" : "disabled";
                ServiceContainer.NotificationService.ShowInfo(
                    "Notification Positioning",
                    $"Notification positioning {status}",
                    TimeSpan.FromSeconds(2)
                );
                
                LogService.LogAction("notifications", $"Toggled via hotkey: {status}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error toggling notification positioning: {ex.Message}");
                LogService.LogAction("notifications", $"Error toggling via hotkey: {ex.Message}");
            }
        });
    }

    private void InitializeInAppToast()
    {
        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (s, e) =>
        {
            _toastTimer.Stop();
            InAppToastCard.Visibility = Visibility.Collapsed;
        };

        ServiceContainer.NotificationService.NotificationTriggered += (s, notification) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ShowToastNotification(notification);
            });
        };

        ServiceContainer.NotificationService.NotificationDismissRequested += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _toastTimer?.Stop();
                InAppToastCard.Visibility = Visibility.Collapsed;
            });
        };
    }

    private void ShowToastNotification(AppNotification notification)
    {
        if (ServiceContainer.Settings.MuteNotifications)
            return;

        _toastTimer?.Stop();

        UpdateToastTheme();

        ToastTitleText.Text = notification.Title;
        ToastMessageText.Text = notification.Message;

        // Reset and trigger smooth reveal
        InAppToastCard.Visibility = Visibility.Collapsed;
        InAppToastCard.Visibility = Visibility.Visible;

        if (_toastTimer != null && notification.Duration > TimeSpan.Zero)
        {
            _toastTimer.Interval = notification.Duration;
            _toastTimer.Start();
        }
    }

    private void ToastCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer?.Stop();
        InAppToastCard.Visibility = Visibility.Collapsed;
    }

    private void StartIdleMonitoring()
    {
        var settings = ServiceContainer.Settings;
        
        if (settings.AutoLockIdleMinutes > 0)
        {
            ServiceContainer.IdleService.StartMonitoring(settings.AutoLockIdleMinutes);
            System.Diagnostics.Debug.WriteLine($"Idle monitoring started: {settings.AutoLockIdleMinutes} minutes");
        }
    }

    /// <summary>
    /// Tracks interaction with the AppLock window and locks it when this window has been idle for
    /// the configured time. Only activity in this window counts — working in other apps does not
    /// keep AppLock unlocked, and system-wide idleness is not required.
    /// </summary>
    private void InitializeIdleLockTracking()
    {
        try
        {
            if (Content is UIElement root)
            {
                var pointerHandler = new Microsoft.UI.Xaml.Input.PointerEventHandler((s, e) => NoteWindowActivity());
                root.AddHandler(UIElement.PointerMovedEvent, pointerHandler, true);
                root.AddHandler(UIElement.PointerPressedEvent, pointerHandler, true);
                root.AddHandler(UIElement.PointerWheelChangedEvent, pointerHandler, true);
                root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((s, e) => NoteWindowActivity()), true);
            }

            Activated += (s, e) => NoteWindowActivity();

            _idleWatchdogTimer?.Stop();
            _idleWatchdogTimer = DispatcherQueue.CreateTimer();
            _idleWatchdogTimer.Interval = TimeSpan.FromSeconds(5);
            _idleWatchdogTimer.IsRepeating = true;
            _idleWatchdogTimer.Tick += (s, e) => CheckIdleLock();
            _idleWatchdogTimer.Start();

            IdleService.Log("Idle lock tracking started (per AppLock window)");
        }
        catch (Exception ex)
        {
            IdleService.Log($"Idle lock tracking error: {ex.Message}");
        }
    }

    private void NoteWindowActivity() => _lastWindowActivity = DateTime.UtcNow;

    /// <summary>True when the AppLock window is actually on screen and can be locked.</summary>
    private bool IsWindowActivelyVisible()
    {
        if (_appWindow == null || !_appWindow.IsVisible) return false;

        if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
            presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
        {
            return false;
        }

        return true;
    }

    private void CheckIdleLock()
    {
        try
        {
            var minutes = ServiceContainer.Settings.AutoLockIdleMinutes;
            if (minutes <= 0) return;

            // Already asking for the password, or nothing visible to lock.
            if (!_isMainAuthenticated || _isAuthDialogOpen || !IsWindowActivelyVisible())
            {
                NoteWindowActivity();
                return;
            }

            double idleMinutes = (DateTime.UtcNow - _lastWindowActivity).TotalMinutes;
            if (idleMinutes >= minutes)
            {
                IdleService.Log($"AppLock window idle {idleMinutes:F1} min (threshold {minutes}) - minimizing to tray and locking");
                LockToTray();
            }
        }
        catch (Exception ex)
        {
            IdleService.Log($"Idle lock check error: {ex.Message}");
        }
    }

    private void OnEmergencyLockHotkey()
    {
        System.Diagnostics.Debug.WriteLine("Emergency lock hotkey pressed!");
        
        DispatcherQueue.TryEnqueue(() =>
        {
            // Stop all overlays
            ServiceContainer.OverlayManager.Stop();
            
            // Re-lock everything immediately (ignore unlock timers)
            ServiceContainer.LockManager.DisableProtection();
            ServiceContainer.LockManager.EnableProtection();
            
            // Restart overlay manager
            ServiceContainer.OverlayManager.Start();
        });
    }

    private DateTime _lastWindowActivity = DateTime.UtcNow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleWatchdogTimer;

    /// <summary>
    /// Closes every ContentDialog currently open in this window so the window can be locked and the
    /// idle unlock prompt can be shown later. Returns true when nothing is left open.
    /// </summary>
    private bool CloseOpenDialogs()
    {
        try
        {
            var xamlRoot = ContentFrame.XamlRoot ?? Content?.XamlRoot;
            if (xamlRoot == null) return true;

            var openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
            var closed = 0;

            foreach (var popup in openPopups)
            {
                var dialog = FindContentDialog(popup.Child);
                if (dialog == null) continue;

                dialog.Hide();
                closed++;
            }

            if (closed > 0)
            {
                IdleService.Log($"Closed {closed} open dialog(s) before locking");
            }
            else if (openPopups.Count > 0)
            {
                // No ContentDialog element in the popup layer (e.g. a flyout or drop-down): close
                // the popups so nothing is left floating over the locked window.
                foreach (var popup in openPopups)
                {
                    try { popup.IsOpen = false; } catch { }
                }
                IdleService.Log($"Closed {openPopups.Count} open popup(s) before locking");
            }

            return closed == 0 ? openPopups.Count == 0 : true;
        }
        catch (Exception ex)
        {
            IdleService.Log($"Closing open dialogs failed: {ex.Message}");
            return false;
        }
    }

    private static ContentDialog? FindContentDialog(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is ContentDialog dialog) return dialog;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindContentDialog(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }

        return null;
    }

    private void IdleService_IdleTimeReached(object? sender, EventArgs e) => LockToTray();

    /// <summary>
    /// Idle auto-lock: require the password again and minimize AppLock to the tray. The password
    /// prompt is shown inside the AppLock window (never in a separate window) when it is reopened
    /// from the tray, and cancelling that prompt keeps the app minimized and locked.
    /// </summary>
    private void LockToTray()
    {
        try
        {
            if (!_isMainAuthenticated) return;

            _isMainAuthenticated = false;

            // A ContentDialog that is open right now (e.g. a hotkey or settings dialog) would keep
            // the window from being locked and later block the unlock prompt. Close it first.
            CloseOpenDialogs();

            IdleService.Log("Idle auto-lock: minimized to tray, password required");
            Hide();
        }
        catch (Exception ex)
        {
            IdleService.Log($"Idle auto-lock error: {ex.Message}");
        }
    }

    private async Task ShowNotificationAsync(string title, string message)
    {
        if (ServiceContainer.Settings.MuteNotifications)
            return;

        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content?.XamlRoot
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing notification: {ex.Message}");
        }
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!_isExplicitExit)
        {
            // Cancel close and prompt for exit password or minimize to tray
            args.Cancel = true;
            DispatcherQueue.TryEnqueue(async () => await HandleCloseRequestAsync());
        }
    }
    
    private void MainWindow_ActualClose()
    {
        // Signal authorized exit to watchdog and restore process DACL
        AppLock.Services.ProcessProtectionService.SignalAuthorizedExit();
        AppLock.Services.ProcessProtectionService.UnprotectCurrentProcess();

        // Actual cleanup when exiting app
        UnregisterEmergencyHotkey();
        UnregisterRingLightHotkey();
        UnregisterNotificationToggleHotkey();
        // Remove WndProc subclass
        if (_subclassProc != null)
        {
            RemoveWindowSubclass(_hwnd.Value, _subclassProc, 1);
            _subclassProc = null;
        }
        _idleWatchdogTimer?.Stop();
        _idleWatchdogTimer = null;
        ServiceContainer.IdleService.StopMonitoring();
        ServiceContainer.OverlayManager.Stop();

        // CRITICAL: Restore all locked thumbnails before exiting.
        // Without this, the "Locked" DWM bitmap persists in target apps after AppLock exits.
        ServiceContainer.ThumbnailService.Dispose();

        // The speed widget, flyout and toasts are separate top-level windows. Close them and stop
        // their timers while the XAML host is still alive, otherwise exiting the process with them
        // open can crash during teardown.
        try
        {
            ServiceContainer.SpeedMeterService?.Stop();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Shutdown error: {ex.Message}");
        }

        ServiceContainer.TrayService.Dispose();
        _windowIcon?.Dispose();
        _windowIcon = null;
        Instance = null;
    }

    public void Show()
    {
        _startInTray = false;

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_appWindow != null)
            {
                _appWindow.Show();
            }
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
            this.Activate();
            PInvoke.BringWindowToTop(_hwnd);
            PInvoke.SetForegroundWindow(_hwnd);

            // Reopening the window counts as activity for the idle clock.
            NoteWindowActivity();

            System.Diagnostics.Debug.WriteLine("MainWindow restored and brought to foreground");

            if (!_isMainAuthenticated && !_isAuthDialogOpen)
            {
                await ShowLogin();
            }
        });
    }

    public void Hide()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appWindow != null)
            {
                _appWindow.Hide();
            }
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            System.Diagnostics.Debug.WriteLine("MainWindow hidden to system tray");
        });
    }

    /// <summary>
    /// Hides the window synchronously, before the message loop can paint it. Used by an autostart
    /// launch: the window is activated so its XAML tree is realized, then hidden in the same pass.
    /// </summary>
    internal void HideImmediately()
    {
        if (_appWindow != null)
        {
            _appWindow.Hide();
        }
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        System.Diagnostics.Debug.WriteLine("MainWindow hidden to system tray (startup mode)");
    }
}
