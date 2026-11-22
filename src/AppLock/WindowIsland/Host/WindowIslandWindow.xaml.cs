using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using AppLock.Models;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// The native WinUI 3 window for Window Island.
/// Shape and animations are handled entirely by the Windows.UI.Composition API.
/// No XAML Width/Height/Margin animations — only Composition spring animations
/// (scale, offset, opacity, clip).
/// </summary>
public sealed partial class WindowIslandWindow : Window
{
    // Win32 constants
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOPMOST    = 0x0000_0008;
    private const nint WS_EX_TOOLWINDOW = 0x0000_0080;
    private const nint WS_EX_NOACTIVATE = 0x0800_0000;
    private const int  SWP_NOMOVE       = 0x0002;
    private const int  SWP_NOSIZE       = 0x0001;
    private const int  SWP_NOACTIVATE   = 0x0010;
    private static readonly nint HWND_TOPMOST = new(-1);

    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMNCRP_DISABLED = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, int cbAttribute);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private nint _hwnd;
    private AppWindow? _appWindow;
    private WindowIslandSettings _settings = new();
    private WindowIslandCompositor? _compositor;

    public WindowIslandWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        // Hide from taskbar and Alt-Tab; prevent focus stealing
        ApplyWindowStyle();
        // Strip DWM decorations for per-pixel transparency
        StripDwmDecorations();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void Show(WindowIslandSettings settings)
    {
        _settings = settings;
        PositionAndSize(settings);
        _compositor = new WindowIslandCompositor(RootGrid, settings);

        if (_appWindow != null)
        {
            _appWindow.IsShownInSwitchers = false;
            _appWindow.Show();
        }
        else
        {
            Activate();
        }

        // Keep topmost — another topmost window might have jumped above us
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        _settings = settings;
        PositionAndSize(settings);
        _compositor?.ApplySettings(settings);
    }

    public new void Close()
    {
        _compositor?.Dispose();
        _compositor = null;
        base.Close();
    }

    // ── Win32 helpers ──────────────────────────────────────────────────────────

    private void ApplyWindowStyle()
    {
        nint style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, style);
    }

    private void StripDwmDecorations()
    {
        // Remove 1px DWM border
        uint borderColor = 0xFFFFFFFE; // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        // No OS corner rounding — Composition handles the shape
        uint cornerPref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(uint));

        // Disable non-client rendering entirely
        uint ncPolicy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(uint));
    }

    private void PositionAndSize(WindowIslandSettings settings)
    {
        if (_appWindow == null) return;

        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            _appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

        double scale = display?.OuterBounds.Width > 0
            ? (double)display.OuterBounds.Width / display.WorkArea.Width
            : 1.0;

        // Design dimensions in DIPs (96 dpi reference), scaled by settings
        double factor = settings.SizePercent / 100.0;
        int widthPx  = (int)(176 * factor * scale);
        int heightPx = (int)(32  * factor * scale);

        int screenW = display?.OuterBounds.Width ?? 1920;
        int x = settings.Position switch
        {
            "TopLeft"  => 0,
            "TopRight" => screenW - widthPx,
            _          => (screenW - widthPx) / 2  // TopCenter
        };
        int y = settings.YOffsetDip;

        _appWindow.MoveAndResize(new RectInt32(x, y, widthPx, heightPx));
    }
}
