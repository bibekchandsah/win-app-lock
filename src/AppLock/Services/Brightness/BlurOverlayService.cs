using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;

namespace AppLock.Services.Brightness;

/// <summary>
/// Blur overlay service with configurable blur and dim effects using screen capture
/// </summary>
public class BlurOverlayService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private Window? _overlayWindow;
    private IntPtr _overlayHwnd = IntPtr.Zero;
    private bool _isActive;
    private int _blurLevel = 50;      // 0-100%
    private int _dimAmount = 0;       // 0-60%
    private CanvasControl? _canvasControl;
    private CanvasBitmap? _capturedScreen;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    public bool IsActive => _isActive;

    public BlurOverlayService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        
        // Load initial values from settings
        var settings = ServiceContainer.Settings.Brightness.Blur;
        _blurLevel = settings.LevelPercent;
        _dimAmount = settings.DimPercent;
    }

    public void Toggle()
    {
        if (_isActive)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void Show()
    {
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_overlayWindow == null)
                {
                    CreateOverlayWindow();
                }

                if (_overlayWindow != null)
                {
                    _overlayWindow.Activate();
                    MakeClickThrough();
                    _isActive = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Blur overlay show error: {ex.Message}");
            }
        });
    }

    public void Hide()
    {
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                _capturedScreen?.Dispose();
                _capturedScreen = null;
                
                if (_canvasControl != null)
                {
                    _canvasControl.CreateResources -= Canvas_CreateResources;
                    _canvasControl.Draw -= Canvas_Draw;
                    _canvasControl.RemoveFromVisualTree();
                    _canvasControl = null;
                }
                
                _overlayWindow?.Close();
                _overlayWindow = null;
                _overlayHwnd = IntPtr.Zero;
                _isActive = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Blur overlay hide error: {ex.Message}");
            }
        });
    }

    public void SetBlurLevel(int percent)
    {
        _blurLevel = Math.Clamp(percent, 0, 100);
        
        if (_isActive && _overlayWindow != null)
        {
            _dispatcher.TryEnqueue(() =>
            {
                UpdateOverlayEffect();
            });
        }
    }

    public void SetDimAmount(int percent)
    {
        _dimAmount = Math.Clamp(percent, 0, 60);
        
        if (_isActive && _overlayWindow != null)
        {
            _dispatcher.TryEnqueue(() =>
            {
                UpdateOverlayEffect();
            });
        }
    }

    private void CreateOverlayWindow()
    {
        _overlayWindow = new Window();

        // Create canvas for blur rendering
        _canvasControl = new CanvasControl();
        _canvasControl.CreateResources += Canvas_CreateResources;
        _canvasControl.Draw += Canvas_Draw;

        _overlayWindow.Content = _canvasControl;

        // Configure window for borderless, no titlebar
        _overlayWindow.ExtendsContentIntoTitleBar = true;
        _overlayWindow.SetTitleBar(null);
        
        // Don't use DesktopAcrylicBackdrop - we'll handle blur ourselves
        _overlayWindow.SystemBackdrop = null;
        
        var appWindow = _overlayWindow.AppWindow;
        if (appWindow != null)
        {
            // Remove titlebar
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
            
            // Make borderless
            var presenter = appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            // Make it fullscreen
            var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
            var bounds = displayArea.OuterBounds;
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }

        // Get HWND
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_overlayWindow);
        _overlayHwnd = hwnd;
    }

    private void Canvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        args.TrackAsyncAction(LoadWallpaperAsync(sender).AsAsyncAction());
    }

    private async Task LoadWallpaperAsync(CanvasControl canvas)
    {
        try
        {
            var wallpaperPath = GetWallpaperPath();
            
            if (!string.IsNullOrEmpty(wallpaperPath) && System.IO.File.Exists(wallpaperPath))
            {
                _capturedScreen = await CanvasBitmap.LoadAsync(canvas, wallpaperPath);
                System.Diagnostics.Debug.WriteLine($"[BlurOverlay] Wallpaper loaded: {_capturedScreen.SizeInPixels.Width}x{_capturedScreen.SizeInPixels.Height}");
            }
            else
            {
                // Create fallback gradient
                await CreateFallbackBitmapAsync();
            }
            
            canvas.Invalidate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BlurOverlay] Failed to load wallpaper: {ex.Message}");
            await CreateFallbackBitmapAsync();
            canvas.Invalidate();
        }
    }

    private async Task CreateFallbackBitmapAsync()
    {
        try
        {
            var canvasDevice = CanvasDevice.GetSharedDevice();
            
            await Task.Run(() =>
            {
                var width = 1920;
                var height = 1080;
                var pixelCount = width * height;
                var pixels = new byte[pixelCount * 4];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = (y * width + x) * 4;
                        float progress = (x + y) / (float)(width + height);
                        
                        pixels[index + 0] = (byte)(183 + (243 - 183) * progress);
                        pixels[index + 1] = (byte)(58 + (150 - 58) * progress);
                        pixels[index + 2] = (byte)(103 + (33 - 103) * progress);
                        pixels[index + 3] = 255;
                    }
                }

                _capturedScreen = CanvasBitmap.CreateFromBytes(
                    canvasDevice,
                    pixels,
                    width,
                    height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);
            });
            
            System.Diagnostics.Debug.WriteLine("[BlurOverlay] Fallback gradient created");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BlurOverlay] CreateFallbackBitmapAsync error: {ex.Message}");
        }
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_capturedScreen == null)
            return;

        try
        {
            var canvasWidth = sender.ActualWidth;
            var canvasHeight = sender.ActualHeight;

            // If both blur and dim are 0, show nothing (transparent)
            if (_blurLevel == 0 && _dimAmount == 0)
            {
                args.DrawingSession.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                return;
            }

            // Map 0-100% to 0-48px blur amount
            var blurAmount = (_blurLevel / 100f) * 48f;

            // Apply Gaussian blur effect
            var blurEffect = new GaussianBlurEffect
            {
                Source = _capturedScreen,
                BlurAmount = blurAmount,
                BorderMode = EffectBorderMode.Hard
            };

            // Scale to fill screen
            var sourceWidth = _capturedScreen.SizeInPixels.Width;
            var sourceHeight = _capturedScreen.SizeInPixels.Height;
            var scaleX = canvasWidth / sourceWidth;
            var scaleY = canvasHeight / sourceHeight;
            var scale = Math.Max(scaleX, scaleY); // Fill screen (cover)

            var scaledWidth = sourceWidth * scale;
            var scaledHeight = sourceHeight * scale;
            var offsetX = (canvasWidth - scaledWidth) / 2;
            var offsetY = (canvasHeight - scaledHeight) / 2;

            // Draw blurred screen capture
            var transform = System.Numerics.Matrix3x2.CreateScale((float)scale) * 
                           System.Numerics.Matrix3x2.CreateTranslation((float)offsetX, (float)offsetY);
            
            args.DrawingSession.Transform = transform;
            args.DrawingSession.DrawImage(blurEffect);
            args.DrawingSession.Transform = System.Numerics.Matrix3x2.Identity;

            // Apply dim overlay
            if (_dimAmount > 0)
            {
                var dimAlpha = (byte)(255 * _dimAmount / 100.0);
                var dimColor = Windows.UI.Color.FromArgb(dimAlpha, 0, 0, 0);
                args.DrawingSession.FillRectangle(0, 0, (float)canvasWidth, (float)canvasHeight, dimColor);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BlurOverlay] Draw failed: {ex.Message}");
        }
    }

    private string? GetWallpaperPath()
    {
        try
        {
            // Method 1: Registry
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
            if (key != null)
            {
                var wallpaperPath = key.GetValue("Wallpaper") as string;
                key.Close();

                if (!string.IsNullOrEmpty(wallpaperPath) && System.IO.File.Exists(wallpaperPath))
                {
                    return wallpaperPath;
                }
            }

            // Method 2: TranscodedWallpaper
            var transcoded = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");

            if (System.IO.File.Exists(transcoded))
            {
                return transcoded;
            }

            // Method 3: CachedFiles
            var cached = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\CachedFiles");

            if (System.IO.Directory.Exists(cached))
            {
                var files = System.IO.Directory.GetFiles(cached, "*.*");
                if (files.Length > 0)
                {
                    return files[0];
                }
            }

            // Method 4: Common locations
            var commonPaths = new[]
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows\img0.jpg")
            };

            foreach (var path in commonPaths)
            {
                if (System.IO.File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private void MakeClickThrough()
    {
        if (_overlayHwnd == IntPtr.Zero) return;

        try
        {
            // Get current extended style
            uint exStyle = GetWindowLong(_overlayHwnd, GWL_EXSTYLE);
            
            // Add click-through, layered, and noactivate
            exStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            
            // Set new style
            SetWindowLong(_overlayHwnd, GWL_EXSTYLE, exStyle);

            // Make it topmost
            SetWindowPos(_overlayHwnd, HWND_TOPMOST, 0, 0, 0, 0, 
                SWP_SHOWWINDOW | SWP_NOACTIVATE | 0x0002 | 0x0001); // NOMOVE | NOSIZE
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MakeClickThrough error: {ex.Message}");
        }
    }

    private void UpdateOverlayEffect()
    {
        if (_canvasControl != null)
        {
            _canvasControl.Invalidate();
        }
    }

    public void Dispose()
    {
        Hide();
    }
}
