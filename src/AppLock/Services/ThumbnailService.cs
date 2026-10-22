using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;

namespace AppLock.Services;

/// <summary>
/// Sets DWM taskbar thumbnails and live previews to show "Locked" image
/// for protected windows, hiding their actual content from Alt-Tab and taskbar hover.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly Dictionary<HWND, Bitmap> _lockedBitmaps = new();
    
    public ThumbnailService()
    {
    }

    /// <summary>
    /// Enable iconic representation for a window and set its thumbnail to "Locked" image.
    /// </summary>
    public void SetLockedThumbnail(HWND hwnd)
    {
        if (hwnd.Value == IntPtr.Zero) return;

        try
        {
            unsafe
            {
                // Enable iconic representation (forces DWM to use our custom thumbnail)
                var enable = true;
                PInvoke.DwmSetWindowAttribute(
                    hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_FORCE_ICONIC_REPRESENTATION,
                    &enable,
                    sizeof(int));

                // Enable has iconic bitmap flag
                PInvoke.DwmSetWindowAttribute(
                    hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_HAS_ICONIC_BITMAP,
                    &enable,
                    sizeof(int));
            }

            System.Diagnostics.Debug.WriteLine($"Set locked thumbnail attributes for HWND {hwnd.Value:X}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting locked thumbnail: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove locked thumbnail representation from a window.
    /// </summary>
    public void ClearLockedThumbnail(HWND hwnd)
    {
        if (hwnd.Value == IntPtr.Zero) return;

        try
        {
            unsafe
            {
                var disable = false;
                PInvoke.DwmSetWindowAttribute(
                    hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_FORCE_ICONIC_REPRESENTATION,
                    &disable,
                    sizeof(int));

                PInvoke.DwmSetWindowAttribute(
                    hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_HAS_ICONIC_BITMAP,
                    &disable,
                    sizeof(int));
            }

            // Clean up stored bitmap
            if (_lockedBitmaps.TryGetValue(hwnd, out var bitmap))
            {
                bitmap.Dispose();
                _lockedBitmaps.Remove(hwnd);
            }

            System.Diagnostics.Debug.WriteLine($"Cleared locked thumbnail for HWND {hwnd.Value:X}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error clearing locked thumbnail: {ex.Message}");
        }
    }

    /// <summary>
    /// Create a "Locked" bitmap for use in thumbnails.
    /// Dark background with "Locked" text and padlock icon.
    /// </summary>
    public Bitmap CreateLockedBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        
        using (var g = Graphics.FromImage(bitmap))
        {
            // Dark background
            g.Clear(Color.FromArgb(255, 32, 32, 32));
            
            // Draw "Locked" text
            using (var font = new Font("Segoe UI", 24, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.FromArgb(255, 217, 160, 232))) // Purple accent
            {
                var text = "🔒 Locked";
                var size = g.MeasureString(text, font);
                var x = (width - size.Width) / 2;
                var y = (height - size.Height) / 2;
                
                g.DrawString(text, font, brush, x, y);
            }
            
            // Optional: Draw subtitle
            using (var font = new Font("Segoe UI", 12, FontStyle.Regular))
            using (var brush = new SolidBrush(Color.FromArgb(200, 200, 200, 200)))
            {
                var text = "Protected by AppLock";
                var size = g.MeasureString(text, font);
                var x = (width - size.Width) / 2;
                var y = (height / 2) + 30;
                
                g.DrawString(text, font, brush, x, y);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Handle WM_DWMSENDICONICTHUMBNAIL message.
    /// Called by DWM when it needs the iconic thumbnail.
    /// </summary>
    public void HandleIconicThumbnailRequest(HWND hwnd, int width, int height)
    {
        try
        {
            // Create or retrieve cached bitmap
            if (!_lockedBitmaps.TryGetValue(hwnd, out var bitmap))
            {
                bitmap = CreateLockedBitmap(width, height);
                _lockedBitmaps[hwnd] = bitmap;
            }

            // Convert bitmap to HBITMAP and send to DWM
            var hbitmap = bitmap.GetHbitmap();
            try
            {
                unsafe
                {
                    PInvoke.DwmSetIconicThumbnail(hwnd, new HBITMAP(hbitmap), 0);
                }
            }
            finally
            {
                // Clean up HBITMAP
                PInvoke.DeleteObject(new HGDIOBJ(hbitmap));
            }

            System.Diagnostics.Debug.WriteLine($"Sent iconic thumbnail for HWND {hwnd.Value:X} ({width}x{height})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error handling iconic thumbnail request: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle WM_DWMSENDICONICLIVEPREVIEWBITMAP message.
    /// Called by DWM when it needs the live preview bitmap.
    /// </summary>
    public void HandleIconicLivePreviewRequest(HWND hwnd)
    {
        try
        {
            // Get window dimensions for live preview
            RECT rect;
            if (!PInvoke.GetWindowRect(hwnd, out rect))
            {
                return;
            }

            var width = rect.right - rect.left;
            var height = rect.bottom - rect.top;

            if (width <= 0 || height <= 0) return;

            // Create or retrieve bitmap
            if (!_lockedBitmaps.TryGetValue(hwnd, out var bitmap))
            {
                bitmap = CreateLockedBitmap(width, height);
                _lockedBitmaps[hwnd] = bitmap;
            }

            // Convert and send to DWM
            var hbitmap = bitmap.GetHbitmap();
            try
            {
                unsafe
                {
                    // DwmSetIconicLivePreviewBitmap expects a pointer to POINT
                    // Use System.Drawing.Point converted to native struct
                    var point = new System.Drawing.Point(0, 0);
                    
                    // Create a simple struct for the point
                    int x = point.X;
                    int y = point.Y;
                    
                    // Call with pointer to coordinates
                    // Note: This may need adjustment based on actual CsWin32 signature
                    PInvoke.DwmSetIconicLivePreviewBitmap(hwnd, new HBITMAP(hbitmap), null, 0);
                }
            }
            finally
            {
                PInvoke.DeleteObject(new HGDIOBJ(hbitmap));
            }

            System.Diagnostics.Debug.WriteLine($"Sent iconic live preview for HWND {hwnd.Value:X}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error handling iconic live preview request: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var bitmap in _lockedBitmaps.Values)
        {
            bitmap.Dispose();
        }
        _lockedBitmaps.Clear();
    }
}
