using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AppLock.Views.Controls;

public sealed partial class ScreenBlurPreview : UserControl
{
    private float _blurAmount = 24f; // Default blur
    private int _dimAmount = 0;      // Default dim (0-60%)
    private bool _isLoaded;
    private CanvasBitmap? _wallpaperBitmap;

    public ScreenBlurPreview()
    {
        this.InitializeComponent();
    }

    /// <summary>
    /// Set the blur intensity (0-100%)
    /// </summary>
    public void SetBlurLevel(int percent)
    {
        // Map 0-100% to 0-48px blur amount
        _blurAmount = (percent / 100f) * 48f;
        
        if (_isLoaded && PreviewCanvas != null)
        {
            PreviewCanvas.Invalidate();
        }
    }

    /// <summary>
    /// Set the dim amount (0-60%)
    /// </summary>
    public void SetDimAmount(int percent)
    {
        _dimAmount = Math.Clamp(percent, 0, 60);
        
        if (_isLoaded && PreviewCanvas != null)
        {
            PreviewCanvas.Invalidate();
        }
    }

    /// <summary>
    /// Start the live preview
    /// </summary>
    public void StartPreview()
    {
        try
        {
            LoadingRing.Visibility = Visibility.Visible;
            FallbackPanel.Visibility = Visibility.Collapsed;
            
            System.Diagnostics.Debug.WriteLine("[ScreenBlurPreview] StartPreview called, waiting for canvas resources");
            
            // Canvas will trigger CreateResources which will load the image
            // Just show loading state here
        }
        catch (Exception ex)
        {
            ShowFallback($"Preview unavailable: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] StartPreview failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop the live preview
    /// </summary>
    public void StopPreview()
    {
        _wallpaperBitmap?.Dispose();
        _wallpaperBitmap = null;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private async Task LoadWallpaperAsync(string path)
    {
        try
        {
            if (PreviewCanvas == null)
                return;

            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Attempting to load wallpaper from: {path}");

            // Check if file exists
            if (!System.IO.File.Exists(path))
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Wallpaper file does not exist: {path}");
                await LoadFallbackImageAsync();
                return;
            }

            // Load image from file
            _wallpaperBitmap = await CanvasBitmap.LoadAsync(PreviewCanvas, path);
            
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Wallpaper loaded: {_wallpaperBitmap.SizeInPixels.Width}x{_wallpaperBitmap.SizeInPixels.Height}");

            // Hide loading ring and trigger redraw
            LoadingRing.Visibility = Visibility.Collapsed;
            PreviewCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Failed to load wallpaper: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Exception type: {ex.GetType().Name}");
            
            // Try fallback image
            await LoadFallbackImageAsync();
        }
    }

    private async Task LoadFallbackImageAsync()
    {
        try
        {
            if (PreviewCanvas == null)
                return;

            System.Diagnostics.Debug.WriteLine("[ScreenBlurPreview] Creating fallback gradient image");

            // Create a simple solid color bitmap as fallback
            var width = 1920;
            var height = 1080;
            var pixelCount = width * height;
            var pixels = new byte[pixelCount * 4]; // BGRA format

            // Create a simple gradient by filling pixels
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    
                    // Simple diagonal gradient from purple to blue
                    float progress = (x + y) / (float)(width + height);
                    
                    pixels[index + 0] = (byte)(183 + (243 - 183) * progress); // B
                    pixels[index + 1] = (byte)(58 + (150 - 58) * progress);   // G
                    pixels[index + 2] = (byte)(103 + (33 - 103) * progress);  // R
                    pixels[index + 3] = 255;                                   // A
                }
            }

            _wallpaperBitmap = CanvasBitmap.CreateFromBytes(
                PreviewCanvas,
                pixels,
                width,
                height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

            System.Diagnostics.Debug.WriteLine("[ScreenBlurPreview] Fallback image created successfully");
            
            LoadingRing.Visibility = Visibility.Collapsed;
            PreviewCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Failed to create fallback image: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Stack trace: {ex.StackTrace}");
            ShowFallback($"Failed to load preview image: {ex.Message}");
        }
    }

    private void ShowFallback(string message)
    {
        LoadingRing.Visibility = Visibility.Collapsed;
        FallbackPanel.Visibility = Visibility.Visible;
        FallbackText.Text = message;
    }

    private void PreviewCanvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        // Track async resource creation
        args.TrackAsyncAction(CreateResourcesAsync(sender).AsAsyncAction());
    }

    private async Task CreateResourcesAsync(CanvasControl sender)
    {
        // Load wallpaper or fallback when canvas is ready
        System.Diagnostics.Debug.WriteLine("[ScreenBlurPreview] Canvas resources being created");
        
        var wallpaperPath = GetWallpaperPath();
        
        if (!string.IsNullOrEmpty(wallpaperPath))
        {
            await LoadWallpaperAsync(wallpaperPath);
        }
        else
        {
            await LoadFallbackImageAsync();
        }
    }

    private void PreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_wallpaperBitmap == null)
            return;

        try
        {
            // Apply Gaussian blur effect
            var blurEffect = new GaussianBlurEffect
            {
                Source = _wallpaperBitmap,
                BlurAmount = _blurAmount,
                BorderMode = EffectBorderMode.Hard
            };

            // Calculate scale to fill width while maintaining aspect ratio
            var canvasWidth = sender.ActualWidth;
            var canvasHeight = sender.ActualHeight;
            var sourceWidth = _wallpaperBitmap.SizeInPixels.Width;
            var sourceHeight = _wallpaperBitmap.SizeInPixels.Height;

            // Scale to fill width
            var scale = canvasWidth / sourceWidth;
            var scaledWidth = sourceWidth * scale;
            var scaledHeight = sourceHeight * scale;
            
            // Center vertically
            var offsetX = 0;
            var offsetY = (canvasHeight - scaledHeight) / 2;

            // Draw scaled and blurred image
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
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Draw failed: {ex.Message}");
        }
    }

    private void PreviewCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        StartPreview();
    }

    private void PreviewCanvas_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopPreview();
    }

    #region Wallpaper Path Detection

    private string? GetWallpaperPath()
    {
        try
        {
            // Method 1: Get wallpaper path from registry
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
            if (key != null)
            {
                var wallpaperPath = key.GetValue("Wallpaper") as string;
                key.Close();

                if (!string.IsNullOrEmpty(wallpaperPath) && System.IO.File.Exists(wallpaperPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Found wallpaper via registry: {wallpaperPath}");
                    return wallpaperPath;
                }
            }

            // Method 2: Try TranscodedWallpaper
            var transcoded = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");

            if (System.IO.File.Exists(transcoded))
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Found TranscodedWallpaper: {transcoded}");
                return transcoded;
            }

            // Method 3: Try CachedImage
            var cached = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\CachedFiles");

            if (System.IO.Directory.Exists(cached))
            {
                var files = System.IO.Directory.GetFiles(cached, "*.*");
                if (files.Length > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Found cached wallpaper: {files[0]}");
                    return files[0];
                }
            }

            // Method 4: Common wallpaper locations
            var commonPaths = new[]
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "wallpaper.jpg"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows\img0.jpg"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Theme1\img1.jpg")
            };

            foreach (var path in commonPaths)
            {
                if (System.IO.File.Exists(path))
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] Found wallpaper at common location: {path}");
                    return path;
                }
            }

            System.Diagnostics.Debug.WriteLine("[ScreenBlurPreview] No wallpaper found, will use gradient fallback");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenBlurPreview] GetWallpaperPath failed: {ex.Message}");
            return null;
        }
    }

    #endregion
}
