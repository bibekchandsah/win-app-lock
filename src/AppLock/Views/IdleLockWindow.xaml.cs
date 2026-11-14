using System;
using System.Threading.Tasks;
using AppLock.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AppLock.Views;

/// <summary>
/// The idle auto-lock screen. It is a window of its own instead of a ContentDialog on the main
/// window so that it is always visible: the main window is hidden while locked, and a ContentDialog
/// cannot be shown when another dialog is already open in that XamlRoot.
/// </summary>
public sealed partial class IdleLockWindow : Window
{
    private readonly AuthService _authService;
    private AppWindow? _appWindow;
    private nint _hwnd;
    private bool _promptOpen;

    /// <summary>True when the user unlocked this window (password or Windows Hello).</summary>
    public bool Unlocked { get; private set; }

    public IdleLockWindow(AuthService authService)
    {
        _authService = authService;

        InitializeComponent();
        SetupWindow();

        RootGrid.Loaded += (s, e) => _ = PromptAsync();
    }

    private void SetupWindow()
    {
        try
        {
            const int width = 420;
            const int height = 230;

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow != null)
            {
                _appWindow.IsShownInSwitchers = false;
                _appWindow.Resize(new SizeInt32(width, height));

                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsMaximizable = false;
                }

                var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
                _appWindow.Move(new PointInt32(
                    workArea.X + Math.Max(0, (workArea.Width - width) / 2),
                    workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
            }

            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = ServiceContainer.CurrentTheme;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IdleLock] Window setup error: {ex.Message}");
        }
    }

    private async Task PromptAsync()
    {
        if (_promptOpen || Unlocked) return;
        _promptOpen = true;

        try
        {
            while (!Unlocked)
            {
                if (RootGrid.XamlRoot == null)
                {
                    await Task.Delay(150);
                    continue;
                }

                var dialog = new AuthDialog(_authService)
                {
                    XamlRoot = RootGrid.XamlRoot,
                    HostWindowHandle = new Windows.Win32.Foundation.HWND(_hwnd),
                    Title = "AppLock Idle Auto-Lock",
                    IntroText = "Enter your AppLock password to unlock."
                };

                await dialog.ShowAsync();

                if (dialog.IsAuthenticated)
                {
                    Unlocked = true;
                    break;
                }

                // Cancelled: keep this window up so the user can try again from the Unlock button.
                StatusText.Text = "AppLock is locked. Click Unlock to try again.";
                break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IdleLock] Prompt error: {ex.Message}");
            StatusText.Text = "Could not open the unlock prompt. Click Unlock to retry.";
        }
        finally
        {
            _promptOpen = false;
        }

        if (Unlocked)
        {
            Close();
        }
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => _ = PromptAsync();
}
