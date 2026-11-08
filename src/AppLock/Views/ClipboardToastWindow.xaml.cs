using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;
using AppLock.Services;

namespace AppLock.Views;

public sealed partial class ClipboardToastWindow : Window
{
    private static ClipboardToastWindow? _instance;
    private AppWindow? _appWindow;
    private DispatcherTimer? _hideTimer;
    private nint _hwnd;

    public static ClipboardToastWindow Instance => _instance ??= new ClipboardToastWindow();

    public ClipboardToastWindow()
    {
        _instance = this;
        this.InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow != null)
        {
            _appWindow.IsShownInSwitchers = false;
            _appWindow.Resize(new SizeInt32(360, 52));

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
        }

        ExtendsContentIntoTitleBar = true;

        _hideTimer = new DispatcherTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _hideTimer.Tick += (s, e) =>
        {
            _hideTimer.Stop();
            _appWindow?.Hide();
        };
    }

    public static void ShowToast(string message)
    {
        Instance.ShowInternal(message);
    }

    private void ShowInternal(string message)
    {
        ToastMessageText.Text = message;

        // Apply theme
        if (Content is FrameworkElement root)
        {
            string theme = ServiceContainer.Settings.Clipboard.ThemeMode;
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        PositionBottomRight();

        _appWindow?.Show();
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void PositionBottomRight()
    {
        const int width = 360;
        const int height = 52;

        GetCursorPos(out POINT pt);
        nint hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        MONITORINFO mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);

        int x = mi.rcWork.right - width - 24;
        int y = mi.rcWork.bottom - height - 24;

        _appWindow?.Move(new PointInt32(x, y));
    }

    #region Win32 Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    #endregion
}
