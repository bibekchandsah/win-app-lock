using System;
using System.Runtime.InteropServices;
using AppLock.Services;
using AppLock.Services.SpeedMeter;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AppLock.Views;

/// <summary>
/// The floating speed widget: borderless, rounded, topmost, never activates.
/// Milestone 2 shows live download/upload; appearance options and dragging come later.
/// </summary>
public sealed partial class SpeedWidgetWindow : Window
{
    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMNCRP_DISABLED = 1;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
        public MARGINS(int all)
        {
            cxLeftWidth = all;
            cxRightWidth = all;
            cyTopHeight = all;
            cyBottomHeight = all;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    public SpeedWidgetWindow()
    {
        InitializeComponent();
        Title = "Speed Meter";

        SetupWindow();

        WidgetCard.Loaded += OnWidgetLoaded;
    }

    private void SetupWindow()
    {
        try
        {
            if (AppWindow is { } appWindow)
            {
                appWindow.IsShownInSwitchers = false;

                var presenter = OverlappedPresenter.Create();
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                appWindow.SetPresenter(presenter);
            }

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Window setup error: {ex.Message}");
        }
    }

    private void OnWidgetLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowChrome();
        ResizeToContent();
        PositionOnScreen();

        // Apply the numbers the service already has so the widget never flashes placeholders.
        Update(new NetworkSpeedSample());
    }

    /// <summary>
    /// After the first frame exists, make the window per-pixel transparent (so the rounded card
    /// shows the desktop through its corners) and keep it out of Alt+Tab and focus.
    /// </summary>
    private void ApplyWindowChrome()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == nint.Zero) return;

            uint ncPolicy = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(uint));

            var margins = new MARGINS(-1);
            DwmExtendFrameIntoClientArea(hWnd, ref margins);

            // IsShownInSwitchers=false already keeps it out of Alt+Tab and the taskbar; the volume
            // overlay had WS_EX_TOOLWINDOW make the window invisible here, so it is not applied.
            var exStyle = (int)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            exStyle |= WS_EX_NOACTIVATE;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Chrome error: {ex.Message}");
        }
    }

    private void ResizeToContent()
    {
        try
        {
            WidgetCard.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = WidgetCard.DesiredSize;

            // AppWindow sizes are physical pixels, DesiredSize is DIPs.
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;

            var width = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));

            AppWindow?.Resize(new SizeInt32(width, height));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Resize error: {ex.Message}");
        }
    }

    /// <summary>Places the widget at the stored percentage of the monitor work area.</summary>
    private void PositionOnScreen()
    {
        try
        {
            if (AppWindow is not { } appWindow) return;

            var settings = ServiceContainer.Settings.SpeedMeter;
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;

            var width = appWindow.Size.Width;
            var height = appWindow.Size.Height;

            var percentX = Math.Clamp(settings.PositionPercentX, 0f, 100f) / 100f;
            var percentY = Math.Clamp(settings.PositionPercentY, 0f, 100f) / 100f;

            var x = work.X + (int)Math.Round((work.Width - width) * percentX);
            var y = work.Y + (int)Math.Round((work.Height - height) * percentY);

            appWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Position error: {ex.Message}");
        }
    }

    /// <summary>Writes one reading into the widget with the configured units and decimals.</summary>
    public void Update(NetworkSpeedSample sample)
    {
        var settings = ServiceContainer.Settings.SpeedMeter;
        var units = settings.Units;
        var decimals = settings.Decimals;

        DownText.Text = SpeedFormatter.Format(sample.BytesDownPerSec, units, decimals);
        UpText.Text = SpeedFormatter.Format(sample.BytesUpPerSec, units, decimals);
    }
}
