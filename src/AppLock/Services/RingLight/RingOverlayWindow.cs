using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.UI;          // Colors.*
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AppLock.Services.RingLight;

/// <summary>
/// One borderless, click-through, topmost overlay window that draws the ring light
/// frame on a single display. The center of the window is "transparent" via LWA_COLORKEY
/// so the user can see and click through it; only the edge strips are opaque.
/// </summary>
public sealed class RingOverlayWindow : IRingOverlay
{
    // ── Win32 constants ─────────────────────────────────────────────────────
    private const int  GWL_EXSTYLE            = -20;
    private const int  WS_EX_LAYERED          = 0x00080000;
    private const int  WS_EX_TRANSPARENT      = 0x00000020;
    private const int  WS_EX_NOACTIVATE       = 0x08000000;
    private const int  WS_EX_TOOLWINDOW       = 0x00000080;
    private const uint SWP_NOACTIVATE         = 0x0010;
    private const uint SWP_SHOWWINDOW         = 0x0040;
    private const uint LWA_COLORKEY           = 0x00000001;
    private const uint LWA_ALPHA              = 0x00000002;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private static readonly nint HWND_TOPMOST = new nint(-1);

    // The background color used as a chroma key (nearly-black, invisible to users)
    // COLORREF format = 0x00BBGGRR  → R=3,G=3,B=3
    private const uint KEY_COLOR_REF = 0x00030303u;
    private static readonly Color KEY_COLOR = Color.FromArgb(255, 3, 3, 3);

    // ── P/Invoke ─────────────────────────────────────────────────────────────
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    // ── Fields ───────────────────────────────────────────────────────────────
    private readonly Window   _window;
    private readonly nint     _hwnd;
    private readonly Rectangle _topStrip;
    private readonly Rectangle _bottomStrip;
    private readonly Rectangle _leftStrip;
    private readonly Rectangle _rightStrip;

    private CancellationTokenSource? _fadeCts;
    private bool _disposed;

    // ── Constructor ──────────────────────────────────────────────────────────
    public RingOverlayWindow()
    {
        _window = new Window();

        // Build XAML content: 4 edge strips + chroma-key background
        var grid = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new SolidColorBrush(KEY_COLOR)
        };

        _topStrip    = MakeStrip();
        _bottomStrip = MakeStrip();
        _leftStrip   = MakeStrip();
        _rightStrip  = MakeStrip();

        _topStrip.VerticalAlignment    = VerticalAlignment.Top;
        _bottomStrip.VerticalAlignment = VerticalAlignment.Bottom;
        _leftStrip.HorizontalAlignment   = HorizontalAlignment.Left;
        _rightStrip.HorizontalAlignment  = HorizontalAlignment.Right;

        grid.Children.Add(_topStrip);
        grid.Children.Add(_bottomStrip);
        grid.Children.Add(_leftStrip);
        grid.Children.Add(_rightStrip);

        _window.Content = grid;

        // ── AppWindow setup ──────────────────────────────────────────────────
        var appWindow = _window.AppWindow;
        appWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop  = true;
        presenter.IsResizable    = false;
        presenter.IsMinimizable  = false;
        presenter.IsMaximizable  = false;
        presenter.SetBorderAndTitleBar(false, false);
        appWindow.SetPresenter(presenter);

        _window.ExtendsContentIntoTitleBar = true;
        _window.SetTitleBar(null);

        // ── Win32 extended styles ────────────────────────────────────────────
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        var exStyle = (int)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (nint)exStyle);

        // Start fully transparent (alpha = 0)
        SetLayeredWindowAttributes(_hwnd, KEY_COLOR_REF, 0, LWA_COLORKEY | LWA_ALPHA);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Apply settings and position/size the overlay on the given display bounds.</summary>
    public void ApplySettings(RingLightSettings s, Windows.Graphics.RectInt32 bounds)
    {
        // Calculate strip thickness
        int shorter   = Math.Min(bounds.Width, bounds.Height);
        int thickness = Math.Max(2, (int)(shorter * s.ThicknessPercent / 100.0));

        // Ring color (cap brightness at 60% for FullGlow to keep screen usable)
        int bPct = s.Style == "FullGlow" ? Math.Min(s.BrightnessPercent, 60) : s.BrightnessPercent;
        Color ringColor = s.ColorMode == "Temperature"
            ? ColorTemperature.ToColor(s.Kelvin, bPct)
            : ParseHexColor(s.CustomColorHex, bPct);

        double softness = s.SoftnessPercent / 100.0;

        // Apply strips
        ApplyStrip(_topStrip,    ringColor, softness, thickness, true,  s.EdgeTop,    s.Style);
        ApplyStrip(_bottomStrip, ringColor, softness, thickness, true,  s.EdgeBottom, s.Style);
        ApplyStrip(_leftStrip,   ringColor, softness, thickness, false, s.EdgeLeft,   s.Style);
        ApplyStrip(_rightStrip,  ringColor, softness, thickness, false, s.EdgeRight,  s.Style);

        // Position and size (before Activate so it appears in final position)
        _window.AppWindow.MoveAndResize(bounds);
        SetWindowPos(_hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                     SWP_SHOWWINDOW | SWP_NOACTIVATE);
    }

    /// <summary>Apply screen-capture exclusion based on the ShowInScreenCapture setting.</summary>
    public void ApplyCaptureExclusion(bool showInCapture)
    {
        SetWindowDisplayAffinity(_hwnd, showInCapture ? 0u : WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Show the window and fade in over FadeMs milliseconds.</summary>
    public void Show(RingLightSettings s)
    {
        _window.Activate();
        _ = FadeAsync(targetAlpha: 255, durationMs: s.FadeMs);
    }

    /// <summary>Fade out and then hide.</summary>
    public async Task HideAsync(int fadeMs)
    {
        await FadeAsync(targetAlpha: 0, durationMs: fadeMs);
        _window.AppWindow.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fadeCts?.Cancel();
        try { _window.Close(); } catch { }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static Rectangle MakeStrip() => new Rectangle
    {
        Fill = new SolidColorBrush(Colors.White)
    };

    private static void ApplyStrip(Rectangle strip, Color color, double softness,
                                    int thickness, bool isHorizontal, bool edgeOn, string style)
    {
        strip.Opacity = edgeOn ? 1.0 : 0.0;

        if (isHorizontal) { strip.Height = thickness; strip.Width  = double.NaN; }
        else              { strip.Width  = thickness; strip.Height = double.NaN; }

        // Build fill brush
        if (style == "FullGlow" || softness <= 0.01)
        {
            strip.Fill = new SolidColorBrush(color);
        }
        else
        {
            // Gradient: opaque at outer edge → transparent at inner edge
            var transparent = Color.FromArgb(0, color.R, color.G, color.B);
            double hardStop = 1.0 - softness;

            var brush = new LinearGradientBrush();
            // Horizontal strip: top=outer edge → bottom=inner edge (vertical gradient)
            // Vertical strip: left=outer edge → right=inner edge (horizontal gradient)
            brush.StartPoint = isHorizontal
                ? new Windows.Foundation.Point(0, 0)
                : new Windows.Foundation.Point(0, 0);
            brush.EndPoint = isHorizontal
                ? new Windows.Foundation.Point(0, 1)
                : new Windows.Foundation.Point(1, 0);

            brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
            if (hardStop > 0.01)
                brush.GradientStops.Add(new GradientStop { Color = color, Offset = hardStop });
            brush.GradientStops.Add(new GradientStop { Color = transparent, Offset = 1.0 });

            strip.Fill = brush;
        }
    }

    private async Task FadeAsync(int targetAlpha, int durationMs)
    {
        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        var ct = _fadeCts.Token;

        const int Steps = 20;
        int stepMs = Math.Max(8, durationMs / Steps);

        int startAlpha = targetAlpha == 255 ? 0 : 255;
        double stepSize = (targetAlpha - startAlpha) / (double)Steps;

        try
        {
            for (int i = 1; i <= Steps; i++)
            {
                if (ct.IsCancellationRequested) return;
                int alpha = (int)(startAlpha + stepSize * i);
                alpha = Math.Clamp(alpha, 0, 255);
                SetLayeredWindowAttributes(_hwnd, KEY_COLOR_REF, (byte)alpha, LWA_COLORKEY | LWA_ALPHA);
                await Task.Delay(stepMs, ct);
            }
            SetLayeredWindowAttributes(_hwnd, KEY_COLOR_REF, (byte)targetAlpha, LWA_COLORKEY | LWA_ALPHA);
        }
        catch (OperationCanceledException) { }
    }

    private static Color ParseHexColor(string hex, int brightnessPercent)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                float scale = brightnessPercent / 100f;
                return Color.FromArgb(255, (byte)(r * scale), (byte)(g * scale), (byte)(b * scale));
            }
        }
        catch { }
        return Colors.White;
    }
}
