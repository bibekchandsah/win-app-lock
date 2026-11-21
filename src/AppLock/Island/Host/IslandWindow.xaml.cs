using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace AppLock.Island.Host;

/// <summary>
/// Milestone 1 spike host: a borderless, topmost, non-activating window with a transparent
/// WebView2 surface that loads the local island bundle through a virtual host.
/// Mirrors the layout constants of <c>Assets/island-ui/src/motion.ts</c>.
/// </summary>
public sealed partial class IslandWindow : Window
{
    // ── Layout (CSS px, mirrored in motion.ts) ───────────────────────────────
    private const int FoldedWidth = 196;
    private const int FoldedHeight = 36;
    private const int FoldedRadius = 18;
    private const int OpenWidth = 420;
    private const int OpenHeight = 180;
    private const int SideMargin = 8;
    private const int BottomMargin = 8;
    private const int PageWidth = OpenWidth + (SideMargin * 2);
    private const int PageHeight = OpenHeight + BottomMargin;

    // ── Win32 ────────────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    /// <summary>Messages the UI is allowed to send. Anything else is dropped.</summary>
    private static readonly HashSet<string> KnownMessageTypes = new(StringComparer.Ordinal)
    {
        "ui.ready",
        "layout.target",
        "hover.state",
        "perf.report",
        "perf.done",
        "input.focusRequested",
        "input.focusReleased",
        "state.hover",
    };

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _subclassProc;
    private readonly DispatcherTimer _topmostTimer = new();
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly string _benchFile;
    private double _scale = 1;

    /// <summary>True while the island is drawn open; drives the hover hit box.</summary>
    private bool _hoverTargetOpen;
    private bool _hoverInside;

    public IslandWindow()
    {
        InitializeComponent();
        IslandSpikeLog.Write("ctor: begin");

        _hwnd = WindowNative.GetWindowHandle(this);
        _subclassProc = IslandSubclassProc;
        Title = "Island";

        _benchFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppLock", "island", "bench.txt");

        ConfigureWindowChrome();
        IslandSpikeLog.Write("ctor: chrome configured");

        ResizeForPage();
        // Start folded: the notch is centred in the page, so the click-through region matches it.
        UpdateHitRegion(
            side: (PageWidth - FoldedWidth) / 2,
            width: FoldedWidth,
            height: FoldedHeight,
            radius: FoldedRadius);
        IslandSpikeLog.Write("ctor: sized");

        // Re-assert the topmost slot: another topmost window (a browser PWA, another overlay) can
        // otherwise end up above the island. This never touches the visual tree.
        _topmostTimer.Interval = TimeSpan.FromSeconds(1);
        _topmostTimer.Tick += (_, _) => SetWindowPos(
            _hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _topmostTimer.Start();

        Activate();
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);

        IslandSpikeLog.Write(
            $"window shown: visible={AppWindow.IsVisible} rect={AppWindow.Position.X},{AppWindow.Position.Y} " +
            $"{AppWindow.Size.Width}x{AppWindow.Size.Height} scale={_scale:0.##}");

        _ = InitializeWebViewAsync();
    }

    private void ConfigureWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.IsShownInSwitchers = false;

        // Tool window keeps it out of the taskbar and Alt-Tab. Focus is kept by answering
        // WM_MOUSEACTIVATE instead of using WS_EX_NOACTIVATE, which blocks WinUI/WebView2 input.
        long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        SetWindowSubclass(_hwnd, _subclassProc, 1, IntPtr.Zero);
    }

    private IntPtr IslandSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ResizeForPage()
    {
        _scale = DpiScale();

        int width = (int)Math.Round(PageWidth * _scale);
        int height = (int)Math.Round(PageHeight * _scale);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var outer = area.OuterBounds;

        int x = outer.X + ((outer.Width - width) / 2);
        int y = outer.Y;

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private double DpiScale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1d : dpi / 96d;
    }

    /// <summary>
    /// Clicks outside the visible notch fall through to the desktop. Called at animation
    /// boundaries only, never per frame.
    /// </summary>
    private void UpdateHitRegion(int side, int width, int height, int radius)
    {
        int pad = 2;
        int left = (int)Math.Round(side * _scale) - pad;
        int top = -pad;
        int right = (int)Math.Round((side + width) * _scale) + pad;
        int bottom = (int)Math.Round(height * _scale) + pad;
        int ellipse = (int)Math.Round(radius * 2 * _scale);

        IntPtr region = CreateRoundRectRgn(left, top, right, bottom, ellipse, ellipse);
        if (region == IntPtr.Zero) return;

        SetWindowRgn(_hwnd, region, true);
    }

    // ── WebView2 ─────────────────────────────────────────────────────────────

    private async Task InitializeWebViewAsync()
    {
        string contentDir = Path.Combine(AppContext.BaseDirectory, "Assets", "island-ui", "dist");
        if (!File.Exists(Path.Combine(contentDir, "index.html")))
        {
            IslandSpikeLog.Write($"webview: bundle missing at {contentDir}");
            return;
        }

        WebView.DefaultBackgroundColor = Colors.Transparent;

        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppLock", "island", "webview-ui");
        Directory.CreateDirectory(userDataFolder);

        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=Translate,msPdfOOUI,msWebOOUI",
            };
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
            await WebView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"webview: init failed: {ex.Message}");
            return;
        }

        var core = WebView.CoreWebView2;
        if (core == null)
        {
            IslandSpikeLog.Write("webview: CoreWebView2 null after init");
            return;
        }

        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsSwipeNavigationEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            "island.appassets", contentDir, CoreWebView2HostResourceAccessKind.DenyCors);

        // Local content only: nothing may navigate away, open windows or ask for permissions.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("https://island.appassets/", StringComparison.Ordinal))
            {
                IslandSpikeLog.Write($"webview: blocked navigation to {e.Uri}");
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessageReceived;

        bool bench = Array.Exists(
            Environment.GetCommandLineArgs(),
            a => string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase));

        string url = "https://island.appassets/index.html" + (bench ? "?bench=1&cycles=100" : string.Empty);
        core.Navigate(url);
        IslandSpikeLog.Write($"webview: navigating to {url}");

        // Hover is detected here, not in the page: WinUI's WebView2 control only receives pointer
        // input while its window is active, and the island must react without ever taking focus.
        // A 30 Hz cursor poll costs well under 1% of a core and gives exact geometry hysteresis.
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(33);
        _hoverTimer.Tick += (_, _) => PollHover();
        _hoverTimer.Start();

    }

    // ── Bridge (trust boundary) ──────────────────────────────────────────────

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string json;
        try
        {
            json = args.WebMessageAsJson;
        }
        catch (Exception)
        {
            IslandSpikeLog.Write("bridge: message was not JSON");
            return;
        }

        if (json.Length > 8192)
        {
            IslandSpikeLog.Write($"bridge: dropped oversized message ({json.Length} chars)");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("v", out var version) || version.GetInt32() != 1) return;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.GetString() is not { } type) return;

            if (!KnownMessageTypes.Contains(type))
            {
                IslandSpikeLog.Write($"bridge: dropped unknown type '{type}'");
                return;
            }

            switch (type)
            {
                case "layout.target":
                    HandleLayoutTarget(root);
                    break;

                case "perf.report":
                    HandlePerfReport(root);
                    break;

                case "perf.done":
                    IslandSpikeLog.Write("bench: all variants finished");
                    break;

                case "ui.ready":
                    IslandSpikeLog.Write($"bridge: ui ready ({json})");
                    break;

                case "hover.state":
                case "input.focusRequested":
                case "input.focusReleased":
                    IslandSpikeLog.Write($"bridge: {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"bridge: rejected message ({ex.GetType().Name})");
        }
    }

    private void PollHover()
    {
        if (WebView.CoreWebView2 == null || !GetCursorPos(out POINT cursor)) return;

        var origin = AppWindow.Position;
        int islandLeft = _hoverTargetOpen ? SideMargin : (PageWidth - FoldedWidth) / 2;
        int islandWidth = _hoverTargetOpen ? OpenWidth : FoldedWidth;
        int islandHeight = _hoverTargetOpen ? OpenHeight : FoldedHeight;

        // A little hysteresis keeps the pointer on the edge from flapping.
        int pad = (int)Math.Round(8 * _scale);
        int left = origin.X + (int)Math.Round(islandLeft * _scale) - pad;
        int right = origin.X + (int)Math.Round((islandLeft + islandWidth) * _scale) + pad;
        int top = origin.Y - pad;
        int bottom = origin.Y + (int)Math.Round(islandHeight * _scale) + pad;

        bool inside = cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
        if (inside == _hoverInside) return;

        _hoverInside = inside;
        PostToUi("state.hover", new { inside });
        IslandSpikeLog.Write($"hover: {(inside ? "in" : "out")} at {cursor.X},{cursor.Y}");
    }

    private void HandleLayoutTarget(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
        if (!TryReadString(payload, "phase", out string phase)) return;
        if (!TryReadInt(payload, "x", out int x) || !TryReadInt(payload, "y", out _)) return;
        if (!TryReadInt(payload, "w", out int w) || !TryReadInt(payload, "h", out int h)) return;
        if (!TryReadInt(payload, "r", out int radius)) return;

        // The UI is untrusted: only the two layouts this host offers are accepted.
        if (phase != "open" && phase != "folded") return;
        if (x < 0 || x > PageWidth || w <= 0 || w > PageWidth || h <= 0 || h > PageHeight) return;
        if (radius < 0 || radius > 64) return;

        _hoverTargetOpen = phase == "open";
        UpdateHitRegion(x, w, h, radius);
        IslandSpikeLog.Write($"layout.target: {phase} {w}x{h} r={radius} at x={x}");
    }

    private void HandlePerfReport(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
        if (!TryReadString(payload, "variant", out string variant)) return;
        if (!TryReadDouble(payload, "p95", out double p95) || !TryReadDouble(payload, "max", out double max)) return;
        TryReadDouble(payload, "p50", out double p50);
        TryReadDouble(payload, "p99", out double p99);
        TryReadInt(payload, "cycles", out int cycles);
        TryReadInt(payload, "samples", out int samples);
        TryReadManyStrings(payload, "text", out string text);

        string line =
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} variant={variant} cycles={cycles} samples={samples} " +
            $"p50={p50:0.0} p95={p95:0.0} p99={p99:0.0} max={max:0.0} {text}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_benchFile)!);
            File.AppendAllText(_benchFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"bench: write failed ({ex.GetType().Name})");
        }

        IslandSpikeLog.Write("bench: " + line);
    }

    private void PostToUi(string type, object payload)
    {
        var core = WebView.CoreWebView2;
        if (core == null) return;

        string json = JsonSerializer.Serialize(new { v = 1, type, payload });
        core.PostWebMessageAsJson(json);
    }

    private static bool TryReadString(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0 && value.Length <= 64;
    }

    private static bool TryReadManyStrings(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return value.Length <= 256;
    }

    private static bool TryReadInt(JsonElement payload, string name, out int value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out value);
    }

    private static bool TryReadDouble(JsonElement payload, string name, out double value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetDouble(out value)
               && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
