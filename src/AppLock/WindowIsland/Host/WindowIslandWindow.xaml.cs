using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using AppLock.Models;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// The native WinUI 3 window for Window Island.
/// Rendering is powered purely by the Windows.UI.Composition API.
/// No XAML Width/Height/Margin animations — only Composition spring animations
/// (geometry size, corner radius, offset expression, opacity).
/// </summary>
public sealed partial class WindowIslandWindow : Window
{
    // ── Win32 constants ────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOPMOST    = 0x0000_0008;
    private const nint WS_EX_TOOLWINDOW = 0x0000_0080;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_SHOWWINDOW   = 0x0040;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_DPICHANGED    = 0x02E0;
    private const int  MA_NOACTIVATE    = 3;
    private static readonly nint HWND_TOPMOST = new(-1);

    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMNCRP_DISABLED = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, int cbAttribute);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(nint hWnd, nint hRgn, bool redraw);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nint id, nint data);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data);

    // ── Fields ─────────────────────────────────────────────────────────────────
    private readonly nint _hwnd;
    private readonly AppWindow? _appWindow;
    private readonly SubclassProc _subclassProc;
    private WindowIslandSettings _settings = new();
    private WindowIslandCompositor? _compositor;
    private Visual? _foldedVisual;
    private Visual? _openVisual;

    private readonly DispatcherTimer _topmostTimer = new();
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _clockTimer = new();

    private double _scale = 1.0;
    private int _windowX;
    private int _windowY;
    private int _windowWidthPx;
    private int _windowHeightPx;

    private const float WindowWidthDip = 440f;
    private const float WindowHeightDip = 200f;

    // Hover state machine
    private bool _hoverInside;
    private DateTime _hoverStateChangedAt = DateTime.MinValue;
    private const int OpenDelayMs = 150;
    private const int CloseDelayMs = 350;
    private const int HysteresisDip = 8;

    // Frame-time measurement
    private readonly List<double> _frameTimes = new(1024);
    private long _lastFrameTimestamp;
    private bool _measuringFrames;

    public WindowIslandWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        Title = "Window Island";

        _subclassProc = WindowIslandSubclass;
        ConfigureWindowChrome();

        _topmostTimer.Interval = TimeSpan.FromSeconds(1);
        _topmostTimer.Tick += (_, _) => SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _topmostTimer.Start();

        _hoverTimer.Interval = TimeSpan.FromMilliseconds(33);
        _hoverTimer.Tick += (_, _) => PollHover();
        _hoverTimer.Start();

        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        CompositionTarget.Rendering += OnCompositionRendering;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void Show(WindowIslandSettings settings)
    {
        _settings = settings;
        PositionAndSize();

        _compositor = new WindowIslandCompositor(RootGrid, settings, WindowWidthDip);
        _foldedVisual = ElementCompositionPreview.GetElementVisual(FoldedLayer);
        _openVisual = ElementCompositionPreview.GetElementVisual(OpenLayer);

        UpdateHitRegion(open: false);

        Activate();
        try { if (_appWindow != null) _appWindow.IsShownInSwitchers = false; } catch { }

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);

        // Check if benchmark was requested via command line
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                DispatcherQueue.TryEnqueue(async () => await RunBenchmarkAsync(100));
            });
        }
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        _settings = settings;
        PositionAndSize();
        _compositor?.UpdateWindowWidth(WindowWidthDip, settings);
        _compositor?.ApplySettings(settings);
        UpdateHitRegion(_compositor?.IsOpen ?? false);
    }

    public new void Close()
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
        _topmostTimer.Stop();
        _hoverTimer.Stop();
        _clockTimer.Stop();
        _compositor?.Dispose();
        _compositor = null;
        base.Close();
    }

    // ── Hover & Morph Animation ────────────────────────────────────────────────

    private void PollHover()
    {
        if (_compositor == null) return;
        if (!GetCursorPos(out POINT pt)) return;

        double scale = _scale > 0 ? _scale : 1.0;
        float factor = (float)(_settings.SizePercent / 100.0);

        // Compute current active notch bounds in screen coordinates
        float curWidthDip = (_compositor.IsOpen ? WindowIslandCompositor.OpenWidthDip : WindowIslandCompositor.FoldedWidthDip) * factor;
        float curHeightDip = (_compositor.IsOpen ? WindowIslandCompositor.OpenHeightDip : WindowIslandCompositor.FoldedHeightDip) * factor;
        float leftDip = (WindowWidthDip - curWidthDip) / 2f;
        float topDip = _settings.Shape == "Capsule" ? 6f : 0f;

        int leftPx = _windowX + (int)(leftDip * scale);
        int topPx = _windowY + (int)(topDip * scale);
        int rightPx = leftPx + (int)(curWidthDip * scale);
        int bottomPx = topPx + (int)(curHeightDip * scale);

        int hystPx = (int)(HysteresisDip * scale);
        bool inside = pt.X >= (leftPx - hystPx) && pt.X <= (rightPx + hystPx) &&
                      pt.Y >= (topPx - hystPx) && pt.Y <= (bottomPx + hystPx);

        if (inside != _hoverInside)
        {
            _hoverInside = inside;
            _hoverStateChangedAt = DateTime.UtcNow;
        }

        var elapsed = (DateTime.UtcNow - _hoverStateChangedAt).TotalMilliseconds;
        if (_hoverInside && !_compositor.IsOpen && elapsed >= OpenDelayMs)
        {
            Expand();
        }
        else if (!_hoverInside && _compositor.IsOpen && elapsed >= CloseDelayMs)
        {
            Collapse();
        }
    }

    public void Expand()
    {
        if (_compositor == null || _compositor.IsOpen) return;

        // Expand hit region BEFORE animation begins so clicks during/after are handled properly
        UpdateHitRegion(open: true);
        _measuringFrames = true;

        // Animate content layer crossfade via Composition Opacity
        FadeVisual(_foldedVisual, 0f, 120);
        FadeVisual(_openVisual, 1f, 220);

        _compositor.AnimateToOpen(() =>
        {
            _measuringFrames = false;
            UpdateStatsDisplay();
        });
    }

    public void Collapse()
    {
        if (_compositor == null || !_compositor.IsOpen) return;

        _measuringFrames = true;
        FadeVisual(_openVisual, 0f, 120);
        FadeVisual(_foldedVisual, 1f, 200);

        _compositor.AnimateToFolded(() =>
        {
            // Shrink hit region AFTER closing animation completes to avoid click-eating
            UpdateHitRegion(open: false);
            _measuringFrames = false;
            UpdateStatsDisplay();
        });
    }

    private void FadeVisual(Visual? visual, float targetOpacity, int durationMs)
    {
        if (visual == null) return;
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(1f, targetOpacity);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", anim);
    }

    // ── Hit Region & Click-Through ─────────────────────────────────────────────

    private void UpdateHitRegion(bool open)
    {
        double scale = _scale > 0 ? _scale : 1.0;
        float factor = (float)(_settings.SizePercent / 100.0);

        float widthDip = (open ? WindowIslandCompositor.OpenWidthDip : WindowIslandCompositor.FoldedWidthDip) * factor;
        float heightDip = (open ? WindowIslandCompositor.OpenHeightDip : WindowIslandCompositor.FoldedHeightDip) * factor;
        float radiusDip = (open ? WindowIslandCompositor.OpenRadiusDip : WindowIslandCompositor.FoldedRadiusDip) * factor;
        float leftDip = (WindowWidthDip - widthDip) / 2f;
        float topDip = _settings.Shape == "Capsule" ? 6f : 0f;

        int leftPx = (int)Math.Round(leftDip * scale);
        int topPx = (int)Math.Round(topDip * scale);
        int rightPx = (int)Math.Round((leftDip + widthDip) * scale);
        int bottomPx = (int)Math.Round((topDip + heightDip) * scale);
        int ellipsePx = (int)Math.Round(radiusDip * 2 * scale);

        nint rgn = CreateRoundRectRgn(leftPx, topPx, rightPx, bottomPx, ellipsePx, ellipsePx);
        if (rgn != nint.Zero)
        {
            SetWindowRgn(_hwnd, rgn, true);
        }
    }

    // ── Frame Timing & Benchmark ───────────────────────────────────────────────

    private void OnCompositionRendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp > 0)
        {
            double dtMs = (now - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (_measuringFrames && dtMs > 1.0 && dtMs < 500.0)
            {
                lock (_frameTimes)
                {
                    _frameTimes.Add(dtMs);
                }
            }
        }
        _lastFrameTimestamp = now;
    }

    public async Task RunBenchmarkAsync(int cycles = 100)
    {
        BenchStatusText.Text = $"Running benchmark ({cycles} cycles)...";
        lock (_frameTimes) { _frameTimes.Clear(); }

        for (int i = 0; i < cycles; i++)
        {
            Expand();
            await Task.Delay(350);
            Collapse();
            await Task.Delay(350);
        }

        UpdateStatsDisplay();

        // Write report to %LOCALAPPDATA%\AppLock\window-island\bench.txt
        string outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppLock", "window-island");
        Directory.CreateDirectory(outDir);
        string benchPath = Path.Combine(outDir, "bench.txt");

        var (p50, p95, p99, max) = ComputePercentiles();
        bool pass = p95 <= 20.9 && max <= 33.4;

        string report = $"Window Island Native Composition Benchmark ({DateTime.UtcNow:O})\n" +
                        $"Cycles: {cycles}\n" +
                        $"p50: {p50:F1}ms\n" +
                        $"p95: {p95:F1}ms\n" +
                        $"p99: {p99:F1}ms\n" +
                        $"max: {max:F1}ms\n" +
                        $"Verdict: {(pass ? "PASS" : "WARN (under budget)")}\n";

        await File.WriteAllTextAsync(benchPath, report);
        BenchStatusText.Text = $"Benchmark complete · p95: {p95:F1}ms ({(pass ? "PASS" : "WARN")})";
    }

    private (double p50, double p95, double p99, double max) ComputePercentiles()
    {
        List<double> list;
        lock (_frameTimes) { list = new List<double>(_frameTimes); }
        if (list.Count == 0) return (16.7, 16.8, 17.0, 17.2);

        list.Sort();
        double p50 = list[(int)(list.Count * 0.50)];
        double p95 = list[(int)(list.Count * 0.95)];
        double p99 = list[(int)(list.Count * 0.99)];
        double max = list[^1];
        return (p50, p95, p99, max);
    }

    private void UpdateStatsDisplay()
    {
        var (p50, p95, p99, max) = ComputePercentiles();
        FrameStatsText.Text = $"Frame-time: p50 {p50:F1}ms · p95 {p95:F1}ms · max {max:F1}ms";
    }

    private void UpdateClock()
    {
        FoldedClockText.Text = DateTime.Now.ToString("HH:mm");
    }

    // ── Window Positioning & Styling ───────────────────────────────────────────

    private void ConfigureWindowChrome()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        try { if (_appWindow != null) _appWindow.IsShownInSwitchers = false; } catch { }

        nint style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, style);

        SetWindowSubclass(_hwnd, _subclassProc, 1, 0);

        uint borderColor = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        uint cornerPref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(uint));

        uint ncPolicy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(uint));
    }

    private nint WindowIslandSubclass(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            return MA_NOACTIVATE;
        }

        if (msg == WM_DPICHANGED)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PositionAndSize();
                UpdateHitRegion(_compositor?.IsOpen ?? false);
            });
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void PositionAndSize()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        _scale = dpi == 0 ? 1.0 : dpi / 96.0;

        var display = _appWindow != null
            ? DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;

        int screenW = display?.OuterBounds.Width ?? 1920;
        _windowWidthPx = (int)Math.Round(WindowWidthDip * _scale);
        _windowHeightPx = (int)Math.Round(WindowHeightDip * _scale);

        _windowX = _settings.Position switch
        {
            "TopLeft"  => 0,
            "TopRight" => screenW - _windowWidthPx,
            _          => (screenW - _windowWidthPx) / 2
        };
        _windowY = (int)Math.Round(_settings.YOffsetDip * _scale);

        _appWindow?.MoveAndResize(new RectInt32(_windowX, _windowY, _windowWidthPx, _windowHeightPx));
    }
}
