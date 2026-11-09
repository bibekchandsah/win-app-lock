using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services.EyeCare;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace AppLock.Views;

public sealed partial class EyeCareOverlayWindow : Window
{
    private readonly string _htmlPath;
    private readonly int _durationSeconds;
    private readonly string _reminderMessage;
    private readonly Action _closeAllCallback;
    private Timer? _autoCloseTimer;
    private int _remainingSeconds;
    private Timer? _fallbackTicker;

    private const nint HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    public EyeCareOverlayWindow(string htmlPath, int durationSeconds, string reminderMessage, Action closeAllCallback)
    {
        this.InitializeComponent();

        _htmlPath = htmlPath;
        _durationSeconds = durationSeconds > 0 ? durationSeconds : 20;
        _remainingSeconds = _durationSeconds;
        _reminderMessage = string.IsNullOrWhiteSpace(reminderMessage) ? EyeCareSettings.DefaultMessage : reminderMessage;
        _closeAllCallback = closeAllCallback;

        SetupWindow();
        _ = InitializeWebViewAsync();
        StartAutoCloseTimer();
    }

    private void SetupWindow()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appWindow = AppWindow;

            if (appWindow != null)
            {
                // Hide from taskbar and task switcher
                appWindow.IsShownInSwitchers = false;

                // Use OverlappedPresenter configured for fullscreen borderless topmost
                var presenter = OverlappedPresenter.Create();
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                appWindow.SetPresenter(presenter);
            }

            // Remove title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);

            // Win32 TopMost style enforcement
            if (hWnd != 0)
            {
                var exStyle = (int)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                exStyle |= WS_EX_TOPMOST;
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)exStyle);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Window setup error: {ex.Message}");
        }
    }

    public void MakeTopMostAndPosition(Windows.Graphics.RectInt32 bounds)
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd != 0)
            {
                SetWindowPos(
                    hWnd,
                    HWND_TOPMOST,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    SWP_SHOWWINDOW);

                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }

            if (AppWindow != null)
            {
                AppWindow.MoveAndResize(bounds);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] MakeTopMostAndPosition error: {ex.Message}");
        }
    }

    private static CoreWebView2Environment? _sharedEnvironment;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Dark background prevents white flash while WebView2 initializes
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 33, 33, 33);

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var webViewDataFolder = Path.Combine(localAppData, "AppLock", "WebView2");
            Directory.CreateDirectory(webViewDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewDataFolder);

            // One-time thread-safe environment creation on UI thread (passing null uses system installed runtime)
            if (_sharedEnvironment == null)
            {
                await _initLock.WaitAsync();
                try
                {
                    if (_sharedEnvironment == null)
                    {
                        try
                        {
                            _sharedEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, webViewDataFolder, null);
                        }
                        catch (Exception createEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[EyeCare] CreateAsync failed, using default: {createEx.Message}");
                        }
                    }
                }
                finally
                {
                    _initLock.Release();
                }
            }

            if (_sharedEnvironment != null)
            {
                await WebView.EnsureCoreWebView2Async(_sharedEnvironment);
            }
            else
            {
                await WebView.EnsureCoreWebView2Async();
            }

            var core = WebView.CoreWebView2;
            if (core == null)
            {
                throw new InvalidOperationException("CoreWebView2 is null after EnsureCoreWebView2Async");
            }

            // Security and UI settings
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;

            // Context menu handler: provide a "Close" menu item for all pages
            core.ContextMenuRequested += (s, args) =>
            {
                args.MenuItems.Clear();
                try
                {
                    var closeItem = core.Environment.CreateContextMenuItem(
                        "Close",
                        null,
                        CoreWebView2ContextMenuItemKind.Command);

                    closeItem.CustomItemSelected += (itemSender, itemArgs) =>
                    {
                        DispatcherQueue.TryEnqueue(() => _closeAllCallback());
                    };

                    args.MenuItems.Add(closeItem);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] ContextMenuRequested error: {ex.Message}");
                }
            };

            // Global Esc key listener injected into any page
            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.addEventListener('keydown', function(e) {
                    if (e.key === 'Escape') {
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage('close');
                        }
                    }
                });
            ");

            // Virtual host mapping for local file access
            var renderFolder = Path.GetDirectoryName(_htmlPath);
            if (!string.IsNullOrEmpty(renderFolder) && Directory.Exists(renderFolder))
            {
                core.SetVirtualHostNameToFolderMapping(
                    "eyecare.local",
                    renderFolder,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            // Handle close message from HTML
            core.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var message = e.TryGetWebMessageAsString();
                    if (message == "close")
                    {
                        System.Diagnostics.Debug.WriteLine("[EyeCare] Close message received from WebView");
                        DispatcherQueue.TryEnqueue(() => _closeAllCallback());
                    }
                }
                catch { }
            };

            // Block navigation away from eyecare.local
            core.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.Contains("eyecare.local") && !e.Uri.StartsWith("file://") && !e.Uri.StartsWith("about:blank"))
                {
                    e.Cancel = true;
                }
            };

            // Block new windows
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
            };

            // If virtual host navigation fails for any reason, seamlessly fallback to in-memory NavigateToString
            core.NavigationCompleted += (s, e) =>
            {
                if (!e.IsSuccess)
                {
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Navigation unsuccessful ({e.WebErrorStatus}), falling back to NavigateToString");
                    try
                    {
                        if (File.Exists(_htmlPath))
                        {
                            var html = File.ReadAllText(_htmlPath);
                            core.NavigateToString(html);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] NavigateToString fallback error: {ex.Message}");
                    }
                }
            };

            // Navigate to rendered HTML
            var fileName = Path.GetFileName(_htmlPath);
            core.Navigate($"https://eyecare.local/{fileName}");

            System.Diagnostics.Debug.WriteLine($"[EyeCare] WebView initialized, navigated to https://eyecare.local/{fileName}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] WebView initialization error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Stack trace: {ex.StackTrace}");

            // Show native fallback UI if WebView2 fails
            ShowFallbackUI();
        }
    }

    private void ShowFallbackUI()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            WebView.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
            FallbackMessageText.Text = _reminderMessage;
            FallbackCountdownText.Text = $"{_remainingSeconds}s";

            _fallbackTicker?.Dispose();
            _fallbackTicker = new Timer(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _remainingSeconds--;
                    if (_remainingSeconds <= 0)
                    {
                        _fallbackTicker?.Dispose();
                        _fallbackTicker = null;
                        _closeAllCallback();
                    }
                    else
                    {
                        FallbackCountdownText.Text = $"{_remainingSeconds}s";
                    }
                });
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        });
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

    private void MenuFlyoutClose_Click(object sender, RoutedEventArgs e)
    {
        _closeAllCallback();
    }

    private void FallbackClose_Click(object sender, RoutedEventArgs e)
    {
        _closeAllCallback();
    }

    public new void Close()
    {
        _autoCloseTimer?.Dispose();
        _autoCloseTimer = null;

        _fallbackTicker?.Dispose();
        _fallbackTicker = null;

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
