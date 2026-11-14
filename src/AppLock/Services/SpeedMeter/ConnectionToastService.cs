using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AppLock.Views;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AppLock.Services.SpeedMeter;

/// <summary>
/// Shows connection lost / restored alerts either as AppLock's own themed toast window
/// (default) or as a native Windows notification.
/// </summary>
public sealed class ConnectionToastService
{
    private const int MaxToasts = 3;

    private readonly DispatcherQueue _dispatcher;
    private readonly List<ConnectionToastWindow> _open = new();

    private static bool _nativeRegistered;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    public ConnectionToastService(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
    }

    public void Show(bool lost, string subtitle)
    {
        var alerts = ServiceContainer.Settings.SpeedMeter.Alerts;

        if (!lost && !alerts.NotifyRestored) return;
        if (lost && !alerts.NotifyLost) return;

        // Full-screen apps stay undisturbed when the user asked for that.
        if (alerts.SuppressInFullscreen && FullscreenDetector.IsFullscreenAppActive()) return;

        var title = lost ? "Connection lost" : "Back online";

        if (string.Equals(alerts.Style, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            ShowNative(title, subtitle);
            PlaySound(lost);
            return;
        }

        // Keep the stack small: close the oldest toast when a fourth would appear.
        _open.RemoveAll(w => w is null);
        while (_open.Count >= MaxToasts)
        {
            try { _open[0].Close(); } catch { /* already gone */ }
            _open.RemoveAt(0);
        }

        var toast = new ConnectionToastWindow(alerts, lost, title, subtitle, _open.Count);
        toast.Closed += (_, _) => _open.Remove(toast);
        _open.Add(toast);

        toast.Activate();
        PlaySound(lost);
    }

    private static void ShowNative(string title, string subtitle)
    {
        try
        {
            if (!_nativeRegistered)
            {
                AppNotificationManager.Default.Register();
                _nativeRegistered = true;
            }

            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(subtitle)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Native toast failed: {ex.Message}");
        }
    }

    /// <summary>System beep respecting the sound toggle (0x30 = warning, 0x40 = information).</summary>
    private static void PlaySound(bool lost)
    {
        var alerts = ServiceContainer.Settings.SpeedMeter.Alerts;
        if (!alerts.SoundEnabled) return;

        try
        {
            MessageBeep(lost ? 0x00000030u : 0x00000040u);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Sound failed: {ex.Message}");
        }
    }

    /// <summary>Used by the page's preview buttons.</summary>
    public void ShowPreview(bool lost)
    {
        var subtitle = lost
            ? "Preview · the internet connection dropped"
            : "Preview · offline for 2 min 14 s";

        Show(lost, subtitle);
    }

    public void CloseAll()
    {
        foreach (var toast in _open.ToList())
        {
            try { toast.Close(); } catch { /* already closed */ }
        }

        _open.Clear();
    }
}
