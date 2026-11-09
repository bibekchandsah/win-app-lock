using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Windows.Graphics;

namespace AppLock.Services.RingLight;

/// <summary>
/// A borderless, click-through, topmost Win32 layered overlay window that renders
/// the ring light frame on a single monitor using GDI+ and UpdateLayeredWindow.
/// The center is 100% transparent (Alpha = 0), allowing the screen content to remain
/// completely visible and click-through without any DirectComposition chroma-key artifacts.
/// </summary>
public sealed class RingOverlayWindow : IRingOverlay
{
    // ── Win32 constants ─────────────────────────────────────────────────────
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_POPUP          = unchecked((int)0x80000000);

    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_SHOWWINDOW   = 0x0040;
    private const int  SW_HIDE          = 0;
    private const int  SW_SHOWNOACTIVATE = 4;

    private const byte AC_SRC_OVER      = 0x00;
    private const byte AC_SRC_ALPHA     = 0x01;
    private const uint ULW_ALPHA        = 0x00000002;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private static readonly nint HWND_TOPMOST = new nint(-1);

    // ── Win32 structs ───────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint hwnd, nint hdcDst, ref POINT pptDst, ref SIZE psize,
        nint hdcSrc, ref POINT pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint hwnd, nint hdcDst, nint pptDst, nint psize,
        nint hdcSrc, nint pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    // ── Fields ───────────────────────────────────────────────────────────────
    private nint _hwnd;
    private RectInt32 _bounds;
    private RingLightSettings? _settings;
    private byte _currentAlpha;
    private bool _showInCapture;
    private bool _disposed;
    private CancellationTokenSource? _fadeCts;

    public nint Handle => _hwnd;

    // ── IRingOverlay Implementation ──────────────────────────────────────────

    public void ApplySettings(RingLightSettings settings, RectInt32 bounds)
    {
        _settings = settings;
        _bounds = bounds;

        if (_hwnd == nint.Zero)
        {
            EnsureWindowCreated(bounds);
        }
        else
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        ApplyCaptureExclusion(_showInCapture);
        // Render at full opacity so layered window bitmap is valid and ready
        RenderFrame(settings, bounds, 255);
    }

    public void ApplyCaptureExclusion(bool showInScreenCapture)
    {
        _showInCapture = showInScreenCapture;
        if (_hwnd != nint.Zero)
        {
            SetWindowDisplayAffinity(_hwnd, showInScreenCapture ? 0u : WDA_EXCLUDEFROMCAPTURE);
        }
    }

    public void Show(RingLightSettings settings)
    {
        _settings = settings;
        if (_bounds.Width <= 0 || _bounds.Height <= 0) return;

        EnsureWindowCreated(_bounds);

        // Render bitmap with full alpha=255 so the frame content is permanently stored in the DC
        RenderFrame(settings, _bounds, 255);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

        if (settings.FadeMs > 0)
        {
            // Start transparent and fade in
            _currentAlpha = 0;
            UpdateWindowAlpha(0);
            _ = FadeAsync(targetAlpha: 255, durationMs: settings.FadeMs);
        }
        else
        {
            _currentAlpha = 255;
            UpdateWindowAlpha(255);
        }
    }

    public async Task HideAsync(int fadeMs)
    {
        await FadeAsync(targetAlpha: 0, durationMs: fadeMs);
        if (_hwnd != nint.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fadeCts?.Cancel();
        _fadeCts?.Dispose();
        _fadeCts = null;

        if (_hwnd != nint.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    // ── Frame Rendering & Layered Window Setup ───────────────────────────────

    private void EnsureWindowCreated(RectInt32 bounds)
    {
        if (_hwnd != nint.Zero) return;

        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "Static",
            "AppLockRingLightOverlay",
            WS_POPUP,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            nint.Zero, nint.Zero, nint.Zero, nint.Zero);

        if (_hwnd != nint.Zero && !_showInCapture)
        {
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
        }
    }

    private void RenderFrame(RingLightSettings s, RectInt32 bounds, byte alpha)
    {
        int w = bounds.Width;
        int h = bounds.Height;
        if (w <= 0 || h <= 0) return;

        EnsureWindowCreated(bounds);

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(System.Drawing.Color.Transparent);

            int shorter = Math.Min(w, h);
            int thickness = Math.Max(2, (int)(shorter * s.ThicknessPercent / 100.0));

            int topT    = s.EdgeTop    ? thickness : 0;
            int bottomT = s.EdgeBottom ? thickness : 0;
            int leftT   = s.EdgeLeft   ? thickness : 0;
            int rightT  = s.EdgeRight  ? thickness : 0;

            float ix = leftT;
            float iy = topT;
            float iw = Math.Max(0, w - leftT - rightT);
            float ih = Math.Max(0, h - topT - bottomT);
            float r  = Math.Clamp((float)s.CornerRadiusPx, 0, Math.Min(iw / 2f, ih / 2f));

            // Inner cutout path that leaves the center 100% transparent and handles corner radius
            using var innerPath = new GraphicsPath();
            if (r > 0 && iw > 0 && ih > 0)
            {
                float d = r * 2f;
                innerPath.AddArc(ix, iy, d, d, 180, 90);
                innerPath.AddArc(ix + iw - d, iy, d, d, 270, 90);
                innerPath.AddArc(ix + iw - d, iy + ih - d, d, d, 0, 90);
                innerPath.AddArc(ix, iy + ih - d, d, d, 90, 90);
                innerPath.CloseFigure();
            }
            else if (iw > 0 && ih > 0)
            {
                innerPath.AddRectangle(new RectangleF(ix, iy, iw, ih));
            }

            // Exclude the inner cutout so nothing drawn here touches the center screen
            if (iw > 0 && ih > 0)
            {
                g.SetClip(innerPath, CombineMode.Exclude);
            }

            int bPct = s.Style == "FullGlow" ? Math.Min(s.BrightnessPercent, 60) : s.BrightnessPercent;
            var winColor = s.ColorMode == "Temperature"
                ? ColorTemperature.ToColor(s.Kelvin, bPct)
                : ParseHexColor(s.CustomColorHex, bPct);
            var gdiColor = System.Drawing.Color.FromArgb(255, winColor.R, winColor.G, winColor.B);

            double softness = s.SoftnessPercent / 100.0;

            if (s.Style == "FullGlow" || softness <= 0.01)
            {
                using var brush = new SolidBrush(gdiColor);
                g.FillRectangle(brush, 0, 0, w, h);
            }
            else
            {
                float hardStop = (float)Math.Clamp(1.0 - softness, 0.0, 0.99);
                var transparentColor = System.Drawing.Color.FromArgb(0, gdiColor.R, gdiColor.G, gdiColor.B);

                // Top: outer y=0 -> inner y=topT (fading inward toward center)
                if (topT > 0)
                {
                    int drawH = topT + (int)r;
                    using var brush = new LinearGradientBrush(
                        new PointF(0, 0),
                        new PointF(0, topT),
                        gdiColor,
                        transparentColor)
                    {
                        WrapMode = WrapMode.Clamp
                    };
                    brush.InterpolationColors = CreateColorBlend(gdiColor, transparentColor, hardStop);
                    g.FillRectangle(brush, 0, 0, w, drawH);
                }

                // Bottom: outer y=h -> inner y=h-bottomT (fading inward toward center)
                if (bottomT > 0)
                {
                    int drawY = h - bottomT - (int)r;
                    int drawH = bottomT + (int)r;
                    using var brush = new LinearGradientBrush(
                        new PointF(0, h),
                        new PointF(0, h - bottomT),
                        gdiColor,
                        transparentColor)
                    {
                        WrapMode = WrapMode.Clamp
                    };
                    brush.InterpolationColors = CreateColorBlend(gdiColor, transparentColor, hardStop);
                    g.FillRectangle(brush, 0, drawY, w, drawH);
                }

                // Left: outer x=0 -> inner x=leftT (fading inward toward center)
                if (leftT > 0)
                {
                    int drawW = leftT + (int)r;
                    using var brush = new LinearGradientBrush(
                        new PointF(0, 0),
                        new PointF(leftT, 0),
                        gdiColor,
                        transparentColor)
                    {
                        WrapMode = WrapMode.Clamp
                    };
                    brush.InterpolationColors = CreateColorBlend(gdiColor, transparentColor, hardStop);
                    g.FillRectangle(brush, 0, 0, drawW, h);
                }

                // Right: outer x=w -> inner x=w-rightT (fading inward toward center)
                if (rightT > 0)
                {
                    int drawX = w - rightT - (int)r;
                    int drawW = rightT + (int)r;
                    using var brush = new LinearGradientBrush(
                        new PointF(w, 0),
                        new PointF(w - rightT, 0),
                        gdiColor,
                        transparentColor)
                    {
                        WrapMode = WrapMode.Clamp
                    };
                    brush.InterpolationColors = CreateColorBlend(gdiColor, transparentColor, hardStop);
                    g.FillRectangle(brush, drawX, 0, drawW, h);
                }
            }
        }

        ApplyBitmapToLayeredWindow(bitmap, bounds.X, bounds.Y, bounds.Width, bounds.Height, alpha);
    }

    private static ColorBlend CreateColorBlend(System.Drawing.Color solid, System.Drawing.Color trans, float hardStop)
    {
        if (hardStop <= 0.01f)
        {
            return new ColorBlend(2)
            {
                Colors = new[] { solid, trans },
                Positions = new[] { 0.0f, 1.0f }
            };
        }

        return new ColorBlend(3)
        {
            Colors = new[] { solid, solid, trans },
            Positions = new[] { 0.0f, hardStop, 1.0f }
        };
    }

    private void ApplyBitmapToLayeredWindow(Bitmap bitmap, int x, int y, int w, int h, byte alpha)
    {
        nint screenDc = GetDC(nint.Zero);
        nint memDc = CreateCompatibleDC(screenDc);
        nint hBitmap = bitmap.GetHbitmap(System.Drawing.Color.FromArgb(0));
        nint oldBitmap = SelectObject(memDc, hBitmap);

        try
        {
            POINT ptDst = new POINT(x, y);
            SIZE size = new SIZE(w, h);
            POINT ptSrc = new POINT(0, 0);

            BLENDFUNCTION blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(
                _hwnd,
                screenDc,
                ref ptDst,
                ref size,
                memDc,
                ref ptSrc,
                0,
                ref blend,
                ULW_ALPHA);

            SetWindowPos(
                _hwnd,
                HWND_TOPMOST,
                x, y, w, h,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(nint.Zero, screenDc);
        }
    }

    private void UpdateWindowAlpha(byte alpha)
    {
        if (_hwnd == nint.Zero) return;
        BLENDFUNCTION blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
            AlphaFormat = AC_SRC_ALPHA
        };
        UpdateLayeredWindow(_hwnd, nint.Zero, nint.Zero, nint.Zero, nint.Zero, nint.Zero, 0, ref blend, ULW_ALPHA);
    }

    private async Task FadeAsync(byte targetAlpha, int durationMs)
    {
        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        var ct = _fadeCts.Token;

        byte startAlpha = _currentAlpha;
        if (startAlpha == targetAlpha) return;

        const int Steps = 20;
        int stepMs = Math.Max(8, durationMs / Steps);
        double stepSize = (targetAlpha - startAlpha) / (double)Steps;

        try
        {
            for (int i = 1; i <= Steps; i++)
            {
                if (ct.IsCancellationRequested || _disposed || _hwnd == nint.Zero) return;
                int a = (int)(startAlpha + stepSize * i);
                _currentAlpha = (byte)Math.Clamp(a, 0, 255);
                UpdateWindowAlpha(_currentAlpha);
                await Task.Delay(stepMs, ct);
            }
            _currentAlpha = targetAlpha;
            UpdateWindowAlpha(_currentAlpha);
        }
        catch (OperationCanceledException) { }
    }

    private static Windows.UI.Color ParseHexColor(string hex, int brightnessPercent)
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
                return Windows.UI.Color.FromArgb(255, (byte)(r * scale), (byte)(g * scale), (byte)(b * scale));
            }
        }
        catch { }
        return Microsoft.UI.Colors.White;
    }
}
