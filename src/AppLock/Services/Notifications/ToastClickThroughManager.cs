using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using AppLock.Models;

namespace AppLock.Services.Notifications;

/// <summary>
/// Manages click-through behavior for toast notifications.
/// Applies WS_EX_TRANSPARENT and monitors Alt key for bypass.
/// </summary>
public class ToastClickThroughManager : IDisposable
{
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;
    private const int VK_MENU = 0x12; // Alt key

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private CancellationTokenSource? _monitorCts;
    private bool _disposed;

    /// <summary>
    /// Apply click-through to a toast window and monitor Alt key if needed.
    /// </summary>
    public void ApplyClickThrough(HWND hwnd, NotificationCustomSettings settings, CancellationToken ct)
    {
        try
        {
            if (!settings.ClickThrough)
            {
                LogService.LogAction("notifications", "Click-through disabled, skipping");
                return;
            }

            // Get current extended style
            var exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
            var originalStyle = exStyle;

            // Add WS_EX_TRANSPARENT for click-through
            exStyle |= WS_EX_TRANSPARENT;
            SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle);

            LogService.LogAction("notifications", 
                $"Applied click-through to 0x{hwnd.Value:X}, AltToInteract={settings.AltToInteract}");

            // If Alt-to-interact is enabled, start monitoring
            if (settings.AltToInteract)
            {
                StartAltKeyMonitor(hwnd, originalStyle, ct);
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Error applying click-through: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove click-through from a toast window.
    /// </summary>
    public void RemoveClickThrough(HWND hwnd)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd))
                return;

            var exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
            
            // Remove WS_EX_TRANSPARENT
            if ((exStyle & WS_EX_TRANSPARENT) != 0)
            {
                exStyle &= ~WS_EX_TRANSPARENT;
                SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle);
                
                LogService.LogAction("notifications", 
                    $"Removed click-through from 0x{hwnd.Value:X}");
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Error removing click-through: {ex.Message}");
        }
    }

    /// <summary>
    /// Monitor Alt key and toggle click-through accordingly.
    /// </summary>
    private void StartAltKeyMonitor(HWND hwnd, uint originalStyle, CancellationToken ct)
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task.Run(async () =>
        {
            try
            {
                bool wasAltPressed = false;

                while (!_monitorCts.Token.IsCancellationRequested)
                {
                    if (!PInvoke.IsWindow(hwnd))
                        break;

                    // Check if Alt is pressed (high-order bit set means pressed)
                    bool isAltPressed = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

                    if (isAltPressed != wasAltPressed)
                    {
                        wasAltPressed = isAltPressed;

                        var exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);

                        if (isAltPressed)
                        {
                            // Alt pressed: remove transparent flag to allow interaction
                            exStyle &= ~WS_EX_TRANSPARENT;
                            LogService.LogAction("notifications", "Alt pressed, enabling interaction");
                        }
                        else
                        {
                            // Alt released: restore transparent flag
                            exStyle |= WS_EX_TRANSPARENT;
                            LogService.LogAction("notifications", "Alt released, restoring click-through");
                        }

                        SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle);
                    }

                    // Poll every 50ms
                    await Task.Delay(50, _monitorCts.Token);
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                LogService.LogAction("notifications", $"Alt key monitor error: {ex.Message}");
            }
        }, _monitorCts.Token);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        _disposed = true;
    }
}
