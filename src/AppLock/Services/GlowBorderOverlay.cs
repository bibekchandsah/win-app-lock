using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace AppLock.Services;

/// <summary>
/// A transparent topmost Win32 layered overlay window that renders a vibrant,
/// glowing rainbow gradient border around any target window being picked.
/// Clicks pass straight through (WS_EX_TRANSPARENT) and it never steals focus (WS_EX_NOACTIVATE).
/// </summary>
public sealed class GlowBorderOverlay : IDisposable
{
    private nint _hwnd;
    private RECT _currentRect;
    private bool _isVisible;

    public nint Handle => _hwnd;

    public static bool GetVisibleWindowRect(nint hwnd, out RECT rect)
    {
        rect = default;
        if (hwnd == nint.Zero) return false;

        // Try DwmGetWindowAttribute first to get exact visible frame without drop shadow
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>());
        if (hr == 0 && (rect.right - rect.left) > 0 && (rect.bottom - rect.top) > 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect) && (rect.right - rect.left) > 0 && (rect.bottom - rect.top) > 0;
    }

    public void ShowBorder(RECT rect)
    {
        int targetW = rect.right - rect.left;
        int targetH = rect.bottom - rect.top;

        if (targetW <= 10 || targetH <= 10)
        {
            Hide();
            return;
        }

        EnsureWindowCreated();

        if (_isVisible && _currentRect.Equals(rect))
        {
            return; // Already showing for this exact rect
        }

        _currentRect = rect;
        _isVisible = true;

        const int padding = 8; // Extra padding around border for outer glow feathering
        int overlayX = rect.left - padding;
        int overlayY = rect.top - padding;
        int overlayW = targetW + (padding * 2);
        int overlayH = targetH + (padding * 2);

        using var bitmap = new Bitmap(overlayW, overlayH, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            float innerX = padding;
            float innerY = padding;
            float innerW = targetW;
            float innerH = targetH;

            // Corner radius matching Windows 11 window corners (~8px)
            float r = Math.Min(8.0f, Math.Min(innerW / 4.0f, innerH / 4.0f));
            float d = r * 2.0f;

            // Colors matching reference image:
            // Top: Lime Green -> Yellow -> Orange -> Deep Orange
            // Right: Deep Orange -> Red/Pink -> Magenta -> Purple
            // Bottom: Purple -> Electric Blue -> Cyan
            // Left: Cyan -> Teal -> Lime Green
            var cGreen = Color.FromArgb(0, 230, 118);
            var cYellow = Color.FromArgb(255, 234, 0);
            var cOrange = Color.FromArgb(255, 140, 0);
            var cDeepOrange = Color.FromArgb(255, 61, 0);
            var cPink = Color.FromArgb(255, 23, 68);
            var cMagenta = Color.FromArgb(224, 64, 251);
            var cPurple = Color.FromArgb(124, 77, 255);
            var cBlue = Color.FromArgb(41, 121, 255);
            var cCyan = Color.FromArgb(0, 229, 255);
            var cTeal = Color.FromArgb(0, 200, 150);

            // 4 Connected paths representing the 4 segments and rounded corners
            using var pathTop = new GraphicsPath();
            pathTop.AddLine(innerX + r, innerY, innerX + innerW - r, innerY);
            if (r > 0) pathTop.AddArc(innerX + innerW - d, innerY, d, d, 270, 90);

            using var pathRight = new GraphicsPath();
            pathRight.AddLine(innerX + innerW, innerY + r, innerX + innerW, innerY + innerH - r);
            if (r > 0) pathRight.AddArc(innerX + innerW - d, innerY + innerH - d, d, d, 0, 90);

            using var pathBottom = new GraphicsPath();
            pathBottom.AddLine(innerX + innerW - r, innerY + innerH, innerX + r, innerY + innerH);
            if (r > 0) pathBottom.AddArc(innerX, innerY + innerH - d, d, d, 90, 90);

            using var pathLeft = new GraphicsPath();
            pathLeft.AddLine(innerX, innerY + innerH - r, innerX, innerY + r);
            if (r > 0) pathLeft.AddArc(innerX, innerY, d, d, 180, 90);

            // Pass 1: Outer Soft Glow (width = 6.5px, alpha ~ 80)
            DrawSegment(g, pathTop, new PointF(innerX, innerY), new PointF(innerX + innerW, innerY),
                new[] { cGreen, cYellow, cOrange, cDeepOrange }, new[] { 0.0f, 0.33f, 0.66f, 1.0f }, 80, 6.5f);

            DrawSegment(g, pathRight, new PointF(innerX + innerW, innerY), new PointF(innerX + innerW, innerY + innerH),
                new[] { cDeepOrange, cPink, cMagenta, cPurple }, new[] { 0.0f, 0.33f, 0.66f, 1.0f }, 80, 6.5f);

            DrawSegment(g, pathBottom, new PointF(innerX + innerW, innerY + innerH), new PointF(innerX, innerY + innerH),
                new[] { cPurple, cBlue, cCyan }, new[] { 0.0f, 0.5f, 1.0f }, 80, 6.5f);

            DrawSegment(g, pathLeft, new PointF(innerX, innerY + innerH), new PointF(innerX, innerY),
                new[] { cCyan, cTeal, cGreen }, new[] { 0.0f, 0.5f, 1.0f }, 80, 6.5f);

            // Pass 2: Vivid Crisp Core (width = 3.5px, alpha = 255)
            DrawSegment(g, pathTop, new PointF(innerX, innerY), new PointF(innerX + innerW, innerY),
                new[] { cGreen, cYellow, cOrange, cDeepOrange }, new[] { 0.0f, 0.33f, 0.66f, 1.0f }, 255, 3.5f);

            DrawSegment(g, pathRight, new PointF(innerX + innerW, innerY), new PointF(innerX + innerW, innerY + innerH),
                new[] { cDeepOrange, cPink, cMagenta, cPurple }, new[] { 0.0f, 0.33f, 0.66f, 1.0f }, 255, 3.5f);

            DrawSegment(g, pathBottom, new PointF(innerX + innerW, innerY + innerH), new PointF(innerX, innerY + innerH),
                new[] { cPurple, cBlue, cCyan }, new[] { 0.0f, 0.5f, 1.0f }, 255, 3.5f);

            DrawSegment(g, pathLeft, new PointF(innerX, innerY + innerH), new PointF(innerX, innerY),
                new[] { cCyan, cTeal, cGreen }, new[] { 0.0f, 0.5f, 1.0f }, 255, 3.5f);
        }

        // Send bitmap to Win32 layered window
        ApplyBitmapToLayeredWindow(bitmap, overlayX, overlayY, overlayW, overlayH);
    }

    private static void DrawSegment(Graphics g, GraphicsPath path, PointF pt1, PointF pt2, Color[] colors, float[] positions, int alpha, float width)
    {
        // Avoid identical start and end points for linear gradient
        if (Math.Abs(pt1.X - pt2.X) < 1 && Math.Abs(pt1.Y - pt2.Y) < 1)
        {
            pt2 = new PointF(pt2.X + 1, pt2.Y + 1);
        }

        using var brush = new LinearGradientBrush(pt1, pt2, colors[0], colors[^1]);
        ColorBlend blend = new ColorBlend(colors.Length)
        {
            Colors = colors.Select(c => Color.FromArgb((c.A * alpha) / 255, c.R, c.G, c.B)).ToArray(),
            Positions = positions
        };
        brush.InterpolationColors = blend;

        using var pen = new Pen(brush, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        g.DrawPath(pen, path);
    }

    private void ApplyBitmapToLayeredWindow(Bitmap bitmap, int x, int y, int w, int h)
    {
        nint screenDc = GetDC(nint.Zero);
        nint memDc = CreateCompatibleDC(screenDc);
        nint hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
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
                SourceConstantAlpha = 255,
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

    public void Hide()
    {
        if (_hwnd != nint.Zero && _isVisible)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _isVisible = false;
            _currentRect = default;
        }
    }

    private void EnsureWindowCreated()
    {
        if (_hwnd != nint.Zero) return;

        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "Static",
            "AppLockGlowBorder",
            WS_POPUP,
            -10000, -10000, 1, 1,
            nint.Zero, nint.Zero, nint.Zero, nint.Zero);
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    #region Win32 Interop

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static readonly nint HWND_TOPMOST = new nint(-1);

    private const int SW_HIDE = 0;

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x00000002;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT : IEquatable<RECT>
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public bool Equals(RECT other) =>
            left == other.left && top == other.top && right == other.right && bottom == other.bottom;

        public override bool Equals(object? obj) => obj is RECT other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(left, top, right, bottom);
    }

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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int X, int Y, int cx, int cy,
        uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint hwnd,
        nint hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        nint hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(nint hDC);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint SelectObject(nint hDC, nint hObject);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    #endregion
}
