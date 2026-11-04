using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AppLock.Services.Brightness;

/// <summary>
/// On-screen display service for showing brightness changes
/// </summary>
public class OsdService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private IntPtr _osdHwnd = IntPtr.Zero;
    private Window? _osdWindow;
    private DispatcherTimer? _hideTimer;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    public OsdService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Show(int brightnessPercent)
    {
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_osdWindow == null)
                {
                    CreateOsdWindow();
                }

                if (_osdWindow != null)
                {
                    UpdateContent(brightnessPercent);
                    ShowWindow();
                    StartHideTimer();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OSD Show error: {ex.Message}");
            }
        });
    }

    private void CreateOsdWindow()
    {
        _osdWindow = new Window
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(),
            ExtendsContentIntoTitleBar = true
        };

        // Create content
        var grid = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 30, 30, 30)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 12, 20, 12),
            MinWidth = 200
        };

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Sun icon
        var icon = new FontIcon
        {
            Glyph = "\uE706",
            FontSize = 24,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 100))
        };

        // Brightness text
        var textBlock = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
        textBlock.SetValue(FrameworkElement.NameProperty, "BrightnessText");

        stackPanel.Children.Add(icon);
        stackPanel.Children.Add(textBlock);
        grid.Children.Add(stackPanel);

        _osdWindow.Content = grid;

        // Set window size and position
        var appWindow = _osdWindow.AppWindow;
        if (appWindow != null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(250, 80));
            
            // Position at bottom-center
            var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
            var workArea = displayArea.WorkArea;
            int x = workArea.X + (workArea.Width - 250) / 2;
            int y = workArea.Y + workArea.Height - 120;
            appWindow.Move(new Windows.Graphics.PointInt32(x, y));

            // Hide title bar
            if (appWindow.TitleBar != null)
            {
                appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                appWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            }
        }

        // Get HWND
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_osdWindow);
        _osdHwnd = hwnd;
    }

    private void UpdateContent(int brightnessPercent)
    {
        if (_osdWindow?.Content is Grid grid)
        {
            var textBlock = FindTextBlock(grid);
            if (textBlock != null)
            {
                textBlock.Text = $"{brightnessPercent}%";
            }
        }
    }

    private TextBlock? FindTextBlock(DependencyObject parent)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock textBlock && textBlock.Name == "BrightnessText")
            {
                return textBlock;
            }

            var result = FindTextBlock(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private void ShowWindow()
    {
        if (_osdWindow == null) return;

        try
        {
            _osdWindow.Activate();

            // Make it topmost and non-activating
            if (_osdHwnd != IntPtr.Zero)
            {
                SetWindowPos(_osdHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OSD ShowWindow error: {ex.Message}");
        }
    }

    private void StartHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _hideTimer.Tick += (s, e) =>
        {
            _hideTimer.Stop();
            HideWindow();
        };
        _hideTimer.Start();
    }

    private void HideWindow()
    {
        try
        {
            _osdWindow?.Close();
            _osdWindow = null;
            _osdHwnd = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OSD HideWindow error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
        HideWindow();
    }
}
