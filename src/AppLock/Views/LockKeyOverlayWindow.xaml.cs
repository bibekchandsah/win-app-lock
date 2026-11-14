using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace AppLock.Views
{
    public sealed partial class LockKeyOverlayWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private readonly AppWindow _appWindow;
        private readonly DispatcherTimer _hideTimer;

        public LockKeyOverlayWindow()
        {
            this.InitializeComponent();

            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // Configure window
            _appWindow.IsShownInSwitchers = false;
            
            var presenter = _appWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }

            // Set transparent background using SystemBackdrop
            this.SystemBackdrop = new TransparentBackdrop();

            // Position window OFF-SCREEN initially to pre-warm without visible flash
            PositionWindowOffscreen();

            // Auto-hide timer
            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            _hideTimer.Tick += HideTimer_Tick;
            
            // Make the root grid background truly transparent
            RootGrid.Background = new SolidColorBrush(Colors.Transparent);
            
            // IMPORTANT: Activate FIRST to let compositor render initial frame
            this.Activate();
            
            // THEN apply transparent/click-through styles AFTER activation
            SetWindowExTransparent(hwnd);
            
            // After a brief moment, hide and reposition to correct location
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _appWindow.Hide();
                PositionWindow(); // Move to correct position for actual use
            });
        }

        private void PositionWindow()
        {
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);

            int windowWidth = 280;
            int windowHeight = 100;
            int bottomMargin = 120;

            int x = (screenWidth - windowWidth) / 2;
            int y = screenHeight - windowHeight - bottomMargin;

            _appWindow.MoveAndResize(new RectInt32(x, y, windowWidth, windowHeight));
        }

        private void PositionWindowOffscreen()
        {
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);

            int windowWidth = 280;
            int windowHeight = 100;

            // Position off-screen (far right, out of view)
            int x = screenWidth + 1000;
            int y = screenHeight / 2;

            _appWindow.MoveAndResize(new RectInt32(x, y, windowWidth, windowHeight));
        }

        private void SetWindowExTransparent(IntPtr hwnd)
        {
            const int GWL_EXSTYLE = -20;
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_TOOLWINDOW = 0x00000080;

            nint exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

        public void ShowIndicator(string keyName, bool isOn)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[LockKeyOverlay] ShowIndicator called: {keyName} = {(isOn ? "ON" : "OFF")}");
                
                // Show the window
                _appWindow.Show();
                
                KeyNameText.Text = keyName;
                KeyStateText.Text = isOn ? "ON" : "OFF";
                KeyStateText.Foreground = isOn
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.LimeGreen)
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.OrangeRed);

                // Set icon based on key type
                if (keyName.Contains("Caps"))
                {
                    KeyIcon.Glyph = "\uE8AB"; // Font icon
                }
                else if (keyName.Contains("Num"))
                {
                    KeyIcon.Glyph = "\uE1D2"; // Calculator icon
                }
                else
                {
                    KeyIcon.Glyph = "\uE92E"; // Keyboard icon
                }

                System.Diagnostics.Debug.WriteLine($"[LockKeyOverlay] Starting fade-in animation");
                
                // Animate in
                var fadeIn = new DoubleAnimation
                {
                    From = 0,
                    To = 0.95,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                var storyboard = new Storyboard();
                storyboard.Children.Add(fadeIn);
                Storyboard.SetTarget(fadeIn, IndicatorBorder);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");
                storyboard.Begin();

                // Restart hide timer
                _hideTimer.Stop();
                _hideTimer.Start();
                
                System.Diagnostics.Debug.WriteLine($"[LockKeyOverlay] Animation started, hide timer set");
            });
        }

        private void HideTimer_Tick(object? sender, object e)
        {
            _hideTimer.Stop();

            // Animate out
            var fadeOut = new DoubleAnimation
            {
                From = 0.95,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(fadeOut);
            Storyboard.SetTarget(fadeOut, IndicatorBorder);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            storyboard.Completed += (s, args) =>
            {
                // Hide the window after animation completes
                _appWindow.Hide();
            };
            storyboard.Begin();
        }
    }

    // Custom transparent backdrop for overlay window
    public class TransparentBackdrop : SystemBackdrop
    {
        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, Microsoft.UI.Xaml.XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }
}
