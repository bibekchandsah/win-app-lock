using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AppLock.Island.Bridge;
using AppLock.Island.Events;
using AppLock.Island.Modules;
using AppLock.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace AppLock.Island.Host;

/// <summary>
/// The island window: borderless, topmost, never activates on hover, and per-pixel transparent.
/// It hosts the local WebView2 bundle and talks to it only through the validated message bridge.
/// </summary>
public sealed partial class IslandWindow : Window
{
    // ── Win32 ────────────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const int MA_NOACTIVATE = 3;

    // Windows 11 DWM attributes: a transparent window still gets a 1px border and OS rounding
    // unless both are turned off, which otherwise shows as a rectangle around the notch.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

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
        "state.hover",
        "input.focusRequested",
        "input.focusReleased",
        "volume.set",
        "volume.step",
        "volume.device",
        "home.visible",
        "tile.run",
        "media.command",
        "card.action",
    };

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _subclassProc;
    private readonly DispatcherTimer _topmostTimer = new();
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly string _benchFile;
    private readonly string _contentDir;
    private BridgeBatcher? _batcher;

    private IslandSettings _settings;
    private double _scale = 1;

    /// <summary>Island rectangle in page CSS pixels, as last reported by the UI.</summary>
    private (int X, int Y, int W, int H) _islandRect;
    private bool _hoverTargetOpen;
    private bool _hoverInside;
    private bool _isVisible = true;

    /// <summary>Raised once the page has loaded and the first snapshot can be sent.</summary>
    public event Action? Ready;

    /// <summary>Mouse wheel over the island; the value is the number of notches (positive = up).</summary>
    public event Action<int>? WheelTurned;

    /// <summary>An open/close animation started and will run for this long.</summary>
    public event Action<int>? MotionStarted;

    /// <summary>The page asked for an absolute volume in percent.</summary>
    public event Action<int>? VolumeRequested;

    /// <summary>The Home tab is (or is no longer) on screen, so the host can start/stop sampling.</summary>
    public event Action<bool>? HomeVisibilityChanged;

    /// <summary>The island was shown or hidden, including the host hiding it for a capture.</summary>
    public event Action<bool>? VisibleChanged;

    /// <summary>A Home tile was clicked; only the ids in <see cref="TileIds"/> get through.</summary>
    public event Action<string>? TileRequested;

    /// <summary>Play / pause / next / previous for the current media session.</summary>
    public event Action<string>? MediaCommandRequested;

    /// <summary>The user picked another output device in the Home volume row.</summary>
    public event Action<string>? VolumeDeviceRequested;

    /// <summary>An action button on a live card (QR result: Open / Copy).</summary>
    public event Action<string>? CardActionRequested;

    private static readonly HashSet<string> TileIds = new(StringComparer.Ordinal)
    {
        "area", "fullscreen", "screentext", "qr", "network", "cpuram", "record", "command",
    };

    public ClipboardCardSource Clipboard { get; }

    public IslandWindow(IslandSettings settings)
    {
        InitializeComponent();
        _settings = settings.Clone();

        _hwnd = WindowNative.GetWindowHandle(this);
        _subclassProc = IslandSubclassProc;
        Title = "Island";
        Clipboard = new ClipboardCardSource(DispatcherQueue, _hwnd);

        _contentDir = Path.Combine(AppContext.BaseDirectory, "Assets", "island-ui", "dist");
        _benchFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppLock", "island", "bench.txt");

        // Must be set before the WebView2 is shown: the controller takes its background from the
        // control when the core is created, and otherwise paints the theme colour (#202020).
        WebView.DefaultBackgroundColor = Colors.Transparent;

        ConfigureWindowChrome();
        ApplyWindowGeometry();

        // Re-assert the topmost slot: another topmost window can end up above the island.
        _topmostTimer.Interval = TimeSpan.FromSeconds(1);
        _topmostTimer.Tick += (_, _) => SetWindowPos(
            _hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _topmostTimer.Start();

        // Hover is detected here, not in the page: WinUI's WebView2 control only receives pointer
        // input while its window is active, and the island must react without taking focus.
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(33);
        _hoverTimer.Tick += (_, _) => PollHover();
        _hoverTimer.Start();

        Activate();
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);

        _ = InitializeWebViewAsync();
    }

    public bool IsVisible => _isVisible;

    public bool IsHovered => _hoverInside;

    /// <summary>True while the open shell (not the folded pill) is what the page is showing.</summary>
    public bool IsOpen { get; private set; }

    // ── Window ───────────────────────────────────────────────────────────────

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
        // WM_MOUSEACTIVATE instead of WS_EX_NOACTIVATE, which blocks WinUI/WebView2 input.
        long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        SetWindowSubclass(_hwnd, _subclassProc, 1, IntPtr.Zero);

        try
        {
            int noBorder = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

            int noRounding = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRounding, sizeof(int));
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"dwm: border/corner attributes unavailable: {ex.Message}");
        }
    }

    private IntPtr IslandSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;

        if (Clipboard.HandleMessage(msg)) return IntPtr.Zero;

        if (msg == WM_MOUSEWHEEL)
        {
            // Wheel over the notch changes the system volume; consumed so nothing else reacts.
            int delta = (short)((long)wParam >> 16);
            if (delta != 0) WheelTurned?.Invoke(Math.Sign(delta));
            return IntPtr.Zero;
        }

        if (msg == WM_DPICHANGED)
        {
            // The page follows the new DPI on its own; the window and hit region do not.
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyWindowGeometry();
                UpdateHitRegion(
                    side: (IslandLayout.PageSize(_settings.SizePercent).Width / 2)
                          - (int)Math.Round(IslandLayout.FoldedWidth * IslandLayout.SizeFactor(_settings.SizePercent) / 2),
                    width: (int)Math.Round(IslandLayout.FoldedWidth * IslandLayout.SizeFactor(_settings.SizePercent)),
                    height: (int)Math.Round(IslandLayout.FoldedHeight * IslandLayout.SizeFactor(_settings.SizePercent)),
                    radius: (int)Math.Round(IslandLayout.FoldedRadius * IslandLayout.SizeFactor(_settings.SizePercent)));
            });
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private double DpiScale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1d : dpi / 96d;
    }

    private void ApplyWindowGeometry()
    {
        _scale = DpiScale();
        var (pageWidth, pageHeight) = IslandLayout.PageSize(_settings.SizePercent);
        var rect = IslandPositioner.Compute(AppWindow, _settings, pageWidth, pageHeight, _scale);
        AppWindow.MoveAndResize(rect);

        double factor = IslandLayout.SizeFactor(_settings.SizePercent);
        int foldedWidth = (int)Math.Round(IslandLayout.FoldedWidth * factor);
        int foldedHeight = (int)Math.Round(IslandLayout.FoldedHeight * factor);
        int foldedLeft = (pageWidth - foldedWidth) / 2;
        _islandRect = (foldedLeft, 0, foldedWidth, foldedHeight);
        UpdateHitRegion(
            side: foldedLeft,
            width: foldedWidth,
            height: foldedHeight,
            radius: (int)Math.Round(IslandLayout.FoldedRadius * factor));
    }

    /// <summary>
    /// Clicks outside the visible notch fall through to the desktop. Called at animation
    /// boundaries only (the UI sends layout.target), never per frame.
    /// </summary>
    private void UpdateHitRegion(int side, int width, int height, int radius)
    {
        // The WinUI XAML content island clears its surface to the theme colour (#202020 in dark),
        // so the window region is what keeps that colour off the desktop. Padding it would show a
        // grey rim around the notch, so the region follows the shape itself.
        int pad = 0;
        int left = (int)Math.Round(side * _scale) - pad;
        int top = -pad;
        int right = (int)Math.Round((side + width) * _scale) + pad;
        int bottom = (int)Math.Round(height * _scale) + pad;
        int ellipse = (int)Math.Round(radius * 2 * _scale);

        IntPtr region = CreateRoundRectRgn(left, top, right, bottom, ellipse, ellipse);
        if (region == IntPtr.Zero) return;

        SetWindowRgn(_hwnd, region, true);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public void ApplySettings(IslandSettings settings)
    {
        _settings = settings.Clone();
        ApplyWindowGeometry();
        SendSnapshot();
    }

    public void SetIslandVisible(bool visible)
    {
        if (_isVisible == visible) return;
        _isVisible = visible;

        ShowWindow(_hwnd, visible ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);
        if (visible)
        {
            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        PostRaw(BridgeMessages.Envelope(visible ? BridgeMessages.AnimResume : BridgeMessages.AnimPause, null));
        VisibleChanged?.Invoke(visible);
    }

    // ── WebView2 ─────────────────────────────────────────────────────────────

    private async Task InitializeWebViewAsync()
    {
        if (!File.Exists(Path.Combine(_contentDir, "index.html")))
        {
            IslandSpikeLog.Write($"webview: bundle missing at {_contentDir}");
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
                AdditionalBrowserArguments = _settings.HardwareAcceleration
                    ? "--disable-features=Translate,msPdfOOUI,msWebOOUI"
                    : "--disable-features=Translate,msPdfOOUI,msWebOOUI --disable-gpu",
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

        // Re-assert transparency after init (the control resets it when the core is created).
        WebView.DefaultBackgroundColor = Colors.Transparent;

        var webSettings = core.Settings;
        webSettings.AreDefaultContextMenusEnabled = false;
        webSettings.AreDevToolsEnabled = false;
        webSettings.IsStatusBarEnabled = false;
        webSettings.IsZoomControlEnabled = false;
        webSettings.AreBrowserAcceleratorKeysEnabled = false;
        webSettings.IsPasswordAutosaveEnabled = false;
        webSettings.AreDefaultScriptDialogsEnabled = false;
        webSettings.IsSwipeNavigationEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            "island.appassets", _contentDir, CoreWebView2HostResourceAccessKind.DenyCors);

        // Album art is written here by the media source and loaded by the page as an image, so it
        // never has to travel over the message bridge (island-webview2.md 5).
        try
        {
            string artFolder = MediaSessionSource.ArtFolder;
            Directory.CreateDirectory(artFolder);
            core.SetVirtualHostNameToFolderMapping(
                "island.cache", artFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"webview: art folder unavailable: {ex.Message}");
        }

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

        _batcher = new BridgeBatcher(DispatcherQueue, PostRaw, (module, ex) => IslandSpikeLog.Write($"bridge: {module} send failed ({ex.GetType().Name})"));

        bool bench = Array.Exists(
            Environment.GetCommandLineArgs(),
            a => string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase));

        string? tab = ReadTabArgument();
        string query = bench ? "?bench=1&cycles=100" : tab is null ? string.Empty : $"?tab={tab}";
        string url = "https://island.appassets/index.html" + query;
        core.Navigate(url);
        IslandSpikeLog.Write($"webview: navigating to {url}");
    }

    /// <summary>--tab=chat opens the shell on that tab instead of Home.</summary>
    private static string? ReadTabArgument()
    {
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (!arg.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase)) continue;
            string value = arg[6..].ToLowerInvariant();
            return TabIds.Contains(value) ? value : null;
        }
        return null;
    }

    private static readonly HashSet<string> TabIds = new(StringComparer.Ordinal)
    {
        "home", "clipboard", "shelf", "text", "chat", "note", "weather", "controls",
    };

    private static readonly HashSet<string> MediaCommands = new(StringComparer.Ordinal)
    {
        "toggle", "next", "previous",
    };

    private static readonly HashSet<string> CardActions = new(StringComparer.Ordinal)
    {
        "qr.open", "qr.copy",
    };

    public void PostRaw(string json)
    {
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            IslandSpikeLog.Write($"bridge: post failed ({ex.GetType().Name})");
        }
    }

    // ── Host → UI ────────────────────────────────────────────────────────────

    public void SendSnapshot()
    {
        if (_batcher == null) return;

        var palette = BridgeMessages.ResolvePalette(_settings, SystemUsesLightTheme());
        string message = BridgeMessages.SettingsSnapshotMessage(_settings, palette, AppVersion());

        // A snapshot must never wait behind a batched module update.
        _batcher.SendNow(message);
    }

    public void SendTime(DateTime now) => _batcher?.Post("time", BridgeMessages.TimeMessage(now));

    public void SendBattery(int percent, bool charging, bool present) =>
        _batcher?.Post("battery", BridgeMessages.BatteryMessage(percent, charging, present));

    public void SendStats(HomeStats stats) => _batcher?.Post("stats", BridgeMessages.StatsMessage(stats));

    public void SendMedia(MediaSnapshot media) => _batcher?.Post("media", BridgeMessages.MediaMessage(media));

    public void SendRecent(IReadOnlyList<RecentClip> items) =>
        _batcher?.Post("recent", BridgeMessages.RecentMessage(items));

    public void SendVolume(int percent, IReadOnlyList<VolumeDeviceInfo> devices) =>
        _batcher?.Post("volume", BridgeMessages.VolumeMessage(percent, devices));

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string AppVersion()
    {
        try
        {
            return typeof(IslandWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
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

                case "ui.ready":
                    IslandSpikeLog.Write("bridge: ui ready");
                    SendSnapshot();

                    // The pointer can already be on the notch when the page loads (or the page can
                    // reload): replay the current hover state, or the island would never open.
                    PostRaw(BridgeMessages.HoverMessage(_hoverInside));
                    Ready?.Invoke();
                    break;

                case "volume.set":
                    if (root.TryGetProperty("payload", out var volume) && volume.ValueKind == JsonValueKind.Object
                        && TryReadInt(volume, "percent", out int percent))
                    {
                        VolumeRequested?.Invoke(Math.Clamp(percent, 0, 100));
                    }
                    break;

                case "volume.step":
                    if (root.TryGetProperty("payload", out var stepPayload) && stepPayload.ValueKind == JsonValueKind.Object
                        && TryReadInt(stepPayload, "delta", out int delta) && delta != 0)
                    {
                        WheelTurned?.Invoke(Math.Clamp(delta, -3, 3));
                    }
                    break;

                case "volume.device":
                    if (root.TryGetProperty("payload", out var devicePayload) && devicePayload.ValueKind == JsonValueKind.Object
                        && TryReadText(devicePayload, "id", out string deviceId) && deviceId.Length > 0)
                    {
                        VolumeDeviceRequested?.Invoke(deviceId);
                    }
                    break;

                case "home.visible":
                    if (root.TryGetProperty("payload", out var homePayload) && homePayload.ValueKind == JsonValueKind.Object
                        && homePayload.TryGetProperty("visible", out var visibleElement)
                        && (visibleElement.ValueKind == JsonValueKind.True || visibleElement.ValueKind == JsonValueKind.False))
                    {
                        HomeVisibilityChanged?.Invoke(visibleElement.ValueKind == JsonValueKind.True);
                    }
                    break;

                case "tile.run":
                    if (root.TryGetProperty("payload", out var tilePayload) && tilePayload.ValueKind == JsonValueKind.Object
                        && TryReadString(tilePayload, "id", out string tileId) && TileIds.Contains(tileId))
                    {
                        TileRequested?.Invoke(tileId);
                    }
                    break;

                case "media.command":
                    if (root.TryGetProperty("payload", out var mediaPayload) && mediaPayload.ValueKind == JsonValueKind.Object
                        && TryReadString(mediaPayload, "command", out string command)
                        && MediaCommands.Contains(command))
                    {
                        MediaCommandRequested?.Invoke(command);
                    }
                    break;

                case "card.action":
                    if (root.TryGetProperty("payload", out var actionPayload) && actionPayload.ValueKind == JsonValueKind.Object
                        && TryReadString(actionPayload, "id", out string cardAction) && CardActions.Contains(cardAction))
                    {
                        CardActionRequested?.Invoke(cardAction);
                    }
                    break;

                case "perf.done":
                case "hover.state":
                case "state.hover":
                case "input.focusRequested":
                case "input.focusReleased":
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
        if (!_isVisible || WebView.CoreWebView2 == null || !GetCursorPos(out POINT cursor)) return;

        var origin = AppWindow.Position;

        // The rectangle the page last reported (folded notch or the active tab's shell), padded a
        // little so the pointer on the edge does not flap.
        int pad = (int)Math.Round(8 * _scale);
        int left = origin.X + (int)Math.Round(_islandRect.X * _scale) - pad;
        int right = origin.X + (int)Math.Round((_islandRect.X + _islandRect.W) * _scale) + pad;
        int top = origin.Y - pad;
        int bottom = origin.Y + (int)Math.Round(_islandRect.H * _scale) + pad;

        bool inside = cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
        if (inside == _hoverInside) return;

        _hoverInside = inside;
        IslandSpikeLog.Write($"hover: {inside} at {cursor.X},{cursor.Y} rect {_islandRect.X},{_islandRect.Y} {_islandRect.W}x{_islandRect.H}");
        PostRaw(BridgeMessages.HoverMessage(inside));
    }

    private void HandleLayoutTarget(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
        if (!TryReadString(payload, "phase", out string phase)) return;
        if (!TryReadInt(payload, "x", out int x) || !TryReadInt(payload, "y", out _)) return;
        if (!TryReadInt(payload, "w", out int w) || !TryReadInt(payload, "h", out int h)) return;
        if (!TryReadInt(payload, "r", out int radius)) return;

        // The UI is untrusted: only the layouts this host offers are accepted.
        if (phase != "open" && phase != "folded") return;
        var (pageWidth, pageHeight) = IslandLayout.PageSize(_settings.SizePercent);
        if (x < 0 || x > pageWidth || w <= 0 || w > pageWidth || h <= 0 || h > pageHeight) return;
        if (radius < 0 || radius > 64) return;

        _islandRect = (x, 0, w, h);
        _hoverTargetOpen = phase == "open";
        IsOpen = _hoverTargetOpen;
        IslandSpikeLog.Write($"layout: {phase} {x},{0} {w}x{h} r{radius}");
        UpdateHitRegion(x, w, h, radius);

        // The page is about to animate: hold non-visual updates until it settles (island.md 5).
        MotionStarted?.Invoke(TryReadInt(payload, "durationMs", out int duration) ? duration : 0);
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
        TryReadText(payload, "text", out string text);

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

    private static bool TryReadString(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0 && value.Length <= 64;
    }

    private static bool TryReadText(JsonElement payload, string name, out string value)
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
