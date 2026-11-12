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
    private DispatcherTimer? _toastTimer;
    private Windows.UI.ViewManagement.UISettings? _uiSettings;

    internal static MainWindow? Instance { get; private set; }
    internal HWND Hwnd => _hwnd;

    public MainWindow()
    {
        Instance = this;
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
        SetNavPaneExpanded(ServiceContainer.Settings.IsNavPaneExpanded, saveSetting: false);

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
            NavRail.Visibility = Visibility.Collapsed;
            Grid.SetColumn(ContentFrame, 0);
            ContentFrame.SetValue(Grid.ColumnSpanProperty, 2);

            // Show login immediately after loading completes
            ContentFrame.Loaded += async (s, e) =>
            {
                if (!_loginShown)
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
            indicator.Background = activeAccent;
            indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
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
        NavRail.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ContentFrame, 0);
        ContentFrame.SetValue(Grid.ColumnSpanProperty, 2);
    }

    public void OnFirstRunComplete()
    {
        _isMainAuthenticated = true;

        // Restore navigation rail
        Grid.SetColumn(ContentFrame, 1);
        ContentFrame.ClearValue(Grid.ColumnSpanProperty);
        NavRail.Visibility = Visibility.Visible;
        
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
            while (!_isMainAuthenticated)
            {
                var dialog = new AuthDialog(ServiceContainer.AuthService)
                {
                    XamlRoot = ContentFrame.XamlRoot ?? Content?.XamlRoot
                };

                await dialog.ShowAsync();
                
                System.Diagnostics.Debug.WriteLine($"IsAuthenticated: {dialog.IsAuthenticated}");

                // Check IsAuthenticated - this is set to true only when password is correct
                if (dialog.IsAuthenticated)
                {
                    _isMainAuthenticated = true;
                    System.Diagnostics.Debug.WriteLine("Authentication successful, navigating to ProtectionPage");

                    // Restore navigation rail
                    Grid.SetColumn(ContentFrame, 1);
                    ContentFrame.ClearValue(Grid.ColumnSpanProperty);
                    NavRail.Visibility = Visibility.Visible;
                    
                    try
                    {
                        ContentFrame.Navigate(typeof(ProtectionPage));
                        
                        // Start overlay manager if protection is enabled
                        if (ServiceContainer.Settings.IsProtectionEnabled)
                        {
                            ServiceContainer.OverlayManager.Start();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR during initialization: {ex.Message}");
                    }
                    
                    break;
                }
                else
                {
                    // User cancelled the login prompt on startup - minimize to tray instead of exiting,
                    // so protected apps REMAIN strictly locked and guarded!
                    Hide();
                    _loginShown = false;
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
        SetNavPaneExpanded(!_isNavPaneExpanded, saveSetting: true);
    }

    public void SetNavPaneExpanded(bool expanded, bool saveSetting = true)
    {
        _isNavPaneExpanded = expanded;
        if (NavPaneColumn != null)
        {
            NavPaneColumn.Width = expanded ? new GridLength(210) : new GridLength(48);
        }

        var labelVis = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (ProtectionNavLabel != null) ProtectionNavLabel.Visibility = labelVis;
        if (RulesNavLabel != null) RulesNavLabel.Visibility = labelVis;
        if (WebsitesNavLabel != null) WebsitesNavLabel.Visibility = labelVis;
        if (FeedbackNavLabel != null) FeedbackNavLabel.Visibility = labelVis;
        if (SettingsNavLabel != null) SettingsNavLabel.Visibility = labelVis;
        if (BitLockerNavLabel != null) BitLockerNavLabel.Visibility = labelVis;
        if (InternetAccessNavLabel != null) InternetAccessNavLabel.Visibility = labelVis;
        if (OthersNavLabel != null) OthersNavLabel.Visibility = labelVis;

        if (saveSetting && ServiceContainer.Settings != null)
        {
            ServiceContainer.Settings.IsNavPaneExpanded = expanded;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
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

        // Start idle monitoring if configured
        StartIdleMonitoring();

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
                Application.Current.Exit();
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

    private void IdleService_IdleTimeReached(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Idle time reached - locking app");
        _isMainAuthenticated = false;
        
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Hide main window
            Hide();
            
            // Show auth dialog
            await ShowIdleLockDialog();
        });
    }

    private async Task ShowIdleLockDialog()
    {
        while (true)
        {
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                Title = "AppLock Idle Auto-Lock",
                XamlRoot = Content?.XamlRoot
            };

            if (Content?.XamlRoot == null)
            {
                // Window not ready, wait a bit
                await Task.Delay(500);
                continue;
            }

            await dialog.ShowAsync();

            if (dialog.IsAuthenticated)
            {
                // Show window again
                Show();
                
                // Restart idle monitoring
                StartIdleMonitoring();
                break;
            }
            
            await Task.Delay(100);
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
        ServiceContainer.IdleService.StopMonitoring();
        ServiceContainer.OverlayManager.Stop();

        // CRITICAL: Restore all locked thumbnails before exiting.
        // Without this, the "Locked" DWM bitmap persists in target apps after AppLock exits.
        ServiceContainer.ThumbnailService.Dispose();

        ServiceContainer.TrayService.Dispose();
        _windowIcon?.Dispose();
        _windowIcon = null;
        Instance = null;
    }

    public void Show()
    {
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
}
