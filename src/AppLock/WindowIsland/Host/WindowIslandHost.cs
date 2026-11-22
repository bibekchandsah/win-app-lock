using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using AppLock.Models;
using AppLock.Services;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Windows-native Window Island host — a pure WinUI 3 / Composition notch or capsule at the
/// top of the screen.  This is completely separate from the WebView2 "Island" feature.
///
/// Architecture:
///   - Owns a borderless, topmost, non-activating Win32-backed WinUI 3 window.
///   - Draws the shape and content with the Windows.UI.Composition API (springs, scale,
///     offset, opacity, clip) — NEVER resizing HWND per frame, NEVER animating XAML
///     Width/Height/Margin.
///   - All data work is done off the UI thread and posted back via DispatcherQueue.
/// </summary>
public sealed class WindowIslandHost
{
    // ── Singleton ──────────────────────────────────────────────────────────────
    private static readonly Lazy<WindowIslandHost> _instance = new(() => new WindowIslandHost());
    public static WindowIslandHost Instance => _instance.Value;

    private WindowIslandWindow? _window;
    private readonly object _lock = new();
    private bool _started;

    private WindowIslandHost() { }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }

        // Must be created on the UI thread.
        var settings = ServiceContainer.Settings.WindowIsland;
        if (!settings.Enabled) return;

        var dq = DispatcherQueue.GetForCurrentThread()
                 ?? Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread()
                                .DispatcherQueue;

        dq.TryEnqueue(() =>
        {
            try
            {
                _window = new WindowIslandWindow();
                _window.Show(settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowIsland] Start failed: {ex}");
                lock (_lock) { _started = false; }
            }
        });
    }

    public void Stop()
    {
        lock (_lock) { _started = false; }

        var dq = DispatcherQueue.GetForCurrentThread();
        void DoStop()
        {
            try { _window?.Close(); }
            catch { }
            _window = null;
        }

        if (dq != null) dq.TryEnqueue(DoStop);
        else DoStop();
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        var dq = DispatcherQueue.GetForCurrentThread();
        void Do() => _window?.ApplySettings(settings);
        if (dq != null) dq.TryEnqueue(Do);
    }
}
