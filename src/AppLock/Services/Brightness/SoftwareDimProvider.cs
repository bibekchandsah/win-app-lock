using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.Brightness;

/// <summary>
/// Software dimming fallback using a black overlay
/// </summary>
public class SoftwareDimProvider : IBrightnessProvider, IDisposable
{
    private readonly string _displayId;
    private readonly DispatcherQueue _dispatcher;
    private IntPtr _overlayHwnd = IntPtr.Zero;
    private int _currentBrightness = 100;
    private int _minBrightness = 10;

    // User32.dll imports
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int width, int height,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    public string DisplayId => _displayId;
    public string ControlMethod => "Software dimming";

    public SoftwareDimProvider(string displayId, int minBrightness, DispatcherQueue dispatcher)
    {
        _displayId = displayId;
        _minBrightness = minBrightness;
        _dispatcher = dispatcher;
    }

    public bool IsAvailable()
    {
        return true; // Software dimming always works as fallback
    }

    public Task<int> GetBrightnessAsync()
    {
        return Task.FromResult(_currentBrightness);
    }

    public async Task<bool> SetBrightnessAsync(int percent)
    {
        if (percent < _minBrightness) percent = _minBrightness;
        if (percent > 100) percent = 100;

        _currentBrightness = percent;

        // Update overlay opacity on UI thread
        bool success = false;
        _dispatcher.TryEnqueue(() =>
        {
            success = UpdateOverlay(percent);
        });
        
        // Wait a bit for UI thread to process
        await Task.Delay(50);

        return success;
    }

    private bool UpdateOverlay(int percent)
    {
        try
        {
            if (percent >= 100)
            {
                // Remove overlay when at max brightness
                if (_overlayHwnd != IntPtr.Zero)
                {
                    DestroyWindow(_overlayHwnd);
                    _overlayHwnd = IntPtr.Zero;
                }
                return true;
            }

            // Calculate opacity: 0% brightness = 90% black, 99% brightness = 1% black
            // opacity = 1 - (brightness / 100), clamped by minBrightness
            double opacity = 1.0 - (percent / 100.0);
            byte alpha = (byte)(opacity * 255);

            if (_overlayHwnd == IntPtr.Zero)
            {
                // Create overlay window
                // Note: This is a simplified version - full implementation would need
                // to get monitor bounds, handle multi-monitor, etc.
                _overlayHwnd = CreateWindowEx(
                    WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                    "Static",
                    "SoftwareDimOverlay",
                    WS_POPUP | WS_VISIBLE,
                    0, 0,
                    System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920,
                    System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (_overlayHwnd == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to create software dim overlay");
                    return false;
                }

                // Make it topmost
                SetWindowPos(_overlayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                // Fill with black
                IntPtr hdc = GetDC(_overlayHwnd);
                if (hdc != IntPtr.Zero)
                {
                    IntPtr brush = CreateSolidBrush(0x00000000); // Black
                    RECT rect = new RECT
                    {
                        Left = 0,
                        Top = 0,
                        Right = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920,
                        Bottom = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080
                    };
                    FillRect(hdc, ref rect, brush);
                    DeleteObject(brush);
                    ReleaseDC(_overlayHwnd, hdc);
                }
            }

            // Update alpha
            SetLayeredWindowAttributes(_overlayHwnd, 0, alpha, LWA_ALPHA);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Software dim overlay error: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_overlayHwnd != IntPtr.Zero)
        {
            DestroyWindow(_overlayHwnd);
            _overlayHwnd = IntPtr.Zero;
        }
    }
}
