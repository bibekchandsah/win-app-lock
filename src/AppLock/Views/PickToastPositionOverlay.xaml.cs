using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.Graphics.Gdi;

namespace AppLock.Views;

public sealed partial class PickToastPositionOverlay : Window
{
    private readonly HMONITOR _targetMonitor;
    private readonly RectInt32 _monitorBounds;
    private const int SnapDistance = 20;
    private const int ToastWidth = 396;
    private const int ToastHeight = 120;

    // Last snapped position, kept in sync by OnPointerMoved so OnPointerPressed reads it
    private double _snappedX;
    private double _snappedY;

    // Used to properly await the picker from the ViewModel without polling
    private readonly TaskCompletionSource<bool> _closedTcs = new();

    public float SelectedPercentX { get; private set; }
    public float SelectedPercentY { get; private set; }
    public bool WasConfirmed { get; private set; }

    /// <summary>Await this to block until the user confirms or cancels.</summary>
    public Task WaitForCloseAsync() => _closedTcs.Task;

    public PickToastPositionOverlay(HMONITOR monitor, RectInt32 bounds)
    {
        this.InitializeComponent();

        _targetMonitor = monitor;
        _monitorBounds = bounds;

        // Signal the TCS when the window actually closes
        this.Closed += (_, _) => _closedTcs.TrySetResult(WasConfirmed);

        SetupWindow();
        SetupEventHandlers();
    }

    private void SetupWindow()
    {
        // Get AppWindow
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Full-screen on target monitor
        appWindow.MoveAndResize(_monitorBounds);

        // Borderless, topmost
        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }

        // Set extended styles - toolwindow and noactivate
        var currentStyle = (int)PInvoke.GetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        currentStyle |= 0x00000080; // WS_EX_TOOLWINDOW
        currentStyle |= 0x08000000; // WS_EX_NOACTIVATE
        PInvoke.SetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)currentStyle);

        // Update guide positions for this monitor
        UpdateGuidePositions();
    }

    private void SetupEventHandlers()
    {
        // Attach events to the root grid, not the window
        if (Content is Microsoft.UI.Xaml.FrameworkElement rootElement)
        {
            rootElement.PointerMoved += OnPointerMoved;
            rootElement.PointerPressed += OnPointerPressed;
            rootElement.KeyDown += OnKeyDown;
        }
    }

    private void UpdateGuidePositions()
    {
        int width = _monitorBounds.Width;
        int height = _monitorBounds.Height;

        // Vertical guides
        LeftGuide.X1 = LeftGuide.X2 = SnapDistance;
        CenterVGuide.X1 = CenterVGuide.X2 = width / 2;
        RightGuide.X1 = RightGuide.X2 = width - SnapDistance;

        LeftGuide.Y2 = CenterVGuide.Y2 = RightGuide.Y2 = height;

        // Horizontal guides
        TopGuide.Y1 = TopGuide.Y2 = SnapDistance;
        CenterHGuide.Y1 = CenterHGuide.Y2 = height / 2;
        BottomGuide.Y1 = BottomGuide.Y2 = height - SnapDistance;

        TopGuide.X2 = CenterHGuide.X2 = BottomGuide.X2 = width;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Content is not Microsoft.UI.Xaml.FrameworkElement rootElement)
            return;

        var point = e.GetCurrentPoint(rootElement);
        double x = point.Position.X;
        double y = point.Position.Y;

        // Check if Shift is held (disable snapping)
        bool shiftHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (!shiftHeld)
        {
            // Snap to edges and center
            (x, y) = ApplySnapping(x, y);
        }
        else
        {
            HideAllGuides();
        }

        // Keep track of the snapped coordinates so OnPointerPressed uses them
        _snappedX = x;
        _snappedY = y;

        // Position ghost toast (top-left corner of the ghost = snapped position)
        GhostToast.Margin = new Thickness(x, y, 0, 0);
    }

    private (double x, double y) ApplySnapping(double x, double y)
    {
        double snappedX = x;
        double snappedY = y;
        bool snappedH = false;
        bool snappedV = false;

        // Snap horizontal
        if (Math.Abs(x - SnapDistance) < SnapDistance)
        {
            snappedX = SnapDistance;
            LeftGuide.Opacity = 1;
            snappedH = true;
        }
        else if (Math.Abs(x - (_monitorBounds.Width / 2.0)) < SnapDistance)
        {
            snappedX = _monitorBounds.Width / 2.0;
            CenterVGuide.Opacity = 1;
            snappedH = true;
        }
        else if (Math.Abs(x - (_monitorBounds.Width - ToastWidth - SnapDistance)) < SnapDistance)
        {
            snappedX = _monitorBounds.Width - ToastWidth - SnapDistance;
            RightGuide.Opacity = 1;
            snappedH = true;
        }

        if (!snappedH)
        {
            LeftGuide.Opacity = 0;
            CenterVGuide.Opacity = 0;
            RightGuide.Opacity = 0;
        }

        // Snap vertical
        if (Math.Abs(y - SnapDistance) < SnapDistance)
        {
            snappedY = SnapDistance;
            TopGuide.Opacity = 1;
            snappedV = true;
        }
        else if (Math.Abs(y - (_monitorBounds.Height / 2.0)) < SnapDistance)
        {
            snappedY = _monitorBounds.Height / 2.0;
            CenterHGuide.Opacity = 1;
            snappedV = true;
        }
        else if (Math.Abs(y - (_monitorBounds.Height - ToastHeight - SnapDistance)) < SnapDistance)
        {
            snappedY = _monitorBounds.Height - ToastHeight - SnapDistance;
            BottomGuide.Opacity = 1;
            snappedV = true;
        }

        if (!snappedV)
        {
            TopGuide.Opacity = 0;
            CenterHGuide.Opacity = 0;
            BottomGuide.Opacity = 0;
        }

        return (snappedX, snappedY);
    }

    private void HideAllGuides()
    {
        LeftGuide.Opacity = 0;
        CenterVGuide.Opacity = 0;
        RightGuide.Opacity = 0;
        TopGuide.Opacity = 0;
        CenterHGuide.Opacity = 0;
        BottomGuide.Opacity = 0;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Use the snapped position (kept by OnPointerMoved), not the raw cursor.
        // The percentage is the top-left corner of the ghost toast relative to
        // the monitor, so the positioner places it exactly where the ghost was.
        SelectedPercentX = (float)(_snappedX / _monitorBounds.Width);
        SelectedPercentY = (float)(_snappedY / _monitorBounds.Height);

        WasConfirmed = true;
        this.Close();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            WasConfirmed = false;
            this.Close();
        }
    }
}
