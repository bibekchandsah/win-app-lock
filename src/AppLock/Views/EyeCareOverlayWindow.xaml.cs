using System;
using System.Threading;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace AppLock.Views;

public sealed partial class EyeCareOverlayWindow : Window
{
    private readonly string _htmlPath;
    private readonly int _durationSeconds;
    private readonly Action _closeAllCallback;
    private Timer? _autoCloseTimer;
    private AppWindow? _appWindow;

    public AppWindow? AppWindow => _appWindow;

    public EyeCareOverlayWindow(string htmlPath, int durationSeconds, Action closeAllCallback)
    {
        System.Diagnostics.Debug.WriteLine("[EyeCare] Constructor called");
        
        this.InitializeComponent();
        
        System.Diagnostics.Debug.WriteLine("[EyeCare] InitializeComponent completed");
        
        _htmlPath = htmlPath;
        _durationSeconds = durationSeconds;
        _closeAllCallback = closeAllCallback;

        SetupWindow();
        
        // Temporarily disabled for testing - no WebView in XAML
        // _ = InitializeWebViewAsync();
        
        // Start auto-close timer for testing
        StartAutoCloseTimer();
    }

    private void SetupWindow()
    {
        try
        {
            // Get AppWindow
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Window handle: {hWnd}, WindowId: {windowId.Value}");
            
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                _appWindow = appWindow;
                System.Diagnostics.Debug.WriteLine("[EyeCare] AppWindow obtained successfully");

                // Hide from taskbar and task switcher
                appWindow.IsShownInSwitchers = false;

                // Fullscreen
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

                // Always on top (if available in FullScreen, otherwise handled by Z-order)
                var presenter = appWindow.Presenter;
                if (presenter is OverlappedPresenter overlapped)
                {
                    overlapped.IsAlwaysOnTop = true;
                }
                
                System.Diagnostics.Debug.WriteLine("[EyeCare] Window configured successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[EyeCare] WARNING: AppWindow is null!");
            }

            // Remove title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Window setup error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Stack trace: {ex.StackTrace}");
        }
    }

    private async System.Threading.Tasks.Task InitializeWebViewAsync()
    {
        // Temporarily disabled for testing - WebView removed from XAML
        /*
        try
        {
            await WebView.EnsureCoreWebView2Async();
            
            var core = WebView.CoreWebView2;
            
            // Security settings
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;

            // Virtual host mapping for local file access
            var renderFolder = System.IO.Path.GetDirectoryName(_htmlPath);
            if (!string.IsNullOrEmpty(renderFolder))
            {
                core.SetVirtualHostNameToFolderMapping(
                    "eyecare.local", 
                    renderFolder, 
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            // Handle close message from HTML
            core.WebMessageReceived += (s, e) =>
            {
                var message = e.TryGetWebMessageAsString();
                if (message == "close")
                {
                    System.Diagnostics.Debug.WriteLine("[EyeCare] Close message received from WebView");
                    DispatcherQueue.TryEnqueue(() => _closeAllCallback());
                }
            };

            // Block navigation away from eyecare.local
            core.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.Contains("eyecare.local") && !e.Uri.StartsWith("file://"))
                {
                    e.Cancel = true;
                }
            };

            // Block new windows
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
            };

            // Navigate to rendered HTML
            var fileName = System.IO.Path.GetFileName(_htmlPath);
            core.Navigate($"https://eyecare.local/{fileName}");

            System.Diagnostics.Debug.WriteLine($"[EyeCare] WebView initialized, navigating to {fileName}");

            // Start auto-close timer
            StartAutoCloseTimer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] WebView initialization error: {ex.Message}");
            // Fallback: close immediately if WebView fails
            DispatcherQueue.TryEnqueue(() => _closeAllCallback());
        }
        */
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void StartAutoCloseTimer()
    {
        var delay = TimeSpan.FromSeconds(_durationSeconds);
        _autoCloseTimer = new Timer(_ =>
        {
            System.Diagnostics.Debug.WriteLine("[EyeCare] Auto-close timer expired");
            DispatcherQueue.TryEnqueue(() => _closeAllCallback());
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    private void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            System.Diagnostics.Debug.WriteLine("[EyeCare] Escape key pressed");
            _closeAllCallback();
            e.Handled = true;
        }
    }

    public new void Close()
    {
        _autoCloseTimer?.Dispose();
        _autoCloseTimer = null;
        
        try
        {
            base.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Close error: {ex.Message}");
        }
    }
}
